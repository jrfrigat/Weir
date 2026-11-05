using Weir.Contracts;

namespace Weir.Abstractions;

/// <summary>Optional secret-free diagnostics for a response-cache provider.</summary>
public interface IResponseCacheDiagnostics
{
    /// <summary>Returns backend state and locally retained payload measurements.</summary>
    /// <returns>A snapshot that never includes connection strings or request values.</returns>
    ResponseCacheStatus GetStatus();
}

/// <summary>Exposes live cache limits to providers without depending on the engine or host.</summary>
public interface IResponseCacheSettings
{
    /// <summary>The current immutable system settings containing the cache policy.</summary>
    WeirSystemSettings Current { get; }
}

/// <summary>Shared byte admission for active response buffers and provider-owned payloads.</summary>
public interface IResponseBufferBudget
{
    /// <summary>Gets bytes currently reserved by asynchronous consumers, excluding idle stored cache.</summary>
    long Used { get; }

    /// <summary>Reserves bytes before allocating or borrowing storage.</summary>
    /// <param name="bytes">The allocation capacity, including transient copies.</param>
    /// <returns>An idempotent token held through the last awaited consumer.</returns>
    IDisposable Reserve(long bytes);
}

/// <summary>
/// A cached response payload together with its pre-computed entity tag, stored as a single unit
/// so the engine never has to re-hash a cache hit to evaluate <c>If-None-Match</c>.
/// </summary>
/// <param name="Bytes">The complete response body bytes.</param>
/// <param name="ETag">The pre-computed quoted strong entity tag (e.g. <c>"A1B2..."</c>).</param>
public readonly record struct CachedResponse(ReadOnlyMemory<byte> Bytes, string ETag);

/// <summary>Owns borrowed response memory until every awaited consumer has finished.</summary>
public sealed class CachedResponseLease : IDisposable
{
    /// <summary>The immutable response valid for this lease's lifetime.</summary>
    private CachedResponse _response;

    /// <summary>The provider's payload owner, released exactly once.</summary>
    private IDisposable? _owner;

    /// <summary>Whether ownership has ended.</summary>
    private int _disposed;

    /// <summary>Creates a lease over immutable response memory.</summary>
    /// <param name="response">Complete payload and validator.</param>
    /// <param name="owner">Provider ownership held through the last awaited read; null for independent GC-owned memory.</param>
    public CachedResponseLease(CachedResponse response, IDisposable? owner = null) { _response = response; _owner = owner; }

    /// <summary>Gets the response while its owner is still retained.</summary>
    public CachedResponse Response
    {
        get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); return _response; }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { Interlocked.Exchange(ref _owner, null)?.Dispose(); }
            finally { _response = default; }
        }
    }
}

/// <summary>An opaque invalidation generation captured before a fill begins.</summary>
/// <param name="ScopePrefix">The logical key prefix whose invalidation fences this fill.</param>
/// <param name="Version">Provider-defined generation; never interpret it as a client value.</param>
public readonly record struct CacheWriteFence(string ScopePrefix, string Version);

/// <summary>The result of one versioned lookup, including a fence for a subsequent fill.</summary>
/// <param name="Entry">Borrowed complete body, or null on a miss.</param>
/// <param name="Fence">The generation captured before lookup, or null when the provider cannot safely admit a store.</param>
public readonly record struct ResponseCacheLookup(CachedResponseLease? Entry, CacheWriteFence? Fence);

/// <summary>
/// Extends legacy caches with borrowed payload ownership and atomic invalidation fencing.
/// Calls begun after a completed invalidation must not read the invalidated generation. Already
/// running calls may finish their response, but a stale fill cannot publish it again.
/// </summary>
public interface IVersionedResponseCache : IResponseCache
{
    /// <summary>Looks up a complete body and captures the generation used by a subsequent conditional store.</summary>
    /// <param name="key">Logical response key, including hashed vary-by inputs.</param>
    /// <param name="scopePrefix">The endpoint's invalidation prefix; the key must start with it.</param>
    /// <param name="cancellationToken">The lookup lifetime.</param>
    /// <returns>A leased hit or miss, together with an optional write fence.</returns>
    ValueTask<ResponseCacheLookup> LookupAsync(string key, string scopePrefix, CancellationToken cancellationToken = default);

    /// <summary>Publishes complete bytes only if the fence is still current, atomically with invalidation.</summary>
    /// <param name="key">Logical response key.</param>
    /// <param name="entry">Immutable bytes whose owner remains alive until this await finishes.</param>
    /// <param name="ttl">Maximum entry lifetime.</param>
    /// <param name="fence">The generation captured before the database execution.</param>
    /// <param name="cancellationToken">The store lifetime.</param>
    /// <returns>A task that completes when publication is finished or refused.</returns>
    ValueTask SetIfCurrentAsync(string key, CachedResponse entry, TimeSpan ttl, CacheWriteFence fence,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores rendered response bytes for cache-eligible endpoints. Caching the already-serialized JSON
/// keeps cache hits allocation-light. Providers may additionally implement
/// <see cref="IVersionedResponseCache"/> for atomic fencing and explicit borrowed-memory ownership.
/// </summary>
public interface IResponseCache
{
    /// <summary>Returns the cached payload for <paramref name="key"/>, or null on a miss.</summary>
    ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an already-built <paramref name="entry"/> under <paramref name="key"/> for
    /// <paramref name="ttl"/>. The engine builds the entry (including its entity tag) and serves the
    /// response from it. The engine observes the store in the background and retains the bytes until
    /// it completes. Legacy providers must retain independently owned immutable memory after returning;
    /// borrowed memory is available only through <see cref="IVersionedResponseCache"/>.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="entry">The complete response bytes and their entity tag.</param>
    /// <param name="ttl">How long the entry stays valid.</param>
    /// <param name="cancellationToken">Cancellation token. The engine passes <see cref="CancellationToken.None"/>:
    /// the store outlives the request that produced it, so one client disconnecting must not discard a
    /// payload every other caller is about to reuse.</param>
    ValueTask SetAsync(string key, CachedResponse entry, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Removes a cached entry.</summary>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every cached entry whose key starts with <paramref name="keyPrefix"/>. Used to evict
    /// all cached responses for an endpoint when its definition changes, since per-response keys embed
    /// the vary-by parameter values and cannot be enumerated by the caller.
    /// </summary>
    /// <param name="keyPrefix">Key prefix to match (e.g. <c>weir:orders/get:</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);
}
