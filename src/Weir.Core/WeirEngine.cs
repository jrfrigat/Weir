using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>
/// Metadata describing how one data-plane response was produced, returned by
/// <see cref="WeirEngine.ExecuteAsync(WeirInvocation, Stream, WeirResponseControl, CancellationToken)"/>
/// so the host can set cache validators and headers and answer conditional requests. The engine
/// exposes the entity tag for a cache-eligible response (a cache hit, or a fresh non-truncated
/// response it just buffered) before the body is streamed, which is what makes a pre-write 304 possible.
/// </summary>
public readonly record struct WeirResponseMetadata
{
    /// <summary>True when the response was served from the cache rather than by executing the object.</summary>
    public bool CacheHit { get; init; }

    /// <summary>True when the row cap was reached; a truncated response is partial and never cached.</summary>
    public bool Truncated { get; init; }

    /// <summary>True when the response is cache-eligible and carries an <see cref="ETag"/>.</summary>
    public bool Cacheable { get; init; }

    /// <summary>The strong entity tag (already quoted) for a cache-eligible response, or null.</summary>
    public string? ETag { get; init; }

    /// <summary>The <c>max-age</c> in seconds to advertise for a cache-eligible response.</summary>
    public int MaxAgeSeconds { get; init; }

    /// <summary>
    /// True when the caller's <c>If-None-Match</c> matched <see cref="ETag"/>; in that case the engine
    /// does not write the body, and the host answers <c>304 Not Modified</c>.
    /// </summary>
    public bool NotModified { get; init; }
}

/// <summary>
/// Optional per-call inputs for conditional and cache-header handling. Passing the default value keeps
/// the engine's behaviour unconditional (no <c>If-None-Match</c> evaluation, no header callback).
/// </summary>
public readonly record struct WeirResponseControl
{
    /// <summary>The caller's raw <c>If-None-Match</c> header value, or null when there is none.</summary>
    public string? IfNoneMatch { get; init; }

    /// <summary>
    /// Invoked once for a cache-eligible response, immediately before the body is written (or skipped
    /// for a 304), so the host can set the <c>ETag</c> and <c>Cache-Control</c> headers while they can
    /// still be sent. Not invoked for truncated or non-cacheable responses.
    /// </summary>
    public Func<WeirResponseMetadata, ValueTask>? OnResponseHead { get; init; }
}

/// <summary>
/// The data-plane engine: binds parameters, serves or fills the response cache, executes the target
/// object through the matching connector, streams the JSON envelope, and notifies call observers.
/// </summary>
public sealed class WeirEngine : IDisposable
{
    /// <summary>Shared JSON writer options; validation is skipped since the writer emits well-formed output.</summary>
    private static readonly JsonWriterOptions WriterOptions = new() { SkipValidation = true };

    /// <summary>Binds request inputs to driver parameters.</summary>
    private readonly IParameterBinder _binder;

    /// <summary>Resolves connection names to their descriptors (provider, connection string, timeouts).</summary>
    private readonly IDataConnectionRegistry _registry;

    /// <summary>Registered connectors keyed by provider name (case-insensitive).</summary>
    private readonly Dictionary<string, IDbConnector> _connectors;

    /// <summary>Response cache for cache-eligible endpoints.</summary>
    private readonly IResponseCache _cache;

    /// <summary>Observers notified around each call (telemetry, audit).</summary>
    private readonly IReadOnlyList<IWeirCallObserver> _observers;

    /// <summary>Runtime-tunable settings; the row cap is read from here on each call.</summary>
    private readonly IRuntimeSettings _settings;

    /// <summary>Per-connection bulkhead and circuit breaker, applied around each database execution.</summary>
    private readonly DataConnectionGuards _guards = new();

    /// <summary>
    /// Cache fills currently in flight, keyed by cache key. Without this, a burst of concurrent requests
    /// for the same cache key all miss and all execute the same object: the cache only starts absorbing
    /// load once the first response has been stored, which is exactly when it is needed least. The first
    /// caller executes and every other caller for that key waits for its bytes.
    /// </summary>
    private readonly ConcurrentDictionary<string, CacheFill> _fills = new(StringComparer.Ordinal);

    /// <summary>Creates the engine from its collaborators.</summary>
    /// <param name="binder">Parameter binder.</param>
    /// <param name="registry">Data-connection registry.</param>
    /// <param name="connectors">Registered database connectors.</param>
    /// <param name="cache">Response cache.</param>
    /// <param name="observers">Call observers.</param>
    /// <param name="settings">Runtime settings (e.g. the row cap), applied without a restart.</param>
    public WeirEngine(
        IParameterBinder binder,
        IDataConnectionRegistry registry,
        IEnumerable<IDbConnector> connectors,
        IResponseCache cache,
        IEnumerable<IWeirCallObserver> observers,
        IRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _binder = binder;
        _registry = registry;
        _cache = cache;
        _observers = observers.ToArray();
        _connectors = connectors.ToDictionary(c => c.ProviderName, StringComparer.OrdinalIgnoreCase);
        _settings = settings;
    }

    /// <summary>
    /// Executes an invocation and writes the JSON response envelope to <paramref name="output"/>, with no
    /// conditional-request handling. Convenience overload for callers that do not serve HTTP validators
    /// (for example the admin "try it" runner).
    /// </summary>
    /// <param name="invocation">The resolved invocation.</param>
    /// <param name="output">Destination stream for the response envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Metadata describing how the response was produced.</returns>
    public Task<WeirResponseMetadata> ExecuteAsync(WeirInvocation invocation, Stream output, CancellationToken cancellationToken = default) =>
        ExecuteAsync(invocation, output, default, cancellationToken);

    /// <summary>
    /// Executes an invocation and writes the JSON response envelope to <paramref name="output"/>.
    /// Throws <see cref="WeirValidationException"/> for bad input and <see cref="WeirConfigurationException"/>
    /// for misconfiguration; the host maps these to problem+json responses. For a cache-eligible response
    /// the engine exposes the entity tag through <paramref name="control"/> before writing, so the host can
    /// answer a matching <c>If-None-Match</c> with <c>304 Not Modified</c> and set cache headers.
    /// </summary>
    /// <param name="invocation">The resolved invocation.</param>
    /// <param name="output">Destination stream for the response envelope.</param>
    /// <param name="control">Conditional-request inputs and the header callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Metadata describing how the response was produced.</returns>
    public async Task<WeirResponseMetadata> ExecuteAsync(
        WeirInvocation invocation,
        Stream output,
        WeirResponseControl control,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(output);

        var endpoint = invocation.Endpoint;
        var descriptor = _registry.Resolve(endpoint.ConnectionName);
        if (!_connectors.TryGetValue(descriptor.Provider, out var connector))
        {
            throw new WeirConfigurationException($"No connector is registered for provider '{descriptor.Provider}'.");
        }

        var context = new WeirCallContext
        {
            Route = endpoint.Route,
            EndpointId = endpoint.Id,
            HttpMethod = endpoint.HttpMethod,
            ConnectionName = endpoint.ConnectionName,
            ObjectName = endpoint.QualifiedName,
            ApiKeyPrefix = invocation.ApiKeyPrefix,
            StartTimestamp = Stopwatch.GetTimestamp(),
            LogRequests = endpoint.Logging.Enabled,
            SlowThresholdPercent = endpoint.Logging.SlowThresholdPercent,
        };

        await NotifyAsync(context, static (o, c) => o.OnStartedAsync(c));

        // This call's stake in a fill, whether it started one or is waiting on someone else's. Held for as
        // long as this call wants the answer; dropping it is what eventually stops the query, once every
        // other caller has dropped theirs too.
        CacheFill.Participation? participation = null;

        try
        {
            var maxRows = Math.Max(0, _settings.Current.MaxRows);

            // W-5: measure binding duration separately from DB execution.
            var bindingStart = Stopwatch.GetTimestamp();
            var binding = _binder.Bind(invocation);
            context.BindingDurationMs = Stopwatch.GetElapsedTime(bindingStart).TotalMilliseconds;

            var cacheKey = endpoint.Cache.Enabled ? CacheKey.Build(endpoint, binding.Values, invocation.ApiKeyPrefix) : null;

            // Request-log capture is opt-in per endpoint and gated by the global switch. Parameters come
            // from the bound scalar values; the result is captured from the buffered body (see below).
            var logEnabled = _settings.Current.RequestLogEnabled && endpoint.Logging.Enabled;
            if (logEnabled && endpoint.Logging.LogParameters)
            {
                context.CapturedParameters = CaptureParameters(binding.Values);
            }

            var captureResult = logEnabled && endpoint.Logging.LogResult;

            var request = new DbExecutionRequest
            {
                ConnectionName = endpoint.ConnectionName,
                Schema = endpoint.Schema,
                ObjectName = endpoint.ObjectName,
                ObjectType = endpoint.ObjectType,
                CommandTimeoutSeconds = endpoint.CommandTimeoutSeconds ?? descriptor.DefaultCommandTimeoutSeconds,
                Parameters = binding.Parameters,
            };

            WeirResponseMetadata metadata;
            if (cacheKey is not null)
            {
                // W-4: measure cache lookup and use the pre-computed ETag from the cache entry,
                // avoiding a redundant SHA-256 recomputation on every cache hit.
                var cacheStart = Stopwatch.GetTimestamp();
                var cached = await _cache.GetAsync(cacheKey, cancellationToken);
                context.CacheLookupDurationMs = Stopwatch.GetElapsedTime(cacheStart).TotalMilliseconds;

                if (cached is { } entry)
                {
                    var streamStart = Stopwatch.GetTimestamp();
                    metadata = await EmitCacheableAsync(output, entry.Bytes, entry.ETag, cacheHit: true, endpoint.Cache.TtlSeconds, control, cancellationToken);
                    context.StreamingDurationMs = Stopwatch.GetElapsedTime(streamStart).TotalMilliseconds;
                    context.CacheHit = true;
                    context.StatusCode = metadata.NotModified ? 304 : 200;
                    Finish(context);
                    await NotifyAsync(context, static (o, c) => o.OnCompletedAsync(c));
                    return metadata;
                }

                // A miss. Either claim the fill for this key or join the one already running: the first
                // caller starts the query, the rest wait for its bytes instead of piling the same query
                // onto the database. The endpoint can opt out, in which case every caller runs its own.
                var candidate = endpoint.Cache.CoalesceRequests ? new CacheFill() : null;
                var inFlight = candidate is null ? null : _fills.GetOrAdd(cacheKey, candidate);
                participation = inFlight?.TryJoin(cancellationToken);
                if (inFlight is not null && participation is not null)
                {
                    var owned = ReferenceEquals(inFlight, candidate);
                    if (owned)
                    {
                        // Started, not awaited inline. This call waits on the fill exactly like every other
                        // caller, so its own token can end its wait - a disconnect, or the gateway timeout -
                        // without ending the query the others are still waiting for.
                        _ = RunFillAsync(cacheKey, inFlight, connector, request, endpoint, maxRows);
                    }

                    var shared = await inFlight.Task.WaitAsync(cancellationToken);

                    if (captureResult)
                    {
                        context.CapturedResult = CaptureResult(shared.Payload.Span);
                    }

                    var streamStart = Stopwatch.GetTimestamp();
                    metadata = shared.ETag is { } sharedETag
                        ? await EmitCacheableAsync(output, shared.Payload, sharedETag, cacheHit: !owned, endpoint.Cache.TtlSeconds, control, cancellationToken)
                        : await EmitRawAsync(output, shared.Payload, shared.Truncated, cancellationToken);
                    context.StreamingDurationMs = Stopwatch.GetElapsedTime(streamStart).TotalMilliseconds;

                    // A caller that waited is counted as a cache hit: it was served the fill's bytes without
                    // touching the database, which is what the hit ratio measures. Its duration still
                    // includes the wait, so the latency it reports is the one its client actually saw.
                    context.CacheHit = !owned;
                    context.DbDurationMs = shared.DbDurationMs;
                    context.RowsReturned = shared.RowCount;
                    context.StatusCode = metadata.NotModified ? 304 : 200;
                    Finish(context);
                    await NotifyAsync(context, static (o, c) => o.OnCompletedAsync(c));
                    return metadata;
                }

                // Either the endpoint does not coalesce, or the fill this call found was given up on between
                // being found and being joined - everyone waiting on it had gone, so its query was dropped.
                // Fall through and run this call's own.
            }

            if (cacheKey is not null)
            {
                // A cache-eligible call running on its own: the endpoint opted out of coalescing, or the
                // fill it found was given up on as it arrived. Either way nobody else is waiting on this
                // one, so this caller's own token is the right lifetime - and it still stores what it
                // produces, so the next caller can hit the cache.
                var built = await BuildAsync(connector, request, endpoint, maxRows, cancellationToken);
                context.DbDurationMs = built.DbDurationMs;

                if (captureResult)
                {
                    context.CapturedResult = CaptureResult(built.Payload.Span);
                }

                if (built.ETag is { } builtETag)
                {
                    StoreInBackground(cacheKey, new CachedResponse(built.Payload, builtETag), TimeSpan.FromSeconds(endpoint.Cache.TtlSeconds));
                }

                var builtStreamStart = Stopwatch.GetTimestamp();
                metadata = built.ETag is { } emitETag
                    ? await EmitCacheableAsync(output, built.Payload, emitETag, cacheHit: false, endpoint.Cache.TtlSeconds, control, cancellationToken)
                    : await EmitRawAsync(output, built.Payload, built.Truncated, cancellationToken);
                context.StreamingDurationMs = Stopwatch.GetElapsedTime(builtStreamStart).TotalMilliseconds;

                context.RowsReturned = built.RowCount;
                context.StatusCode = metadata.NotModified ? 304 : 200;
                Finish(context);
                await NotifyAsync(context, static (o, c) => o.OnCompletedAsync(c));
                return metadata;
            }

            // Per-connection resilience: reject fast if the breaker is open, then take a bulkhead permit
            // held for the whole execution (including streaming), so a saturated connection is not piled on.
            var settings = _settings.Current;
            var guard = _guards.For(endpoint.ConnectionName);
            guard.EnsureClosed(settings.CircuitBreakerFailureThreshold);

            WeirResponseWriter.WriteResult result;
            using (guard.Enter(settings.MaxConcurrentRequestsPerConnection))
            {
                try
                {
                    // Buffer the body when this endpoint captures its result for the request log (an
                    // explicit opt-in); otherwise stream straight to the output.
                    if (captureResult)
                    {
                        using var buffer = new MemoryStream(BufferCapacityFor(endpoint.Id));
                        // DB phase: execute and drain all rows into the buffer. Streaming to the client
                        // is measured separately below, so the two phases do not overlap.
                        var dbStart = Stopwatch.GetTimestamp();
                        await using (var execution = await connector.ExecuteAsync(request, cancellationToken))
                        {
                            result = await WeirResponseWriter.WriteAsync(buffer, execution, endpoint, WriterOptions, maxRows, cancellationToken);
                        }

                        context.DbDurationMs = Stopwatch.GetElapsedTime(dbStart).TotalMilliseconds;
                        RecordBufferSize(endpoint.Id, buffer.Length);
                        context.CapturedResult = CaptureResult(buffer);

                        var streamStart = Stopwatch.GetTimestamp();
                        buffer.Position = 0;
                        await buffer.CopyToAsync(output, cancellationToken);
                        context.StreamingDurationMs = Stopwatch.GetElapsedTime(streamStart).TotalMilliseconds;
                        metadata = new WeirResponseMetadata { Truncated = result.Truncated };
                    }
                    else
                    {
                        // DB and streaming are fused on the direct-to-output path (rows are written to
                        // the client as they are read), so they cannot be separated; attribute the whole
                        // span to the DB phase and leave StreamingDurationMs at zero.
                        var dbStart = Stopwatch.GetTimestamp();
                        await using var execution = await connector.ExecuteAsync(request, cancellationToken);
                        result = await WeirResponseWriter.WriteAsync(output, execution, endpoint, WriterOptions, maxRows, cancellationToken);
                        context.DbDurationMs = Stopwatch.GetElapsedTime(dbStart).TotalMilliseconds;
                        metadata = new WeirResponseMetadata { Truncated = result.Truncated };
                    }

                    guard.RecordSuccess();
                }
                catch (Exception ex) when (TripsBreaker(ex))
                {
                    guard.RecordFailure(settings.CircuitBreakerFailureThreshold, settings.CircuitBreakerResetSeconds);
                    throw;
                }
            }

            context.RowsReturned = result.RowCount;
            context.StatusCode = metadata.NotModified ? 304 : 200;
            Finish(context);
            await NotifyAsync(context, static (o, c) => o.OnCompletedAsync(c));
            return metadata;
        }
        catch (Exception ex)
        {
            context.Outcome = OutcomeCodes.Error;
            context.Error = ex.Message;
            // Record the status the host will map this exception to, so traces and observers do not see
            // the default 0 for every failed call.
            context.StatusCode = ex switch
            {
                WeirValidationException => 400,
                OperationCanceledException => 499,
                WeirConnectionUnavailableException => 503,
                _ => 500,
            };
            // Classify database failures (timeout / deadlock / constraint / connection) for telemetry.
            context.DbError = connector.ClassifyError(ex);
            Finish(context);
            await NotifyFailAsync(context, ex);
            throw;
        }
        finally
        {
            // Drops this call's stake in the fill. If it was the last one, the query behind it stops - there
            // is no longer anybody to answer. The fill itself is owned by RunFillAsync, not by this request.
            participation?.Dispose();
        }
    }

    /// <summary>Upper bound on a seeded buffer, so one huge response cannot make every later call pre-allocate megabytes.</summary>
    private const int MaxSeededBufferBytes = 4 * 1024 * 1024;

    /// <summary>Smallest seed worth asking for; below this MemoryStream's own growth is cheap enough.</summary>
    private const int MinSeededBufferBytes = 4 * 1024;

    /// <summary>
    /// The last buffered body size per endpoint, used to size the next one. Not a metric - only a hint.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, int> _bufferSizes = new();

    /// <summary>
    /// Suggests an initial capacity for an endpoint's response buffer, from the size its last response
    /// came to. A default MemoryStream starts at nothing and doubles as rows arrive, so a large response
    /// is assembled by copying itself through every intermediate size, and each intermediate over ~85 KB
    /// lands on the large object heap. An endpoint's responses are usually about the same size, so the
    /// previous one is a good guess and turns that chain into a single allocation. It is only a hint: the
    /// stream still grows if the guess is low, and the buffer is right-sized by ToArray before it is cached.
    /// </summary>
    /// <param name="endpointId">The endpoint whose response is about to be buffered.</param>
    /// <returns>The capacity to start the buffer at, or zero to let it grow from scratch.</returns>
    private int BufferCapacityFor(Guid endpointId) =>
        _bufferSizes.TryGetValue(endpointId, out var last) && last >= MinSeededBufferBytes
            ? Math.Min(last, MaxSeededBufferBytes)
            : 0;

    /// <summary>Records the size an endpoint's response came to, as the hint for its next one.</summary>
    /// <param name="endpointId">The endpoint id.</param>
    /// <param name="length">The buffered body length in bytes.</param>
    private void RecordBufferSize(Guid endpointId, long length)
    {
        if (endpointId != Guid.Empty)
        {
            _bufferSizes[endpointId] = (int)Math.Min(length, MaxSeededBufferBytes);
        }
    }

    /// <summary>
    /// Runs one cache fill, detached from any single request. Nothing awaits this: the caller that started
    /// it waits on the fill like everyone else, so its own request can end - a disconnect, or the gateway
    /// timeout reaching its client - without taking the query down with it. The query stops only when the
    /// last caller waiting for it has gone, which is what <see cref="CacheFill.Token"/> tracks.
    /// </summary>
    /// <param name="key">The cache key being filled.</param>
    /// <param name="fill">The fill to publish into.</param>
    /// <param name="connector">The connector for the endpoint's connection.</param>
    /// <param name="request">The execution request.</param>
    /// <param name="endpoint">The endpoint being called.</param>
    /// <param name="maxRows">The row cap.</param>
    /// <returns>A task that completes once the fill has been published and stored.</returns>
    private async Task RunFillAsync(
        string key,
        CacheFill fill,
        IDbConnector connector,
        DbExecutionRequest request,
        EndpointDefinition endpoint,
        int maxRows)
    {
        try
        {
            var filled = await BuildAsync(connector, request, endpoint, maxRows, fill.Token);

            // Publish before storing: everyone waiting can start writing to their clients while the entry
            // is still on its way into the cache.
            fill.Complete(filled);

            if (filled.ETag is { } etag)
            {
                try
                {
                    // CancellationToken.None on purpose: the entry outlives the request that produced it,
                    // so a client giving up must not throw away a payload others are already waiting on.
                    await _cache.SetAsync(key, new CachedResponse(filled.Payload, etag), TimeSpan.FromSeconds(endpoint.Cache.TtlSeconds), CancellationToken.None);
                }
                catch (Exception)
                {
                    // The response is already served; a cache-store failure has nowhere useful to surface.
                    // The entry is simply absent, so the next caller re-fills it.
                }
            }
        }
        catch (Exception ex)
        {
            fill.Fail(ex);
        }
        finally
        {
            // Held until the store lands, so a caller arriving in that window still joins the fill instead
            // of missing both it and the cache.
            Release(key, fill);
        }
    }

    /// <summary>
    /// Executes the endpoint and renders the whole response into memory, under the connection's bulkhead
    /// and circuit breaker.
    /// </summary>
    /// <param name="connector">The connector for the endpoint's connection.</param>
    /// <param name="request">The execution request.</param>
    /// <param name="endpoint">The endpoint being called.</param>
    /// <param name="maxRows">The row cap.</param>
    /// <param name="cancellationToken">The lifetime of the execution.</param>
    /// <returns>The rendered body, its entity tag when cacheable, and what it cost.</returns>
    private async Task<FilledResponse> BuildAsync(
        IDbConnector connector,
        DbExecutionRequest request,
        EndpointDefinition endpoint,
        int maxRows,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var guard = _guards.For(endpoint.ConnectionName);
        guard.EnsureClosed(settings.CircuitBreakerFailureThreshold);

        using (guard.Enter(settings.MaxConcurrentRequestsPerConnection))
        {
            try
            {
                using var buffer = new MemoryStream(BufferCapacityFor(endpoint.Id));
                var dbStart = Stopwatch.GetTimestamp();
                WeirResponseWriter.WriteResult result;
                await using (var execution = await connector.ExecuteAsync(request, cancellationToken))
                {
                    result = await WeirResponseWriter.WriteAsync(buffer, execution, endpoint, WriterOptions, maxRows, cancellationToken);
                }

                var dbDurationMs = Stopwatch.GetElapsedTime(dbStart).TotalMilliseconds;
                RecordBufferSize(endpoint.Id, buffer.Length);
                guard.RecordSuccess();

                // Never cache a truncated (row-capped) response: it would serve partial data as if complete
                // for the whole TTL, and a partial body must not carry an ETag that a complete one could be
                // revalidated against. It is still handed to anyone waiting - they would have produced the
                // same partial body.
                var payload = buffer.ToArray();
                var etag = result.Truncated ? null : ResponseETag.Compute(payload);
                return new FilledResponse(payload, etag, result.Truncated, result.RowCount, dbDurationMs);
            }
            catch (Exception ex) when (TripsBreaker(ex))
            {
                guard.RecordFailure(settings.CircuitBreakerFailureThreshold, settings.CircuitBreakerResetSeconds);
                throw;
            }
        }
    }

    /// <summary>
    /// Stores a filled entry without the caller waiting for it, then releases the in-flight registration.
    /// This is what keeps a cache store off the response path: the bytes have already been handed to the
    /// waiters and are on their way to this call's client, so a store that talks to a network cache costs
    /// the client nothing. The registration is held until the store lands so that a request arriving in
    /// between still joins the fill rather than re-executing.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="entry">The response bytes and their entity tag.</param>
    /// <param name="ttl">The endpoint's cache TTL.</param>
    private void StoreInBackground(string key, CachedResponse entry, TimeSpan ttl)
    {
        // Deliberately not awaited. An in-process cache completes this synchronously, so there is no
        // thread-pool hop to pay for; a cache doing I/O suspends at its first await and finishes behind
        // the response. Task.Run would only add a hop to the common case.
        _ = StoreAsync();

        async Task StoreAsync()
        {
            try
            {
                // CancellationToken.None on purpose: the entry outlives the request that produced it, so
                // that client giving up must not throw away a payload others are already waiting on.
                await _cache.SetAsync(key, entry, ttl, CancellationToken.None);
            }
            catch (Exception)
            {
                // The response is already served; a cache-store failure has nowhere useful to surface and
                // must not become an unobserved task exception. The entry is simply absent, so the next
                // caller re-fills it.
            }
        }
    }

    /// <summary>
    /// Removes an in-flight registration, but only if it is still the one this call owns, so a fill that
    /// has already been replaced by a later one is left alone.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="fill">The registration to remove.</param>
    private void Release(string key, CacheFill fill)
    {
        _fills.TryRemove(new KeyValuePair<string, CacheFill>(key, fill));
        fill.Dispose();
    }

    /// <summary>
    /// One in-flight cache fill. The caller that claims it executes; everyone else for the same key
    /// awaits <see cref="Task"/> and serves the bytes it produces.
    /// <para>
    /// The execution runs on <see cref="Token"/>, not on any one caller's request token, because the work
    /// belongs to everyone waiting on it rather than to whoever happened to arrive first. A client hanging
    /// up cancels its own request and nothing else: the query keeps running for the callers still queued
    /// behind it, and they get the answer it was already most of the way to producing. The token fires only
    /// once the last of them has gone - disconnected, or timed out - so the query lives exactly as long as
    /// somebody still wants its result, and no longer.
    /// </para>
    /// </summary>
    private sealed class CacheFill : IDisposable
    {
        /// <summary>
        /// Completed with the filled response, or with null when the owner produced nothing. Continuations
        /// run asynchronously so that completing the fill never drags a waiter's response-writing onto the
        /// owner's thread.
        /// </summary>
        private readonly TaskCompletionSource<FilledResponse> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Cancelled once nobody is waiting for this fill any more.</summary>
        private readonly CancellationTokenSource _abandoned = new();

        /// <summary>Guards the waiter count against the disposal of <see cref="_abandoned"/>.</summary>
        private readonly Lock _gate = new();

        /// <summary>How many callers still want this fill's result.</summary>
        private int _waiting;

        /// <summary>Set once the fill has been given up on, so no later caller waits for an answer that is not coming.</summary>
        private bool _closed;

        /// <summary>Whether <see cref="Dispose"/> has already run.</summary>
        private bool _disposed;

        /// <summary>Resolves to the filled response, or faults with whatever the execution failed on.</summary>
        public Task<FilledResponse> Task => _completion.Task;

        /// <summary>The token the execution runs on; it fires when the last waiter gives up.</summary>
        public CancellationToken Token => _abandoned.Token;

        /// <summary>
        /// Registers a caller as waiting for this fill for as long as its own request is alive. Dispose the
        /// result once the caller no longer needs the answer - because it got one, or because it gave up.
        /// </summary>
        /// <param name="cancellationToken">The caller's request token; when it fires, this caller stops waiting.</param>
        /// <returns>The caller's participation, or null when the fill has already been given up on and
        /// cannot be waited for.</returns>
        public Participation? TryJoin(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return null;
                }

                _waiting++;
            }

            return new Participation(this, cancellationToken);
        }

        /// <summary>Publishes the response to every waiter.</summary>
        /// <param name="response">The bytes the execution produced.</param>
        public void Complete(FilledResponse response) => _completion.TrySetResult(response);

        /// <summary>
        /// Hands the execution's failure to everyone waiting. Sharing it is right here: a fill is only ever
        /// cancelled once nobody is left waiting, so any failure a waiter can still see is one the database
        /// really produced - it would have produced the same for each of them had they all run it.
        /// </summary>
        /// <param name="exception">The failure the execution raised.</param>
        public void Fail(Exception exception)
        {
            if (_completion.TrySetException(exception))
            {
                // Everyone may already have gone, in which case nothing awaits this task and its exception
                // would surface later as an unobserved-task crash. Observe it here; a waiter that does await
                // still sees it too.
                _completion.Task.ContinueWith(
                    static faulted => _ = faulted.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        /// <summary>Disposes the abandonment source once the fill is finished with.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _abandoned.Dispose();
            }
        }

        /// <summary>
        /// Drops one waiter, cancelling the execution when it was the last. Cancelling inside the lock is
        /// what keeps it from racing <see cref="Dispose"/> onto a disposed source; the section stays short
        /// because the only registrations on this token belong to the running execution.
        /// </summary>
        private void Leave()
        {
            lock (_gate)
            {
                if (--_waiting > 0)
                {
                    return;
                }

                // The answer already exists, so there is nothing to cancel and no reason to turn away a
                // caller arriving now - the fill is still registered until its store lands, and serving
                // them from it beats making them re-run the query.
                if (_completion.Task.IsCompleted)
                {
                    return;
                }

                _closed = true;
                if (!_disposed)
                {
                    _abandoned.Cancel();
                }
            }
        }

        /// <summary>
        /// One caller's stake in a fill. Ends when the caller stops caring, whether that is because it has
        /// its answer or because its own request was cancelled first.
        /// </summary>
        internal sealed class Participation : IDisposable
        {
            private readonly CacheFill _fill;
            private readonly CancellationTokenRegistration _registration;

            /// <summary>Guards against leaving twice: the request token can fire before or after disposal.</summary>
            private int _left;

            /// <summary>Registers the caller and hooks its request token.</summary>
            /// <param name="fill">The fill being waited on.</param>
            /// <param name="cancellationToken">The caller's request token.</param>
            public Participation(CacheFill fill, CancellationToken cancellationToken)
            {
                _fill = fill;
                _registration = cancellationToken.Register(static state => ((Participation)state!).Leave(), this);
            }

            /// <summary>Ends this caller's stake in the fill.</summary>
            public void Dispose()
            {
                _registration.Dispose();
                Leave();
            }

            /// <summary>Leaves the fill exactly once, however many times this is reached.</summary>
            private void Leave()
            {
                if (Interlocked.Exchange(ref _left, 1) == 0)
                {
                    _fill.Leave();
                }
            }
        }
    }

    /// <summary>A response body produced by one execution and shared with everyone waiting on its fill.</summary>
    /// <param name="Payload">The complete response bytes.</param>
    /// <param name="ETag">The entity tag, or null when the response is truncated and so not cacheable.</param>
    /// <param name="Truncated">Whether the row cap was hit.</param>
    /// <param name="RowCount">Rows across all result sets, reported by each waiter as its own row count.</param>
    /// <param name="DbDurationMs">How long the execution took, reported by everyone it served.</param>
    private readonly record struct FilledResponse(
        ReadOnlyMemory<byte> Payload, string? ETag, bool Truncated, int RowCount, double DbDurationMs);

    /// <summary>
    /// Writes (or, on a validator match, skips) a fully buffered cache-eligible body. Uses the
    /// pre-computed entity tag from the cache entry, invokes the header callback so the host can set
    /// <c>ETag</c>/<c>Cache-Control</c>, and honours <c>If-None-Match</c> by not writing the body
    /// when it matches.
    /// </summary>
    /// <param name="output">Destination stream.</param>
    /// <param name="payload">The complete response bytes.</param>
    /// <param name="etag">The pre-computed quoted entity tag.</param>
    /// <param name="cacheHit">Whether the bytes came from the cache.</param>
    /// <param name="ttlSeconds">The endpoint's cache TTL, advertised as <c>max-age</c>.</param>
    /// <param name="control">Conditional-request inputs and the header callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response metadata, including whether a 304 short-circuit applied.</returns>
    private static async Task<WeirResponseMetadata> EmitCacheableAsync(
        Stream output,
        ReadOnlyMemory<byte> payload,
        string etag,
        bool cacheHit,
        int ttlSeconds,
        WeirResponseControl control,
        CancellationToken cancellationToken)
    {
        var metadata = new WeirResponseMetadata
        {
            CacheHit = cacheHit,
            Cacheable = true,
            Truncated = false,
            ETag = etag,
            MaxAgeSeconds = ttlSeconds,
            NotModified = IfNoneMatchSatisfied(control.IfNoneMatch, etag),
        };

        if (control.OnResponseHead is not null)
        {
            await control.OnResponseHead(metadata);
        }

        if (!metadata.NotModified)
        {
            await output.WriteAsync(payload, cancellationToken);
        }

        return metadata;
    }

    /// <summary>
    /// Writes a buffered body that carries no cache validator (a truncated response), so no header
    /// callback runs and no <c>If-None-Match</c> is evaluated.
    /// </summary>
    /// <param name="output">Destination stream.</param>
    /// <param name="payload">The complete response bytes.</param>
    /// <param name="truncated">Whether the row cap was hit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response metadata.</returns>
    private static async Task<WeirResponseMetadata> EmitRawAsync(
        Stream output,
        ReadOnlyMemory<byte> payload,
        bool truncated,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(payload, cancellationToken);
        return new WeirResponseMetadata { Truncated = truncated };
    }

    /// <summary>
    /// Evaluates an <c>If-None-Match</c> header against an entity tag using the weak comparison function
    /// (RFC 7232): <c>*</c> matches any tag, a comma-separated list matches if any member matches, and the
    /// weak indicator <c>W/</c> is ignored on both sides. Tags are quoted per the spec, so a naive
    /// <c>header.Split(',')</c> is safe here -- quoted entity tags never contain commas, and any
    /// malformed input (e.g. a tag containing a comma) is simply ignored as a non-match.
    /// </summary>
    /// <param name="header">The raw <c>If-None-Match</c> header value, or null.</param>
    /// <param name="etag">The current entity tag (quoted).</param>
    /// <returns>True when the precondition is satisfied and a 304 should be returned.</returns>
    private static bool IfNoneMatchSatisfied(string? header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var target = StripWeak(etag);
        foreach (var raw in header.Split(','))
        {
            var candidate = raw.Trim();
            if (candidate == "*")
            {
                return true;
            }

            if (string.Equals(StripWeak(candidate), target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an execution failure should count against the connection's circuit breaker. Client-side
    /// outcomes - a validation error, a cancellation (client disconnect or gateway timeout) or a breaker
    /// rejection - do not, so client behaviour cannot trip the breaker; database and connection failures do.
    /// </summary>
    /// <param name="ex">The thrown exception.</param>
    /// <returns>True when the failure indicates a connection problem.</returns>
    private static bool TripsBreaker(Exception ex) =>
        ex is not WeirValidationException and not OperationCanceledException and not WeirConnectionUnavailableException;

    /// <summary>Removes the weak-validator prefix <c>W/</c> from an entity tag, if present.</summary>
    /// <param name="tag">The entity tag.</param>
    /// <returns>The tag without a leading <c>W/</c>.</returns>
    private static string StripWeak(string tag) =>
        tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;

    /// <summary>Disposes the per-connection guards (their bulkhead semaphores).</summary>
    public void Dispose() => _guards.Dispose();

    private static void Finish(WeirCallContext context) =>
        context.DurationMs = Stopwatch.GetElapsedTime(context.StartTimestamp).TotalMilliseconds;

    /// <summary>Upper bound (in UTF-16 chars) on the captured parameters JSON, so parameter logging cannot hold unbounded memory.</summary>
    private const int MaxCapturedParameterChars = 64 * 1024;

    /// <summary>Upper bound on the captured response body, so result logging cannot hold large payloads.</summary>
    private const int MaxCapturedResultBytes = 16 * 1024;

    /// <summary>
    /// Serializes the bound scalar input values to JSON for the request log (best-effort).
    /// The output is capped at <see cref="MaxCapturedParameterChars"/> to prevent unbounded
    /// memory growth from large TVP tokens or many parameters. The cap can leave the JSON
    /// syntactically incomplete; the admin log viewer already falls back to raw text on a parse
    /// failure, and the marker below makes the truncation explicit.
    /// </summary>
    /// <param name="values">The bound input values keyed by logical name.</param>
    /// <returns>A JSON object, or null if the values could not be serialized.</returns>
    private static string? CaptureParameters(IReadOnlyDictionary<string, object?> values)
    {
        try
        {
            var json = JsonSerializer.Serialize(values);
            if (json.Length > MaxCapturedParameterChars)
            {
                // Back off one char if the cut would split a UTF-16 surrogate pair, so the result
                // never ends in a lone surrogate.
                var cut = MaxCapturedParameterChars;
                if (char.IsHighSurrogate(json[cut - 1]))
                {
                    cut--;
                }

                return json[..cut] + "\n...[truncated]";
            }

            return json;
        }
        catch (Exception)
        {
            // Never let a capture failure affect the request; the parameters are best-effort.
            return null;
        }
    }

    /// <summary>Reads the buffered response body as UTF-8 JSON text, capped at a fixed size for the log.</summary>
    /// <param name="buffer">The buffered response body.</param>
    /// <returns>The (possibly truncated) response text.</returns>
    private static string CaptureResult(MemoryStream buffer) =>
        CaptureResult(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));

    /// <summary>Reads a response body as UTF-8 JSON text, capped at a fixed size for the log.</summary>
    /// <param name="payload">The response body bytes.</param>
    /// <returns>The (possibly truncated) response text.</returns>
    private static string CaptureResult(ReadOnlySpan<byte> payload)
    {
        var length = Math.Min(payload.Length, MaxCapturedResultBytes);
        var text = System.Text.Encoding.UTF8.GetString(payload[..length]);
        return payload.Length > MaxCapturedResultBytes ? text + "\n...[truncated]" : text;
    }

    private async Task NotifyAsync(WeirCallContext context, Func<IWeirCallObserver, WeirCallContext, ValueTask> action)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await action(observer, context);
            }
            catch (Exception)
            {
                // Observer failures must never affect the request path.
            }
        }
    }

    private async Task NotifyFailAsync(WeirCallContext context, Exception exception)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnFailedAsync(context, exception);
            }
            catch (Exception)
            {
                // Observer failures must never affect the request path.
            }
        }
    }
}
