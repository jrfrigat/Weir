using Weir.Contracts;

namespace Weir.Diagnostics;

/// <summary>
/// A fixed-capacity ring of one-second aggregate buckets used to build rolling time series. Each
/// slot holds the counters for a single wall-clock second; slots are recycled as time advances.
/// </summary>
/// <remarks>
/// <para>
/// The write path is the data-plane hot path, so <see cref="Add"/> is lock-free in the steady state:
/// once a slot already carries the second being recorded, recording is nothing but interlocked
/// increments on fixed array elements. Many cores can record concurrently without serializing on a
/// process-wide lock, which matters because a single ring (the service-wide one, and the per-route
/// one of a hot route) is written by every request.
/// </para>
/// <para>
/// The lock is taken on exactly two paths, both cold. First, rolling a slot over to a new second,
/// which happens at most once per slot per 300 seconds - serializing roll-overs against each other
/// is what stops a slot from being cleared twice. Second, the read path (<see cref="Snapshot"/>,
/// <see cref="Window"/>, <see cref="WindowHistogram"/>), called only by the admin API - serializing
/// readers against roll-overs is what stops a reader from seeing a half-cleared slot.
/// </para>
/// <para>
/// Concurrent recorders are not serialized against readers, so a reader can observe a slot mid-record
/// (for example the call counted but its latency not yet added). This skews a windowed average by at
/// most the handful of calls in flight at that instant and cannot lose or duplicate a sample; it is an
/// accepted trade for an uncontended hot path. All counters are read with <c>Volatile.Read</c>, which
/// is an atomic 64-bit load even on a 32-bit runtime, so an individual counter can never be observed
/// torn.
/// </para>
/// </remarks>
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

    /// <summary>Serializes slot roll-overs against each other and against readers. Not taken to record.</summary>
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
    /// <remarks>
    /// Lock-free unless the slot has to be rolled over first. The stamp read is an acquire, and
    /// <see cref="TryRoll"/> publishes the stamp with a release write only after clearing the slot, so
    /// observing the stamp guarantees observing the cleared counters: no increment can ever be issued
    /// against a slot that is about to be, or is being, cleared for the same second.
    /// </remarks>
    public void Add(long second, double durationMs, bool isError, bool cacheHit, int bucketIndex, double dbDurationMs = 0)
    {
        var slot = Slot(second);

        if (Volatile.Read(ref _second[slot]) != second && !TryRoll(slot, second))
        {
            return;
        }

        Interlocked.Increment(ref _count[slot]);
        if (isError)
        {
            Interlocked.Increment(ref _errors[slot]);
        }

        if (cacheHit)
        {
            Interlocked.Increment(ref _cacheHits[slot]);
        }

        Interlocked.Add(ref _sumMicros[slot], (long)(durationMs * 1000));
        Interlocked.Add(ref _sumDbMicros[slot], (long)(dbDurationMs * 1000));
        Interlocked.Increment(ref _histogram[(slot * _bucketCount) + bucketIndex]);
    }

    /// <summary>Takes ownership of a slot for a new second, clearing the previous second's counters.</summary>
    /// <param name="slot">Slot index that <paramref name="second"/> maps to.</param>
    /// <param name="second">Unix second the caller wants to record into.</param>
    /// <returns>
    /// True when the slot represents <paramref name="second"/> on return and the caller may record;
    /// false when a newer second already owns the slot and the caller's sample must be dropped.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Runs under the lock so that only one thread can ever be clearing a given slot, and so that no
    /// reader can be part-way through summing it. Two racing recorders for the same new second both
    /// arrive here; the first clears and publishes, the second sees the stamp already updated and just
    /// returns true. The clear therefore happens exactly once per second per slot.
    /// </para>
    /// <para>
    /// A slot maps every second congruent to it modulo the capacity, so a roll-over is always a jump of
    /// at least a full ring (300 seconds), never an adjacent-second boundary. The only recorder that can
    /// still be mid-<see cref="Add"/> against the slot being cleared is therefore one that read the stamp
    /// and then stalled for over 300 seconds. <see cref="Clear"/> handles even that case without
    /// corruption, and a stale recorder cannot rewind the slot because of the ordering check below.
    /// </para>
    /// </remarks>
    private bool TryRoll(int slot, long second)
    {
        lock (_lock)
        {
            var current = _second[slot];
            if (current == second)
            {
                // Another thread rolled the slot to our second while we waited for the lock.
                return true;
            }

            if (current > second)
            {
                // A newer second already owns this slot, so our sample is a straggler: it arrived more
                // than a full ring late, or the wall clock stepped backwards. Drop it. Rolling the slot
                // back would discard a whole second of live data that readers can still see, which is a
                // far worse outcome than losing one late sample that no reader would ever have matched.
                return false;
            }

            Clear(_count, slot);
            Clear(_errors, slot);
            Clear(_cacheHits, slot);
            Clear(_sumMicros, slot);
            Clear(_sumDbMicros, slot);

            var offset = slot * _bucketCount;
            for (var b = 0; b < _bucketCount; b++)
            {
                Clear(_histogram, offset + b);
            }

            // Publish last, with release semantics: a recorder that observes this stamp is guaranteed to
            // observe the clears above, so it cannot have its increment wiped by this roll-over.
            Volatile.Write(ref _second[slot], second);
            return true;
        }
    }

    /// <summary>Zeroes one counter cell without losing or corrupting a concurrent increment.</summary>
    /// <param name="counters">Counter array to clear a cell of.</param>
    /// <param name="index">Index of the cell.</param>
    /// <remarks>
    /// Subtracts what was read rather than storing zero. A plain store racing an interlocked increment
    /// could be swallowed by that increment's read-modify-write and leave the whole previous second's
    /// total in place; subtracting is itself a read-modify-write, so the two compose. An increment that
    /// lands between the read and the subtraction survives into the new second instead of being lost.
    /// The result can never go negative: only the lock holder ever subtracts, and it never subtracts
    /// more than it observed.
    /// </remarks>
    private static void Clear(long[] counters, int index)
    {
        var stale = Volatile.Read(ref counters[index]);
        if (stale != 0)
        {
            Interlocked.Add(ref counters[index], -stale);
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
                if (Volatile.Read(ref _second[slot]) != s)
                {
                    continue;
                }

                var offset = slot * _bucketCount;
                for (var b = 0; b < _bucketCount; b++)
                {
                    histogram[b] += Volatile.Read(ref _histogram[offset + b]);
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
                    if (Volatile.Read(ref _second[slot]) == s)
                    {
                        count += Volatile.Read(ref _count[slot]);
                        errors += Volatile.Read(ref _errors[slot]);
                        cacheHits += Volatile.Read(ref _cacheHits[slot]);
                        sumMicros += Volatile.Read(ref _sumMicros[slot]);
                        sumDbMicros += Volatile.Read(ref _sumDbMicros[slot]);
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
                if (Volatile.Read(ref _second[slot]) == s)
                {
                    count += Volatile.Read(ref _count[slot]);
                    errors += Volatile.Read(ref _errors[slot]);
                    cacheHits += Volatile.Read(ref _cacheHits[slot]);
                    sumMicros += Volatile.Read(ref _sumMicros[slot]);
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
