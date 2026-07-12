namespace Weir.Contracts;

/// <summary>A single audit-log entry. Written for endpoint calls and administrative actions.</summary>
public sealed record AuditEntry
{
    /// <summary>Monotonic identifier assigned by the store.</summary>
    public long Id { get; init; }

    /// <summary>When the event occurred.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Event category, e.g. <c>endpoint.call</c>, <c>admin.login</c>, <c>key.created</c>.</summary>
    public required string Category { get; init; }

    /// <summary>Who performed the action - an API key prefix or an admin username.</summary>
    public string? Actor { get; init; }

    /// <summary>Endpoint route, for call events.</summary>
    public string? Route { get; init; }

    /// <summary>Outcome marker, e.g. <c>ok</c> / <c>error</c>.</summary>
    public string? Outcome { get; init; }

    /// <summary>HTTP status code, when applicable.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Wall-clock duration of the call, in milliseconds.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Free-form detail (never contains parameter values by default).</summary>
    public string? Detail { get; init; }
}

/// <summary>Filter for querying the audit log.</summary>
public sealed record AuditQuery
{
    /// <summary>Inclusive lower time bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive upper time bound.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Restrict to a category.</summary>
    public string? Category { get; init; }

    /// <summary>Restrict to an actor.</summary>
    public string? Actor { get; init; }

    /// <summary>Restrict to a route.</summary>
    public string? Route { get; init; }

    /// <summary>Maximum rows to return.</summary>
    public int Limit { get; init; } = 200;

    /// <summary>Rows to skip (offset paging). Ignored when <see cref="AfterId"/> is set.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// Keyset (seek) cursor: return only entries with an id strictly less than this, in descending id
    /// order. Pass the id of the last row of the previous page to fetch the next page. Preferred over
    /// <see cref="Offset"/> for deep paging: it stays fast as the table grows and never skips or repeats
    /// rows when new entries are inserted between page fetches. Null uses offset paging.
    /// </summary>
    public long? AfterId { get; init; }
}
