using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Weir.Abstractions;

namespace Weir.Host.Security;

/// <summary>Authenticates a data-plane request from its API key.</summary>
public interface IApiKeyAuthenticator
{
    /// <summary>
    /// Extracts and validates the API key from the request. Returns the matching enabled,
    /// unexpired key record, or null if authentication fails.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The key record, or null.</returns>
    Task<ApiKeyRecord?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts all cached key records. Call after any key is created, revoked or modified so a change
    /// (for example a revocation) takes effect immediately instead of after the cache TTL.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// Default <see cref="IApiKeyAuthenticator"/>. Reads the key from the <c>X-Api-Key</c> header or a
/// <c>Bearer</c> token, hashes it, and resolves it via the control-plane store with a short-lived
/// in-memory cache to keep the hot path off the database.
/// </summary>
public sealed class ApiKeyAuthenticator : IApiKeyAuthenticator, IDisposable
{
    /// <summary>Lifetime of a cached resolved key record.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Control-plane store used to resolve keys by hash.</summary>
    private readonly IControlPlaneStore _store;

    /// <summary>Short-lived cache for resolved key records.</summary>
    private readonly IMemoryCache _cache;

    /// <summary>Clock used to check key expiry.</summary>
    private readonly TimeProvider _clock;

    /// <summary>
    /// Change token source linked to every cached key entry. Replacing it (in <see cref="Invalidate"/>)
    /// evicts all cached records at once even though <see cref="IMemoryCache"/> exposes no key enumeration.
    /// </summary>
    private CancellationTokenSource _reset = new();

    /// <summary>Creates the authenticator.</summary>
    /// <param name="store">Control-plane store used to resolve keys.</param>
    /// <param name="cache">Short-lived cache for resolved keys.</param>
    /// <param name="clock">Clock used to check expiry.</param>
    public ApiKeyAuthenticator(IControlPlaneStore store, IMemoryCache cache, TimeProvider clock)
    {
        _store = store;
        _cache = cache;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ApiKeyRecord?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var raw = ExtractKey(context.Request);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var hash = ApiKeyHasher.Hash(raw);
        var cacheKey = "weir:apikey:" + hash;
        if (!_cache.TryGetValue(cacheKey, out ApiKeyRecord? record))
        {
            record = await _store.FindApiKeyByHashAsync(hash, cancellationToken);
            if (record is not null)
            {
                var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl }
                    .AddExpirationToken(new CancellationChangeToken(_reset.Token));
                _cache.Set(cacheKey, record, options);
            }
        }

        if (record is null || !record.Enabled)
        {
            return null;
        }

        if (record.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow())
        {
            return null;
        }

        return record;
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        // Swap in a fresh token source and cancel the old one, which expires every entry linked to it.
        var old = Interlocked.Exchange(ref _reset, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    /// <summary>Disposes the current change-token source.</summary>
    public void Dispose() => _reset.Dispose();

    /// <summary>Reads the raw API key from the request headers, trimming surrounding whitespace.</summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>The raw key, or null if absent.</returns>
    private static string? ExtractKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Api-Key", out var apiKey) && !string.IsNullOrEmpty(apiKey))
        {
            return apiKey.ToString().Trim();
        }

        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }
}
