namespace Weir.Contracts;

/// <summary>A stored procedure or function discovered in a target database.</summary>
public sealed record DbObjectDescriptor
{
    /// <summary>Object schema.</summary>
    public required string Schema { get; init; }

    /// <summary>Object name.</summary>
    public required string Name { get; init; }

    /// <summary>The kind of object.</summary>
    public DbObjectType ObjectType { get; init; }
}

/// <summary>A parameter discovered on a stored procedure or function.</summary>
public sealed record DbParameterDescriptor
{
    /// <summary>Parameter name, without the provider prefix.</summary>
    public required string Name { get; init; }

    /// <summary>Provider-agnostic type inferred from the database type.</summary>
    public WeirDbType DbType { get; init; }

    /// <summary>Direction inferred from the database (input or input-output).</summary>
    public ParameterDirection Direction { get; init; }

    /// <summary>Size / max length, when applicable.</summary>
    public int? Size { get; init; }

    /// <summary>Numeric precision, when applicable.</summary>
    public byte? Precision { get; init; }

    /// <summary>Numeric scale, when applicable.</summary>
    public byte? Scale { get; init; }

    /// <summary>SQL type name for user-defined / table types, when applicable.</summary>
    public string? TypeName { get; init; }

    /// <summary>For a table-valued parameter: the discovered column schema of each row; otherwise null.</summary>
    public IReadOnlyList<TvpColumn>? TableColumns { get; init; }
}

/// <summary>The outcome of synchronizing one endpoint's parameters with the database.</summary>
public sealed record EndpointSyncResult
{
    /// <summary>The endpoint that was synchronized.</summary>
    public Guid EndpointId { get; init; }

    /// <summary>The endpoint route.</summary>
    public required string Route { get; init; }

    /// <summary>The endpoint HTTP method.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>Outcome: <c>updated</c>, <c>unchanged</c>, <c>objectNotFound</c> or <c>error</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Names of parameters added to match new database parameters.</summary>
    public IReadOnlyList<string> Added { get; init; } = [];

    /// <summary>Names of parameters whose type or direction changed.</summary>
    public IReadOnlyList<string> Updated { get; init; } = [];

    /// <summary>Names of parameters removed because the database no longer declares them.</summary>
    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>Human-readable detail, e.g. an error message.</summary>
    public string? Message { get; init; }
}
