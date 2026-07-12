using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Weir.Abstractions;

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
            ObjectName = $"{endpoint.Schema}.{endpoint.ObjectName}",
            ApiKeyPrefix = invocation.ApiKeyPrefix,
            StartTimestamp = Stopwatch.GetTimestamp(),
            LogRequests = endpoint.Logging.Enabled,
            SlowThresholdPercent = endpoint.Logging.SlowThresholdPercent,
        };

        await NotifyAsync(context, static (o, c) => o.OnStartedAsync(c));

        try
        {
            var maxRows = Math.Max(0, _settings.Current.MaxRows);
            var binding = _binder.Bind(invocation);
            var cacheKey = endpoint.Cache.Enabled ? CacheKey.Build(endpoint, binding.Values, invocation.ApiKeyPrefix) : null;

            // Request-log capture is opt-in per endpoint and gated by the global switch. Parameters come
            // from the bound scalar values; the result is captured from the buffered body (see below).
            var logEnabled = _settings.Current.RequestLogEnabled && endpoint.Logging.Enabled;
            if (logEnabled && endpoint.Logging.LogParameters)
            {
                context.CapturedParameters = CaptureParameters(binding.Values);
            }

            var captureResult = logEnabled && endpoint.Logging.LogResult;

            WeirResponseMetadata metadata;
            if (cacheKey is not null)
            {
                var cached = await _cache.GetAsync(cacheKey, cancellationToken);
                if (cached is { } bytes)
                {
                    metadata = await EmitCacheableAsync(output, bytes, cacheHit: true, endpoint.Cache.TtlSeconds, control, cancellationToken);
                    context.CacheHit = true;
                    context.StatusCode = metadata.NotModified ? 304 : 200;
                    Finish(context);
                    await NotifyAsync(context, static (o, c) => o.OnCompletedAsync(c));
                    return metadata;
                }
            }

            var request = new DbExecutionRequest
            {
                ConnectionName = endpoint.ConnectionName,
                Schema = endpoint.Schema,
                ObjectName = endpoint.ObjectName,
                ObjectType = endpoint.ObjectType,
                CommandTimeoutSeconds = endpoint.CommandTimeoutSeconds ?? descriptor.DefaultCommandTimeoutSeconds,
                Parameters = binding.Parameters,
            };

            // Per-connection resilience: reject fast if the breaker is open, then take a bulkhead permit
            // held for the whole execution (including streaming), so a saturated connection is not piled on.
            var settings = _settings.Current;
            var guard = _guards.For(endpoint.ConnectionName);
            guard.EnsureClosed(settings.CircuitBreakerFailureThreshold);

            var dbStart = Stopwatch.GetTimestamp();
            WeirResponseWriter.WriteResult result;
            using (guard.Enter(settings.MaxConcurrentRequestsPerConnection))
            {
                try
                {
                    // Buffer the body when caching, or when this endpoint captures its result for the
                    // request log (an explicit opt-in); otherwise stream straight to the output.
                    if (cacheKey is not null || captureResult)
                    {
                        using var buffer = new MemoryStream();
                        await using (var execution = await connector.ExecuteAsync(request, cancellationToken))
                        {
                            result = await WeirResponseWriter.WriteAsync(buffer, execution, endpoint, WriterOptions, maxRows, cancellationToken);
                        }

                        if (captureResult)
                        {
                            context.CapturedResult = CaptureResult(buffer);
                        }

                        // Never cache a truncated (row-capped) response: it would serve partial data as if
                        // complete for the whole TTL. Stream it to this caller, but skip the cache write, and do
                        // not attach an ETag (a partial body must not be revalidated against a complete one).
                        if (cacheKey is not null && !result.Truncated)
                        {
                            var payload = buffer.ToArray();
                            await _cache.SetAsync(cacheKey, payload, TimeSpan.FromSeconds(endpoint.Cache.TtlSeconds), cancellationToken);
                            metadata = await EmitCacheableAsync(output, payload, cacheHit: false, endpoint.Cache.TtlSeconds, control, cancellationToken);
                        }
                        else
                        {
                            buffer.Position = 0;
                            await buffer.CopyToAsync(output, cancellationToken);
                            metadata = new WeirResponseMetadata { Truncated = result.Truncated };
                        }
                    }
                    else
                    {
                        await using var execution = await connector.ExecuteAsync(request, cancellationToken);
                        result = await WeirResponseWriter.WriteAsync(output, execution, endpoint, WriterOptions, maxRows, cancellationToken);
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

            context.DbDurationMs = Stopwatch.GetElapsedTime(dbStart).TotalMilliseconds;
            context.RowsReturned = result.RowCount;
            context.StatusCode = metadata.NotModified ? 304 : 200;
            Finish(context);
            await NotifyAsync(context, static (o, c) => o.OnCompletedAsync(c));
            return metadata;
        }
        catch (Exception ex)
        {
            context.Outcome = "error";
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
    }

    /// <summary>
    /// Writes (or, on a validator match, skips) a fully buffered cache-eligible body. Computes the entity
    /// tag from the bytes, invokes the header callback so the host can set <c>ETag</c>/<c>Cache-Control</c>,
    /// and honours <c>If-None-Match</c> by not writing the body when it matches.
    /// </summary>
    /// <param name="output">Destination stream.</param>
    /// <param name="payload">The complete response bytes.</param>
    /// <param name="cacheHit">Whether the bytes came from the cache.</param>
    /// <param name="ttlSeconds">The endpoint's cache TTL, advertised as <c>max-age</c>.</param>
    /// <param name="control">Conditional-request inputs and the header callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response metadata, including whether a 304 short-circuit applied.</returns>
    private static async Task<WeirResponseMetadata> EmitCacheableAsync(
        Stream output,
        ReadOnlyMemory<byte> payload,
        bool cacheHit,
        int ttlSeconds,
        WeirResponseControl control,
        CancellationToken cancellationToken)
    {
        var etag = ComputeETag(payload.Span);
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

    /// <summary>Computes a quoted strong entity tag from a response body.</summary>
    /// <param name="payload">The complete response bytes.</param>
    /// <returns>A quoted hex SHA-256 tag, e.g. <c>"1A2B..."</c>.</returns>
    private static string ComputeETag(ReadOnlySpan<byte> payload) =>
        string.Concat("\"", Convert.ToHexString(SHA256.HashData(payload)), "\"");

    /// <summary>
    /// Evaluates an <c>If-None-Match</c> header against an entity tag using the weak comparison function
    /// (RFC 7232): <c>*</c> matches any tag, a comma-separated list matches if any member matches, and the
    /// weak indicator <c>W/</c> is ignored on both sides.
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

    /// <summary>Upper bound on the captured response body, so result logging cannot hold large payloads.</summary>
    private const int MaxCapturedResultBytes = 16 * 1024;

    /// <summary>Serializes the bound scalar input values to JSON for the request log (best-effort).</summary>
    /// <param name="values">The bound input values keyed by logical name.</param>
    /// <returns>A JSON object, or null if the values could not be serialized.</returns>
    private static string? CaptureParameters(IReadOnlyDictionary<string, object?> values)
    {
        try
        {
            return JsonSerializer.Serialize(values);
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
    private static string CaptureResult(MemoryStream buffer)
    {
        var length = (int)Math.Min(buffer.Length, MaxCapturedResultBytes);
        var text = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, length);
        return buffer.Length > MaxCapturedResultBytes ? text + "\n...[truncated]" : text;
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
