using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class MemoryResponseCacheTests
{
    /// <summary>Replacement and expiry keep the current entry reachable by prefix invalidation.</summary>
    /// <param name="expired">Whether the replaced entry has already expired.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_RemainsIndexed_AfterReplacementOrExpiry(bool expired)
    {
        var clock = new TestClock();
        using var cache = new MemoryResponseCache(new MutableSettings(0), clock);
        const string key = "weir:replacement:one";
        await cache.SetAsync(key, EntryOfSize(3), TimeSpan.FromMinutes(1));
        if (expired)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Null(await cache.GetAsync(key));
        }
        await cache.SetAsync(key, EntryOfSize(5), TimeSpan.FromMinutes(5));

        Assert.True((await cache.GetAsync(key)).HasValue);
        await cache.RemoveByPrefixAsync("weir:replacement:");
        Assert.Null(await cache.GetAsync(key));
    }

    /// <summary>Bounds key/index metadata even when payloads contain no bytes.</summary>
    [Fact]
    public async Task CountBound_HandlesEmptyEntriesAndSecondChance()
    {
        var settings = new MutableSettings(0);
        await settings.UpdateAsync(new WeirSystemSettings { ResponseCacheMaxBytes = 0, ResponseCacheMaxEntries = 2 });
        using var cache = new MemoryResponseCache(settings);
        await cache.SetAsync("a", Entry(), TimeSpan.FromMinutes(5));
        await cache.SetAsync("b", Entry(), TimeSpan.FromMinutes(5));
        Assert.NotNull(await cache.GetAsync("a"));
        await cache.SetAsync("c", Entry(), TimeSpan.FromMinutes(5));
        Assert.NotNull(await cache.GetAsync("a"));
        Assert.Null(await cache.GetAsync("b"));
        Assert.NotNull(await cache.GetAsync("c"));
        Assert.Equal((0L, 2, 0L), cache.GetStorageSnapshot());
    }

    /// <summary>Refused payloads and metadata cannot evict a useful admitted entry.</summary>
    [Fact]
    public async Task EntryAndMetadataBounds_RejectBeforeEvicting()
    {
        var settings = new MutableSettings(0);
        await settings.UpdateAsync(new WeirSystemSettings { ResponseCacheMaxBytes = 0, ResponseCacheMaxEntryBytes = 4 });
        using var cache = new MemoryResponseCache(settings);
        await cache.SetAsync("good", EntryOfSize(4), TimeSpan.FromMinutes(5));
        await cache.SetAsync("good", EntryOfSize(5), TimeSpan.FromMinutes(5));
        await cache.SetAsync(new string('x', 1025), EntryOfSize(1), TimeSpan.FromMinutes(5));
        await cache.SetAsync("bad-tag", new CachedResponse(new byte[1], new string('x', 129)), TimeSpan.FromMinutes(5));
        Assert.Equal(4, (await cache.GetAsync("good"))!.Value.Bytes.Length);
        Assert.Equal((4L, 1, 3L), cache.GetStorageSnapshot());
    }

    /// <summary>Live reductions apply to bytes, count and an entry that no longer fits the entry limit.</summary>
    [Fact]
    public async Task RuntimeReductions_TrimAllBounds()
    {
        var settings = new MutableSettings(0);
        using var cache = new MemoryResponseCache(settings);
        for (var i = 0; i < 10; i++) await cache.SetAsync($"k{i}", EntryOfSize(40), TimeSpan.FromMinutes(5));
        await settings.UpdateAsync(new WeirSystemSettings { ResponseCacheMaxBytes = 100, ResponseCacheMaxEntries = 2 });
        Assert.Equal((80L, 2, 0L), cache.GetStorageSnapshot());
        await settings.UpdateAsync(new WeirSystemSettings { ResponseCacheMaxEntryBytes = 8 });
        Assert.Equal((0L, 0, 0L), cache.GetStorageSnapshot());
    }

    /// <summary>A small slice cannot keep a large external backing array alive inside the cache.</summary>
    [Fact]
    public async Task Slice_IsCopiedIntoExactOwnedStorage()
    {
        using var cache = NewCache();
        var backing = new byte[1024 * 1024];
        backing[500] = 42;
        await cache.SetAsync("slice", new CachedResponse(backing.AsMemory(500, 1), "tag"), TimeSpan.FromMinutes(5));
        backing[500] = 0;
        var hit = (await cache.GetAsync("slice"))!.Value;
        Assert.Equal(42, hit.Bytes.Span[0]);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(hit.Bytes, out var storage));
        Assert.Single(storage.Array!);
    }

    /// <summary>Expired payloads leave accounting synchronously without waiting for a background callback.</summary>
    [Fact]
    public async Task Expiry_RemovesStorageSynchronously()
    {
        var clock = new TestClock();
        using var cache = new MemoryResponseCache(new MutableSettings(0), clock);
        await cache.SetAsync("ttl", EntryOfSize(50), TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await cache.GetAsync("ttl"));
        Assert.Equal((0L, 0, 0L), cache.GetStorageSnapshot());
    }

    /// <summary>Concurrent admission never exceeds either stored-payload or entry-count bounds.</summary>
    [Fact]
    public async Task ConcurrentAdmission_StaysWithinBothBounds()
    {
        var settings = new MutableSettings(128);
        await settings.UpdateAsync(new WeirSystemSettings { ResponseCacheMaxBytes = 128, ResponseCacheMaxEntries = 4 });
        using var cache = new MemoryResponseCache(settings);
        await Parallel.ForEachAsync(Enumerable.Range(0, 10000), async (i, token) =>
            await cache.SetAsync($"item:{i}", EntryOfSize(16), TimeSpan.FromMinutes(5), token));
        var snapshot = cache.GetStorageSnapshot();
        Assert.InRange(snapshot.EntryCount, 1, 4);
        Assert.InRange(snapshot.PayloadBytes, 1, 128);
        await cache.RemoveByPrefixAsync("item:");
        Assert.Equal((0L, 0, 0L), cache.GetStorageSnapshot());
    }

    /// <summary>A deterministic TTL clock with no real sleeps or timer callbacks.</summary>
    private sealed class TestClock : TimeProvider
    {
        /// <summary>The current UTC instant.</summary>
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _now;

        /// <summary>Moves the clock forward.</summary>
        /// <param name="duration">The elapsed duration.</param>
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    /// <summary>Competing mutations must never leave a live entry outside the purge index.</summary>
    [Fact]
    public async Task ConcurrentReplacementAndRemoval_RemainPurgeable()
    {
        using var cache = NewCache();
        await Parallel.ForEachAsync(Enumerable.Range(0, 1000), async (i, token) =>
        {
            var key = $"weir:concurrent:{i % 8}";
            await cache.SetAsync(key, EntryOfSize(i + 1), TimeSpan.FromMinutes(5), token);
            if (i % 3 == 0) await cache.RemoveAsync(key, token);
        });
        await cache.RemoveByPrefixAsync("weir:concurrent:");
        for (var i = 0; i < 8; i++) Assert.Null(await cache.GetAsync($"weir:concurrent:{i}"));
    }

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
    /// The stamp exists so a purge that lands while a query is in flight stays visible to the store that
    /// follows it; this pins that it moves on a purge and on nothing else.
    /// </summary>
    [Fact]
    public async Task PurgeStamp_RisesOnAPurgeAndOnNothingElse()
    {
        using var cache = NewCache();
        var before = cache.PurgeStamp;

        await cache.SetAsync("weir:orders/get:aaa", Entry(1), TimeSpan.FromMinutes(5));
        await cache.GetAsync("weir:orders/get:aaa");
        await cache.RemoveAsync("weir:orders/get:aaa");

        Assert.Equal(before, cache.PurgeStamp);

        await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix("orders/get"));

        Assert.NotEqual(before, cache.PurgeStamp);
    }

    /// <summary>
    /// The case the finding turns on: the fill has not stored anything yet, so the purge removes no key at
    /// all - and would leave no trace if the stamp only moved when something was evicted.
    /// </summary>
    [Fact]
    public async Task PurgeStamp_RisesForAPrefixNothingIsStoredUnder()
    {
        using var cache = NewCache();
        var before = cache.PurgeStamp;

        await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix("orders/get"));

        Assert.NotEqual(before, cache.PurgeStamp);
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
