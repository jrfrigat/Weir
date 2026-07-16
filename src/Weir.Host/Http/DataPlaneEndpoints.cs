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
                var status = context.Response.StatusCode;
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

        if (!catalog.TryResolve(context.Request.Method, route, out var match))
        {
            await ProblemResults.WriteAsync(context, StatusCodes.Status404NotFound, "Endpoint not found",
                $"No endpoint is mapped to {context.Request.Method} /api/{route}.");
            return null;
        }

        var endpoint = match.Endpoint;

        var key = await authenticator.AuthenticateAsync(context, cancellationToken);
        if (key is null)
        {
            context.Response.Headers.WWWAuthenticate = "ApiKey";
            await ProblemResults.WriteAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized",
                "A valid API key is required.");
            return null;
        }

        if (!HasRequiredScopes(endpoint, key) || !IsGrantedResource(endpoint, key))
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
            context.Response.Headers.RetryAfter = "60";
            await ProblemResults.WriteAsync(context, StatusCodes.Status429TooManyRequests, "Too many requests",
                "The API key has exceeded its configured rate limit.");
            return key.Prefix;
        }

        JsonDocument? body = null;
        try
        {
            if (HasJsonBody(context.Request))
            {
                body = await JsonDocument.ParseAsync(context.Request.Body, default, cancellationToken);
            }

            var invocation = new WeirInvocation
            {
                Endpoint = endpoint,
                Body = body?.RootElement ?? default,
                HasBody = body is not null,
                Query = new QueryValueSource(context.Request.Query),
                Route = match.RouteValues,
                Header = new HeaderValueSource(context.Request.Headers),
                Claim = new ApiKeyClaimSource(key),
                ApiKeyPrefix = key.Prefix,
            };

            context.Response.ContentType = "application/json; charset=utf-8";
            await engine.ExecuteAsync(invocation, context.Response.Body, BuildResponseControl(context), cancellationToken);
        }
        catch (OperationCanceledException) when (!context.Response.HasStarted)
        {
            // Distinguish our gateway timeout from a client disconnect: only the former gets a 504.
            if (timeoutCts is { IsCancellationRequested: true } && !context.RequestAborted.IsCancellationRequested)
            {
                await ProblemResults.WriteAsync(context, StatusCodes.Status504GatewayTimeout, "Request timeout",
                    "The request exceeded the configured time limit.");
            }
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

    /// <summary>Checks whether the key grants every scope the endpoint requires.</summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="key">The authenticated key record.</param>
    /// <returns>True if all required scopes are present.</returns>
    private static bool HasRequiredScopes(EndpointDefinition endpoint, ApiKeyRecord key)
    {
        if (endpoint.RequiredScopes.Count == 0)
        {
            return true;
        }

        // Both sides hold a handful of entries in practice, so a nested scan beats building a hash set
        // per request: it allocates nothing and, at these sizes, finishes sooner than hashing would.
        foreach (var required in endpoint.RequiredScopes)
        {
            var granted = false;
            foreach (var scope in key.Scopes)
            {
                if (string.Equals(scope, required, StringComparison.Ordinal))
                {
                    granted = true;
                    break;
                }
            }

            if (!granted)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether the key's resource grants allow this endpoint's procedure. A key with no
    /// grants is unrestricted; otherwise at least one grant must match the endpoint's connection,
    /// schema and object.
    /// </summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="key">The authenticated key record.</param>
    /// <returns>True if the key may call this procedure.</returns>
    private static bool IsGrantedResource(EndpointDefinition endpoint, ApiKeyRecord key)
    {
        if (key.Grants.Count == 0)
        {
            return true;
        }

        foreach (var grant in key.Grants)
        {
            if (grant.Allows(endpoint.ConnectionName, endpoint.Schema, endpoint.ObjectName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Determines whether the request carries a JSON body worth parsing.</summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>True if a JSON body should be read.</returns>
    /// <remarks>
    /// A missing <c>Content-Length</c> (HTTP chunked transfer) still carries a body, so only a length
    /// of exactly zero is treated as bodyless. This lets chunked JSON POST/PUT requests bind their
    /// body parameters instead of being silently dropped.
    /// </remarks>
    private static bool HasJsonBody(HttpRequest request) =>
        (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false) &&
        request.ContentLength is not 0;
}
