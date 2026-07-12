namespace Weir.Abstractions;

/// <summary>
/// Stores rendered response bytes for cache-eligible endpoints. Caching the already-serialized JSON
/// keeps cache hits allocation-light on the hot path. Backed by an in-memory store today; the
/// interface allows a distributed backend (e.g. Redis) later.
/// </summary>
public interface IResponseCache
{
    /// <summary>Returns the cached payload for <paramref name="key"/>, or null on a miss.</summary>
    ValueTask<ReadOnlyMemory<byte>?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="payload"/> under <paramref name="key"/> for <paramref name="ttl"/>.</summary>
    ValueTask SetAsync(string key, ReadOnlyMemory<byte> payload, TimeSpan ttl, CancellationToken cancellationToken = default);

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
