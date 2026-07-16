namespace Weir.Abstractions;

/// <summary>
/// A cached response payload together with its pre-computed entity tag, stored as a single unit
/// so the engine never has to re-hash a cache hit to evaluate <c>If-None-Match</c>.
/// </summary>
/// <param name="Bytes">The complete response body bytes.</param>
/// <param name="ETag">The pre-computed quoted strong entity tag (e.g. <c>"A1B2..."</c>).</param>
public readonly record struct CachedResponse(ReadOnlyMemory<byte> Bytes, string ETag);

/// <summary>
/// Stores rendered response bytes for cache-eligible endpoints. Caching the already-serialized JSON
/// keeps cache hits allocation-light on the hot path. Backed by an in-memory store today; the
/// interface allows a distributed backend (e.g. Redis) later.
/// </summary>
public interface IResponseCache
{
    /// <summary>Returns the cached payload for <paramref name="key"/>, or null on a miss.</summary>
    ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an already-built <paramref name="entry"/> under <paramref name="key"/> for
    /// <paramref name="ttl"/>. The engine builds the entry (including its entity tag) and serves the
    /// response from it, then calls this without awaiting, so a store that does I/O does not sit
    /// between the database and the client. An implementation must therefore treat the call as
    /// fire-and-forget: it is not observed, and it must not throw into the caller synchronously.
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
