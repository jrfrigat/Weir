using System.Data;
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
            command = connection.CreateCommand();
            ConfigureCommand(command, request, descriptor);
            var reader = await command.ExecuteReaderAsync(cancellationToken);
            return new SqlServerExecution(connection, command, reader, messages, handler);
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
                    Size = r.MaxLength > 0 ? r.MaxLength : null,
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
            var maxLength = reader.GetInt16(2);
            var precision = reader.GetByte(3);
            var scale = reader.GetByte(4);
            columns.Add(new TvpColumn
            {
                Name = reader.GetString(0),
                DbType = SqlDbTypeMapper.FromSqlTypeName(reader.GetString(1)),
                Size = maxLength > 0 ? maxLength : null,
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
                parameter.Value = wp.Table is null ? DBNull.Value : TableValuedParameters.BuildValue(wp.Table);
            }
            else
            {
                parameter.SqlDbType = SqlDbTypeMapper.Map(wp.DbType);
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
