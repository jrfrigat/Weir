using Weir.Contracts;

namespace Weir.Diagnostics;

/// <summary>
/// Thread-safe rolling statistics for one route (or the service-wide total). Keeps counters, a
/// latency histogram for percentiles, and a <see cref="TimeRing"/> for time series.
/// </summary>
internal sealed class EndpointStats
{
    /// <summary>Number of one-second slots kept for time series and windowed rates (the maximum window length).</summary>
    public const int RingCapacitySeconds = 300;

    /// <summary>Trailing window, in seconds, over which the decaying latency percentiles are computed.</summary>
    public const int PercentileWindowSeconds = RingCapacitySeconds;

    /// <summary>Upper bounds, in milliseconds, of the latency histogram buckets.</summary>
    private static readonly double[] Boundaries = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];

    /// <summary>Per-second ring used for time series, windowed rates and the decaying latency histogram.</summary>
    private readonly TimeRing _ring = new(RingCapacitySeconds, Boundaries.Length + 1);

    /// <summary>Per-second ring for parameter-binding duration percentiles.</summary>
    private readonly TimeRing _bindingRing = new(RingCapacitySeconds, Boundaries.Length + 1);

    /// <summary>Per-second ring for cache-lookup duration percentiles.</summary>
    private readonly TimeRing _cacheRing = new(RingCapacitySeconds, Boundaries.Length + 1);

    /// <summary>Per-second ring for database-execution duration percentiles.</summary>
    private readonly TimeRing _dbRing = new(RingCapacitySeconds, Boundaries.Length + 1);

    /// <summary>Per-second ring for response-streaming duration percentiles.</summary>
    private readonly TimeRing _streamingRing = new(RingCapacitySeconds, Boundaries.Length + 1);

    /// <summary>Total calls recorded (lifetime).</summary>
    private long _count;

    /// <summary>Total failed calls (lifetime).</summary>
    private long _errors;

    /// <summary>Total cache-served calls (lifetime).</summary>
    private long _cacheHits;

    /// <summary>Sum of latencies in microseconds (lifetime), for the mean.</summary>
    private long _sumMicros;

    /// <summary>UTC ticks of the most recent call.</summary>
    private long _lastTicks;

    /// <summary>Schema-qualified object name last seen for this route.</summary>
    private string? _objectName;

    /// <summary>Schema-qualified object name last seen for this route.</summary>
    public string? ObjectName
    {
        get => Volatile.Read(ref _objectName);
        set => Volatile.Write(ref _objectName, value);
    }

    /// <summary>Records one call into the counters, histogram and time ring.</summary>
    /// <param name="second">Unix second of the call.</param>
    /// <param name="durationMs">Call duration in milliseconds.</param>
    /// <param name="isError">Whether the call failed.</param>
    /// <param name="cacheHit">Whether the response was served from cache.</param>
    /// <param name="nowTicks">UTC ticks of the call, for the last-called timestamp.</param>
    /// <param name="bindingDurationMs">Parameter-binding duration in milliseconds.</param>
    /// <param name="cacheLookupDurationMs">Cache-lookup duration in milliseconds.</param>
    /// <param name="dbDurationMs">Database-execution duration in milliseconds.</param>
    /// <param name="streamingDurationMs">Response-streaming duration in milliseconds.</param>
    public void Record(
        long second,
        double durationMs,
        bool isError,
        bool cacheHit,
        long nowTicks,
        double bindingDurationMs = 0,
        double cacheLookupDurationMs = 0,
        double dbDurationMs = 0,
        double streamingDurationMs = 0)
    {
        if (isError)
        {
            Interlocked.Increment(ref _errors);
        }

        if (cacheHit)
        {
            Interlocked.Increment(ref _cacheHits);
        }

        Interlocked.Add(ref _sumMicros, (long)(durationMs * 1000));
        Interlocked.Exchange(ref _lastTicks, nowTicks);
        // The latency histogram now lives in the per-second ring, so percentiles read a decaying window
        // (expired seconds fall out) under the ring's lock rather than a lifetime cumulative view.
        var bucketIdx = BucketIndex(durationMs);
        _ring.Add(second, durationMs, isError, cacheHit, bucketIdx, dbDurationMs);

        // Record sub-phase durations into their own rings for independent percentile computation.
        // Skip zero values for binding and cache (which may legitimately be zero on cache hits).
        if (bindingDurationMs > 0)
        {
            _bindingRing.Add(second, bindingDurationMs, false, false, BucketIndex(bindingDurationMs));
        }

        if (cacheLookupDurationMs > 0)
        {
            _cacheRing.Add(second, cacheLookupDurationMs, false, false, BucketIndex(cacheLookupDurationMs));
        }

        if (dbDurationMs > 0)
        {
            _dbRing.Add(second, dbDurationMs, false, false, BucketIndex(dbDurationMs));
        }

        if (streamingDurationMs > 0)
        {
            _streamingRing.Add(second, streamingDurationMs, false, false, BucketIndex(streamingDurationMs));
        }

        Interlocked.Increment(ref _count);
    }

    /// <summary>Produces an immutable metrics snapshot for the route.</summary>
    /// <param name="route">The route these stats belong to.</param>
    /// <param name="nowSecond">Current unix second, used to window the latency percentiles.</param>
    /// <returns>The current per-endpoint metrics.</returns>
    public EndpointMetrics Snapshot(string route, long nowSecond)
    {
        var count = Interlocked.Read(ref _count);
        var errors = Interlocked.Read(ref _errors);
        var cacheHits = Interlocked.Read(ref _cacheHits);
        var sumMicros = Interlocked.Read(ref _sumMicros);
        var lastTicks = Interlocked.Read(ref _lastTicks);

        // One windowed histogram drives all three percentiles, so they are self-consistent and the
        // ring is only summed once per snapshot.
        var histogram = _ring.WindowHistogram(nowSecond, PercentileWindowSeconds);

        // Compute sub-phase percentiles from their dedicated rings.
        var bindingHistogram = _bindingRing.WindowHistogram(nowSecond, PercentileWindowSeconds);
        var cacheHistogram = _cacheRing.WindowHistogram(nowSecond, PercentileWindowSeconds);
        var dbHistogram = _dbRing.WindowHistogram(nowSecond, PercentileWindowSeconds);
        var streamingHistogram = _streamingRing.WindowHistogram(nowSecond, PercentileWindowSeconds);

        return new EndpointMetrics
        {
            Route = route,
            ObjectName = ObjectName,
            Count = count,
            Errors = errors,
            ErrorRate = count == 0 ? 0 : (double)errors / count,
            P50LatencyMs = Percentile(0.50, histogram),
            P95LatencyMs = Percentile(0.95, histogram),
            P99LatencyMs = Percentile(0.99, histogram),
            AvgLatencyMs = count == 0 ? 0 : sumMicros / 1000.0 / count,
            CacheHitRatio = count == 0 ? 0 : (double)cacheHits / count,
            LastCalledAt = lastTicks == 0 ? null : new DateTimeOffset(lastTicks, TimeSpan.Zero),
            BindingP50Ms = Percentile(0.50, bindingHistogram),
            BindingP95Ms = Percentile(0.95, bindingHistogram),
            CacheLookupP50Ms = Percentile(0.50, cacheHistogram),
            CacheLookupP95Ms = Percentile(0.95, cacheHistogram),
            DbP50Ms = Percentile(0.50, dbHistogram),
            DbP95Ms = Percentile(0.95, dbHistogram),
            StreamingP50Ms = Percentile(0.50, streamingHistogram),
            StreamingP95Ms = Percentile(0.95, streamingHistogram),
        };
    }

    /// <summary>Returns a bucketed time series for the route.</summary>
    /// <param name="nowSecond">Current unix second.</param>
    /// <param name="metric">Metric name (see <see cref="TimeRing.Snapshot"/>).</param>
    /// <param name="windowSeconds">Window length in seconds.</param>
    /// <param name="bucketSeconds">Bucket width in seconds.</param>
    /// <returns>Ordered points, oldest first.</returns>
    public IReadOnlyList<MetricPoint> TimeSeries(long nowSecond, string metric, int windowSeconds, int bucketSeconds) =>
        _ring.Snapshot(nowSecond, metric, windowSeconds, bucketSeconds);

    /// <summary>Aggregates the trailing window (for overview req/s, error rate and cache ratio).</summary>
    /// <param name="nowSecond">Current unix second.</param>
    /// <param name="windowSeconds">Window length in seconds.</param>
    /// <returns>Count, errors, cache hits and average latency over the window.</returns>
    public (long Count, long Errors, long CacheHits, double AverageMs) Window(long nowSecond, int windowSeconds) =>
        _ring.Window(nowSecond, windowSeconds);

    /// <summary>Computes a decaying latency percentile over the trailing percentile window.</summary>
    /// <param name="percentile">Percentile in range 0..1 (e.g. 0.95).</param>
    /// <param name="nowSecond">Current unix second, defining the trailing window.</param>
    /// <returns>Approximate latency in milliseconds.</returns>
    public double Percentile(double percentile, long nowSecond) =>
        Percentile(percentile, _ring.WindowHistogram(nowSecond, PercentileWindowSeconds));

    /// <summary>Computes a latency percentile from a windowed histogram of bucket counts.</summary>
    /// <param name="percentile">Percentile in range 0..1.</param>
    /// <param name="histogram">Per-bucket sample counts over the window (overflow last).</param>
    /// <returns>Approximate latency in milliseconds (the upper bound of the containing bucket).</returns>
    private static double Percentile(double percentile, long[] histogram)
    {
        long total = 0;
        foreach (var bucket in histogram)
        {
            total += bucket;
        }

        if (total == 0)
        {
            return 0;
        }

        var target = (long)Math.Ceiling(percentile * total);
        long cumulative = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= target)
            {
                return i < Boundaries.Length ? Boundaries[i] : Boundaries[^1];
            }
        }

        return Boundaries[^1];
    }

    /// <summary>Finds the histogram bucket index for a duration.</summary>
    /// <param name="durationMs">Duration in milliseconds.</param>
    /// <returns>Bucket index, with the last index used for the overflow bucket.</returns>
    private static int BucketIndex(double durationMs)
    {
        for (var i = 0; i < Boundaries.Length; i++)
        {
            if (durationMs <= Boundaries[i])
            {
                return i;
            }
        }

        return Boundaries.Length;
    }
}
