using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Host.Options;

namespace Weir.Host.Security;

/// <summary>Issues signed JWT access tokens for authenticated admins.</summary>
public sealed class JwtTokenService
{
    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly TimeProvider _clock;
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Creates the token service.</summary>
    /// <param name="options">JWT options.</param>
    /// <param name="signingKey">Shared symmetric signing key.</param>
    /// <param name="clock">Clock for issue/expiry timestamps.</param>
    public JwtTokenService(IOptions<JwtOptions> options, SymmetricSecurityKey signingKey, TimeProvider clock)
    {
        _options = options.Value;
        _signingKey = signingKey;
        _clock = clock;
    }

    /// <summary>Issues an access token for an admin account.</summary>
    /// <param name="admin">The authenticated admin.</param>
    /// <returns>The signed token and its expiry.</returns>
    public (string Token, DateTimeOffset ExpiresAt) Issue(AdminUserRecord admin)
    {
        var now = _clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, admin.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, admin.Username),
                new Claim("role", string.IsNullOrEmpty(admin.Role) ? AdminRoles.Admin : admin.Role),
                // Stamped so the token is invalidated when the account's TokenVersion is bumped.
                new Claim("ver", admin.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]),
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
        };

        return (_handler.CreateToken(descriptor), expires);
    }
}
