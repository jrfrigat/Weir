using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Caching.Memory;
using Weir.Abstractions;

namespace Weir.Core;

/// <summary>In-memory <see cref="IResponseCache"/> over <see cref="IMemoryCache"/>. Stores rendered JSON bytes with pre-computed ETags.</summary>
public sealed class MemoryResponseCache : IResponseCache
{
    private readonly IMemoryCache _cache;

    /// <summary>
    /// Live keys tracked alongside the cache so <see cref="RemoveByPrefixAsync"/> can enumerate them
    /// (<see cref="IMemoryCache"/> exposes no key enumeration). Entries remove themselves via a
    /// post-eviction callback, so this set does not outgrow the cache.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    /// <summary>Creates the cache over the shared memory cache.</summary>
    public MemoryResponseCache(IMemoryCache cache) => _cache = cache;

    /// <inheritdoc />
    public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_cache.TryGetValue(key, out CachedResponse? entry) && entry is { } e
            ? (CachedResponse?)e
            : null);

    /// <inheritdoc />
    public ValueTask<CachedResponse> SetAsync(string key, ReadOnlyMemory<byte> payload, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        // Reuse the caller's array when the memory already wraps a standalone array (the engine passes
        // an owned byte[] from a MemoryStream), avoiding a second full-size copy on the hot path.
        var stored = MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array is { } array &&
            segment.Offset == 0 && segment.Count == array.Length
                ? array
                : payload.ToArray();

        // Pre-compute the ETag once at store time so cache hits never need to re-hash.
        var etag = ComputeETag(stored);
        var entry = new CachedResponse(stored, etag);

        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = stored.Length };
        options.RegisterPostEvictionCallback(
            static (evictedKey, _, _, state) => ((ConcurrentDictionary<string, byte>)state!).TryRemove((string)evictedKey, out _),
            _keys);

        _keys[key] = 0;
        _cache.Set(key, entry, options);
        return ValueTask.FromResult(entry);
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        _keys.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);
        foreach (var key in _keys.Keys)
        {
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                _cache.Remove(key);
                _keys.TryRemove(key, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Computes a quoted strong entity tag from response bytes.</summary>
    /// <param name="payload">The response body bytes.</param>
    /// <returns>A quoted hex SHA-256 tag, e.g. <c>"1A2B..."</c>.</returns>
    private static string ComputeETag(byte[] payload) =>
        string.Concat("\"", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), "\"");
}
