using Flare.Components;

namespace Weir.Admin.Services;

/// <summary>
/// Persists the admin access and refresh tokens in browser local storage via Flare's
/// <see cref="IBrowserStorage"/> service (JSON-serialized, prerender/quota-safe), instead of raw
/// <c>localStorage</c> interop.
/// </summary>
public sealed class TokenStore
{
    private const string StorageKey = "weir.admin.token";
    private const string RefreshKey = "weir.admin.refresh";

    private readonly IBrowserStorage _storage;

    /// <summary>Creates the store over Flare's browser-storage service.</summary>
    /// <param name="storage">The browser-storage service.</param>
    public TokenStore(IBrowserStorage storage) => _storage = storage;

    /// <summary>Reads the stored access token, or null if none.</summary>
    /// <returns>The token or null.</returns>
    public async ValueTask<string?> GetAsync() =>
        await _storage.GetAsync<string>(StorageKey);

    /// <summary>Stores the access token.</summary>
    /// <param name="token">The access token.</param>
    /// <returns>A task that completes when stored.</returns>
    public async ValueTask SetAsync(string token) =>
        await _storage.SetAsync(StorageKey, token);

    /// <summary>Reads the stored refresh token, or null if none.</summary>
    /// <returns>The refresh token or null.</returns>
    public async ValueTask<string?> GetRefreshAsync() =>
        await _storage.GetAsync<string>(RefreshKey);

    /// <summary>Stores (or, when null, clears) the refresh token.</summary>
    /// <param name="refreshToken">The refresh token, or null to clear it.</param>
    /// <returns>A task that completes when stored.</returns>
    public async ValueTask SetRefreshAsync(string? refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            await _storage.RemoveAsync(RefreshKey);
        }
        else
        {
            await _storage.SetAsync(RefreshKey, refreshToken);
        }
    }

    /// <summary>Removes the stored access token.</summary>
    /// <returns>A task that completes when cleared.</returns>
    public async ValueTask ClearAsync() =>
        await _storage.RemoveAsync(StorageKey);

    /// <summary>Removes both the access and refresh tokens (full sign-out).</summary>
    /// <returns>A task that completes when cleared.</returns>
    public async ValueTask ClearAllAsync()
    {
        await _storage.RemoveAsync(StorageKey);
        await _storage.RemoveAsync(RefreshKey);
    }
}
