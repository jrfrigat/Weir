namespace Weir.Core;

/// <summary>
/// Data-plane safety limits, bound from <c>Weir:DataPlane</c>. These protect the service and the
/// database from a single expensive call: they cap how many rows a response may stream and how long
/// a request may run.
/// </summary>
public sealed class WeirDataPlaneOptions
{
    /// <summary>
    /// Maximum number of rows (across all result sets) streamed in one response. When exceeded, the
    /// response is closed early and marked <c>"truncated": true</c> (and is never cached). Zero means
    /// unlimited. Defaults to a safe non-zero cap so an unbounded result set cannot exhaust memory.
    /// </summary>
    public int MaxRows { get; set; } = 100_000;

    /// <summary>
    /// Overall request timeout in seconds. When exceeded, the call is cancelled and the client
    /// receives HTTP 504. Zero means no gateway-level timeout (the database command timeout still
    /// applies). Defaults to a safe non-zero value so a single slow call cannot hang indefinitely.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of rows accepted for a single table-valued parameter. A larger array in the
    /// request body is rejected with HTTP 400 before it is materialized. Zero means unlimited.
    /// </summary>
    public int MaxTvpRows { get; set; } = 100_000;

    /// <summary>
    /// Maximum data-plane request body size in bytes, enforced by the host (Kestrel). A larger body is
    /// rejected with HTTP 413. Zero or less means the server default applies. Applied at startup, so
    /// changing it requires a restart.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 10_485_760;

    /// <summary>
    /// Default per-minute request limit applied to an API key that does not set its own
    /// <c>RateLimitPerMinute</c>. Zero or less leaves such keys unthrottled (the previous behavior).
    /// </summary>
    public int DefaultApiKeyRateLimitPerMinute { get; set; }

    /// <summary>
    /// Maximum number of data-plane executions allowed to run concurrently against a single data
    /// connection (a bulkhead). Requests beyond the limit are rejected with HTTP 503. Zero means
    /// unlimited. Runtime-tunable; seeds <see cref="Weir.Contracts.WeirSystemSettings.MaxConcurrentRequestsPerConnection"/>.
    /// </summary>
    public int MaxConcurrentRequestsPerConnection { get; set; }

    /// <summary>
    /// Consecutive data-connection failures that trip the per-connection circuit breaker. Zero disables
    /// it. Seeds <see cref="Weir.Contracts.WeirSystemSettings.CircuitBreakerFailureThreshold"/>.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; }

    /// <summary>
    /// Seconds a tripped circuit breaker stays open before allowing a probe. Seeds
    /// <see cref="Weir.Contracts.WeirSystemSettings.CircuitBreakerResetSeconds"/>.
    /// </summary>
    public int CircuitBreakerResetSeconds { get; set; } = 30;

    /// <summary>
    /// Total bytes cached response payloads may occupy; the least recently used entries are evicted
    /// once the cache is full. Zero or less means unlimited (an unbounded cache). Defaults to a safe
    /// non-zero cap so a high-cardinality <c>VaryByParameters</c> cannot exhaust memory.
    /// Runtime-tunable; seeds <see cref="Weir.Contracts.WeirSystemSettings.ResponseCacheMaxBytes"/>.
    /// </summary>
    public long ResponseCacheMaxBytes { get; set; } = 134_217_728;
}
