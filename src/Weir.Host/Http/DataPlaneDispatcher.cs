using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Weir.Host.Audit;
using Weir.Host.Security;

namespace Weir.Host.Http;

/// <summary>One call arriving on a transport that is not HTTP, described in the terms the engine needs.</summary>
public sealed class TransportCall
{
    /// <summary>Which door the call came through. Checked against the endpoint's own list.</summary>
    public required EndpointTransports Transport { get; init; }

    /// <summary>The authenticated key. Every transport authenticates once, before it gets here.</summary>
    public required ApiKeyRecord Key { get; init; }

    /// <summary>
    /// The HTTP method the endpoint is registered under. An endpoint's identity in Weir is method plus
    /// route, so a transport with no methods of its own still has to name one.
    /// </summary>
    public required string Method { get; init; }

    /// <summary>The route beneath <c>/api</c>, without the prefix.</summary>
    public required string Route { get; init; }

    /// <summary>The request body: the flat JSON object whose members are the procedure's parameters.</summary>
    public JsonElement Body { get; init; }

    /// <summary>Whether <see cref="Body"/> holds anything.</summary>
    public bool HasBody { get; init; }

    /// <summary>Values for query-sourced parameters.</summary>
    public IValueSource Query { get; init; } = EmptyValueSource.Instance;

    /// <summary>Values for header-sourced parameters.</summary>
    public IValueSource Header { get; init; } = EmptyValueSource.Instance;
}

/// <summary>
/// What became of one transport call. The status is an HTTP status code even where the transport has no
/// HTTP in it: the meanings are the ones every one of these failures already had (404 unknown route, 403
/// not entitled, 429 too fast, 504 out of time), and inventing a second numbering per transport would
/// only make two logs of the same event impossible to compare. Each transport maps it onto whatever it
/// puts on the wire - a JSON frame, a gRPC status.
/// </summary>
/// <param name="Status">HTTP-shaped status code.</param>
/// <param name="Title">Short reason, safe to show a caller.</param>
/// <param name="Detail">Longer reason, safe to show a caller (never a driver or configuration message).</param>
/// <param name="BodyStarted">
/// True when part of the envelope had already been sent, so the failure could not be reported in place
/// of a body and the transport must abort the message instead.
/// </param>
public readonly record struct TransportCallResult(int Status, string? Title, string? Detail, bool BodyStarted)
{
    /// <summary>Whether the call produced a response body rather than an error.</summary>
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>
/// The data-plane call path for every transport except HTTP: resolve the route, check that the endpoint
/// answers on this transport, check scopes and grants, take a rate-limit permit, run the engine into the
/// transport's stream, and turn any failure into a status a caller can be told about.
/// <para>
/// It exists so that gRPC and WebSocket cannot drift from each other, or from HTTP, on the things that
/// must not vary by door: what a key is entitled to call, what the limits are, and which failures
/// disclose a driver message (none of them - those are logged and generalized here, exactly as the HTTP
/// handler does it).
/// </para>
/// </summary>
public sealed class DataPlaneDispatcher
{
    /// <summary>
    /// The caller hung up before the answer was ready. Nginx's 499 rather than an invented number: it is
    /// the one code operators already read as "nobody was left to send this to", and it is only ever
    /// logged - by definition there is no one to send it to.
    /// </summary>
    private const int ClientClosedRequest = 499;

    private readonly IEndpointCatalog _catalog;
    private readonly WeirEngine _engine;
    private readonly IRuntimeSettings _settings;
    private readonly IApiKeyRateLimiter _rateLimiter;
    private readonly IDataPlaneAuditor _auditor;
    private readonly ILogger _log;

    /// <summary>Creates the dispatcher over the shared data-plane collaborators.</summary>
    /// <param name="catalog">Endpoint catalog used to resolve the route.</param>
    /// <param name="engine">The data-plane engine.</param>
    /// <param name="settings">Runtime settings (the gateway timeout).</param>
    /// <param name="rateLimiter">Per-key rate limiter.</param>
    /// <param name="auditor">Data-plane auditor.</param>
    /// <param name="loggerFactory">Factory for the data-plane logger.</param>
    public DataPlaneDispatcher(
        IEndpointCatalog catalog,
        WeirEngine engine,
        IRuntimeSettings settings,
        IApiKeyRateLimiter rateLimiter,
        IDataPlaneAuditor auditor,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _catalog = catalog;
        _engine = engine;
        _settings = settings;
        _rateLimiter = rateLimiter;
        _auditor = auditor;
        _log = loggerFactory.CreateLogger("Weir.DataPlane");
    }

    /// <summary>Runs one call and writes its envelope into the transport's stream.</summary>
    /// <param name="call">The call to run.</param>
    /// <param name="output">Where the envelope goes. Left untouched when the call fails before the engine.</param>
    /// <param name="cancellationToken">Cancellation token for the caller's connection.</param>
    /// <returns>The outcome, for the transport to put on the wire.</returns>
    public async Task<TransportCallResult> InvokeAsync(TransportCall call, TransportWriteStream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(output);

        var startTimestamp = _auditor.Enabled ? Stopwatch.GetTimestamp() : 0;
        var result = await RunAsync(call, output, cancellationToken);

        if (_auditor.Enabled)
        {
            _auditor.Enqueue(new AuditEntry
            {
                Category = "endpoint.call",
                Actor = call.Key.Prefix,
                Route = call.Route,
                StatusCode = result.Status,
                Outcome = result.Status >= 400 ? OutcomeCodes.Error : OutcomeCodes.Ok,
                DurationMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                Detail = TransportName(call.Transport),
            });
        }

        return result;
    }

    /// <summary>The call itself, without the audit bookkeeping around it.</summary>
    /// <param name="call">The call to run.</param>
    /// <param name="output">The transport's stream.</param>
    /// <param name="cancellationToken">Cancellation token for the caller's connection.</param>
    /// <returns>The outcome.</returns>
    private async Task<TransportCallResult> RunAsync(TransportCall call, TransportWriteStream output, CancellationToken cancellationToken)
    {
        if (!_catalog.TryResolve(call.Method, call.Route, out var match))
        {
            return new TransportCallResult(StatusCodes.Status404NotFound, "Endpoint not found",
                $"No endpoint is mapped to {call.Method} /{call.Route.TrimStart('/')}.", false);
        }

        var endpoint = match.Endpoint;

        if ((endpoint.Transports & call.Transport) == 0)
        {
            return new TransportCallResult(StatusCodes.Status404NotFound, "Endpoint not found",
                $"The endpoint for {call.Method} /{call.Route.TrimStart('/')} is not served over " +
                $"{TransportName(call.Transport)} (transports: {endpoint.Transports}).", false);
        }

        if (!DataPlaneAuthorization.HasRequiredScopes(endpoint, call.Key) ||
            !DataPlaneAuthorization.IsGrantedResource(endpoint, call.Key))
        {
            Log.DataPlaneForbidden(_log, call.Key.Prefix, call.Route);
            return new TransportCallResult(StatusCodes.Status403Forbidden, "Forbidden",
                "The API key is not authorized for this endpoint.", false);
        }

        if (!await _rateLimiter.TryAcquireAsync(call.Key, cancellationToken))
        {
            Log.RateLimited(_log, call.Key.Prefix);
            return new TransportCallResult(StatusCodes.Status429TooManyRequests, "Too many requests",
                "The API key has exceeded its configured rate limit.", false);
        }

        // The same gateway timeout the HTTP path applies, linked to the caller's connection. A
        // long-lived session does not get to hold a database connection any longer than a request does.
        var timeoutSeconds = _settings.Current.RequestTimeoutSeconds;
        using var timeoutCts = timeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        timeoutCts?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var token = timeoutCts?.Token ?? cancellationToken;

        try
        {
            var invocation = new WeirInvocation
            {
                Endpoint = endpoint,
                Body = call.Body,
                HasBody = call.HasBody,
                Query = call.Query,
                Route = match.RouteValues,
                Header = call.Header,
                Claim = new ApiKeyClaimSource(call.Key),
                ApiKeyPrefix = call.Key.Prefix,
            };

            await _engine.ExecuteAsync(invocation, output, default, token);
            await output.CompleteAsync(token);
            return new TransportCallResult(StatusCodes.Status200OK, null, null, output.HasStarted);
        }
        catch (OperationCanceledException)
        {
            // Ours or the caller's: a linked token that fired while the caller's did not is the gateway
            // timeout, and anything else is the caller hanging up, which is not an error to report.
            var timedOut = timeoutCts is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested;
            return timedOut
                ? new TransportCallResult(StatusCodes.Status504GatewayTimeout, "Request timeout",
                    "The request exceeded the configured time limit.", output.HasStarted)
                : new TransportCallResult(ClientClosedRequest, "Client closed request",
                    "The caller cancelled the request.", output.HasStarted);
        }
        catch (WeirValidationException ex)
        {
            return new TransportCallResult(StatusCodes.Status400BadRequest, "Invalid parameters", ex.Message, output.HasStarted);
        }
        catch (JsonException ex)
        {
            return new TransportCallResult(StatusCodes.Status400BadRequest, "Invalid JSON body", ex.Message, output.HasStarted);
        }
        catch (WeirConfigurationException ex)
        {
            // Configuration detail (provider / connection names) is internal; log it, return a generic message.
            Log.DataPlaneError(_log, ex, call.Route);
            return new TransportCallResult(StatusCodes.Status500InternalServerError, "Configuration error",
                "The endpoint is misconfigured.", output.HasStarted);
        }
        catch (WeirConnectionUnavailableException ex)
        {
            Log.DataPlaneError(_log, ex, call.Route);
            return new TransportCallResult(StatusCodes.Status503ServiceUnavailable, "Service unavailable",
                "The data connection is temporarily unavailable. Please retry.", output.HasStarted);
        }
        catch (DbException ex)
        {
            // Driver messages can disclose schema/server internals; log them, return a generic message.
            Log.DataPlaneError(_log, ex, call.Route);
            return new TransportCallResult(StatusCodes.Status400BadRequest, "Database error",
                "The database could not process the request.", output.HasStarted);
        }
        catch (Exception ex)
        {
            Log.DataPlaneError(_log, ex, call.Route);
            return new TransportCallResult(StatusCodes.Status500InternalServerError, "Internal error",
                "An unexpected error occurred.", output.HasStarted);
        }
    }

    /// <summary>The wire name of a transport, for audit detail and caller-facing messages.</summary>
    /// <param name="transport">A single transport flag.</param>
    /// <returns>Its lower-case name.</returns>
    private static string TransportName(EndpointTransports transport) => transport switch
    {
        EndpointTransports.Http => "http",
        EndpointTransports.Grpc => "grpc",
        EndpointTransports.WebSocket => "websocket",
        _ => transport.ToString(),
    };
}
