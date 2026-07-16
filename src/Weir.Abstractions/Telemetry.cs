using System.Collections.Concurrent;
using Weir.Contracts;

namespace Weir.Abstractions;

/// <summary>
/// Extension point invoked around every data-plane call. Register any number via DI; the default
/// set includes an OpenTelemetry sink and the in-memory aggregator. Implementations must be cheap
/// and non-throwing - the host isolates observer failures from the request.
/// </summary>
public interface IWeirCallObserver
{
    /// <summary>Called just before the stored procedure is executed.</summary>
    ValueTask OnStartedAsync(WeirCallContext context);

    /// <summary>Called after a successful call, with timing and result stats populated.</summary>
    ValueTask OnCompletedAsync(WeirCallContext context);

    /// <summary>Called when the call fails.</summary>
    ValueTask OnFailedAsync(WeirCallContext context, Exception exception);
}

/// <summary>
/// Mutable per-call telemetry context. Parameter <em>values</em> are intentionally absent - only
/// metadata is carried, so observers cannot accidentally log PII. Owned by the engine for the
/// duration of a single data-plane call. Observers must not mutate it; they may read it.
/// </summary>
public sealed class WeirCallContext
{
    /// <summary>Endpoint route.</summary>
    public required string Route { get; init; }

    /// <summary>The resolved endpoint's id, when the call mapped to a known endpoint.</summary>
    public Guid EndpointId { get; init; }

    /// <summary>HTTP method.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>Target connection name.</summary>
    public string? ConnectionName { get; init; }

    /// <summary>Schema-qualified object name.</summary>
    public string? ObjectName { get; init; }

    /// <summary>Identifying prefix of the calling API key.</summary>
    public string? ApiKeyPrefix { get; init; }

    /// <summary>Start timestamp from <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>.</summary>
    public long StartTimestamp { get; set; }

    /// <summary>Total wall-clock duration, in milliseconds.</summary>
    public double DurationMs { get; set; }

    /// <summary>Time spent binding parameters, in milliseconds.</summary>
    public double BindingDurationMs { get; set; }

    /// <summary>Time spent looking up the cache, in milliseconds.</summary>
    public double CacheLookupDurationMs { get; set; }

    /// <summary>Time spent inside the database call, in milliseconds.</summary>
    public double DbDurationMs { get; set; }

    /// <summary>Time spent streaming the response, in milliseconds.</summary>
    public double StreamingDurationMs { get; set; }

    /// <summary>Number of rows returned across all result sets.</summary>
    public int RowsReturned { get; set; }

    /// <summary>Whether the response was served from cache.</summary>
    public bool CacheHit { get; set; }

    /// <summary>Resulting HTTP status code.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Outcome marker. Use <see cref="OutcomeCodes.Ok"/> / <see cref="OutcomeCodes.Error"/>.</summary>
    public string Outcome { get; set; } = OutcomeCodes.Ok;

    /// <summary>
    /// The classified database-error category for a failed call, or <see cref="DbErrorCategory.None"/>
    /// when the call succeeded or the failure was not a database error. Set by the engine from the
    /// connector's classification and surfaced on the failure metric and span.
    /// </summary>
    public DbErrorCategory DbError { get; set; } = DbErrorCategory.None;

    /// <summary>
    /// Whether the resolved endpoint opts in to request logging (its per-endpoint <c>Logging.Enabled</c>).
    /// Set by the engine; a request-log observer combines it with the global switch to decide whether to
    /// record the call.
    /// </summary>
    public bool LogRequests { get; set; } = true;

    /// <summary>The endpoint's per-endpoint slow-threshold override (percentage), or null to use the global default. Must be non-negative when set.</summary>
    public int? SlowThresholdPercent { get; init; }

    /// <summary>
    /// Captured request parameters as JSON, populated by the engine only when the endpoint opts in
    /// (<c>Logging.LogParameters</c>). Null otherwise, keeping the default PII-safe. Size-capped to
    /// prevent unbounded memory growth from large TVP tokens.
    /// </summary>
    public string? CapturedParameters { get; set; }

    /// <summary>
    /// Captured response body as JSON (size-capped), populated by the engine only when the endpoint opts
    /// in (<c>Logging.LogResult</c>). Null otherwise, keeping the default PII-safe.
    /// </summary>
    public string? CapturedResult { get; set; }

    /// <summary>Error detail for a failed call, set by the engine from the thrown exception.</summary>
    public string? Error { get; set; }

    /// <summary>Backing store for <see cref="Items"/>, created only if an observer asks for it.</summary>
    private IDictionary<string, object?>? _items;

    /// <summary>
    /// Free-form bag for observers to stash correlation data. Thread-safe for concurrent observers.
    /// </summary>
    /// <remarks>
    /// Allocated on first use. A ConcurrentDictionary is not a cheap object - it eagerly builds its
    /// bucket array plus one lock object per core - and on a typical call nothing ever reads this bag,
    /// so a request should not pay for it just by existing.
    /// </remarks>
    public IDictionary<string, object?> Items
    {
        get
        {
            if (_items is null)
            {
                Interlocked.CompareExchange(ref _items, new ConcurrentDictionary<string, object?>(), null);
            }

            return _items;
        }
    }
}

/// <summary>
/// In-process aggregator that keeps rolling per-endpoint statistics powering the admin dashboard,
/// independently of any external telemetry backend.
/// </summary>
public interface IMetricsAggregator
{
    /// <summary>Records a completed (or failed) call.</summary>
    void Record(WeirCallContext context);

    /// <summary>Returns the service-wide overview.</summary>
    MetricsOverview GetOverview();

    /// <summary>Returns per-endpoint rolling metrics.</summary>
    IReadOnlyList<EndpointMetrics> GetEndpoints();

    /// <summary>Returns a bucketed time series for a metric, optionally scoped to a route.</summary>
    TimeSeries GetTimeSeries(string metric, string? route, TimeSpan window, TimeSpan bucket);
}
