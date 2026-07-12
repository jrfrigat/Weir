using System.Collections.Concurrent;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Diagnostics;

/// <summary>
/// In-process metrics store that powers the admin dashboard without any external backend. It is
/// both an <see cref="IWeirCallObserver"/> (fed by the engine) and an <see cref="IMetricsAggregator"/>
/// (read by the admin API). It keeps per-route and service-wide rolling statistics.
/// </summary>
public sealed class InMemoryMetricsAggregator : IMetricsAggregator, IWeirCallObserver
{
    /// <summary>Length, in seconds, of the trailing window used for overview rates.</summary>
    private const int OverviewWindowSeconds = 60;

    /// <summary>Per-route rolling statistics, keyed by route.</summary>
    private readonly ConcurrentDictionary<string, EndpointStats> _endpoints = new(StringComparer.Ordinal);

    /// <summary>Service-wide rolling statistics.</summary>
    private readonly EndpointStats _global = new();

    /// <summary>Clock used for windows and uptime.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Unix second at which the aggregator started, for uptime and first-minute rate scaling.</summary>
    private readonly long _startUnixSeconds;

    /// <summary>Total requests observed since start.</summary>
    private long _totalRequests;

    /// <summary>Total failed requests since start.</summary>
    private long _totalErrors;

    /// <summary>Currently in-flight requests.</summary>
    private int _activeRequests;

    /// <summary>Creates the aggregator over a clock (used for windows and uptime).</summary>
    /// <param name="clock">Time source.</param>
    public InMemoryMetricsAggregator(TimeProvider clock)
    {
        _clock = clock;
        _startUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
    }

    /// <inheritdoc />
    public ValueTask OnStartedAsync(WeirCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Interlocked.Increment(ref _activeRequests);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnCompletedAsync(WeirCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Interlocked.Decrement(ref _activeRequests);
        Record(context);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFailedAsync(WeirCallContext context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        Interlocked.Decrement(ref _activeRequests);
        Record(context);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Record(WeirCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var isError = !string.Equals(context.Outcome, "ok", StringComparison.Ordinal);
        var now = _clock.GetUtcNow();
        var second = now.ToUnixTimeSeconds();

        var stats = _endpoints.GetOrAdd(context.Route, static _ => new EndpointStats());
        stats.ObjectName = context.ObjectName;
        stats.Record(second, context.DurationMs, isError, context.CacheHit, now.UtcTicks);
        _global.Record(second, context.DurationMs, isError, context.CacheHit, now.UtcTicks);

        Interlocked.Increment(ref _totalRequests);
        if (isError)
        {
            Interlocked.Increment(ref _totalErrors);
        }
    }

    /// <inheritdoc />
    public MetricsOverview GetOverview()
    {
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var (count, errors, cacheHits, _) = _global.Window(now, OverviewWindowSeconds);

        // Divide by the actual elapsed span during the first minute of uptime, otherwise a young process
        // dilutes its rate by dividing a partial window's count by a full 60 seconds.
        var effectiveWindow = Math.Min(OverviewWindowSeconds, Math.Max(1, now - _startUnixSeconds + 1));

        var topSlow = _endpoints
            .Select(pair => pair.Value.Snapshot(pair.Key, now))
            .OrderByDescending(metrics => metrics.P95LatencyMs)
            .Take(5)
            .ToList();

        return new MetricsOverview
        {
            TotalRequests = Interlocked.Read(ref _totalRequests),
            TotalErrors = Interlocked.Read(ref _totalErrors),
            RequestsPerSecond = count / (double)effectiveWindow,
            ErrorRate = count == 0 ? 0 : (double)errors / count,
            CacheHitRatio = count == 0 ? 0 : (double)cacheHits / count,
            P50LatencyMs = _global.Percentile(0.50, now),
            P95LatencyMs = _global.Percentile(0.95, now),
            P99LatencyMs = _global.Percentile(0.99, now),
            ActiveRequests = Volatile.Read(ref _activeRequests),
            Uptime = TimeSpan.FromSeconds(now - _startUnixSeconds),
            TopSlow = topSlow,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<EndpointMetrics> GetEndpoints()
    {
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        return _endpoints.Select(pair => pair.Value.Snapshot(pair.Key, now)).ToList();
    }

    /// <inheritdoc />
    public TimeSeries GetTimeSeries(string metric, string? route, TimeSpan window, TimeSpan bucket)
    {
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var windowSeconds = Math.Max(1, (int)window.TotalSeconds);
        var bucketSeconds = Math.Max(1, (int)bucket.TotalSeconds);

        var stats = route is null
            ? _global
            : _endpoints.TryGetValue(route, out var found) ? found : null;

        var points = stats?.TimeSeries(now, metric, windowSeconds, bucketSeconds) ?? [];
        return new TimeSeries { Metric = metric, Route = route, Points = points };
    }
}
