namespace Weir.Contracts;

/// <summary>
/// One data-plane request-log entry: the per-call history the admin panel shows so an operator can
/// see what was called, how long it took, and (for endpoints that opt in) with which parameters and
/// what result. Distinct from <see cref="AuditEntry"/>, which records administrative actions.
/// </summary>
public sealed record RequestLogEntry
{
    /// <summary>Monotonic identifier assigned by the store (used for keyset paging).</summary>
    public long Id { get; init; }

    /// <summary>When the call completed.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The endpoint's id, when the call resolved to a known endpoint.</summary>
    public Guid? EndpointId { get; init; }

    /// <summary>Endpoint route (without the <c>/api/</c> prefix).</summary>
    public required string Route { get; init; }

    /// <summary>HTTP method.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>Target data connection name.</summary>
    public string? ConnectionName { get; init; }

    /// <summary>Schema-qualified target object (for example <c>dbo.usp_CreateOrder</c>).</summary>
    public string? ObjectName { get; init; }

    /// <summary>Resulting HTTP status code.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Outcome marker, e.g. <c>ok</c> / <c>error</c>.</summary>
    public string? Outcome { get; init; }

    /// <summary>Total wall-clock duration, in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Time spent inside the database call, in milliseconds, when known.</summary>
    public double? DbDurationMs { get; init; }

    /// <summary>Rows returned across all result sets, when known.</summary>
    public int? RowsReturned { get; init; }

    /// <summary>Whether the response was served from cache.</summary>
    public bool CacheHit { get; init; }

    /// <summary>Whether the call was flagged slow (exceeded its endpoint's average by the threshold).</summary>
    public bool Slow { get; init; }

    /// <summary>The endpoint's rolling average duration at the time of the call, for context, when known.</summary>
    public double? AverageMs { get; init; }

    /// <summary>Identifying prefix of the calling API key (or a marker such as <c>admin-test</c>).</summary>
    public string? ApiKeyPrefix { get; init; }

    /// <summary>Captured request parameters as JSON, only when the endpoint opts in. Null otherwise.</summary>
    public string? Parameters { get; init; }

    /// <summary>Captured response body as JSON (size-capped), only when the endpoint opts in. Null otherwise.</summary>
    public string? Result { get; init; }

    /// <summary>Error detail for a failed call, when known.</summary>
    public string? Error { get; init; }
}

/// <summary>Filter for querying the data-plane request log.</summary>
public sealed record RequestLogQuery
{
    /// <summary>Restrict to a single endpoint by id.</summary>
    public Guid? EndpointId { get; init; }

    /// <summary>Restrict to a route (exact, case-insensitive).</summary>
    public string? Route { get; init; }

    /// <summary>When true, return only calls flagged slow.</summary>
    public bool SlowOnly { get; init; }

    /// <summary>When true, return only failed calls (status >= 400 or an error outcome).</summary>
    public bool ErrorsOnly { get; init; }

    /// <summary>Inclusive lower time bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive upper time bound.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Maximum rows to return.</summary>
    public int Limit { get; init; } = 100;

    /// <summary>
    /// Keyset (seek) cursor: return only entries with an id strictly less than this, in descending id
    /// order. Pass the id of the last row of the previous page to fetch the next page. Null returns the
    /// most recent page.
    /// </summary>
    public long? AfterId { get; init; }
}
