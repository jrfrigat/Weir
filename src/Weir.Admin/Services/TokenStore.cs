using Microsoft.JSInterop;

namespace Weir.Admin.Services;

/// <summary>Persists the admin access and refresh tokens in browser local storage.</summary>
public sealed class TokenStore
{
    private const string StorageKey = "weir.admin.token";
    private const string RefreshKey = "weir.admin.refresh";

    private readonly IJSRuntime _js;

    /// <summary>Creates the store over the JS runtime.</summary>
    /// <param name="js">The JS interop runtime.</param>
    public TokenStore(IJSRuntime js) => _js = js;

    /// <summary>Reads the stored access token, or null if none.</summary>
    /// <returns>The token or null.</returns>
    public async ValueTask<string?> GetAsync() =>
        await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);

    /// <summary>Stores the access token.</summary>
    /// <param name="token">The access token.</param>
    /// <returns>A task that completes when stored.</returns>
    public async ValueTask SetAsync(string token) =>
        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, token);

    /// <summary>Reads the stored refresh token, or null if none.</summary>
    /// <returns>The refresh token or null.</returns>
    public async ValueTask<string?> GetRefreshAsync() =>
        await _js.InvokeAsync<string?>("localStorage.getItem", RefreshKey);

    /// <summary>Stores (or, when null, clears) the refresh token.</summary>
    /// <param name="refreshToken">The refresh token, or null to clear it.</param>
    /// <returns>A task that completes when stored.</returns>
    public async ValueTask SetRefreshAsync(string? refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", RefreshKey);
        }
        else
        {
            await _js.InvokeVoidAsync("localStorage.setItem", RefreshKey, refreshToken);
        }
    }

    /// <summary>Removes the stored access token.</summary>
    /// <returns>A task that completes when cleared.</returns>
    public async ValueTask ClearAsync() =>
        await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);

    /// <summary>Removes both the access and refresh tokens (full sign-out).</summary>
    /// <returns>A task that completes when cleared.</returns>
    public async ValueTask ClearAllAsync()
    {
        await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        await _js.InvokeVoidAsync("localStorage.removeItem", RefreshKey);
    }
}
