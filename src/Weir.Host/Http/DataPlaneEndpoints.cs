using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Weir.Host.Audit;
using Weir.Host.Security;

namespace Weir.Host.Http;

/// <summary>Maps and handles the dynamic data-plane endpoint that fronts every stored procedure.</summary>
public static class DataPlaneEndpoints
{
    /// <summary>The HTTP methods the data plane answers on.</summary>
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// Carries the status the audit entry must record when the response header cannot: a call the caller
    /// hung up on never wrote one, and one already streaming cannot be changed. Set only on those paths.
    /// </summary>
    private static readonly object AuditStatusKey = new();

    /// <summary>
    /// The status the audit entry records for this request. The response header says what was sent, which
    /// is not the same as what happened - a call the caller hung up on never wrote a header and would
    /// otherwise read as a 200 Ok - so a status recorded by <see cref="RecordCancellation"/> wins.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The status to record.</returns>
    internal static int AuditStatus(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(AuditStatusKey, out var recorded) && recorded is int recordedStatus
            ? recordedStatus
            : context.Response.StatusCode;
    }

    /// <summary>
    /// Records what the audit must say about a call that was cancelled, because the response header
    /// cannot carry it. Every other outcome keeps its existing behaviour.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="gatewayTimeout">True when Weir's own timeout fired rather than the caller cancelling.</param>
    internal static void RecordCancellation(HttpContext context, bool gatewayTimeout)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[AuditStatusKey] =
            gatewayTimeout ? StatusCodes.Status504GatewayTimeout : DataPlaneDispatcher.ClientClosedRequest;
    }

    /// <summary>Maps the catch-all data-plane route <c>/api/{**route}</c>.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapWeirDataPlane(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/api/{**route}", Methods, HandleAsync);
        return endpoints;
    }

    /// <summary>
    /// Handles one data-plane request end to end. The collaborators are handler parameters rather than
    /// per-request <c>GetRequiredService</c> lookups: they are all singletons, and letting the endpoint's
    /// compiled request delegate resolve them once at build time takes the whole category of lookups off
    /// the request path.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="route">The captured route beneath <c>/api/</c>.</param>
    /// <param name="auditor">Data-plane auditor.</param>
    /// <param name="catalog">Endpoint catalog used to resolve the route.</param>
    /// <param name="authenticator">API-key authenticator.</param>
    /// <param name="engine">The data-plane engine.</param>
    /// <param name="settings">Runtime settings (the gateway timeout).</param>
    /// <param name="rateLimiter">Per-key rate limiter.</param>
    /// <param name="loggerFactory">Factory for the security / error loggers.</param>
    /// <returns>A task that completes when the response is written.</returns>
    private static async Task HandleAsync(
        HttpContext context,
        string route,
        IDataPlaneAuditor auditor,
        IEndpointCatalog catalog,
        IApiKeyAuthenticator authenticator,
        WeirEngine engine,
        IRuntimeSettings settings,
        IApiKeyRateLimiter rateLimiter,
        ILoggerFactory loggerFactory)
    {
        // A timestamp rather than a Stopwatch instance: the class would be an allocation per audited
        // request for a value two longs can carry.
        var startTimestamp = auditor.Enabled ? Stopwatch.GetTimestamp() : 0;
        string? actor = null;
        try
        {
            actor = await HandleCoreAsync(context, route, catalog, authenticator, engine, settings, rateLimiter, loggerFactory);
        }
        finally
        {
            if (auditor.Enabled)
            {
                // The response header says what was sent, which is not the same as what happened: a call
                // the caller hung up on never wrote a header at all and would otherwise be recorded as a
                // 200 Ok. AuditStatus prefers what HandleCoreAsync recorded over the header.
                var status = AuditStatus(context);
                auditor.Enqueue(new AuditEntry
                {
                    Category = "endpoint.call",
                    Actor = actor,
                    Route = route,
                    StatusCode = status,
                    Outcome = status >= 400 ? OutcomeCodes.Error : OutcomeCodes.Ok,
                    DurationMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                });
            }
        }
    }

    /// <summary>Runs the request and returns the calling API key prefix (the audit actor), or null.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="route">The captured route beneath <c>/api/</c>.</param>
    /// <param name="catalog">Endpoint catalog used to resolve the route.</param>
    /// <param name="authenticator">API-key authenticator.</param>
    /// <param name="engine">The data-plane engine.</param>
    /// <param name="settings">Runtime settings (the gateway timeout).</param>
    /// <param name="rateLimiter">Per-key rate limiter.</param>
    /// <param name="loggerFactory">Factory for the security / error loggers.</param>
    /// <returns>The authenticated key prefix, or null when the request was not authenticated.</returns>
    private static async Task<string?> HandleCoreAsync(
        HttpContext context,
        string route,
        IEndpointCatalog catalog,
        IApiKeyAuthenticator authenticator,
        WeirEngine engine,
        IRuntimeSettings settings,
        IApiKeyRateLimiter rateLimiter,
        ILoggerFactory loggerFactory)
    {
        var timeoutSeconds = settings.Current.RequestTimeoutSeconds;

        // Apply an overall gateway timeout when configured, linked to the client's abort token.
        using var timeoutCts = timeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)
            : null;
        timeoutCts?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var cancellationToken = timeoutCts?.Token ?? context.RequestAborted;

        // Authenticate before resolving the route, not after. Resolving first told an anonymous caller
        // which routes exist - 404 for one that does not, 401 for one that does - so /api could be
        // enumerated without a key. An unauthenticated caller now gets 401 whatever it asks for, and
        // learns nothing; a caller with a key still gets a useful 404 for a typo.
        var auth = await authenticator.AuthenticateAsync(context, cancellationToken);
        if (auth.Status == ApiKeyAuthStatus.RateLimited)
        {
            // Refused before the store was touched: this caller has flooded unresolved keys. 429, not
            // 401, so it is not confused with an ordinary failed sign-in, and Retry-After tells it when.
            Log.ApiKeyFlood(loggerFactory.CreateLogger("Weir.Security"), context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            await ProblemResults.WriteAsync(context, StatusCodes.Status429TooManyRequests, "Too many requests",
                "Too many unauthenticated requests. Try again later.", retryAfterSeconds: 60);
            return null;
        }

        if (auth.Status != ApiKeyAuthStatus.Authenticated)
        {
            context.Response.Headers.WWWAuthenticate = "ApiKey";
            await ProblemResults.WriteAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized",
                "A valid API key is required.");
            return null;
        }

        var key = auth.Record!;

        if (!catalog.TryResolve(context.Request.Method, route, out var match))
        {
            await ProblemResults.WriteAsync(context, StatusCodes.Status404NotFound, "Endpoint not found",
                $"No endpoint is mapped to {context.Request.Method} /api/{route}.");
            return key.Prefix;
        }

        var endpoint = match.Endpoint;

        // The endpoint exists but does not answer here. 404 rather than 405: the method is not the
        // problem, and to an HTTP caller a route served only over gRPC is simply not a route. The
        // message says which doors are open, because by this point the caller is authenticated - it is
        // holding a key that may well be entitled to call it over one of them.
        if ((endpoint.Transports & EndpointTransports.Http) == 0)
        {
            await ProblemResults.WriteAsync(context, StatusCodes.Status404NotFound, "Endpoint not found",
                $"The endpoint for {context.Request.Method} /api/{route} is not served over HTTP (transports: {endpoint.Transports}).");
            return key.Prefix;
        }

        if (!DataPlaneAuthorization.HasRequiredScopes(endpoint, key) || !DataPlaneAuthorization.IsGrantedResource(endpoint, key))
        {
            var securityLog = loggerFactory.CreateLogger("Weir.DataPlane");
            Log.DataPlaneForbidden(securityLog, key.Prefix, route);
            await ProblemResults.WriteAsync(context, StatusCodes.Status403Forbidden, "Forbidden",
                "The API key is not authorized for this endpoint.");
            return key.Prefix;
        }

        if (!await rateLimiter.TryAcquireAsync(key, cancellationToken))
        {
            var rateLog = loggerFactory.CreateLogger("Weir.DataPlane");
            Log.RateLimited(rateLog, key.Prefix);
            await ProblemResults.WriteAsync(context, StatusCodes.Status429TooManyRequests, "Too many requests",
                "The API key has exceeded its configured rate limit.", retryAfterSeconds: 60);
            return key.Prefix;
        }

        JsonDocument? body = null;
        try
        {
            var bodyRead = await ReadBodyAsync(context.Request, cancellationToken);
            if (bodyRead.UnsupportedContentType)
            {
                // The body was not read at all. Naming the Content-Type that arrived is the point: the
                // alternative is to drop the body silently and answer with a missing parameter, which
                // sends the caller looking in the wrong place.
                await ProblemResults.WriteAsync(context, StatusCodes.Status415UnsupportedMediaType,
                    "Unsupported media type",
                    $"The request body was not read because Content-Type is {DescribeContentType(context.Request)}, not JSON. Send the parameters as a JSON body with Content-Type application/json.");
                return key.Prefix;
            }

            body = bodyRead.Body;

            var invocation = new WeirInvocation
            {
                Endpoint = endpoint,
                Body = body?.RootElement ?? default,
                HasBody = bodyRead.HasBody,
                Query = new QueryValueSource(context.Request.Query),
                Route = match.RouteValues,
                Header = new HeaderValueSource(context.Request.Headers),
                Claim = new ApiKeyClaimSource(key),
                ApiKeyPrefix = key.Prefix,
            };

            context.Response.ContentType = "application/json; charset=utf-8";

            // A streamed body leaves as the rows are read, so ask a reverse proxy in front of Weir not to
            // sit on it. nginx honours this per response; its own proxy buffering happens to pass chunks
            // on promptly anyway, but its gzip filter does not - it holds a JSON body to the end unless the
            // response is unbuffered. A buffered response is whole before its first byte, so it is left
            // to the proxy's defaults.
            if (engine.StreamsResponse(endpoint))
            {
                context.Response.Headers["X-Accel-Buffering"] = "no";
            }

            // Compress per endpoint here, not in the generic middleware: the data plane is excluded from
            // it (see Program) because its routes are dynamic and its compression is a per-endpoint
            // decision. Null means the endpoint opts out or the caller accepts no coding Weir offers.
            var encoding = ResponseCompression.Negotiate(endpoint, settings.Current, context.Request.Headers.AcceptEncoding);
            await using var compressing = encoding is null
                ? null
                : new LazyCompressingStream(context.Response.Body, context.Response, encoding);
            if (compressing is not null)
            {
                // The body now depends on Accept-Encoding, so caches must key on it. Correct on a 304
                // too: its emptiness is reflected by Content-Encoding, which the wrapper sets only if a
                // body is actually written.
                context.Response.Headers.Append("Vary", "Accept-Encoding");
            }

            await engine.ExecuteAsync(invocation, compressing ?? context.Response.Body, BuildResponseControl(context), cancellationToken);
        }
        catch (OperationCanceledException) when (!context.Response.HasStarted)
        {
            // Distinguish our gateway timeout from a client disconnect: only the former gets a 504.
            if (timeoutCts is { IsCancellationRequested: true } && !context.RequestAborted.IsCancellationRequested)
            {
                await ProblemResults.WriteAsync(context, StatusCodes.Status504GatewayTimeout, "Request timeout",
                    "The request exceeded the configured time limit.");
            }
            else
            {
                // The caller hung up. There is nobody left to answer, but the audit must not read the
                // untouched 200 header as success: the call did not finish.
                RecordCancellation(context, gatewayTimeout: false);
            }
        }
        catch (OperationCanceledException)
        {
            // The response had already started, so nothing can be written over it - but the audit still
            // has to record what happened, and the exception carries on exactly as it did before.
            RecordCancellation(context, timeoutCts is { IsCancellationRequested: true } && !context.RequestAborted.IsCancellationRequested);
            throw;
        }
        catch (WeirValidationException ex) when (!context.Response.HasStarted)
        {
            await ProblemResults.WriteAsync(context, StatusCodes.Status400BadRequest, "Invalid parameters", ex.Message, ex.Errors);
        }
        catch (JsonException ex) when (!context.Response.HasStarted)
        {
            await ProblemResults.WriteAsync(context, StatusCodes.Status400BadRequest, "Invalid JSON body", ex.Message);
        }
        catch (WeirConfigurationException ex) when (!context.Response.HasStarted)
        {
            // Configuration detail (provider / connection names) is internal; log it, return a generic message.
            Log.DataPlaneError(loggerFactory.CreateLogger("Weir.DataPlane"), ex, route);
            await ProblemResults.WriteAsync(context, StatusCodes.Status500InternalServerError, "Configuration error",
                "The endpoint is misconfigured.");
        }
        catch (WeirConnectionUnavailableException ex) when (!context.Response.HasStarted)
        {
            // The connection's circuit breaker is open or its bulkhead is full; ask the caller to retry.
            Log.DataPlaneError(loggerFactory.CreateLogger("Weir.DataPlane"), ex, route);
            context.Response.Headers.RetryAfter = "5";
            await ProblemResults.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "Service unavailable",
                "The data connection is temporarily unavailable. Please retry.");
        }
        catch (DbException ex) when (!context.Response.HasStarted)
        {
            // Driver messages can disclose schema/server internals; log them, return a generic message.
            Log.DataPlaneError(loggerFactory.CreateLogger("Weir.DataPlane"), ex, route);
            await ProblemResults.WriteAsync(context, StatusCodes.Status400BadRequest, "Database error",
                "The database could not process the request.");
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            Log.DataPlaneError(loggerFactory.CreateLogger("Weir.DataPlane"), ex, route);
            await ProblemResults.WriteAsync(context, StatusCodes.Status500InternalServerError, "Internal error",
                "An unexpected error occurred.");
        }
        catch (Exception ex) when (context.Response.HasStarted)
        {
            // The response has already begun streaming, so a clean problem+json is impossible. Abort the
            // connection so the client observes a broken response instead of a silently truncated 200.
            Log.DataPlaneError(loggerFactory.CreateLogger("Weir.DataPlane"), ex, route);
            context.Abort();
        }
        finally
        {
            body?.Dispose();
        }

        return key.Prefix;
    }

    /// <summary>
    /// Builds the engine's conditional-request control for this request. For a GET the caller's
    /// <c>If-None-Match</c> is forwarded and a header callback sets <c>ETag</c>/<c>Cache-Control</c> on
    /// cache-eligible responses (turning a validator match into a body-less <c>304 Not Modified</c>).
    /// For every other method the default (unconditional) control is used.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The response control passed to the engine.</returns>
    private static WeirResponseControl BuildResponseControl(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return default;
        }

        var ifNoneMatch = context.Request.Headers.IfNoneMatch.Count > 0
            ? context.Request.Headers.IfNoneMatch.ToString()
            : null;

        return new WeirResponseControl
        {
            IfNoneMatch = ifNoneMatch,
            OnResponseHead = metadata =>
            {
                if (context.Response.HasStarted)
                {
                    return ValueTask.CompletedTask;
                }

                var headers = context.Response.Headers;
                if (metadata.ETag is { } etag)
                {
                    headers.ETag = etag;
                }

                headers.CacheControl = $"private, max-age={metadata.MaxAgeSeconds}";
                if (metadata.NotModified)
                {
                    // A 304 carries no body and no representation metadata describing one.
                    context.Response.StatusCode = StatusCodes.Status304NotModified;
                    headers.ContentType = default;
                    headers.ContentLength = null;
                }

                return ValueTask.CompletedTask;
            },
        };
    }

    /// <summary>Determines whether the request declares a JSON content type.</summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>True when <c>Content-Type</c> names JSON.</returns>
    private static bool IsJsonContentType(HttpRequest request) =>
        request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>The <c>Content-Type</c> as it arrived, or a placeholder when the request named none.</summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>The content type to name in a message.</returns>
    private static string DescribeContentType(HttpRequest request) =>
        request.ContentType is { Length: > 0 } contentType ? contentType : "(none)";

    /// <summary>
    /// Whether the request carries a body when it declared no <c>Content-Length</c>. That case is either
    /// chunked transfer or HTTP/2, where the body is bounded by the stream ending rather than by a
    /// number, so one byte is read to tell an empty body from a non-empty one.
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when at least one byte of body is waiting.</returns>
    /// <remarks>
    /// The probed byte is consumed, and that is safe only because of where this is called from: the
    /// caller answers 415 and discards the body. It is never called on the JSON path.
    /// </remarks>
    private static async ValueTask<bool> HasUndeclaredBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is { } length)
        {
            return length > 0;
        }

        var probe = new byte[1];
        return await request.Body.ReadAsync(probe, cancellationToken) > 0;
    }

    /// <summary>What reading a data-plane request body produced.</summary>
    /// <param name="Body">The parsed JSON document, or null when the request carried no body.</param>
    /// <param name="HasBody">Whether a body was present and parsed.</param>
    /// <param name="UnsupportedContentType">Whether a body arrived whose <c>Content-Type</c> is not JSON.</param>
    internal readonly record struct BodyReadResult(JsonDocument? Body, bool HasBody, bool UnsupportedContentType);

    /// <summary>
    /// Reads the request body, telling apart the three things it can be: no body at all, a JSON body, or
    /// a body that declared some other media type.
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome of reading the body.</returns>
    /// <remarks>
    /// An empty body is not malformed JSON, and a chunked request that sends no bytes at all reaches the
    /// parser as an empty stream - exactly like a malformed one. The bytes read are counted so the two
    /// can be told apart: a parse failure with nothing read is "no body", and only a failure with bytes
    /// read is a caller mistake. Counting rather than buffering keeps the body off the heap and leaves
    /// the parse indifferent to a body that arrives over several reads.
    /// </remarks>
    internal static async ValueTask<BodyReadResult> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (IsJsonContentType(request))
        {
            var counting = new CountingStream(request.Body);
            try
            {
                return new BodyReadResult(
                    await JsonDocument.ParseAsync(counting, default, cancellationToken), true, false);
            }
            catch (JsonException) when (counting.BytesRead == 0)
            {
                // Nothing arrived: no body, not a malformed one.
                return new BodyReadResult(null, false, false);
            }
        }

        return await HasUndeclaredBodyAsync(request, cancellationToken)
            ? new BodyReadResult(null, false, true)
            : new BodyReadResult(null, false, false);
    }

    /// <summary>
    /// Counts the bytes read through it, so a caller can tell an empty request body from a malformed one
    /// after a parse has failed for both. Read-only: writing and seeking are not supported.
    /// </summary>
    /// <param name="inner">The stream being read.</param>
    private sealed class CountingStream(Stream inner) : Stream
    {
        /// <summary>Bytes handed out so far.</summary>
        public long BytesRead { get; private set; }

        /// <inheritdoc />
        public override bool CanRead => inner.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override void Flush() => inner.Flush();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
