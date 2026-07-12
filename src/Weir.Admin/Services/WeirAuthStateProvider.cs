using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Weir.Admin.Services;

/// <summary>
/// Supplies the admin authentication state from the stored JWT. Expired tokens are cleared and
/// treated as anonymous. Call <see cref="NotifyLogin"/> / <see cref="NotifyLogout"/> after changes.
/// </summary>
public sealed class WeirAuthStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly TokenStore _tokens;

    /// <summary>Creates the provider over the token store.</summary>
    /// <param name="tokens">The token store.</param>
    public WeirAuthStateProvider(TokenStore tokens) => _tokens = tokens;

    /// <inheritdoc />
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await _tokens.GetAsync();
        if (string.IsNullOrEmpty(token))
        {
            return Anonymous;
        }

        var claims = JwtParser.ParseClaims(token, out var expires);

        // A token that yields no claims is malformed or tampered; drop it and fall back to anonymous so
        // the user lands on the login page instead of a broken authenticated state.
        if (claims.Count == 0 || (expires is { } expiry && expiry <= DateTimeOffset.UtcNow))
        {
            await _tokens.ClearAsync();
            return Anonymous;
        }

        var identity = new ClaimsIdentity(claims, authenticationType: "jwt", nameType: "unique_name", roleType: "role");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    /// <summary>Signals that the user has signed in, refreshing the auth state.</summary>
    public void NotifyLogin() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    /// <summary>Signals that the user has signed out, resetting to anonymous.</summary>
    public void NotifyLogout() => NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
}
