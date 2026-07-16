using System.Data.Common;
using Weir.Contracts;

namespace Weir.Abstractions;

/// <summary>
/// A data-plane driver for one database engine. Executes a stored procedure / function and exposes
/// the result for streaming. Implementations ship as independent <c>Weir.Connectors.*</c> packages.
/// </summary>
public interface IDbConnector
{
    /// <summary>Stable provider name, e.g. <c>SqlServer</c>. Matches the connection's configured provider.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Opens a connection, invokes the requested object, and returns a handle over the live result.
    /// The caller streams <see cref="IDbExecution.Reader"/>, then calls
    /// <see cref="IDbExecution.CompleteAsync"/> to capture output parameters, return value and messages.
    /// </summary>
    Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that a named connection is reachable (a lightweight round-trip such as SELECT 1).
    /// Throws if the connection cannot be opened or the probe fails. Used by health checks.
    /// </summary>
    /// <param name="connectionName">The named connection to probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default);

    /// <summary>Lists the stored procedures and functions available on a named connection.</summary>
    /// <param name="connectionName">The named connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered objects.</returns>
    Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default);

    /// <summary>Describes the parameters of a stored procedure or function.</summary>
    /// <param name="connectionName">The named connection.</param>
    /// <param name="schema">Object schema.</param>
    /// <param name="objectName">Object name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered parameters, in declaration order.</returns>
    Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(
        string connectionName, string schema, string objectName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies a failure raised during execution into a provider-agnostic category (timeout, deadlock,
    /// constraint, connection) for telemetry. Non-driver failures and anything unrecognized map to
    /// <see cref="DbErrorCategory.Other"/>. The default implementation classifies all failures as
    /// <see cref="DbErrorCategory.Other"/>; each connector overrides it with its driver's error codes.
    /// </summary>
    /// <param name="exception">The failure thrown from <see cref="ExecuteAsync"/> or streaming.</param>
    /// <returns>The error category.</returns>
    DbErrorCategory ClassifyError(Exception exception) => DbErrorCategory.Other;
}

/// <summary>
/// A live execution. The result sets are read from <see cref="Reader"/>; output parameters, the
/// return value, affected-row count and SQL messages are only valid after
/// <see cref="CompleteAsync"/> has drained and closed the reader (ADO.NET semantics).
/// </summary>
public interface IDbExecution : IAsyncDisposable
{
    /// <summary>The open data reader positioned at the first result set. Valid until <see cref="CompleteAsync"/> is called or the execution is disposed.</summary>
    DbDataReader Reader { get; }

    /// <summary>Closes the reader and captures <see cref="Outputs"/>, <see cref="ReturnValue"/>,
    /// <see cref="RecordsAffected"/> and <see cref="Messages"/>. Call after reading all result sets.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>SQL informational messages captured during execution. Valid after <see cref="CompleteAsync"/>.</summary>
    IReadOnlyList<SqlMessage> Messages { get; }

    /// <summary>Output / input-output parameter values by logical name. Valid after <see cref="CompleteAsync"/>.</summary>
    IReadOnlyDictionary<string, object?> Outputs { get; }

    /// <summary>The integer RETURN value, if the object produced one. Valid after <see cref="CompleteAsync"/>.</summary>
    int? ReturnValue { get; }

    /// <summary>Rows affected, as reported by the driver. Valid after <see cref="CompleteAsync"/>.</summary>
    int RecordsAffected { get; }
}

/// <summary>An immutable request to execute one database object on a named connection.</summary>
public sealed class DbExecutionRequest
{
    /// <summary>Target data connection name.</summary>
    public required string ConnectionName { get; init; }

    /// <summary>Object schema, e.g. <c>dbo</c>.</summary>
    public required string Schema { get; init; }

    /// <summary>Object name (procedure / function), without schema.</summary>
    public required string ObjectName { get; init; }

    /// <summary>Kind of object being invoked.</summary>
    public DbObjectType ObjectType { get; init; }

    /// <summary>Optional per-command timeout override, in seconds.</summary>
    public int? CommandTimeoutSeconds { get; init; }

    /// <summary>Bound parameters, including any table-valued parameters.</summary>
    public IReadOnlyList<WeirParameter> Parameters { get; init; } = [];
}

/// <summary>A bound parameter value ready for the driver. Provider-agnostic.</summary>
public sealed class WeirParameter
{
    /// <summary>Database parameter name (e.g. <c>@CustomerId</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Direction of the parameter.</summary>
    public ParameterDirection Direction { get; init; } = ParameterDirection.Input;

    /// <summary>Provider-agnostic type. <see cref="WeirDbType.Structured"/> carries <see cref="Table"/>.</summary>
    public WeirDbType DbType { get; init; } = WeirDbType.String;

    /// <summary>Scalar value for non-structured parameters. Null is a SQL NULL.</summary>
    public object? Value { get; init; }

    /// <summary>Size / max length for sized types.</summary>
    public int? Size { get; init; }

    /// <summary>Numeric precision.</summary>
    public byte? Precision { get; init; }

    /// <summary>Numeric scale.</summary>
    public byte? Scale { get; init; }

    /// <summary>SQL type name for structured / UDT parameters, e.g. <c>dbo.OrderItemType</c>.</summary>
    public string? TypeName { get; init; }

    /// <summary>Row data for a table-valued parameter; null for scalar parameters.</summary>
    public TableParameter? Table { get; init; }
}

/// <summary>The row set of a table-valued parameter.</summary>
public sealed class TableParameter
{
    /// <summary>Ordered column schema.</summary>
    public required IReadOnlyList<TvpColumn> Columns { get; init; }

    /// <summary>Rows, each a list of cell values in column order.</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
}
