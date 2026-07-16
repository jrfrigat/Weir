using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>In-memory, atomically-swappable snapshot of enabled endpoints, keyed by method + route.</summary>
public interface IEndpointCatalog
{
    /// <summary>(Re)loads endpoints from the control-plane store and swaps the snapshot.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves an enabled endpoint by HTTP method and route.</summary>
    bool TryResolve(string method, string route, out EndpointDefinition endpoint);

    /// <summary>All enabled endpoints in the current snapshot.</summary>
    IReadOnlyList<EndpointDefinition> All { get; }
}

/// <summary>Default <see cref="IEndpointCatalog"/> backed by the control-plane store.</summary>
public sealed partial class EndpointCatalog : IEndpointCatalog
{
    private readonly IControlPlaneStore _store;
    private readonly ILogger<EndpointCatalog> _logger;
    private volatile Snapshot _snapshot = new(new Dictionary<string, EndpointDefinition>(StringComparer.Ordinal), []);

    /// <summary>Creates the catalog over the control-plane store.</summary>
    /// <param name="store">The control-plane store to load endpoints from.</param>
    /// <param name="logger">Logger used to warn about route collisions; defaults to a no-op logger.</param>
    public EndpointCatalog(IControlPlaneStore store, ILogger<EndpointCatalog>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<EndpointCatalog>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<EndpointDefinition> All => _snapshot.All;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var endpoints = await _store.GetEndpointsAsync(cancellationToken);
        var map = new Dictionary<string, EndpointDefinition>(StringComparer.Ordinal);
        var list = new List<EndpointDefinition>(endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            if (!endpoint.Enabled)
            {
                continue;
            }

            var key = Key(endpoint.HttpMethod, endpoint.Route);
            if (map.TryGetValue(key, out var existing))
            {
                // Two enabled endpoints canonicalize to the same method+route (differing only by case
                // or slashes). The later one wins the dictionary slot; warn so the collision is visible
                // instead of an endpoint silently disappearing. Remove the old entry from the list
                // so it does not appear as a ghost in the All collection.
                LogRouteCollision(endpoint.HttpMethod, endpoint.Route, endpoint.Id, existing.Id);
                list.Remove(existing);
            }

            list.Add(endpoint);
            map[key] = endpoint;
        }

        _snapshot = new Snapshot(map, list);
    }

    /// <inheritdoc />
    public bool TryResolve(string method, string route, out EndpointDefinition endpoint)
    {
        if (_snapshot.Map.TryGetValue(Key(method, route), out var found))
        {
            endpoint = found;
            return true;
        }

        endpoint = null!;
        return false;
    }

    /// <summary>Canonicalizes a method + route pair into a resolution key (upper method, trimmed lower route).</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="route">Endpoint route.</param>
    /// <returns>The canonical lookup key.</returns>
    private static string Key(string method, string route) =>
        string.Concat(method.ToUpperInvariant(), " ", route.Trim('/').ToLowerInvariant());

    /// <summary>Warns that two enabled endpoints resolve to the same canonical key.</summary>
    /// <param name="method">HTTP method of the colliding endpoint.</param>
    /// <param name="route">Route of the colliding endpoint.</param>
    /// <param name="newId">Id of the endpoint that wins the slot.</param>
    /// <param name="existingId">Id of the endpoint that was overwritten.</param>
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Endpoint route collision: '{Method} {Route}' (id {NewId}) canonicalizes to the same key as id {ExistingId}; the later definition is served.")]
    private partial void LogRouteCollision(string method, string route, Guid newId, Guid existingId);

    /// <summary>An immutable, atomically-swapped view of the enabled endpoints.</summary>
    /// <param name="Map">Method+route key to endpoint lookup.</param>
    /// <param name="All">All enabled endpoints.</param>
    private sealed record Snapshot(Dictionary<string, EndpointDefinition> Map, IReadOnlyList<EndpointDefinition> All);
}
