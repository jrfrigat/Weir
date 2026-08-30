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
    /// Lists the tables and views available on a named connection, for building dictionary and import
    /// endpoints. Separate from <see cref="ListObjectsAsync"/> because the two answer different
    /// questions and a connector may be able to serve one and not the other.
    /// <para>
    /// The default implementation returns nothing, so a connector written before dictionary and import
    /// endpoints existed keeps compiling and simply offers no tables to browse.
    /// </para>
    /// </summary>
    /// <param name="connectionName">The named connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered tables and views.</returns>
    Task<IReadOnlyList<DbObjectDescriptor>> ListTablesAsync(
        string connectionName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DbObjectDescriptor>>([]);

    /// <summary>
    /// Describes the columns of a table or view, so an endpoint can be built from them without the
    /// admin retyping the schema. Returns nothing by default, for the same reason as
    /// <see cref="ListTablesAsync"/>.
    /// </summary>
    /// <param name="connectionName">The named connection.</param>
    /// <param name="schema">Object schema.</param>
    /// <param name="objectName">Table or view name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered columns, in ordinal order.</returns>
    Task<IReadOnlyList<DbColumnDescriptor>> DescribeColumnsAsync(
        string connectionName, string schema, string objectName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DbColumnDescriptor>>([]);

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

    /// <summary>
    /// What to do with the object. <see cref="EndpointOperation.Invoke"/> calls it and uses
    /// <see cref="Parameters"/>; the other two ignore those and read <see cref="Query"/> or
    /// <see cref="Rows"/> instead.
    /// </summary>
    public EndpointOperation Operation { get; init; } = EndpointOperation.Invoke;

    /// <summary>Optional per-command timeout override, in seconds.</summary>
    public int? CommandTimeoutSeconds { get; init; }

    /// <summary>Bound parameters, including any table-valued parameters.</summary>
    public IReadOnlyList<WeirParameter> Parameters { get; init; } = [];

    /// <summary>
    /// The resolved read for an <see cref="EndpointOperation.Dictionary"/> request: which of the
    /// endpoint's filters the caller actually supplied, the search text, the ordering and the page.
    /// Null for every other operation.
    /// </summary>
    public DictionaryQuery? Query { get; init; }

    /// <summary>
    /// The rows to write for an <see cref="EndpointOperation.Import"/> request, already coerced to
    /// their column types. Null for every other operation.
    /// </summary>
    public ImportPayload? Rows { get; init; }
}

/// <summary>
/// One dictionary read, resolved from the endpoint's policy and the caller's request. Everything here
/// has already been checked against the policy: a filter is present only if the endpoint declares it
/// and the request supplied it, and a sort column is one the endpoint allows. The connector composes
/// the statement from this and binds every value as a parameter.
/// </summary>
public sealed class DictionaryQuery
{
    /// <summary>The policy the read was resolved against; carries the projection and search columns.</summary>
    public required DictionaryPolicy Policy { get; init; }

    /// <summary>Filters the caller supplied values for, in policy order.</summary>
    public IReadOnlyList<DictionaryFilterValue> Filters { get; init; } = [];

    /// <summary>Free-text search across the policy's search columns, or null when none was asked for.</summary>
    public string? Search { get; init; }

    /// <summary>The ordering to apply, already resolved to real columns of the object.</summary>
    public IReadOnlyList<DictionarySort> OrderBy { get; init; } = [];

    /// <summary>Rows to skip, or null when the read is unpaged.</summary>
    public int? Skip { get; init; }

    /// <summary>Rows to take, or null when the read is unpaged.</summary>
    public int? Take { get; init; }

    /// <summary>Whether the connector also reports the unpaged row count.</summary>
    public bool IncludeTotalCount { get; init; }
}

/// <summary>One filter of a dictionary read, with the value the caller supplied for it.</summary>
public sealed class DictionaryFilterValue
{
    /// <summary>The filter as the endpoint declares it: column, operator and type.</summary>
    public required DictionaryFilter Filter { get; init; }

    /// <summary>
    /// The coerced value, for every operator except <see cref="DictionaryOperator.In"/>. Null is a
    /// SQL NULL, which <see cref="DictionaryOperator.Equals"/> and
    /// <see cref="DictionaryOperator.NotEquals"/> render as <c>IS NULL</c> / <c>IS NOT NULL</c>.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>The coerced values for <see cref="DictionaryOperator.In"/>; null for every other operator.</summary>
    public IReadOnlyList<object?>? Values { get; init; }
}

/// <summary>The rows one import request writes, already coerced to their target column types.</summary>
public sealed class ImportPayload
{
    /// <summary>The target columns, in the order each row's cells are given.</summary>
    public required IReadOnlyList<ImportColumn> Columns { get; init; }

    /// <summary>The rows, each a list of cell values in <see cref="Columns"/> order.</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>What to do with a row whose key already exists.</summary>
    public ImportMode Mode { get; init; } = ImportMode.Insert;

    /// <summary>Columns identifying an existing row, for <see cref="ImportMode.Upsert"/>.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Rows per statement. Zero lets the connector choose.</summary>
    public int BatchSize { get; init; }

    /// <summary>Whether the whole request runs in one transaction.</summary>
    public bool Transactional { get; init; } = true;
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
