namespace Weir.Contracts;

/// <summary>
/// Runtime-tunable system settings, editable from the admin panel and persisted in the control plane.
/// These overlay the <c>appsettings.json</c> seed values and, unless noted, take effect without a
/// restart. Fields that require a restart carry a note in their documentation and are surfaced
/// read-only in the admin UI.
/// </summary>
public sealed record WeirSystemSettings
{
    /// <summary>
    /// Maximum number of rows (across all result sets) streamed in one data-plane response; the
    /// response is truncated (and not cached) beyond it. Zero means unlimited.
    /// </summary>
    public int MaxRows { get; init; } = 100_000;

    /// <summary>
    /// Overall data-plane request timeout in seconds; on expiry the caller receives HTTP 504. Zero
    /// means no gateway-level timeout (the database command timeout still applies).
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>Maximum rows accepted for a single table-valued parameter. Zero means unlimited.</summary>
    public int MaxTvpRows { get; init; } = 100_000;

    /// <summary>
    /// Default per-minute request limit for an API key that sets no limit of its own. Zero or less
    /// leaves such keys unthrottled.
    /// </summary>
    public int DefaultApiKeyRateLimitPerMinute { get; init; }

    /// <summary>
    /// How many days of audit history to keep; entries older than this are pruned by a background
    /// service. Zero or less keeps audit history forever (no pruning).
    /// </summary>
    public int AuditRetentionDays { get; init; }

    /// <summary>
    /// Maximum number of data-plane executions allowed to run concurrently against a single data
    /// connection (a bulkhead). A request that arrives while the connection is at its limit is rejected
    /// with HTTP 503 rather than piling onto a saturated database. Zero means unlimited.
    /// </summary>
    public int MaxConcurrentRequestsPerConnection { get; init; }

    /// <summary>
    /// Number of consecutive data-connection failures that trips the circuit breaker for that
    /// connection; while open, requests are short-circuited with HTTP 503 until the reset window
    /// elapses and a probe succeeds. Zero disables the breaker.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; init; }

    /// <summary>
    /// How long (in seconds) a tripped circuit breaker stays open before it allows a probe request
    /// through. Applies only when <see cref="CircuitBreakerFailureThreshold"/> is greater than zero.
    /// </summary>
    public int CircuitBreakerResetSeconds { get; init; } = 30;

    /// <summary>
    /// Master switch for the data-plane request log (the per-call history shown in the admin panel).
    /// When off, no request-log rows are written regardless of per-endpoint settings. Basic call
    /// metadata is recorded when on; parameter and result capture remain per-endpoint opt-ins.
    /// </summary>
    public bool RequestLogEnabled { get; init; } = true;

    /// <summary>
    /// Default "slow" threshold, as a percentage above an endpoint's rolling average duration. A logged
    /// request is flagged slow when its duration exceeds the endpoint's recent average by at least this
    /// percentage (for example 20 means "20% slower than usual"). An endpoint may override it. Zero or
    /// less disables slow flagging.
    /// </summary>
    public int SlowRequestThresholdPercent { get; init; } = 20;

    /// <summary>
    /// How many days of request-log history to keep; rows older than this are pruned by a background
    /// service. Zero or less keeps history forever (not recommended - the log can grow quickly).
    /// </summary>
    public int RequestLogRetentionDays { get; init; } = 7;

    /// <summary>
    /// Total size, in bytes, that cached response payloads may occupy in memory. Once the cache is
    /// full, the least recently used entries are evicted to make room for a new one. Zero or less
    /// means unlimited: entries then leave only when their TTL expires, so an endpoint with a
    /// high-cardinality <c>VaryByParameters</c> can grow the cache until the process runs out of
    /// memory. Treat unlimited as a deliberate opt-out, not a default.
    /// <para>
    /// Defaults to 128 MiB: large enough to hold a useful working set (roughly 13,000 responses of
    /// 10 KB), a modest fraction of a typical container's memory, and comfortably larger than any
    /// single response the default <see cref="MaxRows"/> cap can produce. A payload larger than this
    /// limit is never cached, so one oversized response cannot flush the whole cache.
    /// </para>
    /// <para>
    /// Changing this rebuilds the response cache, which discards the entries currently held; the next
    /// calls to the affected endpoints repopulate it.
    /// </para>
    /// </summary>
    public long ResponseCacheMaxBytes { get; init; } = 134_217_728;

    /// <summary>
    /// How response bodies reach callers by default. An endpoint can override it (see
    /// <c>DeliveryPolicy.Mode</c>), and one that caches or captures its result buffers regardless -
    /// it needs the whole body before it can store or log anything.
    /// <para>
    /// <see cref="ResponseDeliveryMode.Auto"/> buffers where the endpoint declares a small result and
    /// streams the row-returning ones, which is the right split for almost every system.
    /// </para>
    /// </summary>
    public ResponseDeliveryMode ResponseDeliveryMode { get; init; } = ResponseDeliveryMode.Auto;

    /// <summary>
    /// How many bytes may sit unflushed in the JSON writer before a streaming response pushes them to
    /// the client; an endpoint can override it (see <c>DeliveryPolicy.FlushBytes</c>).
    /// <para>
    /// The default is deliberate rather than round: comfortably above one typical row, so narrow rows
    /// batch instead of costing a write each, and comfortably below the 85 KB large-object threshold,
    /// so the writer's buffer stays a cheap reusable array. Values above that threshold undo the point
    /// of flushing at all; very small ones trade the whole gain for a write per row.
    /// </para>
    /// </summary>
    public int ResponseFlushBytes { get; init; } = 32_768;
}

/// <summary>
/// A view of the runtime settings plus read-only, restart-required values, returned by the admin
/// settings API so the UI can render both the editable form and the informational fields.
/// </summary>
public sealed record WeirSystemSettingsView
{
    /// <summary>The current runtime-tunable settings.</summary>
    public required WeirSystemSettings Settings { get; init; }

    /// <summary>
    /// Maximum data-plane request body size in bytes (enforced by the host). Read-only here: changing
    /// it requires a restart because it is applied to the web server at startup.
    /// </summary>
    public long MaxRequestBodyBytes { get; init; }

    /// <summary>The file-logging configuration, shown read-only (changing it requires a restart).</summary>
    public WeirLoggingInfo? Logging { get; init; }
}

/// <summary>Read-only view of the file-logging configuration, surfaced in the admin panel.</summary>
public sealed record WeirLoggingInfo
{
    /// <summary>Whether logs are written to a rolling file.</summary>
    public bool FileEnabled { get; init; }

    /// <summary>Directory the log files are written to.</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>Rolling interval (for example <c>Day</c>).</summary>
    public string RollingInterval { get; init; } = string.Empty;

    /// <summary>Maximum number of retained rolling files, if capped.</summary>
    public int? RetainedFileCountLimit { get; init; }

    /// <summary>Maximum age of retained files in days, if capped.</summary>
    public int? RetainedFileTimeLimitDays { get; init; }

    /// <summary>Log file format (<c>Text</c> or <c>Json</c>).</summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>Minimum level written to the log.</summary>
    public string MinimumLevel { get; init; } = string.Empty;
}
