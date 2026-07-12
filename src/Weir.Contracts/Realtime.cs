namespace Weir.Contracts;

/// <summary>
/// A push snapshot of the live dashboard, broadcast to connected admins over the real-time hub so the
/// dashboard reflects service state without polling. Metrics are cheap (read from the in-memory
/// aggregator); connection health is pushed on a slower cadence because probing opens connections.
/// </summary>
public sealed record DashboardSnapshot
{
    /// <summary>The service-wide overview (throughput, error rate, latency percentiles, uptime).</summary>
    public required MetricsOverview Overview { get; init; }

    /// <summary>Per-endpoint rolling metrics.</summary>
    public required IReadOnlyList<EndpointMetrics> Endpoints { get; init; }

    /// <summary>Recent throughput time series (requests per second).</summary>
    public required TimeSeries Throughput { get; init; }

    /// <summary>Recent latency time series (p50, in milliseconds).</summary>
    public required TimeSeries Latency { get; init; }
}
