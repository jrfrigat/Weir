using Microsoft.Extensions.Caching.Memory;
using Weir.Abstractions;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class MemoryResponseCacheTests
{
    private static MemoryResponseCache NewCache() => new(new MemoryCache(new MemoryCacheOptions()));

    /// <summary>Builds an entry the way the engine does: bytes plus the tag computed over them.</summary>
    private static CachedResponse Entry(params byte[] payload) => new(payload, ResponseETag.Compute(payload));

    [Fact]
    public async Task SetThenGet_ReturnsStoredPayloadAndETag()
    {
        var cache = NewCache();
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
        var cache = NewCache();
        await cache.SetAsync("weir:orders/get:aaa", Entry(1), TimeSpan.FromMinutes(5));
        await cache.SetAsync("weir:orders/get:bbb", Entry(2), TimeSpan.FromMinutes(5));
        await cache.SetAsync("weir:other/get:ccc", Entry(3), TimeSpan.FromMinutes(5));

        await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix("orders/get"));

        Assert.False((await cache.GetAsync("weir:orders/get:aaa")).HasValue);
        Assert.False((await cache.GetAsync("weir:orders/get:bbb")).HasValue);
        Assert.True((await cache.GetAsync("weir:other/get:ccc")).HasValue);
    }
}
