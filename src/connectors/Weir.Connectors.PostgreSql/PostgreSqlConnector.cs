using System.Collections.Concurrent;
using System.Data;
using Npgsql;
using Weir.Abstractions;
using Weir.Contracts;
using ParameterDirection = System.Data.ParameterDirection;
using WeirDirection = Weir.Contracts.ParameterDirection;

namespace Weir.Connectors.PostgreSql;

/// <summary>
/// <see cref="IDbConnector"/> for PostgreSQL. Invokes functions and stored procedures via
/// <c>Npgsql</c>, supporting output / INOUT parameters and captured notice messages. Table-valued
/// parameters are not supported by PostgreSQL; pass sets as arrays or JSON parameters instead.
/// Connections come from a pooled <see cref="NpgsqlDataSource"/> cached per connection string, which is
/// the recommended way to pool with Npgsql and prepares the driver for a single shared pool per target.
/// </summary>
public sealed class PostgreSqlConnector : IDbConnector, IAsyncDisposable
{
    private readonly IDataConnectionRegistry _registry;

    /// <summary>Pooled data sources, one per distinct connection string, created on first use.</summary>
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _dataSources = new(StringComparer.Ordinal);

    /// <summary>Creates the connector over the shared connection registry.</summary>
    /// <param name="registry">The data-connection registry.</param>
    public PostgreSqlConnector(IDataConnectionRegistry registry) => _registry = registry;

    /// <inheritdoc />
    public string ProviderName => "PostgreSql";

    /// <inheritdoc />
    public DbErrorCategory ClassifyError(Exception exception)
    {
        if (exception is TimeoutException)
        {
            return DbErrorCategory.Timeout;
        }

        if (exception is PostgresException pg)
        {
            var state = pg.SqlState;
            if (state == PostgresErrorCodes.DeadlockDetected)
            {
                return DbErrorCategory.Deadlock;
            }

            if (state is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable)
            {
                return DbErrorCategory.Timeout;
            }

            if (state is not null && state.StartsWith("23", StringComparison.Ordinal))
            {
                return DbErrorCategory.Constraint; // integrity-constraint-violation class
            }

            if (state is not null && state.StartsWith("08", StringComparison.Ordinal))
            {
                return DbErrorCategory.Connection; // connection-exception class
            }

            return DbErrorCategory.Other;
        }

        if (exception is NpgsqlException npg)
        {
            return npg.IsTransient ? DbErrorCategory.Connection : DbErrorCategory.Other;
        }

        return DbErrorCategory.None;
    }

    /// <summary>Returns the pooled data source for a connection string, building it once on first use.</summary>
    /// <param name="connectionString">The ADO.NET connection string.</param>
    /// <returns>The shared, thread-safe data source.</returns>
    private NpgsqlDataSource DataSourceFor(string connectionString) =>
        _dataSources.GetOrAdd(connectionString, static cs => new NpgsqlDataSourceBuilder(cs).Build());

    /// <inheritdoc />
    public async Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = _registry.Resolve(request.ConnectionName);

        var connection = DataSourceFor(descriptor.ConnectionString).CreateConnection();
        var messages = new List<SqlMessage>();
        NoticeEventHandler handler = (_, e) =>
        {
            // Notices may arrive on a driver thread; lock so concurrent callbacks do not corrupt the list.
            lock (messages)
            {
                messages.Add(new SqlMessage { Text = e.Notice.MessageText, Procedure = e.Notice.Routine });
            }
        };
        connection.Notice += handler;

        NpgsqlCommand? command = null;
        try
        {
            await OpenWithRetryAsync(connection, cancellationToken);
            command = connection.CreateCommand();
            ConfigureCommand(command, request, descriptor);
            var reader = await command.ExecuteReaderAsync(cancellationToken);
            return new PostgreSqlExecution(connection, command, reader, messages, handler);
        }
        catch
        {
            if (command is not null)
            {
                await command.DisposeAsync();
            }

            connection.Notice -= handler;
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        // Use a short connect and command timeout so a down database fails the probe fast.
        var connectionString = new NpgsqlConnectionStringBuilder(descriptor.ConnectionString) { Timeout = 3, CommandTimeout = 3 }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = await DataSourceFor(descriptor.ConnectionString).OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT n.nspname, p.proname, p.prokind, p.proretset " +
            "FROM pg_proc p JOIN pg_namespace n ON p.pronamespace = n.oid " +
            "WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND p.prokind IN ('f', 'p') " +
            "ORDER BY n.nspname, p.proname";

        var objects = new List<DbObjectDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = reader.GetChar(2);
            var returnsSet = reader.GetBoolean(3);
            objects.Add(new DbObjectDescriptor
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                ObjectType = kind == 'p'
                    ? DbObjectType.StoredProcedure
                    : returnsSet ? DbObjectType.TableValuedFunction : DbObjectType.ScalarFunction,
            });
        }

        return objects;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Assumes a single overload per schema-qualified name. PostgreSQL permits overloaded functions
    /// (same name, different signatures); if several exist, their parameters are returned merged.
    /// </remarks>
    public async Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(
        string connectionName, string schema, string objectName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = await DataSourceFor(descriptor.ConnectionString).OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT p.parameter_name, p.data_type, p.parameter_mode " +
            "FROM information_schema.parameters p " +
            "JOIN information_schema.routines r ON p.specific_name = r.specific_name " +
            "WHERE r.routine_schema = @schema AND r.routine_name = @obj AND p.parameter_name IS NOT NULL " +
            "ORDER BY p.ordinal_position";
        command.Parameters.Add(new NpgsqlParameter("schema", schema));
        command.Parameters.Add(new NpgsqlParameter("obj", objectName));

        var parameters = new List<DbParameterDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mode = reader.GetString(2);
            parameters.Add(new DbParameterDescriptor
            {
                Name = reader.GetString(0),
                DbType = PgTypeMapper.FromPgTypeName(reader.GetString(1)),
                Direction = mode switch
                {
                    "OUT" => WeirDirection.Output,
                    "INOUT" => WeirDirection.InputOutput,
                    _ => WeirDirection.Input,
                },
            });
        }

        return parameters;
    }

    /// <summary>
    /// Opens the connection, retrying only transient failures (Npgsql classifies network and
    /// server-unavailable errors). Retrying is limited to the open (no command has executed yet),
    /// so a function or procedure is never invoked more than once.
    /// </summary>
    private static async Task OpenWithRetryAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (NpgsqlException ex) when (attempt < maxAttempts && ex.IsTransient)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>Configures the command text and parameters for the requested object.</summary>
    /// <param name="command">The command to configure.</param>
    /// <param name="request">The execution request.</param>
    /// <param name="descriptor">The resolved connection descriptor (for default timeout).</param>
    private static void ConfigureCommand(NpgsqlCommand command, DbExecutionRequest request, DataConnectionDescriptor descriptor)
    {
        var timeout = request.CommandTimeoutSeconds ?? descriptor.DefaultCommandTimeoutSeconds;
        if (timeout is { } t)
        {
            command.CommandTimeout = t;
        }

        var qualified = $"{Quote(request.Schema)}.{Quote(request.ObjectName)}";
        command.CommandType = CommandType.Text;

        switch (request.ObjectType)
        {
            case DbObjectType.StoredProcedure:
                // A CALL passes every parameter positionally, including OUT / INOUT slots.
                command.CommandText = $"CALL {qualified}({ArgumentList(request.Parameters, inputOnly: false)})";
                AddParameters(command, request.Parameters);
                break;

            case DbObjectType.TableValuedFunction:
                command.CommandText = $"SELECT * FROM {qualified}({ArgumentList(request.Parameters, inputOnly: true)})";
                AddParameters(command, request.Parameters);
                break;

            case DbObjectType.ScalarFunction:
                command.CommandText = $"SELECT {qualified}({ArgumentList(request.Parameters, inputOnly: true)}) AS \"Value\"";
                AddParameters(command, request.Parameters);
                break;

            default:
                throw new NotSupportedException($"Unsupported object type '{request.ObjectType}'.");
        }
    }

    /// <summary>Builds the positional argument list for the call/select from the parameters.</summary>
    /// <param name="parameters">The bound parameters.</param>
    /// <param name="inputOnly">When true, only input/INOUT parameters are emitted (functions).</param>
    /// <returns>A comma-separated argument list.</returns>
    private static string ArgumentList(IReadOnlyList<WeirParameter> parameters, bool inputOnly) =>
        string.Join(", ", parameters
            .Where(p => !inputOnly || p.Direction is WeirDirection.Input or WeirDirection.InputOutput)
            .Select(p => "@" + Strip(p.Name)));

    /// <summary>Adds the bound parameters to the command, coercing values Npgsql cannot bind directly.</summary>
    /// <param name="command">The command to populate.</param>
    /// <param name="parameters">The bound parameters.</param>
    private static void AddParameters(NpgsqlCommand command, IReadOnlyList<WeirParameter> parameters)
    {
        foreach (var wp in parameters)
        {
            if (wp.DbType == WeirDbType.Structured)
            {
                throw new NotSupportedException(
                    "PostgreSQL does not support table-valued parameters. Pass sets as array or JSON parameters instead.");
            }

            // PostgreSQL has no unsigned tinyint: a Byte maps to smallint, but Npgsql cannot write a
            // boxed System.Byte to a smallint parameter, so widen it to short first.
            var value = wp.Value is byte b ? (short)b : wp.Value;

            var parameter = new NpgsqlParameter
            {
                ParameterName = Strip(wp.Name),
                Direction = MapDirection(wp.Direction),
                NpgsqlDbType = PgTypeMapper.Map(wp.DbType),
                Value = value ?? DBNull.Value,
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

    /// <summary>Maps a Weir parameter direction to the ADO.NET direction.</summary>
    /// <param name="direction">The Weir direction.</param>
    /// <returns>The ADO.NET direction.</returns>
    private static ParameterDirection MapDirection(WeirDirection direction) => direction switch
    {
        WeirDirection.Input => ParameterDirection.Input,
        WeirDirection.Output => ParameterDirection.Output,
        WeirDirection.InputOutput => ParameterDirection.InputOutput,
        // PostgreSQL has no distinct RETURN value; treat it as an input.
        _ => ParameterDirection.Input,
    };

    /// <summary>Quotes a SQL identifier with double quotes, escaping any embedded quote.</summary>
    /// <param name="identifier">The raw identifier.</param>
    /// <returns>The quoted identifier.</returns>
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Removes a leading '@' from a parameter name.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>The name without a leading '@'.</returns>
    private static string Strip(string name) => name.StartsWith('@') ? name[1..] : name;

    /// <summary>Disposes every pooled data source, closing its physical connections.</summary>
    /// <returns>A task that completes when all data sources are disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var source in _dataSources.Values)
        {
            await source.DisposeAsync();
        }

        _dataSources.Clear();
    }
}
