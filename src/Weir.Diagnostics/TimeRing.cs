using Weir.Contracts;

namespace Weir.Diagnostics;

/// <summary>
/// A fixed-capacity ring of one-second aggregate buckets used to build rolling time series. Each
/// slot holds the counters for a single wall-clock second; slots are recycled as time advances.
/// </summary>
internal sealed class TimeRing
{
    /// <summary>Number of one-second slots (also the maximum representable window).</summary>
    private readonly int _capacity;

    /// <summary>The unix second currently occupying each slot; -1 for an empty slot.</summary>
    private readonly long[] _second;

    /// <summary>Call count per slot.</summary>
    private readonly long[] _count;

    /// <summary>Error count per slot.</summary>
    private readonly long[] _errors;

    /// <summary>Cache-hit count per slot.</summary>
    private readonly long[] _cacheHits;

    /// <summary>Sum of latencies (microseconds) per slot.</summary>
    private readonly long[] _sumMicros;

    /// <summary>Sum of db durations (microseconds) per slot.</summary>
    private readonly long[] _sumDbMicros;

    /// <summary>Latency histogram bucket counts per slot, flattened as <c>slot * bucketCount + bucket</c>.</summary>
    private readonly long[] _histogram;

    /// <summary>Number of histogram buckets per slot (including the overflow bucket).</summary>
    private readonly int _bucketCount;

    /// <summary>Guards all slot access.</summary>
    private readonly Lock _lock = new();

    /// <summary>Creates a ring covering <paramref name="capacitySeconds"/> seconds.</summary>
    /// <param name="capacitySeconds">Number of one-second slots (the maximum window length).</param>
    /// <param name="bucketCount">Number of latency histogram buckets carried per slot.</param>
    public TimeRing(int capacitySeconds, int bucketCount)
    {
        _capacity = capacitySeconds;
        _bucketCount = bucketCount;
        _second = new long[capacitySeconds];
        _count = new long[capacitySeconds];
        _errors = new long[capacitySeconds];
        _cacheHits = new long[capacitySeconds];
        _sumMicros = new long[capacitySeconds];
        _sumDbMicros = new long[capacitySeconds];
        _histogram = new long[capacitySeconds * bucketCount];
        Array.Fill(_second, -1L);
    }

    /// <summary>Records one call into the slot for <paramref name="second"/>.</summary>
    /// <param name="second">Unix time in whole seconds.</param>
    /// <param name="durationMs">Call duration in milliseconds.</param>
    /// <param name="isError">Whether the call failed.</param>
    /// <param name="cacheHit">Whether the response was served from cache.</param>
    /// <param name="bucketIndex">Latency histogram bucket the duration falls in.</param>
    /// <param name="dbDurationMs">Database execution duration in milliseconds.</param>
    public void Add(long second, double durationMs, bool isError, bool cacheHit, int bucketIndex, double dbDurationMs = 0)
    {
        var slot = Slot(second);
        lock (_lock)
        {
            if (_second[slot] != second)
            {
                _second[slot] = second;
                _count[slot] = 0;
                _errors[slot] = 0;
                _cacheHits[slot] = 0;
                _sumMicros[slot] = 0;
                _sumDbMicros[slot] = 0;
                Array.Clear(_histogram, slot * _bucketCount, _bucketCount);
            }

            _count[slot]++;
            if (isError)
            {
                _errors[slot]++;
            }

            if (cacheHit)
            {
                _cacheHits[slot]++;
            }

            _sumMicros[slot] += (long)(durationMs * 1000);
            _sumDbMicros[slot] += (long)(dbDurationMs * 1000);
            _histogram[(slot * _bucketCount) + bucketIndex]++;
        }
    }

    /// <summary>
    /// Sums the latency histogram over the trailing window into a fresh bucket array. Only slots whose
    /// stored second still falls in the window contribute, so counts from expired seconds decay out.
    /// </summary>
    /// <param name="nowSecond">Current unix second.</param>
    /// <param name="windowSeconds">Length of the window to cover.</param>
    /// <returns>The summed per-bucket counts over the window.</returns>
    public long[] WindowHistogram(long nowSecond, int windowSeconds)
    {
        windowSeconds = Math.Clamp(windowSeconds, 1, _capacity);
        var histogram = new long[_bucketCount];
        var start = nowSecond - windowSeconds + 1;

        lock (_lock)
        {
            for (var s = start; s <= nowSecond; s++)
            {
                var slot = Slot(s);
                if (_second[slot] != s)
                {
                    continue;
                }

                var offset = slot * _bucketCount;
                for (var b = 0; b < _bucketCount; b++)
                {
                    histogram[b] += _histogram[offset + b];
                }
            }
        }

        return histogram;
    }

    /// <summary>Builds a bucketed time series for a metric over the trailing window.</summary>
    /// <param name="nowSecond">Current unix second.</param>
    /// <param name="metric">One of "requests", "errors", "latency", "cacheHitRatio".</param>
    /// <param name="windowSeconds">Length of the window to cover.</param>
    /// <param name="bucketSeconds">Width of each output bucket.</param>
    /// <returns>Ordered points, oldest first.</returns>
    public IReadOnlyList<MetricPoint> Snapshot(long nowSecond, string metric, int windowSeconds, int bucketSeconds)
    {
        // Clamp to capacity: a window longer than the ring would wrap and alias one physical slot to two
        // different seconds, silently double-counting. bucketSeconds must be at least 1 to advance.
        windowSeconds = Math.Clamp(windowSeconds, 1, _capacity);
        bucketSeconds = Math.Max(1, bucketSeconds);
        var points = new List<MetricPoint>();
        var start = nowSecond - windowSeconds + 1;

        lock (_lock)
        {
            for (var bucketStart = start; bucketStart <= nowSecond; bucketStart += bucketSeconds)
            {
                long count = 0, errors = 0, cacheHits = 0, sumMicros = 0, sumDbMicros = 0;
                for (var s = bucketStart; s < bucketStart + bucketSeconds && s <= nowSecond; s++)
                {
                    var slot = Slot(s);
                    if (_second[slot] == s)
                    {
                        count += _count[slot];
                        errors += _errors[slot];
                        cacheHits += _cacheHits[slot];
                        sumMicros += _sumMicros[slot];
                        sumDbMicros += _sumDbMicros[slot];
                    }
                }

                var value = metric switch
                {
                    "requests" => count / (double)bucketSeconds,
                    "errors" => errors / (double)bucketSeconds,
                    "latency" => count == 0 ? 0 : sumMicros / 1000.0 / count,
                    "cacheHitRatio" => count == 0 ? 0 : (double)cacheHits / count,
                    "dbDuration" => count == 0 ? 0 : sumDbMicros / 1000.0 / count,
                    _ => count,
                };

                points.Add(new MetricPoint { Timestamp = DateTimeOffset.FromUnixTimeSeconds(bucketStart), Value = value });
            }
        }

        return points;
    }

    /// <summary>Aggregates the trailing window into totals used by the overview.</summary>
    /// <param name="nowSecond">Current unix second.</param>
    /// <param name="windowSeconds">Length of the window to cover.</param>
    /// <returns>Count, errors, cache hits and average latency (ms) over the window.</returns>
    public (long Count, long Errors, long CacheHits, double AverageMs) Window(long nowSecond, int windowSeconds)
    {
        // Clamp to capacity so a window longer than the ring cannot alias slots and double-count.
        windowSeconds = Math.Clamp(windowSeconds, 1, _capacity);
        long count = 0, errors = 0, cacheHits = 0, sumMicros = 0;
        var start = nowSecond - windowSeconds + 1;

        lock (_lock)
        {
            for (var s = start; s <= nowSecond; s++)
            {
                var slot = Slot(s);
                if (_second[slot] == s)
                {
                    count += _count[slot];
                    errors += _errors[slot];
                    cacheHits += _cacheHits[slot];
                    sumMicros += _sumMicros[slot];
                }
            }
        }

        return (count, errors, cacheHits, count == 0 ? 0 : sumMicros / 1000.0 / count);
    }

    /// <summary>Maps a unix second to its ring slot index.</summary>
    /// <param name="second">Unix second.</param>
    /// <returns>Slot index in range [0, capacity).</returns>
    private int Slot(long second) => (int)(((second % _capacity) + _capacity) % _capacity);
}
