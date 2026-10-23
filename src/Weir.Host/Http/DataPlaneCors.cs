using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Weir.Contracts;
using Weir.Host.Options;
using Weir.Host.Security;

namespace Weir.Host.Http;

/// <summary>
/// CORS for the data plane, where the policy can belong to the calling key rather than to the
/// server.
/// <para>
/// Weir answers the two halves of a CORS exchange from two different places, because the browser
/// gives it two different amounts of information. A preflight (<c>OPTIONS</c> with
/// <c>Access-Control-Request-Method</c>) carries no API key - the browser is still deciding whether
/// it will send the caller's headers at all - so it is answered from the origin alone, through
/// <see cref="CorsPolicyIndex"/>. The real request carries the key, so it is answered by that key's
/// own policy, and a key that states none falls back to <c>Weir:Cors</c>.
/// </para>
/// <para>
/// The headers for a real request are written in <c>HttpResponse.OnStarting</c>, not before
/// the request is handled, because at that point the key has not been resolved yet. Resolving it
/// here as well would charge the flood guard twice for one request; writing the headers in each of
/// the three transports instead would put CORS in three places. OnStarting runs just before the
/// first flush, when the key is known and the headers are still mutable - including on a streamed
/// response, where the flush comes later.
/// </para>
/// </summary>
public static class DataPlaneCors
{
    /// <summary>The data-plane paths this applies to: the HTTP API and the WebSocket door.</summary>
    private static readonly string[] Paths = ["/api", "/ws"];

    /// <summary>Methods the data plane serves, used when a policy names none of its own.</summary>
    private const string DefaultMethods = "GET, POST, PUT, PATCH, DELETE, OPTIONS";

    /// <summary>Whether this request is for a path the data-plane CORS rules cover.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>True for a data-plane path.</returns>
    public static bool IsDataPlanePath(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var path in Paths)
        {
            if (context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Registers the data-plane CORS middleware. It is skipped entirely when neither the server nor
    /// any key could have a policy to apply - which cannot be known for keys, so the test is whether
    /// mTLS-style per-key policies are possible at all: they always are, so the middleware is
    /// registered and the cheap exit is per request, on a request with no <c>Origin</c>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IApplicationBuilder UseWeirDataPlaneCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var index = app.ApplicationServices.GetRequiredService<CorsPolicyIndex>();

        return app.Use(async (context, next) =>
        {
            // A request with no Origin is not a CORS request. One header read and out: this is the
            // overwhelming majority of data-plane traffic and it must not pay for any of the below.
            var origin = context.Request.Headers.Origin.ToString();
            if (string.IsNullOrEmpty(origin))
            {
                await next(context);
                return;
            }

            if (IsPreflight(context.Request))
            {
                var policy = await index.ResolveForPreflightAsync(origin, context.RequestAborted);
                WritePreflight(context, origin, policy);
                return;
            }

            // The real request. The key is not resolved yet, so the answer is deferred to the moment
            // just before the response goes out, when it is.
            context.Response.OnStarting(static state =>
            {
                var (ctx, requestOrigin, policyIndex) = ((HttpContext, string, CorsPolicyIndex))state;
                var key = ctx.GetAuthenticatedApiKey();

                // An unauthenticated answer is given the server-wide policy on purpose: without CORS
                // headers the browser will not let the page read the 401 at all, and the developer sees
                // an opaque CORS failure instead of "the key is wrong". The headers grant no access -
                // they only let the browser show an answer the server already decided to send.
                var policy = key?.Cors ?? policyIndex.ServerPolicy;
                WriteActual(ctx, requestOrigin, policy);
                return Task.CompletedTask;
            }, (context, origin, index));

            await next(context);
        });
    }

    /// <summary>Whether the request is a CORS preflight rather than a real <c>OPTIONS</c> call.</summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>True for a preflight.</returns>
    private static bool IsPreflight(HttpRequest request) =>
        HttpMethods.IsOptions(request.Method)
        && !StringValues.IsNullOrEmpty(request.Headers.AccessControlRequestMethod);

    /// <summary>
    /// Answers a preflight. An origin nothing allows is answered 204 with no CORS headers, which the
    /// browser reads as "not allowed" - the same thing the framework's own middleware does, and the
    /// right one: refusing with a status would break non-browser callers that send <c>OPTIONS</c>.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="origin">The request's origin.</param>
    /// <param name="policy">The policy that allows this origin, or null when none does.</param>
    private static void WritePreflight(HttpContext context, string origin, ApiKeyCorsPolicy? policy)
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        var headers = context.Response.Headers;
        headers.Append(HeaderNames.Vary, "Origin");

        if (policy is null || !policy.AllowsOrigin(origin))
        {
            return;
        }

        headers.AccessControlAllowOrigin = AllowedOriginValue(origin, policy);
        if (policy.AllowCredentials)
        {
            headers.AccessControlAllowCredentials = "true";
        }

        headers.AccessControlAllowMethods = policy.AllowedMethods.Count > 0
            ? string.Join(", ", policy.AllowedMethods)
            : DefaultMethods;

        if (policy.AllowedHeaders.Count > 0)
        {
            headers.AccessControlAllowHeaders = string.Join(", ", policy.AllowedHeaders);
        }
        else
        {
            // Echo what was asked for. A key's own clients send X-Api-Key and Content-Type, and making
            // every operator list those two before anything works is a trap, not a safeguard: the
            // request still has to carry a key the server accepts.
            var requested = context.Request.Headers.AccessControlRequestHeaders;
            if (!StringValues.IsNullOrEmpty(requested))
            {
                headers.AccessControlAllowHeaders = requested;
            }
        }

        if (policy.MaxAgeSeconds is { } maxAge)
        {
            headers.AccessControlMaxAge = maxAge.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Writes the CORS headers of a real response.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="origin">The request's origin.</param>
    /// <param name="policy">The policy that applies, or null when none does.</param>
    private static void WriteActual(HttpContext context, string origin, ApiKeyCorsPolicy? policy)
    {
        // Vary goes on here, in the deferred callback, rather than on the way in. ProblemResults calls
        // Response.Clear() before writing an error body, which drops every header set before it - the
        // same trap its own documentation flags for Retry-After. Set here, it survives, so an error
        // response carries Vary exactly like a successful one.
        //
        // It is set whenever the request had an Origin, and only then. That covers the direction that
        // matters: a response carrying Access-Control-Allow-Origin always came from a request with an
        // Origin, so it always carries Vary too, and no cache can hand one origin's answer to another.
        // The case left over is a cached origin-less response being served to a cross-origin caller,
        // which arrives without CORS headers and is refused by the browser - a false refusal, never a
        // leak - and paying for it would mean registering this callback on every data-plane request,
        // including the overwhelming majority that are not cross-origin at all.
        context.Response.Headers.Append(HeaderNames.Vary, "Origin");

        if (policy is null || !policy.AllowsOrigin(origin))
        {
            // No headers. The browser refuses the answer, which is what "this origin may not call with
            // this key" has to look like - a status of our own would break non-browser callers, which
            // never send Origin and are not affected either way.
            return;
        }

        var headers = context.Response.Headers;
        headers.AccessControlAllowOrigin = AllowedOriginValue(origin, policy);
        if (policy.AllowCredentials)
        {
            headers.AccessControlAllowCredentials = "true";
        }

        if (policy.ExposedHeaders.Count > 0)
        {
            headers.AccessControlExposeHeaders = string.Join(", ", policy.ExposedHeaders);
        }
    }

    /// <summary>
    /// The value for <c>Access-Control-Allow-Origin</c>. A wildcard policy answers <c>*</c> - unless
    /// it also allows credentials, where the specification forbids the wildcard and every browser
    /// refuses the pair, so the caller's own origin is named instead. Deciding this here rather than
    /// trusting the configuration means the combination cannot silently allow nothing.
    /// </summary>
    /// <param name="origin">The request's origin.</param>
    /// <param name="policy">The policy being applied.</param>
    /// <returns>The header value.</returns>
    private static string AllowedOriginValue(string origin, ApiKeyCorsPolicy policy) =>
        policy.AllowsAnyOrigin() && !policy.AllowCredentials ? "*" : origin;
}
