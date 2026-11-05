using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Diagnostics;

/// <summary>Stores one-second memory gauges in a fixed-size ring, independently of request telemetry.</summary>
public sealed class MemoryMetrics : IMemoryMetrics
{
    /// <summary>The shared observation clock.</summary>
    private readonly TimeProvider _clock;
    /// <summary>Serializes one background writer with snapshot readers.</summary>
    private readonly Lock _gate = new();
    /// <summary>Fixed slots; no request or endpoint cardinality can grow this storage.</summary>
    private readonly MemoryPoint?[] _slots = new MemoryPoint?[MemorySnapshot.HistorySeconds];
    /// <summary>Identity of this sampling instance.</summary>
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    /// <summary>Sampling-instance creation time on the shared clock.</summary>
    private readonly DateTimeOffset _startedAt;
    /// <summary>The earliest second in the current uninterrupted clock history.</summary>
    private long _firstSecond;
    /// <summary>The latest actual observation second, absent before sampling.</summary>
    private long? _latestSecond;
    /// <summary>Number of backwards-clock history resets.</summary>
    private long _resets;
    /// <summary>Cache state captured at the latest observation.</summary>
    private ResponseCacheStatus _cache = new();
    /// <summary>Active reservation bytes captured with the latest observation.</summary>
    private long? _activeBytes;
    /// <summary>Active reservation limit captured with the latest observation.</summary>
    private long? _activeBudget;
    /// <summary>The scope of the last environment-capacity observation.</summary>
    private MemoryCapacitySource _capacitySource;

    /// <summary>Creates a bounded memory history on the application's clock.</summary>
    /// <param name="clock">Shared server clock; defaults to the system clock.</param>
    public MemoryMetrics(TimeProvider? clock = null) { _clock = clock ?? TimeProvider.System; _startedAt = _clock.GetUtcNow(); }

    /// <summary>Records one background observation without allocating per-row or per-request state.</summary>
    /// <param name="workingSetBytes">Resident process bytes, null when unavailable.</param>
    /// <param name="cache">Secret-free provider and retained-payload state.</param>
    /// <param name="activeBytes">Active buffer reservations.</param>
    /// <param name="activeBudgetBytes">Active reservation bound; zero explicitly disables it.</param>
    public void RecordSample(long? workingSetBytes, ResponseCacheStatus cache, long activeBytes, long activeBudgetBytes)
        => RecordSample(workingSetBytes, cache, activeBytes, activeBudgetBytes, default);

    /// <summary>Records process, cache and environment capacity on the same observation clock.</summary>
    /// <param name="workingSetBytes">Resident process bytes, or null.</param>
    /// <param name="cache">Secret-free cache state.</param>
    /// <param name="activeBytes">Active buffer reservations.</param>
    /// <param name="activeBudgetBytes">Reservation budget; zero disables its bound.</param>
    /// <param name="capacity">Physical or constrained environment capacity, independently of GC heap limits.</param>
    public void RecordSample(long? workingSetBytes, ResponseCacheStatus cache, long activeBytes, long activeBudgetBytes, MemoryCapacity capacity)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.LocalPayloadBytes is < 0 || cache.LocalEntryCount is < 0)
            cache = cache with
            {
                LocalPayloadBytes = cache.LocalPayloadBytes is >= 0 ? cache.LocalPayloadBytes : null,
                LocalEntryCount = cache.LocalEntryCount is >= 0 ? cache.LocalEntryCount : null
            };
        lock (_gate)
        {
            var second = _clock.GetUtcNow().ToUnixTimeSeconds();
            if (_latestSecond is { } previous && second < previous)
            {
                Array.Clear(_slots);
                _latestSecond = null;
                _resets++;
            }
            if (_latestSecond is null) _firstSecond = second;
            _latestSecond = second;
            _slots[Index(second)] = new MemoryPoint(DateTimeOffset.FromUnixTimeSeconds(second),
                workingSetBytes is >= 0 ? workingSetBytes : null, cache.LocalPayloadBytes)
            { TotalMemoryBytes = capacity.TotalBytes is >= 0 ? capacity.TotalBytes : null };
            _capacitySource = capacity.TotalBytes is >= 0 ? capacity.Source : MemoryCapacitySource.Unknown;
            _cache = cache;
            _activeBytes = activeBytes >= 0 ? activeBytes : null;
            _activeBudget = activeBudgetBytes >= 0 ? activeBudgetBytes : null;
        }
    }

    /// <inheritdoc />
    public MemorySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            MemoryPoint[] points = [];
            if (_latestSecond is { } latest)
            {
                var first = Math.Max(_firstSecond, latest - MemorySnapshot.HistorySeconds + 1);
                points = new MemoryPoint[checked((int)(latest - first + 1))];
                for (var i = 0; i < points.Length; i++)
                {
                    var second = first + i;
                    var point = _slots[Index(second)];
                    points[i] = point is { } observed && observed.Timestamp.ToUnixTimeSeconds() == second
                        ? observed : new MemoryPoint(DateTimeOffset.FromUnixTimeSeconds(second), null, null);
                }
            }
            return new MemorySnapshot
            {
                InstanceId = _instanceId, StartedAt = _startedAt,
                SampledAt = _latestSecond is { } current ? DateTimeOffset.FromUnixTimeSeconds(current) : null,
                HistoryResetCount = _resets, Points = points, Cache = _cache,
                CapacitySource = _capacitySource,
                ActiveResponseBufferBytes = _activeBytes, ActiveResponseBufferBudgetBytes = _activeBudget
            };
        }
    }

    /// <summary>Maps even pre-epoch test clocks to a valid fixed ring slot.</summary>
    /// <param name="second">Unix observation second.</param>
    /// <returns>A slot between zero and the fixed capacity minus one.</returns>
    private static int Index(long second) => (int)((second % MemorySnapshot.HistorySeconds + MemorySnapshot.HistorySeconds) % MemorySnapshot.HistorySeconds);
}
