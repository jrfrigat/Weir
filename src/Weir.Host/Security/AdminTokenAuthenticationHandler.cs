using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Host.Security;

/// <summary>
/// Authenticates admin-API requests presenting a personal access token as the bearer token. The token
/// is hashed and resolved to its owning admin; the request then runs with that admin's current role,
/// so a token is exactly as capable as its owner (and stops working if the account is disabled). Token
/// lookups honor revocation and expiry immediately (no caching), and last-used writes are throttled by
/// the store.
/// </summary>
public sealed class AdminTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The authentication scheme name this handler serves.</summary>
    public const string SchemeName = "AdminToken";

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

        var hash = ApiKeyHasher.Hash(raw);
        var record = await store.FindAdminTokenByHashAsync(hash, Context.RequestAborted);
        if (record is null || !record.AdminEnabled)
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
}
