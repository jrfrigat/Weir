namespace Weir.Contracts;

/// <summary>One observed gauge sample; null represents unavailable data rather than zero.</summary>
/// <param name="Timestamp">UTC observation time aligned to one second.</param>
/// <param name="ProcessWorkingSetBytes">This process's resident working set, not managed-heap or machine RAM.</param>
/// <param name="LocalCachePayloadBytes">Locally stored response bytes, excluding metadata and active buffers.</param>
public readonly record struct MemoryPoint(DateTimeOffset Timestamp, long? ProcessWorkingSetBytes, long? LocalCachePayloadBytes)
{
    /// <summary>Observed environment capacity in bytes, or null when unknown; this is not free memory.</summary>
    public long? TotalMemoryBytes { get; init; }
}

/// <summary>Bounded per-instance memory history and metadata from the same background observation.</summary>
public sealed record MemorySnapshot
{
    /// <summary>The fixed retained observation window.</summary>
    public const int HistorySeconds = 300;
    /// <summary>The fixed background observation interval.</summary>
    public const int SampleIntervalSeconds = 1;
    /// <summary>Opaque sampling-instance identity, replaced on process restart.</summary>
    public string InstanceId { get; init; } = string.Empty;
    /// <summary>When this sampling instance was created.</summary>
    public DateTimeOffset StartedAt { get; init; }
    /// <summary>The latest actual observation, or null before the first sample.</summary>
    public DateTimeOffset? SampledAt { get; init; }
    /// <summary>History resets after backwards clock movement.</summary>
    public long HistoryResetCount { get; init; }
    /// <summary>Ordered samples and explicit gaps, at most 300 points.</summary>
    public IReadOnlyList<MemoryPoint> Points { get; init; } = [];
    /// <summary>Secret-free cache metadata captured with the latest observation.</summary>
    public ResponseCacheStatus Cache { get; init; } = new();
    /// <summary>The source of the latest total-memory observation.</summary>
    public MemoryCapacitySource CapacitySource { get; init; }
    /// <summary>Actual active response-buffer reservations, or null before observation.</summary>
    public long? ActiveResponseBufferBytes { get; init; }
    /// <summary>Configured active-buffer budget; zero disables its bound, null means unobserved.</summary>
    public long? ActiveResponseBufferBudgetBytes { get; init; }
}
