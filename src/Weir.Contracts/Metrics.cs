namespace Weir.Contracts;

/// <summary>Service-wide metrics snapshot for the dashboard overview.</summary>
public sealed record MetricsOverview
{
    /// <summary>Total requests observed since start.</summary>
    public long TotalRequests { get; init; }

    /// <summary>Total failed requests since start.</summary>
    public long TotalErrors { get; init; }

    /// <summary>Requests per second over the recent window.</summary>
    public double RequestsPerSecond { get; init; }

    /// <summary>Fraction of requests that failed over the recent window (0..1).</summary>
    public double ErrorRate { get; init; }

    /// <summary>Cache hit ratio over the recent window (0..1).</summary>
    public double CacheHitRatio { get; init; }

    /// <summary>Median request latency, in milliseconds.</summary>
    public double P50LatencyMs { get; init; }

    /// <summary>95th-percentile request latency, in milliseconds.</summary>
    public double P95LatencyMs { get; init; }

    /// <summary>99th-percentile request latency, in milliseconds.</summary>
    public double P99LatencyMs { get; init; }

    /// <summary>Currently in-flight requests.</summary>
    public int ActiveRequests { get; init; }

    /// <summary>Process uptime.</summary>
    public TimeSpan Uptime { get; init; }

    /// <summary>The slowest endpoints by p95 latency.</summary>
    public IReadOnlyList<EndpointMetrics> TopSlow { get; init; } = [];
}

/// <summary>Rolling metrics for a single endpoint / stored procedure.</summary>
public sealed record EndpointMetrics
{
    /// <summary>Endpoint route.</summary>
    public required string Route { get; init; }

    /// <summary>Underlying schema-qualified object name.</summary>
    public string? ObjectName { get; init; }

    /// <summary>Total calls recorded for this endpoint since start.</summary>
    public long Count { get; init; }

    /// <summary>Total failed calls for this endpoint since start.</summary>
    public long Errors { get; init; }

    /// <summary>Error fraction since start (0..1).</summary>
    public double ErrorRate { get; init; }

    /// <summary>Median latency, in milliseconds.</summary>
    public double P50LatencyMs { get; init; }

    /// <summary>95th-percentile latency, in milliseconds.</summary>
    public double P95LatencyMs { get; init; }

    /// <summary>99th-percentile latency, in milliseconds.</summary>
    public double P99LatencyMs { get; init; }

    /// <summary>Mean latency, in milliseconds.</summary>
    public double AvgLatencyMs { get; init; }

    /// <summary>Cache hit ratio for the endpoint since start (0..1).</summary>
    public double CacheHitRatio { get; init; }

    /// <summary>When the endpoint was last called.</summary>
    public DateTimeOffset? LastCalledAt { get; init; }

    /// <summary>Median parameter-binding time, in milliseconds.</summary>
    public double BindingP50Ms { get; init; }

    /// <summary>95th-percentile parameter-binding time, in milliseconds.</summary>
    public double BindingP95Ms { get; init; }

    /// <summary>Median cache-lookup time, in milliseconds.</summary>
    public double CacheLookupP50Ms { get; init; }

    /// <summary>95th-percentile cache-lookup time, in milliseconds.</summary>
    public double CacheLookupP95Ms { get; init; }

    /// <summary>Median database-execution time, in milliseconds.</summary>
    public double DbP50Ms { get; init; }

    /// <summary>95th-percentile database-execution time, in milliseconds.</summary>
    public double DbP95Ms { get; init; }

    /// <summary>Median response-streaming time, in milliseconds.</summary>
    public double StreamingP50Ms { get; init; }

    /// <summary>95th-percentile response-streaming time, in milliseconds.</summary>
    public double StreamingP95Ms { get; init; }
}

/// <summary>A single time-bucketed data point.</summary>
public sealed record MetricPoint
{
    /// <summary>Bucket timestamp (start of the interval).</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Aggregated value for the bucket.</summary>
    public double Value { get; init; }
}

/// <summary>A named time series returned for charting.</summary>
public sealed record TimeSeries
{
    /// <summary>Metric name, e.g. <c>requests</c> or <c>p95</c>.</summary>
    public required string Metric { get; init; }

    /// <summary>Optional route the series is scoped to; null for service-wide.</summary>
    public string? Route { get; init; }

    /// <summary>Ordered points, oldest first.</summary>
    public IReadOnlyList<MetricPoint> Points { get; init; } = [];
}

/// <summary>Health status of one named data connection.</summary>
public sealed record ConnectionHealth
{
    /// <summary>Connection name.</summary>
    public required string Name { get; init; }

    /// <summary>Provider key, e.g. SqlServer.</summary>
    public string? Provider { get; init; }

    /// <summary>Whether the last probe succeeded.</summary>
    public bool Healthy { get; init; }

    /// <summary>Round-trip time of the last probe, in milliseconds.</summary>
    public double LatencyMs { get; init; }

    /// <summary>Error text if the probe failed.</summary>
    public string? Error { get; init; }

    /// <summary>When the connection was last probed.</summary>
    public DateTimeOffset CheckedAt { get; init; }
}
