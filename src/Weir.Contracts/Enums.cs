namespace Weir.Contracts;

/// <summary>Where the value of an endpoint parameter is read from on the incoming request.</summary>
public enum ParameterSource
{
    /// <summary>A property of the JSON request body (the default).</summary>
    Body,

    /// <summary>A query-string value.</summary>
    Query,

    /// <summary>A segment of the route template.</summary>
    Route,

    /// <summary>An HTTP request header (see <see cref="EndpointParameter.HeaderName"/>).</summary>
    Header,

    /// <summary>A claim from the authenticated principal (see <see cref="EndpointParameter.ClaimType"/>).</summary>
    Claim,

    /// <summary>A fixed server-side constant; never taken from the client.</summary>
    Const,
}

/// <summary>Direction of a database parameter. Mirrors ADO.NET but is provider-agnostic.</summary>
public enum ParameterDirection
{
    /// <summary>Value flows in to the procedure.</summary>
    Input,

    /// <summary>Value is produced by the procedure and returned to the caller.</summary>
    Output,

    /// <summary>Value flows in and an updated value flows back out.</summary>
    InputOutput,

    /// <summary>The procedure's integer RETURN value.</summary>
    ReturnValue,
}

/// <summary>The kind of database object an endpoint maps to.</summary>
public enum DbObjectType
{
    /// <summary>A stored procedure.</summary>
    StoredProcedure,

    /// <summary>A table-valued function (produces a row set).</summary>
    TableValuedFunction,

    /// <summary>A scalar function (produces a single value).</summary>
    ScalarFunction,
}

/// <summary>The shape of the primary result produced by the database object.</summary>
public enum ResultMode
{
    /// <summary>Zero or more rows (the default).</summary>
    MultiRow,

    /// <summary>At most one row.</summary>
    SingleRow,

    /// <summary>A single scalar value.</summary>
    Scalar,

    /// <summary>No result set; only affected-row count / output parameters.</summary>
    NonQuery,

    /// <summary>Several result sets.</summary>
    MultiResultSet,
}

/// <summary>
/// Provider-agnostic parameter type. <see cref="Structured"/> denotes a table-valued parameter (TVP).
/// Concrete connectors map these to their native DB types.
/// </summary>
public enum WeirDbType
{
    String,
    AnsiString,
    Boolean,
    Byte,
    Int16,
    Int32,
    Int64,
    Decimal,
    Double,
    Single,
    DateTime,
    DateTimeOffset,
    Date,
    Time,
    Guid,
    Binary,
    Json,
    Xml,

    /// <summary>A table-valued parameter (TVP). Carries rows rather than a scalar value.</summary>
    Structured,
}
