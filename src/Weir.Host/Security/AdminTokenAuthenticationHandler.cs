using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Host.Http;

namespace Weir.Host.Security;

/// <summary>
/// Authenticates admin-API requests presenting a personal access token as the bearer token. The token
/// is hashed and resolved to its owning admin; the request then runs with that admin's current role,
/// so a token is exactly as capable as its owner (and stops working if the account is disabled). Token
/// lookups honor revocation and expiry immediately (no caching), and last-used writes are throttled by
/// the store.
/// <para>
/// No caching is deliberate, and it has a price: every presentation reaches the store. A stream of
/// unknown tokens is therefore a lookup per request, the same database-exhaustion vector the data
/// plane faces. The same per-caller budget bounds it here (<see cref="IApiKeyFloodGuard"/>), checked
/// before the lookup, so past the budget the caller is refused with 429 and the store is not touched.
/// A token that resolves never counts against the budget, so a legitimate admin is never throttled.
/// </para>
/// </summary>
public sealed class AdminTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The authentication scheme name this handler serves.</summary>
    public const string SchemeName = "AdminToken";

    /// <summary>
    /// Marks, in <see cref="HttpContext.Items"/>, a request the flood guard refused, so the challenge
    /// can answer 429 rather than the usual 401. It lives there rather than in a field because ASP.NET
    /// Core builds one handler instance per operation: a field would not survive from authentication to
    /// the challenge.
    /// </summary>
    private static readonly object FloodBlockedKey = new();

    /// <summary>Creates the handler.</summary>
    /// <param name="options">The scheme options monitor.</param>
    /// <param name="logger">Logger factory.</param>
    /// <param name="encoder">URL encoder.</param>
    public AdminTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var raw = header["Bearer ".Length..].Trim();
        if (!raw.StartsWith(AdminTokenGenerator.Prefix, StringComparison.Ordinal))
        {
            // Not a personal access token; leave it for the JWT handler.
            return AuthenticateResult.NoResult();
        }

        var store = Context.RequestServices.GetRequiredService<IControlPlaneStore>();
        var clock = Context.RequestServices.GetRequiredService<TimeProvider>();
        var floodGuard = Context.RequestServices.GetRequiredService<IApiKeyFloodGuard>();

        var caller = CallerAddress(Context);
        if (floodGuard.ShouldBlock(caller))
        {
            // Refused before the store is touched, so a blocked caller costs one dictionary read. The
            // same budget the data plane applies to unresolved keys bounds this uncached path.
            Context.Items[FloodBlockedKey] = true;
            Log.AdminTokenFlood(Logger, caller);
            return AuthenticateResult.Fail("Too many access tokens were presented from this address.");
        }

        var hash = ApiKeyHasher.Hash(raw);
        var record = await store.FindAdminTokenByHashAsync(hash, Context.RequestAborted);
        if (record is null)
        {
            // Only an unknown token reaches the store on every repeat, and so only an unknown token is
            // unbounded: a token that resolves is one fixed value and cannot flood. Counted here, after
            // the lookup, for the same reason the data plane counts there - a token that resolved must
            // never spend the caller's budget.
            floodGuard.RecordFailure(caller);
            return AuthenticateResult.Fail("The access token is not valid.");
        }

        if (!record.AdminEnabled)
        {
            return AuthenticateResult.Fail("The access token is not valid.");
        }

        if (record.ExpiresAt is { } expiry && expiry <= clock.GetUtcNow())
        {
            return AuthenticateResult.Fail("The access token has expired.");
        }

        await store.TouchAdminTokenAsync(record.Id, clock.GetUtcNow(), Context.RequestAborted);

        // Match the claim shape JwtTokenService issues so RequireRole / the account endpoints work
        // identically whether the caller authenticated with a JWT or a personal access token.
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", record.AdminId.ToString()),
                new Claim("unique_name", record.Username),
                new Claim("role", string.IsNullOrEmpty(record.Role) ? AdminRoles.Admin : record.Role),
            ],
            SchemeName,
            nameType: "unique_name",
            roleType: "role");

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Answers a refused request. A request the flood guard blocked gets 429 with problem+json and a
    /// <c>Retry-After</c>, exactly as the data plane answers the same condition, so a flood is not read
    /// as an ordinary failed sign-in; every other failure keeps the scheme's usual 401 challenge.
    /// </summary>
    /// <param name="properties">Challenge properties supplied by the authentication middleware.</param>
    /// <returns>A task that completes when the response is written.</returns>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(FloodBlockedKey))
        {
            await ProblemResults.WriteAsync(Context, StatusCodes.Status429TooManyRequests,
                "Too many requests", "Too many unauthenticated requests. Try again later.",
                retryAfterSeconds: 60);
            return;
        }

        await base.HandleChallengeAsync(properties);
    }

    /// <summary>
    /// The caller's address for the flood guard, or a fixed placeholder when the connection exposes
    /// none. Deliberately the same rule as <c>ApiKeyAuthenticator.CallerAddress</c>: all address-less
    /// callers share one bucket, so a missing address cannot masquerade as many distinct ones.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The address string.</returns>
    private static string CallerAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
