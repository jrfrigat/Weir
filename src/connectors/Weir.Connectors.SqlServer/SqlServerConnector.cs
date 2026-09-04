using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Weir.Abstractions;
using Weir.Contracts;
using ParameterDirection = System.Data.ParameterDirection;
using WeirDirection = Weir.Contracts.ParameterDirection;

namespace Weir.Connectors.SqlServer;

/// <summary>
/// <see cref="IDbConnector"/> for SQL Server. Executes stored procedures and table-valued / scalar
/// functions via <c>Microsoft.Data.SqlClient</c>, supporting table-valued parameters, output
/// parameters, the procedure return value and captured <c>PRINT</c> / info messages.
/// </summary>
public sealed class SqlServerConnector : IDbConnector
{
    private const string ReturnValueParameter = "@__weir_return";

    private readonly IDataConnectionRegistry _registry;

    /// <summary>Creates the connector over the shared connection registry.</summary>
    public SqlServerConnector(IDataConnectionRegistry registry) => _registry = registry;

    /// <inheritdoc />
    public string ProviderName => "SqlServer";

    /// <inheritdoc />
    public DbErrorCategory ClassifyError(Exception exception)
    {
        if (exception is TimeoutException)
        {
            return DbErrorCategory.Timeout;
        }

        if (exception is SqlException sql)
        {
            foreach (SqlError error in sql.Errors)
            {
                var category = Categorize(error.Number);
                if (category != DbErrorCategory.None)
                {
                    return category;
                }
            }

            return DbErrorCategory.Other;
        }

        return DbErrorCategory.None;
    }

    /// <summary>Maps a SQL Server error number to a provider-agnostic category.</summary>
    /// <param name="number">The <see cref="SqlError.Number"/>.</param>
    /// <returns>The category, or <see cref="DbErrorCategory.None"/> if the number is not recognized.</returns>
    private static DbErrorCategory Categorize(int number) => number switch
    {
        -2 => DbErrorCategory.Timeout,                                   // command timeout
        1205 => DbErrorCategory.Deadlock,                               // deadlock victim
        515 or 547 or 2601 or 2627 => DbErrorCategory.Constraint,       // null / FK / check / unique
        2 or 53 or 40 or 233 or 4060 or 10053 or 10054 or 10060 or 11001 or 18456 => DbErrorCategory.Connection,
        _ => DbErrorCategory.None,
    };

    /// <inheritdoc />
    public async Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = _registry.Resolve(request.ConnectionName);

        // An import finishes its work before it returns: there is no live reader to hand back, so it
        // owns its connection for the duration and closes it here rather than at execution dispose.
        if (request.Operation == EndpointOperation.Import)
        {
            return await ExecuteImportAsync(request, descriptor, cancellationToken);
        }

        var connection = new SqlConnection(descriptor.ConnectionString);
        var messages = new List<SqlMessage>();
        SqlInfoMessageEventHandler handler = (_, e) =>
        {
            // Info messages can be delivered on a driver thread; lock so concurrent PRINT/RAISERROR
            // callbacks do not corrupt the shared list.
            lock (messages)
            {
                foreach (SqlError error in e.Errors)
                {
                    messages.Add(new SqlMessage
                    {
                        Text = error.Message,
                        Severity = error.Class,
                        Number = error.Number,
                        Procedure = string.IsNullOrEmpty(error.Procedure) ? null : error.Procedure,
                        Line = error.LineNumber,
                    });
                }
            }
        };
        connection.InfoMessage += handler;

        SqlCommand? command = null;
        try
        {
            await OpenWithRetryAsync(connection, cancellationToken);

            // The unpaged count is a second statement over the same filters, so it runs before the
            // reader takes the connection. Opt-in per endpoint, which is why the round trip is not
            // folded into the read: a picker showing one page does not pay for a count it never shows.
            var extraOutputs = await CountIfRequestedAsync(connection, request, descriptor, cancellationToken);

            command = connection.CreateCommand();
            ConfigureCommand(command, request, descriptor);
            var reader = await command.ExecuteReaderAsync(cancellationToken);
            return new SqlServerExecution(connection, command, reader, messages, handler, extraOutputs);
        }
        catch
        {
            if (command is not null)
            {
                await command.DisposeAsync();
            }

            connection.InfoMessage -= handler;
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        // Use a short connect timeout so a down database fails the probe fast (health checks must not hang).
        var connectionString = new SqlConnectionStringBuilder(descriptor.ConnectionString) { ConnectTimeout = 3 }.ConnectionString;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = 3;
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = new SqlConnection(descriptor.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.name, o.name, o.type FROM sys.objects o " +
            "JOIN sys.schemas s ON o.schema_id = s.schema_id " +
            "WHERE o.type IN ('P','FN','TF','IF') ORDER BY s.name, o.name";

        var objects = new List<DbObjectDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(2).Trim();
            objects.Add(new DbObjectDescriptor
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                ObjectType = type switch
                {
                    "P" => DbObjectType.StoredProcedure,
                    "FN" => DbObjectType.ScalarFunction,
                    _ => DbObjectType.TableValuedFunction,
                },
            });
        }

        return objects;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbObjectDescriptor>> ListTablesAsync(
        string connectionName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = new SqlConnection(descriptor.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.name, o.name, o.type FROM sys.objects o " +
            "JOIN sys.schemas s ON o.schema_id = s.schema_id " +
            "WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0 ORDER BY s.name, o.name";

        var objects = new List<DbObjectDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new DbObjectDescriptor
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                ObjectType = reader.GetString(2).Trim() == "V" ? DbObjectType.View : DbObjectType.Table,
            });
        }

        return objects;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbColumnDescriptor>> DescribeColumnsAsync(
        string connectionName, string schema, string objectName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = new SqlConnection(descriptor.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // is_identity / is_computed / default_object_id together answer "does the database produce this
        // value itself" - the question that decides whether an import may write the column at all. The
        // primary-key flag comes from the index rather than a constraint name, so a view (which has
        // neither) simply reports none instead of failing the lookup.
        command.CommandText =
            "SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, " +
            "       c.is_identity | c.is_computed | CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END, " +
            "       ISNULL(k.is_primary_key, 0), c.column_id " +
            "FROM sys.columns c " +
            "JOIN sys.types t ON c.user_type_id = t.user_type_id " +
            "OUTER APPLY (SELECT TOP 1 i.is_primary_key FROM sys.index_columns ic " +
            "             JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id " +
            "             WHERE ic.object_id = c.object_id AND ic.column_id = c.column_id AND i.is_primary_key = 1) k " +
            "WHERE c.object_id = OBJECT_ID(@obj) ORDER BY c.column_id";
        command.Parameters.Add(new SqlParameter("@obj", $"{Quote(schema)}.{Quote(objectName)}"));

        var columns = new List<DbColumnDescriptor>();
        var ordinal = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var typeName = reader.GetString(1);
            var maxLength = reader.GetInt16(2);
            var precision = reader.GetByte(3);
            var scale = reader.GetByte(4);
            columns.Add(new DbColumnDescriptor
            {
                Name = reader.GetString(0),
                DbType = SqlDbTypeMapper.FromSqlTypeName(typeName),
                Size = maxLength > 0 && typeName is not ("text" or "ntext" or "image") ? maxLength : null,
                Precision = precision > 0 ? precision : null,
                Scale = scale > 0 ? scale : null,
                Nullable = reader.GetBoolean(5),
                Generated = reader.GetInt32(6) != 0,
                PrimaryKey = reader.GetBoolean(7),
                Ordinal = ordinal++,
            });
        }

        return columns;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(
        string connectionName, string schema, string objectName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = new SqlConnection(descriptor.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Read every parameter first (the reader is closed before any per-TVP column lookup runs, so
        // this does not require MARS).
        var raw = new List<RawParameter>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT p.name, t.name, p.max_length, p.precision, p.scale, p.is_output, t.is_table_type, s.name, t.user_type_id " +
                "FROM sys.parameters p " +
                "JOIN sys.types t ON p.user_type_id = t.user_type_id " +
                "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                "WHERE p.object_id = OBJECT_ID(@obj) ORDER BY p.parameter_id";
            command.Parameters.Add(new SqlParameter("@obj", $"{Quote(schema)}.{Quote(objectName)}"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0).TrimStart('@');
                if (string.IsNullOrEmpty(name))
                {
                    continue; // the unnamed RETURN_VALUE parameter
                }

                raw.Add(new RawParameter(
                    name,
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.GetByte(3),
                    reader.GetByte(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6),
                    reader.GetString(7),
                    reader.GetInt32(8)));
            }
        }

        var parameters = new List<DbParameterDescriptor>(raw.Count);
        foreach (var r in raw)
        {
            if (r.IsTableType)
            {
                parameters.Add(new DbParameterDescriptor
                {
                    Name = r.Name,
                    DbType = WeirDbType.Structured,
                    Direction = WeirDirection.Input, // table-valued parameters are always READONLY input
                    TypeName = $"{r.TypeSchema}.{r.TypeName}",
                    TableColumns = await DescribeTableTypeColumnsAsync(connection, r.UserTypeId, cancellationToken),
                });
            }
            else
            {
                parameters.Add(new DbParameterDescriptor
                {
                    Name = r.Name,
                    DbType = SqlDbTypeMapper.FromSqlTypeName(r.TypeName),
                    Direction = r.IsOutput ? WeirDirection.InputOutput : WeirDirection.Input,
                    Size = r.MaxLength > 0 && r.TypeName is not ("text" or "ntext" or "image") ? r.MaxLength : null,
                    Precision = r.Precision > 0 ? r.Precision : null,
                    Scale = r.Scale > 0 ? r.Scale : null,
                });
            }
        }

        return parameters;
    }

    /// <summary>Reads the column schema of a table type by its user_type_id.</summary>
    private static async Task<IReadOnlyList<TvpColumn>> DescribeTableTypeColumnsAsync(
        SqlConnection connection, int userTypeId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.name, ty.name, c.max_length, c.precision, c.scale " +
            "FROM sys.table_types tt " +
            "JOIN sys.columns c ON c.object_id = tt.type_table_object_id " +
            "JOIN sys.types ty ON c.user_type_id = ty.user_type_id " +
            "WHERE tt.user_type_id = @typeId ORDER BY c.column_id";
        command.Parameters.Add(new SqlParameter("@typeId", userTypeId));

        var columns = new List<TvpColumn>();
        var ordinal = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var typeName = reader.GetString(1);
            var maxLength = reader.GetInt16(2);
            var precision = reader.GetByte(3);
            var scale = reader.GetByte(4);
            columns.Add(new TvpColumn
            {
                Name = reader.GetString(0),
                DbType = SqlDbTypeMapper.FromSqlTypeName(typeName),
                Size = maxLength > 0 && typeName is not ("text" or "ntext" or "image") ? maxLength : null,
                Precision = precision > 0 ? precision : null,
                Scale = scale > 0 ? scale : null,
                Ordinal = ordinal++,
            });
        }

        return columns;
    }

    /// <summary>A raw parameter row read from sys.parameters before per-TVP expansion.</summary>
    private sealed record RawParameter(
        string Name, string TypeName, short MaxLength, byte Precision, byte Scale,
        bool IsOutput, bool IsTableType, string TypeSchema, int UserTypeId);

    /// <summary>
    /// Transient SQL Server error numbers (connectivity, failover and throttling) that are safe to
    /// retry at connection-open time, before any command has run.
    /// </summary>
    private static readonly HashSet<int> TransientErrorNumbers =
        [-2, 20, 64, 233, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 11001, 40143, 40197, 40501, 40540, 40613, 49918, 49919, 49920];

    /// <summary>
    /// Opens the connection, retrying only transient failures. Retrying is limited to the open
    /// (no command has executed yet), so a stored procedure is never invoked more than once.
    /// </summary>
    private static async Task OpenWithRetryAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (SqlException ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>Determines whether a <see cref="SqlException"/> represents a transient failure.</summary>
    private static bool IsTransient(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (TransientErrorNumbers.Contains(error.Number))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Configures the command text, type and parameters for the requested object.</summary>
    /// <param name="command">The command to configure.</param>
    /// <param name="request">The execution request.</param>
    /// <param name="descriptor">The resolved connection descriptor (for default timeout).</param>
    private static void ConfigureCommand(SqlCommand command, DbExecutionRequest request, DataConnectionDescriptor descriptor)
    {
        var timeout = request.CommandTimeoutSeconds ?? descriptor.DefaultCommandTimeoutSeconds;
        if (timeout is { } t)
        {
            command.CommandTimeout = t;
        }

        // A dictionary endpoint names a table or a view, which cannot be called: the statement is
        // composed from the endpoint's metadata and the caller's filters instead.
        if (request.Operation == EndpointOperation.Dictionary)
        {
            var query = request.Query
                ?? throw new InvalidOperationException("A dictionary request must carry a resolved query.");
            var statement = TableSql.Select(SqlServerDialect.Instance, request.Schema, request.ObjectName, query);
            command.CommandType = CommandType.Text;
            command.CommandText = statement.Text;
            BindComposed(command, statement.Parameters);
            return;
        }

        var qualified = $"{Quote(request.Schema)}.{Quote(request.ObjectName)}";

        switch (request.ObjectType)
        {
            case DbObjectType.StoredProcedure:
                command.CommandType = CommandType.StoredProcedure;
                command.CommandText = qualified;
                AddParameters(command, request.Parameters, includeReturnValue: true);
                break;

            case DbObjectType.TableValuedFunction:
                command.CommandType = CommandType.Text;
                command.CommandText = $"SELECT * FROM {qualified}({ArgumentList(request.Parameters)})";
                AddParameters(command, request.Parameters, includeReturnValue: false);
                break;

            case DbObjectType.ScalarFunction:
                command.CommandType = CommandType.Text;
                command.CommandText = $"SELECT {qualified}({ArgumentList(request.Parameters)}) AS [Value]";
                AddParameters(command, request.Parameters, includeReturnValue: false);
                break;

            default:
                throw new NotSupportedException($"Unsupported object type '{request.ObjectType}'.");
        }
    }

    /// <summary>
    /// Runs an import to completion on its own connection and returns the finished execution. Nothing
    /// is streamed, so the connection is closed here rather than being handed on.
    /// </summary>
    /// <param name="request">The execution request, carrying the coerced rows.</param>
    /// <param name="descriptor">The resolved connection descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The execution carrying the row count and the summary.</returns>
    private static async Task<IDbExecution> ExecuteImportAsync(
        DbExecutionRequest request, DataConnectionDescriptor descriptor, CancellationToken cancellationToken)
    {
        var payload = request.Rows
            ?? throw new InvalidOperationException("An import request must carry its rows.");

        await using var connection = new SqlConnection(descriptor.ConnectionString);
        await OpenWithRetryAsync(connection, cancellationToken);

        return await TableImport.RunAsync(
            connection,
            SqlServerDialect.Instance,
            request.Schema,
            request.ObjectName,
            payload,
            request.CommandTimeoutSeconds ?? descriptor.DefaultCommandTimeoutSeconds,
            static (command, parameters) => BindComposed(command, parameters),
            messages: null,
            cancellationToken);
    }

    /// <summary>
    /// Runs the unpaged COUNT for a dictionary read that asked for one, and returns it as an output
    /// value. Returns null when the request is not a dictionary read or did not ask.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="request">The execution request.</param>
    /// <param name="descriptor">The resolved connection descriptor (for the default timeout).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outputs to merge, or null.</returns>
    private static async Task<IReadOnlyDictionary<string, object?>?> CountIfRequestedAsync(
        SqlConnection connection,
        DbExecutionRequest request,
        DataConnectionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (request.Operation != EndpointOperation.Dictionary || request.Query is not { IncludeTotalCount: true } query)
        {
            return null;
        }

        var statement = TableSql.Count(SqlServerDialect.Instance, request.Schema, request.ObjectName, query);
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = statement.Text;
        if ((request.CommandTimeoutSeconds ?? descriptor.DefaultCommandTimeoutSeconds) is { } timeout)
        {
            command.CommandTimeout = timeout;
        }

        BindComposed(command, statement.Parameters);
        var total = await command.ExecuteScalarAsync(cancellationToken);
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["totalCount"] = total is DBNull or null ? 0 : Convert.ToInt32(total, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Binds the parameters of a statement Weir composed. Simpler than <see cref="AddParameters"/>:
    /// composed statements have no output, return-value or table-valued parameters, only input values.
    /// </summary>
    /// <param name="command">The command to populate.</param>
    /// <param name="parameters">The parameters to bind.</param>
    private static void BindComposed(DbCommand command, IReadOnlyList<WeirParameter> parameters)
    {
        foreach (var wp in parameters)
        {
            var parameter = new SqlParameter
            {
                ParameterName = "@" + Strip(wp.Name),
                SqlDbType = SqlDbTypeMapper.Map(wp.DbType),
                Value = wp.Value ?? DBNull.Value,
            };

            if (wp.Size is { } size)
            {
                parameter.Size = size;
            }

            if (wp.Precision is { } precision)
            {
                parameter.Precision = precision;
            }

            if (wp.Scale is { } scale)
            {
                parameter.Scale = scale;
            }

            command.Parameters.Add(parameter);
        }
    }

    /// <summary>Builds the positional argument list for a function call from the input parameters.</summary>
    /// <param name="parameters">The bound parameters.</param>
    /// <returns>A comma-separated list of parameter references.</returns>
    private static string ArgumentList(IReadOnlyList<WeirParameter> parameters) =>
        string.Join(", ", parameters
            .Where(p => p.Direction is WeirDirection.Input or WeirDirection.InputOutput)
            .Select(p => "@" + Strip(p.Name)));

    /// <summary>Adds the bound parameters (and optionally a return-value parameter) to the command.</summary>
    /// <param name="command">The command to populate.</param>
    /// <param name="parameters">The bound parameters.</param>
    /// <param name="includeReturnValue">Whether to append a RETURN_VALUE parameter (stored procedures only).</param>
    private static void AddParameters(SqlCommand command, IReadOnlyList<WeirParameter> parameters, bool includeReturnValue)
    {
        foreach (var wp in parameters)
        {
            var parameter = new SqlParameter
            {
                ParameterName = "@" + Strip(wp.Name),
                Direction = MapDirection(wp.Direction),
            };

            if (wp.DbType == WeirDbType.Structured)
            {
                parameter.SqlDbType = SqlDbType.Structured;
                parameter.TypeName = wp.TypeName;
                // An empty table - or one the request omitted entirely - must be sent as a null value:
                // SqlClient reads that as "TVP with no rows", and rejects DBNull outright.
                parameter.Value = wp.Table is null ? null : TableValuedParameters.BuildValue(wp.Table);
            }
            else
            {
                parameter.SqlDbType = SqlDbTypeMapper.Map(wp.DbType);
                if (wp.Size is { } size)
                {
                    parameter.Size = size;
                }
                else if (wp.Direction is WeirDirection.Output or WeirDirection.InputOutput)
                {
                    // Output parameters must have an explicit buffer size so SqlClient can receive
                    // the value. For max-capable types (NVarChar, VarChar, VarBinary), -1 tells the
                    // driver to use its default max-size buffer. Without this, Size defaults to 0 and
                    // the output is truncated or rejected.
                    parameter.Size = -1;
                }

                if (wp.Precision is { } precision)
                {
                    parameter.Precision = precision;
                }

                if (wp.Scale is { } scale)
                {
                    parameter.Scale = scale;
                }

                parameter.Value = wp.Value ?? DBNull.Value;
            }

            command.Parameters.Add(parameter);
        }

        if (includeReturnValue)
        {
            command.Parameters.Add(new SqlParameter(ReturnValueParameter, SqlDbType.Int)
            {
                Direction = ParameterDirection.ReturnValue,
            });
        }
    }

    /// <summary>Maps a Weir parameter direction to the ADO.NET direction.</summary>
    /// <param name="direction">The Weir direction.</param>
    /// <returns>The ADO.NET direction.</returns>
    private static ParameterDirection MapDirection(WeirDirection direction) => direction switch
    {
        WeirDirection.Input => ParameterDirection.Input,
        WeirDirection.Output => ParameterDirection.Output,
        WeirDirection.InputOutput => ParameterDirection.InputOutput,
        WeirDirection.ReturnValue => ParameterDirection.ReturnValue,
        _ => ParameterDirection.Input,
    };

    /// <summary>Quotes a SQL identifier with brackets, escaping any embedded closing bracket.</summary>
    /// <param name="identifier">The raw identifier.</param>
    /// <returns>The bracket-quoted identifier.</returns>
    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>Removes a leading '@' from a parameter name.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>The name without a leading '@'.</returns>
    private static string Strip(string name) => name.StartsWith('@') ? name[1..] : name;
}
