using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>
/// In-memory <see cref="IResponseCache"/> storing rendered JSON bytes with pre-computed ETags, bounded
/// by <see cref="WeirSystemSettings.ResponseCacheMaxBytes"/>.
/// <para>
/// The cache owns its <see cref="MemoryCache"/> rather than sharing the one in the container, because a
/// size bound is a property of the whole <see cref="MemoryCache"/> instance: <c>SizeLimit</c> is fixed at
/// construction, and once it is set every entry must declare a <c>Size</c> or <c>Set</c> throws. Other
/// consumers of the shared cache (for example the API-key authenticator) store entries without a size,
/// so bounding the shared instance would break them. A private instance also keeps one endpoint's
/// payloads from evicting unrelated cached state.
/// </para>
/// <para>
/// <see cref="MemoryCache"/> alone does not give the behaviour wanted here: when a new entry would
/// exceed <c>SizeLimit</c> it refuses that entry and leaves the existing ones in place, so a full cache
/// would stop admitting fresh responses. Entries are therefore evicted (least recently used first) to
/// make room before a store, and the underlying <c>SizeLimit</c> remains as a hard backstop.
/// </para>
/// </summary>
public sealed class MemoryResponseCache : IResponseCache, IDisposable
{
    /// <summary>
    /// How many times a single store may compact the cache while making room. <see cref="MemoryCache.Compact"/>
    /// treats its argument as a target rather than a guarantee and can free less than asked, so freeing is
    /// re-checked against the measured size; the cap stops an unproductive compaction from looping.
    /// </summary>
    private const int MaxCompactionAttempts = 8;

    /// <summary>The runtime settings, read on each call so a changed bound takes effect without a restart.</summary>
    private readonly IRuntimeSettings _settings;

    /// <summary>Guards replacing <see cref="_generation"/> so only one thread rebuilds the backing cache.</summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The live backing cache and the bound it was built for. Swapped wholesale when the bound changes;
    /// read without the lock on the hot path.
    /// </summary>
    private Generation _generation;

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    private bool _disposed;

    /// <summary>Creates the cache, sizing its backing store from the current runtime settings.</summary>
    /// <param name="settings">The runtime settings supplying <see cref="WeirSystemSettings.ResponseCacheMaxBytes"/>.</param>
    public MemoryResponseCache(IRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _generation = new Generation(NormalizeLimit(settings.Current.ResponseCacheMaxBytes));
    }

    /// <inheritdoc />
    public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Current().Cache.TryGetValue(key, out CachedResponse? entry) && entry is { } e
            ? (CachedResponse?)e
            : null);

    /// <inheritdoc />
    public ValueTask SetAsync(string key, CachedResponse entry, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var generation = Current();
        var size = entry.Bytes.Length;

        // A payload that cannot fit under the bound is never cached: admitting it would evict every other
        // entry and still be refused by the backing cache.
        if (generation.Limit > 0 && size > generation.Limit)
        {
            return ValueTask.CompletedTask;
        }

        MakeRoom(generation, size);

        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = size };
        options.RegisterPostEvictionCallback(
            static (evictedKey, _, _, state) => ((ConcurrentDictionary<string, byte>)state!).TryRemove((string)evictedKey, out _),
            generation.Keys);

        generation.Keys[key] = 0;
        generation.Cache.Set(key, entry, options);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var generation = Current();
        generation.Cache.Remove(key);
        generation.Keys.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);
        var generation = Current();
        foreach (var key in generation.Keys.Keys)
        {
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                generation.Cache.Remove(key);
                generation.Keys.TryRemove(key, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Disposes the backing cache.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation.Cache.Dispose();
        }
    }

    /// <summary>
    /// Returns the live generation, rebuilding it first if the configured bound has changed since it was
    /// built. Rebuilding drops the cached entries, which is acceptable: they are a cache, and the next
    /// call to each endpoint repopulates them.
    /// </summary>
    /// <returns>The generation whose bound matches the current settings.</returns>
    private Generation Current()
    {
        var limit = NormalizeLimit(_settings.Current.ResponseCacheMaxBytes);
        var generation = Volatile.Read(ref _generation);
        if (generation.Limit == limit)
        {
            return generation;
        }

        lock (_gate)
        {
            if (_disposed || _generation.Limit == limit)
            {
                return _generation;
            }

            var previous = _generation;
            Volatile.Write(ref _generation, new Generation(limit));

            // Release the old payloads now, but leave the instance usable: a request that read the old
            // generation a moment ago may still be calling into it, and disposing it under that call
            // would throw into the data plane. Emptied and unreferenced, it is collected normally.
            previous.Cache.Clear();
            return _generation;
        }
    }

    /// <summary>
    /// Evicts entries, least recently used first, until <paramref name="incoming"/> bytes fit under the
    /// generation's bound. A no-op for an unbounded generation.
    /// </summary>
    /// <param name="generation">The generation to make room in.</param>
    /// <param name="incoming">Size in bytes of the payload about to be stored.</param>
    private static void MakeRoom(Generation generation, long incoming)
    {
        if (generation.Limit <= 0)
        {
            return;
        }

        for (var attempt = 0; attempt < MaxCompactionAttempts; attempt++)
        {
            var used = generation.Cache.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0;
            var overflow = used + incoming - generation.Limit;
            if (overflow <= 0 || used <= 0)
            {
                return;
            }

            generation.Cache.Compact(Math.Min(1d, (double)overflow / used));
        }
    }

    /// <summary>Normalizes a configured bound, mapping any non-positive value onto zero (unlimited).</summary>
    /// <param name="configured">The configured bound in bytes.</param>
    /// <returns>The bound in bytes, or zero for unlimited.</returns>
    private static long NormalizeLimit(long configured) => configured > 0 ? configured : 0;

    /// <summary>
    /// One backing cache together with the bound it was constructed for. A bound cannot be changed on a
    /// live <see cref="MemoryCache"/>, so a change is applied by building a new generation.
    /// </summary>
    private sealed class Generation
    {
        /// <summary>Creates a generation whose backing cache is bounded by <paramref name="limit"/>.</summary>
        /// <param name="limit">The bound in bytes; zero means unlimited.</param>
        internal Generation(long limit)
        {
            Limit = limit;

            // TrackStatistics exposes the measured size, which is what eviction decisions are made against.
            var options = new MemoryCacheOptions { TrackStatistics = true };
            if (limit > 0)
            {
                options.SizeLimit = limit;
            }

            Cache = new MemoryCache(options);
        }

        /// <summary>The bound in bytes this generation was built for; zero means unlimited.</summary>
        internal long Limit { get; }

        /// <summary>The backing cache.</summary>
        internal MemoryCache Cache { get; }

        /// <summary>
        /// Live keys tracked alongside the cache so <see cref="RemoveByPrefixAsync"/> can enumerate them
        /// (<see cref="MemoryCache"/> exposes no key enumeration). Entries remove themselves via a
        /// post-eviction callback, so this set does not outgrow the cache.
        /// </summary>
        internal ConcurrentDictionary<string, byte> Keys { get; } = new(StringComparer.Ordinal);
    }
}
