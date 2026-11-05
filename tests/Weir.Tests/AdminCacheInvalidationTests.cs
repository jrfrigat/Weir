using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

/// <summary>Checks immediate invalidation of old and new routes through actual admin endpoints.</summary>
/// <param name="factory">The isolated SQLite-backed host.</param>
public sealed class AdminCacheInvalidationTests(TransportGatingTests.HostFactory factory)
    : IClassFixture<TransportGatingTests.HostFactory>
{
    /// <summary>Renaming through any write endpoint invalidates both route generations.</summary>
    /// <param name="method">The admin operation used for the rename.</param>
    /// <returns>The completed HTTP and cache check.</returns>
    [Theory]
    [InlineData("post")]
    [InlineData("put")]
    [InlineData("import")]
    public async Task Rename_InvalidatesBothRoutes(string method)
    {
        using var client = await factory.CreateAdminClientAsync();
        var oldRoute = "rename/" + Guid.NewGuid().ToString("N");
        var newRoute = oldRoute + "/new";
        var original = new EndpointDefinition { Route = oldRoute, HttpMethod = "GET", ConnectionName = "default", ObjectName = "usp_Nothing" };
        using var created = await client.PostAsJsonAsync("/admin/api/endpoints", original);
        created.EnsureSuccessStatusCode();
        var saved = (await created.Content.ReadFromJsonAsync<EndpointDefinition>())!;
        var cache = factory.Services.GetRequiredService<IResponseCache>();
        var oldKey = CacheKey.RoutePrefix(oldRoute) + "hash";
        var newKey = CacheKey.RoutePrefix(newRoute) + "hash";
        var bytes = new CachedResponse(new byte[8], "tag");
        await cache.SetAsync(oldKey, bytes, TimeSpan.FromMinutes(1));
        await cache.SetAsync(newKey, bytes, TimeSpan.FromMinutes(1));
        var edited = saved with { Route = newRoute };
        using var response = method switch
        {
            "post" => await client.PostAsJsonAsync("/admin/api/endpoints", edited),
            "put" => await client.PutAsJsonAsync("/admin/api/endpoints/" + saved.Id, edited),
            _ => await client.PostAsJsonAsync("/admin/api/endpoints/import", new[] { edited })
        };
        response.EnsureSuccessStatusCode();
        Assert.Null(await cache.GetAsync(oldKey));
        Assert.Null(await cache.GetAsync(newKey));
    }
}
