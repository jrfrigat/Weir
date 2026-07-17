using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Weir.Host.Options;

namespace Weir.Host;

/// <summary>
/// Periodically reloads the endpoint catalog from the control-plane store so that, in a
/// multi-instance deployment, each instance picks up metadata changes made through another instance,
/// and evicts the cached responses of every route whose definition changed with it.
/// Disabled when the configured interval is zero (single-node deployments).
/// <para>
/// The eviction half exists because the admin API only evicts the cache of the instance that happened
/// to serve the edit. Every other instance reloaded the new definition here but kept serving bodies
/// rendered from the old one until the TTL ran out, so an edit that was supposed to take effect at
/// once silently did not. Reloading the definition and dropping the responses it produced are the same
/// event; splitting them is what let the two disagree.
/// </para>
/// </summary>
public sealed class CatalogRefreshService : BackgroundService
{
    private readonly IEndpointCatalog _catalog;
    private readonly IResponseCache _cache;
    private readonly ILogger<CatalogRefreshService> _logger;
    private readonly TimeSpan _interval;

    /// <summary>
    /// The identity and revision of each route as of the last reload, keyed by the route exactly as it
    /// was spelled then. Only touched from <see cref="ExecuteAsync"/>, which is single-threaded, so it
    /// needs no lock.
    /// </summary>
    private Dictionary<string, RouteRevision> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <see cref="_seen"/> holds a real reading yet. The first pass only records what is there:
    /// with nothing to compare against, every route would look new, and treating a first sighting as a
    /// change would drop the whole cache on the first tick after every start.
    /// </summary>
    private bool _primed;

    /// <summary>Creates the refresh service from the catalog, the response cache, options and a logger.</summary>
    /// <param name="catalog">The endpoint catalog to reload.</param>
    /// <param name="cache">The response cache to evict changed routes from.</param>
    /// <param name="options">The refresh options.</param>
    /// <param name="logger">The logger.</param>
    public CatalogRefreshService(
        IEndpointCatalog catalog,
        IResponseCache cache,
        IOptions<CatalogRefreshOptions> options,
        ILogger<CatalogRefreshService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _catalog = catalog;
        _cache = cache;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Max(0, options.Value.ReloadSeconds));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _catalog.LoadAsync(stoppingToken);
                    await EvictChangedRoutesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.CatalogReloadFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Drops the cached responses of every route that is no longer serving what it served at the last
    /// reload, and records the new reading.
    /// </summary>
    /// <param name="cancellationToken">Cancels the eviction.</param>
    private async Task EvictChangedRoutesAsync(CancellationToken cancellationToken)
    {
        var current = new Dictionary<string, RouteRevision>(StringComparer.OrdinalIgnoreCase);
        var endpoints = _catalog.All;
        for (var i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];
            current[endpoint.Route] = new RouteRevision(endpoint.Id, endpoint.UpdatedAt);
        }

        if (_primed)
        {
            foreach (var (route, previous) in _seen)
            {
                // Gone from the catalog means deleted, disabled or renamed. The route stops resolving, so
                // the entries are unreachable rather than wrong - until the route comes back within the
                // TTL, and a cache keyed by route hands the new endpoint the old one's bodies.
                if (!current.TryGetValue(route, out var now) || now != previous)
                {
                    // By the route as it was spelled when those entries were written: that is the string
                    // their keys were built from, and a rename or a change of case would miss them.
                    await _cache.RemoveByPrefixAsync(CacheKey.RoutePrefix(route), cancellationToken);
                }
            }
        }

        _seen = current;
        _primed = true;
    }

    /// <summary>
    /// What a route was serving at a given reload: which endpoint answered it, and which revision of
    /// that endpoint. <see cref="EndpointDefinition.UpdatedAt"/> is the revision because every store
    /// stamps it on save, so it already reaches the other instances with the definition itself and
    /// needs nothing added to the schema. The definition cannot stand in for it: a record compares its
    /// collection members by reference, and each reload builds fresh lists, so two identical loads
    /// would compare unequal and evict everything every time.
    /// </summary>
    /// <param name="Id">The endpoint answering the route.</param>
    /// <param name="UpdatedAt">When that endpoint's definition was last saved.</param>
    private readonly record struct RouteRevision(Guid Id, DateTimeOffset UpdatedAt);
}
