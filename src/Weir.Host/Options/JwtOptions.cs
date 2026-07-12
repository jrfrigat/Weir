namespace Weir.Host.Options;

/// <summary>
/// JWT settings for admin sessions, bound from <c>Weir:Jwt</c>. When <see cref="SigningKey"/> is
/// empty an ephemeral key is generated at startup (tokens then do not survive a restart), so set a
/// stable secret in production.
/// </summary>
public sealed class JwtOptions
{
    /// <summary>Token issuer.</summary>
    public string Issuer { get; set; } = "weir";

    /// <summary>Token audience.</summary>
    public string Audience { get; set; } = "weir-admin";

    /// <summary>Symmetric signing secret. Empty means generate an ephemeral key at startup.</summary>
    public string? SigningKey { get; set; }

    /// <summary>
    /// Access-token lifetime, in minutes. Kept short because a refresh token silently renews the session;
    /// a shorter access token narrows the window in which a leaked or not-yet-revoked token is usable.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 30;

    /// <summary>
    /// Refresh-token lifetime, in days. A refresh token is long-lived, revocable and rotated on each use;
    /// it is exchanged for a fresh access token once the access token expires.
    /// </summary>
    public int RefreshTokenDays { get; set; } = 14;
}
