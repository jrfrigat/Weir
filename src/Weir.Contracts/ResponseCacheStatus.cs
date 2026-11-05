namespace Weir.Contracts;

/// <summary>Secret-free cache state; byte counts describe this process rather than the Redis server.</summary>
public sealed record ResponseCacheStatus
{
    /// <summary>The configured backend, or Custom when its diagnostics are unknown.</summary>
    public string Provider { get; init; } = "Custom";
    /// <summary>The non-secret installation namespace, if a remote backend is used.</summary>
    public string? Namespace { get; init; }
    /// <summary>The configured Redis database, or null for other backends.</summary>
    public int? Database { get; init; }
    /// <summary>Restart-required driver timeout, or null for other backends.</summary>
    public int? OperationTimeoutMilliseconds { get; init; }
    /// <summary>Restart-required snapshot lifetime ceiling, or null for other backends.</summary>
    public int? MaximumTtlSeconds { get; init; }
    /// <summary>Whether the backend is available, or null when unknown.</summary>
    public bool? Available { get; init; }
    /// <summary>Locally stored payload bytes, excluding active borrowed responses; null when unknown.</summary>
    public long? LocalPayloadBytes { get; init; }
    /// <summary>Locally stored entries; null when unknown.</summary>
    public int? LocalEntryCount { get; init; }
    /// <summary>Configured local payload budget, or null for a remote/custom store.</summary>
    public long? LocalPayloadBudgetBytes { get; init; }
    /// <summary>Configured maximum payload per entry; zero explicitly disables the policy bound.</summary>
    public long MaxEntryBytes { get; init; }
    /// <summary>Configured local entry-count bound, or null for a remote/custom store.</summary>
    public int? LocalMaxEntries { get; init; }
    /// <summary>Failed backend lookups since startup.</summary>
    public long LookupFailures { get; init; }
    /// <summary>Failed backend stores since startup.</summary>
    public long StoreFailures { get; init; }
    /// <summary>Store deadlines exceeded, including operations still holding bounded ownership.</summary>
    public long StoreTimeouts { get; init; }
    /// <summary>Invalidations whose completion could not be confirmed.</summary>
    public long InvalidationFailures { get; init; }
    /// <summary>Rejected or skipped stores since startup.</summary>
    public long SkippedStores { get; init; }
    /// <summary>Invalid binary entries detected since startup.</summary>
    public long CorruptEntries { get; init; }
    /// <summary>Provider writes currently retained locally.</summary>
    public int PendingWrites { get; init; }
    /// <summary>Payload bytes retained by provider writes currently in flight.</summary>
    public long PendingWriteBytes { get; init; }
}
