using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class MemoryResponseCacheTests
{
    /// <summary>
    /// Runtime settings whose snapshot can be swapped, standing in for an admin edit. Only
    /// <see cref="IRuntimeSettings.Current"/> is exercised here; nothing is persisted.
    /// </summary>
    private sealed class MutableSettings(long responseCacheMaxBytes) : IRuntimeSettings
    {
        public WeirSystemSettings Current { get; private set; } =
            new() { ResponseCacheMaxBytes = responseCacheMaxBytes };

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(WeirSystemSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>Creates a cache bounded to <paramref name="maxBytes"/> (zero means unlimited).</summary>
    private static MemoryResponseCache NewCache(long maxBytes = 0) => new(new MutableSettings(maxBytes));

    /// <summary>Builds an entry the way the engine does: bytes plus the tag computed over them.</summary>
    private static CachedResponse Entry(params byte[] payload) => new(payload, ResponseETag.Compute(payload));

    /// <summary>Builds an entry of exactly <paramref name="size"/> bytes.</summary>
    private static CachedResponse EntryOfSize(int size) => Entry(new byte[size]);

    [Fact]
    public async Task SetThenGet_ReturnsStoredPayloadAndETag()
    {
        using var cache = NewCache();
        var entry = Entry(1, 2, 3);
        await cache.SetAsync("weir:orders/get:aaa", entry, TimeSpan.FromMinutes(5));

        var result = await cache.GetAsync("weir:orders/get:aaa");

        Assert.True(result.HasValue);
        Assert.Equal(new byte[] { 1, 2, 3 }, result!.Value.Bytes.ToArray());
        Assert.Equal(entry.ETag, result.Value.ETag);
    }

    [Fact]
    public async Task RemoveByPrefix_EvictsOnlyMatchingKeys()
    {
        using var cache = NewCache();
        await cache.SetAsync("weir:orders/get:aaa", Entry(1), TimeSpan.FromMinutes(5));
        await cache.SetAsync("weir:orders/get:bbb", Entry(2), TimeSpan.FromMinutes(5));
        await cache.SetAsync("weir:other/get:ccc", Entry(3), TimeSpan.FromMinutes(5));

        await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix("orders/get"));

        Assert.False((await cache.GetAsync("weir:orders/get:aaa")).HasValue);
        Assert.False((await cache.GetAsync("weir:orders/get:bbb")).HasValue);
        Assert.True((await cache.GetAsync("weir:other/get:ccc")).HasValue);
    }

    /// <summary>
    /// The bound is the whole point: storing past it must evict, not grow. Without a bound every entry
    /// survives, which is exactly the unbounded-growth failure this guards against.
    /// </summary>
    [Fact]
    public async Task Bound_Evicts_Entries_Once_The_Cache_Is_Full()
    {
        const int entrySize = 1_000;
        const int limit = 3 * entrySize;
        using var cache = NewCache(limit);

        for (var i = 0; i < 10; i++)
        {
            await cache.SetAsync($"weir:orders/get:{i}", EntryOfSize(entrySize), TimeSpan.FromMinutes(5));
        }

        var retained = 0;
        for (var i = 0; i < 10; i++)
        {
            if ((await cache.GetAsync($"weir:orders/get:{i}")).HasValue)
            {
                retained++;
            }
        }

        // Ten entries went in and the cache has room for three: the rest must be gone.
        Assert.InRange(retained, 1, limit / entrySize);
        Assert.True((long)retained * entrySize <= limit);
    }

    /// <summary>
    /// A full cache must evict something old to admit a new response. This is the behaviour the backing
    /// store does not provide by itself: left to its own size limit it refuses the incoming entry and
    /// keeps the stale ones, so a busy endpoint would stop being cached at all.
    /// </summary>
    [Fact]
    public async Task Bound_Admits_The_Newest_Entry_Rather_Than_Refusing_It()
    {
        const int entrySize = 1_000;
        using var cache = NewCache(3 * entrySize);

        for (var i = 0; i < 10; i++)
        {
            await cache.SetAsync($"weir:orders/get:{i}", EntryOfSize(entrySize), TimeSpan.FromMinutes(5));
        }

        Assert.True((await cache.GetAsync("weir:orders/get:9")).HasValue);
        Assert.False((await cache.GetAsync("weir:orders/get:0")).HasValue);
    }

    /// <summary>A payload that could never fit is skipped rather than flushing everything else out.</summary>
    [Fact]
    public async Task Payload_Larger_Than_The_Bound_Is_Not_Cached()
    {
        using var cache = NewCache(1_000);
        await cache.SetAsync("weir:orders/get:small", EntryOfSize(100), TimeSpan.FromMinutes(5));

        await cache.SetAsync("weir:orders/get:huge", EntryOfSize(5_000), TimeSpan.FromMinutes(5));

        Assert.False((await cache.GetAsync("weir:orders/get:huge")).HasValue);
        Assert.True((await cache.GetAsync("weir:orders/get:small")).HasValue);
    }

    /// <summary>Zero keeps the historical unbounded behaviour, as an explicit opt-out.</summary>
    [Fact]
    public async Task Zero_Bound_Keeps_Every_Entry()
    {
        using var cache = NewCache(0);

        for (var i = 0; i < 50; i++)
        {
            await cache.SetAsync($"weir:orders/get:{i}", EntryOfSize(1_000), TimeSpan.FromMinutes(5));
        }

        for (var i = 0; i < 50; i++)
        {
            Assert.True((await cache.GetAsync($"weir:orders/get:{i}")).HasValue);
        }
    }

    /// <summary>
    /// The bound is editable from the admin panel, so a changed setting must take effect on a live cache
    /// without a restart - the backing store cannot be resized, so the cache is rebuilt behind it.
    /// </summary>
    [Fact]
    public async Task Lowering_The_Bound_At_Runtime_Takes_Effect()
    {
        var settings = new MutableSettings(0);
        using var cache = new MemoryResponseCache(settings);

        for (var i = 0; i < 10; i++)
        {
            await cache.SetAsync($"weir:orders/get:{i}", EntryOfSize(1_000), TimeSpan.FromMinutes(5));
        }

        Assert.True((await cache.GetAsync("weir:orders/get:0")).HasValue);

        // An admin lowers the cap; the rebuilt cache starts empty and then honours the new bound.
        await settings.UpdateAsync(new WeirSystemSettings { ResponseCacheMaxBytes = 2_000 });

        for (var i = 0; i < 10; i++)
        {
            await cache.SetAsync($"weir:orders/get:{i}", EntryOfSize(1_000), TimeSpan.FromMinutes(5));
        }

        var retained = 0;
        for (var i = 0; i < 10; i++)
        {
            if ((await cache.GetAsync($"weir:orders/get:{i}")).HasValue)
            {
                retained++;
            }
        }

        Assert.InRange(retained, 1, 2);
    }
}
