using Microsoft.Extensions.Caching.Memory;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class MemoryResponseCacheTests
{
    private static MemoryResponseCache NewCache() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task SetThenGet_ReturnsStoredPayload()
    {
        var cache = NewCache();
        await cache.SetAsync("weir:orders/get:aaa", new byte[] { 1, 2, 3 }, TimeSpan.FromMinutes(5));

        var result = await cache.GetAsync("weir:orders/get:aaa");

        Assert.True(result.HasValue);
        Assert.Equal(new byte[] { 1, 2, 3 }, result!.Value.Bytes.ToArray());
    }

    [Fact]
    public async Task RemoveByPrefix_EvictsOnlyMatchingKeys()
    {
        var cache = NewCache();
        await cache.SetAsync("weir:orders/get:aaa", new byte[] { 1 }, TimeSpan.FromMinutes(5));
        await cache.SetAsync("weir:orders/get:bbb", new byte[] { 2 }, TimeSpan.FromMinutes(5));
        await cache.SetAsync("weir:other/get:ccc", new byte[] { 3 }, TimeSpan.FromMinutes(5));

        await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix("orders/get"));

        Assert.False((await cache.GetAsync("weir:orders/get:aaa")).HasValue);
        Assert.False((await cache.GetAsync("weir:orders/get:bbb")).HasValue);
        Assert.True((await cache.GetAsync("weir:other/get:ccc")).HasValue);
    }
}
