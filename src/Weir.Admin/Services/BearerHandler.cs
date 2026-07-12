using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Weir.Contracts;

namespace Weir.Admin.Services;

/// <summary>
/// Attaches the stored admin access token as a Bearer header, and transparently refreshes an expired
/// session. When a request that carried a token comes back 401, the handler exchanges the stored refresh
/// token for a new access token (rotating the refresh token) and retries the original request once. Only
/// if the refresh fails is the session cleared and the app flipped to anonymous.
/// </summary>
public sealed class BearerHandler : DelegatingHandler
{
    private readonly TokenStore _tokens;
    private readonly WeirAuthStateProvider _authState;

    /// <summary>Creates the handler over the token store and auth state provider.</summary>
    /// <param name="tokens">The token store.</param>
    /// <param name="authState">The auth state provider notified when a session is rejected.</param>
    public BearerHandler(TokenStore tokens, WeirAuthStateProvider authState)
    {
        _tokens = tokens;
        _authState = authState;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetAsync();

        // Buffer the request body up front (admin payloads are small) so the request can be replayed after
        // a refresh; a streamed content instance cannot be sent twice.
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var response = await SendOnceAsync(request, token, body, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || string.IsNullOrEmpty(token))
        {
            // A 401 with no token is a failed login, left for the login page to handle.
            return response;
        }

        // The access token expired or was revoked. Try to refresh, then replay the request once.
        var refreshed = await TryRefreshAsync(cancellationToken);
        if (refreshed is null)
        {
            await _tokens.ClearAllAsync();
            _authState.NotifyLogout();
            return response;
        }

        response.Dispose();
        return await SendOnceAsync(request, refreshed, body, cancellationToken);
    }

    /// <summary>Sends a fresh copy of the request with the given token and buffered body.</summary>
    /// <param name="original">The original request to clone.</param>
    /// <param name="token">The bearer token to attach, or null.</param>
    /// <param name="body">The buffered request body, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage original, string? token, byte[]? body, CancellationToken cancellationToken)
    {
        using var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (original.Content?.Headers.ContentType is { } contentType)
            {
                clone.Content.Headers.ContentType = contentType;
            }
        }

        if (!string.IsNullOrEmpty(token))
        {
            clone.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(clone, cancellationToken);
    }

    /// <summary>
    /// Exchanges the stored refresh token for a new access token (and rotated refresh token), persisting
    /// both. Returns the new access token, or null when there is no refresh token or the exchange fails.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new access token, or null.</returns>
    private async Task<string?> TryRefreshAsync(CancellationToken cancellationToken)
    {
        var refreshToken = await _tokens.GetRefreshAsync();
        if (string.IsNullOrEmpty(refreshToken))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "admin/api/auth/refresh")
        {
            Content = JsonContent.Create(new RefreshRequest { RefreshToken = refreshToken }),
        };

        using var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
        if (login is null)
        {
            return null;
        }

        await _tokens.SetAsync(login.Token);
        await _tokens.SetRefreshAsync(login.RefreshToken);
        _authState.NotifyLogin();
        return login.Token;
    }
}
