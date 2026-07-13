namespace Weir.Contracts;

/// <summary>
/// The outcome of a cache-purge request: how many endpoints matched the filter and which routes had
/// their cached responses evicted. A route is the eviction unit (the cache-key prefix), so several
/// endpoints that share a route collapse to a single entry in <see cref="PurgedRoutes"/>.
/// </summary>
public sealed record CachePurgeResult
{
    /// <summary>Number of endpoints that matched the purge filter.</summary>
    public int MatchedEndpoints { get; init; }

    /// <summary>The distinct routes whose cached responses were evicted (one prefix cleared per route).</summary>
    public IReadOnlyList<string> PurgedRoutes { get; init; } = [];
}
