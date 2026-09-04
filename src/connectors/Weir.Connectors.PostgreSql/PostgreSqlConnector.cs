using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
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

        // NpgsqlTimeoutException does not exist in Npgsql 10; the driver wraps TimeoutException
        // as InnerException of NpgsqlException. Catch it before the generic NpgsqlException branch
        // so command/connection timeouts are classified as Timeout, not Connection/Other.
        if (exception is NpgsqlException npg)
        {
            return npg.InnerException is TimeoutException
                ? DbErrorCategory.Timeout
                : npg.IsTransient ? DbErrorCategory.Connection : DbErrorCategory.Other;
        }

        return DbErrorCategory.None;
    }

    /// <summary>
    /// Returns the pooled data source for a connection, building it once on first use with the
    /// connection's pool settings applied. Keyed by the configured connection string, so two
    /// connections that name the same string share one pool - which is the point of pooling.
    /// </summary>
    /// <param name="descriptor">The resolved data connection.</param>
    /// <returns>The shared, thread-safe data source.</returns>
    private NpgsqlDataSource DataSourceFor(DataConnectionDescriptor descriptor) =>
        _dataSources.GetOrAdd(
            descriptor.ConnectionString,
            static (cs, pool) => new NpgsqlDataSourceBuilder(PostgreSqlConnectionStrings.ApplyPool(cs, pool)).Build(),
            descriptor.Pool);

    /// <inheritdoc />
    public async Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = _registry.Resolve(request.ConnectionName);

        // An import finishes its work before it returns: there is no live reader to hand back, so it
        // owns its connection for the duration and returns it to the pool here.
        if (request.Operation == EndpointOperation.Import)
        {
            return await ExecuteImportAsync(request, descriptor, cancellationToken);
        }

        var connection = DataSourceFor(descriptor).CreateConnection();
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

            // The unpaged count is a second statement over the same filters, so it runs before the
            // reader takes the connection. Opt-in per endpoint, which is why the round trip is not
            // folded into the read: a picker showing one page does not pay for a count it never shows.
            var extraOutputs = await CountIfRequestedAsync(connection, request, descriptor, cancellationToken);

            command = connection.CreateCommand();
            ConfigureCommand(command, request, descriptor);
            var reader = await command.ExecuteReaderAsync(cancellationToken);
            return new PostgreSqlExecution(connection, command, reader, messages, handler, extraOutputs);
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
        // Use a short connect and command timeout so a down database fails the probe fast. The pool
        // settings still apply - a probe that opened an unpooled connection would not be testing the
        // path requests take - but the short Timeout overrides their acquire timeout on purpose.
        var pooled = PostgreSqlConnectionStrings.ApplyPool(descriptor.ConnectionString, descriptor.Pool);
        var connectionString = new NpgsqlConnectionStringBuilder(pooled) { Timeout = 3, CommandTimeout = 3 }.ConnectionString;
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
        await using var connection = await DataSourceFor(descriptor).OpenConnectionAsync(cancellationToken);
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
        await using var connection = await DataSourceFor(descriptor).OpenConnectionAsync(cancellationToken);
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

        command.CommandType = CommandType.Text;

        // A dictionary endpoint names a table or a view, which cannot be called: the statement is
        // composed from the endpoint's metadata and the caller's filters instead.
        if (request.Operation == EndpointOperation.Dictionary)
        {
            var query = request.Query
                ?? throw new InvalidOperationException("A dictionary request must carry a resolved query.");
            var statement = TableSql.Select(PostgreSqlDialect.Instance, request.Schema, request.ObjectName, query);
            command.CommandText = statement.Text;
            BindComposed(command, statement.Parameters);
            return;
        }

        var qualified = $"{Quote(request.Schema)}.{Quote(request.ObjectName)}";

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

    /// <summary>
    /// Runs an import to completion on its own pooled connection and returns the finished execution.
    /// Nothing is streamed, so the connection goes back to the pool here rather than being handed on.
    /// </summary>
    /// <param name="request">The execution request, carrying the coerced rows.</param>
    /// <param name="descriptor">The resolved connection descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The execution carrying the row count and the summary.</returns>
    private async Task<IDbExecution> ExecuteImportAsync(
        DbExecutionRequest request, DataConnectionDescriptor descriptor, CancellationToken cancellationToken)
    {
        var payload = request.Rows
            ?? throw new InvalidOperationException("An import request must carry its rows.");

        await using var connection = DataSourceFor(descriptor).CreateConnection();
        await OpenWithRetryAsync(connection, cancellationToken);

        return await TableImport.RunAsync(
            connection,
            PostgreSqlDialect.Instance,
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
        NpgsqlConnection connection,
        DbExecutionRequest request,
        DataConnectionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (request.Operation != EndpointOperation.Dictionary || request.Query is not { IncludeTotalCount: true } query)
        {
            return null;
        }

        var statement = TableSql.Count(PostgreSqlDialect.Instance, request.Schema, request.ObjectName, query);
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
    /// composed statements have no output or INOUT parameters, only input values.
    /// </summary>
    /// <param name="command">The command to populate.</param>
    /// <param name="parameters">The parameters to bind.</param>
    private static void BindComposed(DbCommand command, IReadOnlyList<WeirParameter> parameters)
    {
        foreach (var wp in parameters)
        {
            // Same widening as AddParameters: Npgsql cannot write a boxed Byte to a smallint.
            var value = wp.Value is byte b ? (short)b : wp.Value;

            var parameter = new NpgsqlParameter
            {
                ParameterName = Strip(wp.Name),
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbObjectDescriptor>> ListTablesAsync(
        string connectionName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = DataSourceFor(descriptor).CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_schema, table_name, table_type FROM information_schema.tables " +
            "WHERE table_schema NOT IN ('pg_catalog', 'information_schema') " +
            "ORDER BY table_schema, table_name";

        var objects = new List<DbObjectDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new DbObjectDescriptor
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                ObjectType = reader.GetString(2) == "VIEW" ? DbObjectType.View : DbObjectType.Table,
            });
        }

        return objects;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbColumnDescriptor>> DescribeColumnsAsync(
        string connectionName, string schema, string objectName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = DataSourceFor(descriptor).CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // is_identity / is_generated / column_default together answer "does the database produce this
        // value itself" - the question that decides whether an import may write the column at all. The
        // primary-key flag joins through the key-column usage view, which a view simply has no rows in.
        command.CommandText = """
            SELECT c.column_name,
                   c.data_type,
                   c.character_maximum_length,
                   c.numeric_precision,
                   c.numeric_scale,
                   c.is_nullable = 'YES',
                   c.is_identity = 'YES' OR c.is_generated = 'ALWAYS' OR c.column_default IS NOT NULL,
                   k.column_name IS NOT NULL,
                   c.ordinal_position
            FROM information_schema.columns c
            LEFT JOIN information_schema.table_constraints tc
                   ON tc.table_schema = c.table_schema
                  AND tc.table_name = c.table_name
                  AND tc.constraint_type = 'PRIMARY KEY'
            LEFT JOIN information_schema.key_column_usage k
                   ON k.constraint_name = tc.constraint_name
                  AND k.table_schema = tc.table_schema
                  AND k.column_name = c.column_name
            WHERE c.table_schema = @schema AND c.table_name = @name
            ORDER BY c.ordinal_position
            """;
        command.Parameters.Add(new NpgsqlParameter("schema", schema));
        command.Parameters.Add(new NpgsqlParameter("name", objectName));

        var columns = new List<DbColumnDescriptor>();
        var ordinal = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new DbColumnDescriptor
            {
                Name = reader.GetString(0),
                DbType = PgTypeMapper.FromPgTypeName(reader.GetString(1)),
                Size = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Precision = reader.IsDBNull(3) ? null : (byte)Math.Min(reader.GetInt32(3), byte.MaxValue),
                Scale = reader.IsDBNull(4) ? null : (byte)Math.Min(reader.GetInt32(4), byte.MaxValue),
                Nullable = reader.GetBoolean(5),
                Generated = reader.GetBoolean(6),
                PrimaryKey = reader.GetBoolean(7),
                Ordinal = ordinal++,
            });
        }

        return columns;
    }

    /// <summary>Adds the bound parameters to a command for an invoked object.</summary>
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
            else if (wp.Direction is WeirDirection.Output or WeirDirection.InputOutput)
            {
                // Output parameters need an explicit buffer size so the driver can receive the
                // value. For text/bytea types, -1 tells Npgsql to use its default max-size buffer.
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
