using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Weir.Host;
using Weir.Host.Options;
using Xunit;

namespace Weir.Tests;

// Editing an endpoint evicts the cached responses of the instance that served the edit, and only that
// one. Every other instance reloaded the new definition on its next poll but went on serving bodies
// rendered from the old one until the TTL expired, so "a change never serves stale data" held on one
// node out of N. The reload is where the news arrives, so the eviction belongs there too.
public class CatalogRefreshEvictionTests
{
    /// <summary>How long a test waits for a background tick before giving up.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task A_Definition_Edited_On_Another_Instance_Evicts_This_Instance_Cache()
    {
        // The catalog answers with a newer revision from the second load on, which is what this instance
        // sees when someone edits the endpoint through a different one.
        var catalog = new FakeCatalog(Endpoint(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero)));
        catalog.ChangeOnLoad(2, Endpoint(new DateTimeOffset(2026, 7, 17, 12, 5, 0, TimeSpan.Zero)));
        var cache = new RecordingCache();

        using var service = Service(catalog, cache);
        await service.StartAsync(CancellationToken.None);
        var evicted = await cache.FirstEviction.WaitAsync(Timeout);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(CacheKey.RoutePrefix("orders/get"), evicted);
    }

    [Fact]
    public async Task A_Reload_That_Finds_Nothing_Changed_Keeps_The_Cache()
    {
        // The guard on the fix above: a reload happens every ReloadSeconds forever, so evicting on one
        // that found no change would empty the cache on a timer and quietly turn caching off.
        var catalog = new FakeCatalog(Endpoint(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero)));
        var cache = new RecordingCache();

        using var service = Service(catalog, cache);
        await service.StartAsync(CancellationToken.None);
        await catalog.Reached(3).WaitAsync(Timeout);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(cache.Evictions);
    }

    [Fact]
    public async Task An_Endpoint_That_Leaves_The_Catalog_Evicts_Too()
    {
        // Deleted or disabled, the route stops resolving, so the entries are unreachable rather than
        // wrong - until it is re-created or re-enabled inside the TTL. The cache is keyed by route, not
        // by endpoint id, so the returning endpoint would be handed the old one's bodies.
        var catalog = new FakeCatalog(Endpoint(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero)));
        catalog.ChangeOnLoad(2, null);
        var cache = new RecordingCache();

        using var service = Service(catalog, cache);
        await service.StartAsync(CancellationToken.None);
        var evicted = await cache.FirstEviction.WaitAsync(Timeout);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(CacheKey.RoutePrefix("orders/get"), evicted);
    }

    /// <summary>Builds the service under test with the shortest interval its options allow.</summary>
    /// <param name="catalog">The catalog to reload.</param>
    /// <param name="cache">The cache to evict from.</param>
    /// <returns>The service, not yet started.</returns>
    private static CatalogRefreshService Service(FakeCatalog catalog, RecordingCache cache) =>
        new(catalog, cache, Options.Create(new CatalogRefreshOptions { ReloadSeconds = 1 }), NullLogger<CatalogRefreshService>.Instance);

    /// <summary>Builds the one endpoint these tests use, at a given revision.</summary>
    /// <param name="updatedAt">The revision stamp the store would have written on save.</param>
    /// <returns>The definition.</returns>
    private static EndpointDefinition Endpoint(DateTimeOffset updatedAt) => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Route = "orders/get",
        ConnectionName = "main",
        ObjectName = "usp_GetOrder",
        UpdatedAt = updatedAt,
    };

    /// <summary>
    /// An <see cref="IEndpointCatalog"/> whose contents change on a chosen load, standing in for an edit
    /// made through another instance and picked up by this one's next poll.
    /// </summary>
    private sealed class FakeCatalog : IEndpointCatalog
    {
        /// <summary>Completes when the load count reaches the awaited figure.</summary>
        private readonly TaskCompletionSource<int> _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The load count that completes <see cref="_reached"/>; zero when nobody is waiting.</summary>
        private int _target;

        /// <summary>The load from which <see cref="_next"/> replaces the current contents; zero for never.</summary>
        private int _changeAt;

        /// <summary>What the catalog holds from <see cref="_changeAt"/> on; null means the endpoint is gone.</summary>
        private EndpointDefinition? _next;

        /// <summary>How many times <see cref="LoadAsync"/> has been called.</summary>
        private int _loads;

        /// <summary>Creates the catalog holding one endpoint.</summary>
        /// <param name="endpoint">The endpoint the catalog starts with.</param>
        internal FakeCatalog(EndpointDefinition endpoint) => All = [endpoint];

        /// <inheritdoc />
        public IReadOnlyList<EndpointDefinition> All { get; private set; }

        /// <summary>Replaces the contents from a given load onwards.</summary>
        /// <param name="load">The 1-based load on which the change becomes visible.</param>
        /// <param name="endpoint">The new definition, or null to drop the endpoint entirely.</param>
        internal void ChangeOnLoad(int load, EndpointDefinition? endpoint)
        {
            _changeAt = load;
            _next = endpoint;
        }

        /// <summary>Returns a task that completes once <see cref="LoadAsync"/> has run this many times.</summary>
        /// <param name="loads">The load count to wait for.</param>
        /// <returns>The task.</returns>
        internal Task<int> Reached(int loads)
        {
            _target = loads;
            return _reached.Task;
        }

        /// <inheritdoc />
        public Task LoadAsync(CancellationToken cancellationToken = default)
        {
            var loads = ++_loads;
            if (_changeAt > 0 && loads >= _changeAt)
            {
                All = _next is null ? [] : [_next];
            }

            if (_target > 0 && loads >= _target)
            {
                _reached.TrySetResult(loads);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public bool TryResolve(string method, string route, out EndpointMatch match)
        {
            match = default;
            return false;
        }
    }

    /// <summary>An <see cref="IResponseCache"/> that records the prefixes it was asked to evict.</summary>
    private sealed class RecordingCache : IResponseCache
    {
        /// <summary>Completes with the first prefix evicted.</summary>
        private readonly TaskCompletionSource<string> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Every prefix evicted, in order.</summary>
        internal List<string> Evictions { get; } = [];

        /// <summary>The first prefix evicted; never completes if nothing is.</summary>
        internal Task<string> FirstEviction => _first.Task;

        /// <inheritdoc />
        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CachedResponse?>(null);

        /// <inheritdoc />
        public ValueTask SetAsync(string key, CachedResponse entry, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
        {
            Evictions.Add(keyPrefix);
            _first.TrySetResult(keyPrefix);
            return ValueTask.CompletedTask;
        }
    }
}
