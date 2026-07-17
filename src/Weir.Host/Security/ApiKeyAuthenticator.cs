using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Weir.Abstractions;

namespace Weir.Host.Security;

/// <summary>The outcome of authenticating a data-plane request.</summary>
public enum ApiKeyAuthStatus
{
    /// <summary>No valid key was presented. The caller should receive 401.</summary>
    Unauthenticated,

    /// <summary>A valid, enabled, unexpired key was presented.</summary>
    Authenticated,

    /// <summary>
    /// The caller has presented too many unresolved keys and was refused before a lookup. The caller
    /// should receive 429; distinct from <see cref="Unauthenticated"/> so a flood is not mistaken for an
    /// ordinary failed sign-in.
    /// </summary>
    RateLimited,
}

/// <summary>
/// The result of <see cref="IApiKeyAuthenticator.AuthenticateAsync"/>: a status and, when
/// authenticated, the resolved key record. A struct so the hot path does not allocate to report it.
/// </summary>
public readonly record struct ApiKeyAuthResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="status">The outcome.</param>
    /// <param name="record">The resolved record when authenticated; otherwise null.</param>
    private ApiKeyAuthResult(ApiKeyAuthStatus status, ApiKeyRecord? record)
    {
        Status = status;
        Record = record;
    }

    /// <summary>The outcome.</summary>
    public ApiKeyAuthStatus Status { get; }

    /// <summary>The resolved key record, present only when <see cref="Status"/> is
    /// <see cref="ApiKeyAuthStatus.Authenticated"/>.</summary>
    public ApiKeyRecord? Record { get; }

    /// <summary>A "no valid key" result.</summary>
    public static ApiKeyAuthResult Unauthenticated { get; } = new(ApiKeyAuthStatus.Unauthenticated, null);

    /// <summary>A "refused before a lookup" result.</summary>
    public static ApiKeyAuthResult RateLimited { get; } = new(ApiKeyAuthStatus.RateLimited, null);

    /// <summary>An authenticated result carrying the resolved record.</summary>
    /// <param name="record">The resolved key record.</param>
    /// <returns>The result.</returns>
    public static ApiKeyAuthResult Ok(ApiKeyRecord record) => new(ApiKeyAuthStatus.Authenticated, record);
}

/// <summary>Authenticates a data-plane request from its API key.</summary>
public interface IApiKeyAuthenticator
{
    /// <summary>
    /// Extracts and validates the API key from the request, resolving it against the control-plane
    /// store through a short-lived cache. A caller that has presented too many unresolved keys is
    /// refused before the store is touched.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authentication result.</returns>
    Task<ApiKeyAuthResult> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken = default);

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

    /// <summary>Caps how many unresolved keys one caller address may cost the store per window.</summary>
    private readonly IApiKeyFloodGuard _floodGuard;

    /// <summary>
    /// Change token source linked to every cached key entry. Replacing it (in <see cref="Invalidate"/>)
    /// evicts all cached records at once even though <see cref="IMemoryCache"/> exposes no key enumeration.
    /// </summary>
    private CancellationTokenSource _reset = new();

    /// <summary>Creates the authenticator.</summary>
    /// <param name="store">Control-plane store used to resolve keys.</param>
    /// <param name="cache">Short-lived cache for resolved keys.</param>
    /// <param name="clock">Clock used to check expiry.</param>
    /// <param name="floodGuard">Caps unresolved-key lookups per caller address.</param>
    public ApiKeyAuthenticator(IControlPlaneStore store, IMemoryCache cache, TimeProvider clock, IApiKeyFloodGuard floodGuard)
    {
        _store = store;
        _cache = cache;
        _clock = clock;
        _floodGuard = floodGuard;
    }

    /// <inheritdoc />
    public async Task<ApiKeyAuthResult> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var raw = ExtractKey(context.Request);
        if (string.IsNullOrEmpty(raw))
        {
            // A request with no key never reaches the store, so it neither consults nor feeds the guard.
            return ApiKeyAuthResult.Unauthenticated;
        }

        var hash = ApiKeyHasher.Hash(raw);
        var cacheKey = "weir:apikey:" + hash;
        if (!_cache.TryGetValue(cacheKey, out ApiKeyRecord? record))
        {
            // The store lookup below is the per-request cost this guard bounds. A caller that has already
            // spent its budget of unresolved keys this window is refused here, before the lookup.
            var caller = CallerAddress(context);
            if (_floodGuard.ShouldBlock(caller))
            {
                return ApiKeyAuthResult.RateLimited;
            }

            record = await _store.FindApiKeyByHashAsync(hash, cancellationToken);
            if (record is not null)
            {
                var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl }
                    .AddExpirationToken(new CancellationChangeToken(_reset.Token));
                _cache.Set(cacheKey, record, options);
            }
            else
            {
                // Only an unknown key returns null, and only a null is left uncached and so hits the
                // store on every repeat. That is precisely the attempt the guard counts; a resolved key
                // (even a disabled or expired one) is cached and cannot flood.
                _floodGuard.RecordFailure(caller);
            }
        }

        if (record is null || !record.Enabled)
        {
            return ApiKeyAuthResult.Unauthenticated;
        }

        if (record.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow())
        {
            return ApiKeyAuthResult.Unauthenticated;
        }

        return ApiKeyAuthResult.Ok(record);
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

    /// <summary>
    /// The caller's address for the flood guard, or a fixed placeholder when the connection exposes
    /// none (in-memory test transports). All such callers share one bucket, which is the safe default:
    /// a missing address cannot masquerade as many distinct ones to dodge the budget.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The address string.</returns>
    private static string CallerAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

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
