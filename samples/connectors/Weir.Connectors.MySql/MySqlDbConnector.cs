using System.Data;
using MySqlConnector;
using Weir.Abstractions;
using Weir.Contracts;
using ParameterDirection = System.Data.ParameterDirection;
using WeirDirection = Weir.Contracts.ParameterDirection;

namespace Weir.Connectors.MySql;

/// <summary>
/// Sample <see cref="IDbConnector"/> for MySQL, built on <c>MySqlConnector</c>. It is a reference
/// implementation that shows how to write a third-party Weir connector; it is not an officially
/// maintained connector. It supports stored procedures (with output / INOUT parameters) and scalar
/// functions. MySQL has neither table-valued functions nor table-valued parameters, so those are not
/// supported. In MySQL the endpoint "schema" is the database name.
/// </summary>
public sealed class MySqlDbConnector : IDbConnector
{
    private readonly IDataConnectionRegistry _registry;

    /// <summary>Creates the connector over the shared connection registry.</summary>
    /// <param name="registry">The data-connection registry.</param>
    public MySqlDbConnector(IDataConnectionRegistry registry) => _registry = registry;

    /// <inheritdoc />
    public string ProviderName => "MySql";

    /// <inheritdoc />
    public async Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = _registry.Resolve(request.ConnectionName);

        var connection = new MySqlConnection(descriptor.ConnectionString);
        MySqlCommand? command = null;
        try
        {
            await connection.OpenAsync(cancellationToken);
            command = connection.CreateCommand();
            ConfigureCommand(command, request, descriptor);
            var reader = await command.ExecuteReaderAsync(cancellationToken);
            return new MySqlExecution(connection, command, reader);
        }
        catch
        {
            if (command is not null)
            {
                await command.DisposeAsync();
            }

            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        var connectionString = new MySqlConnectionStringBuilder(descriptor.ConnectionString) { ConnectionTimeout = 3 }.ConnectionString;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = new MySqlConnection(descriptor.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES " +
            "WHERE ROUTINE_SCHEMA = DATABASE() ORDER BY ROUTINE_SCHEMA, ROUTINE_NAME";

        var objects = new List<DbObjectDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new DbObjectDescriptor
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                ObjectType = reader.GetString(2).Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase)
                    ? DbObjectType.StoredProcedure
                    : DbObjectType.ScalarFunction,
            });
        }

        return objects;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(
        string connectionName, string schema, string objectName, CancellationToken cancellationToken = default)
    {
        var descriptor = _registry.Resolve(connectionName);
        await using var connection = new MySqlConnection(descriptor.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT PARAMETER_NAME, DATA_TYPE, PARAMETER_MODE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE " +
            "FROM information_schema.PARAMETERS " +
            "WHERE SPECIFIC_SCHEMA = @schema AND SPECIFIC_NAME = @obj AND PARAMETER_NAME IS NOT NULL " +
            "ORDER BY ORDINAL_POSITION";
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@obj", objectName);

        var parameters = new List<DbParameterDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mode = reader.GetString(2);
            parameters.Add(new DbParameterDescriptor
            {
                Name = reader.GetString(0),
                DbType = MySqlDbTypeMapper.FromMySqlTypeName(reader.GetString(1)),
                Direction = mode.ToUpperInvariant() switch
                {
                    "OUT" => WeirDirection.Output,
                    "INOUT" => WeirDirection.InputOutput,
                    _ => WeirDirection.Input,
                },
                Size = reader.IsDBNull(3) ? null : (int?)reader.GetInt64(3),
                Precision = reader.IsDBNull(4) ? null : (byte?)reader.GetInt64(4),
                Scale = reader.IsDBNull(5) ? null : (byte?)reader.GetInt64(5),
            });
        }

        return parameters;
    }

    private static void ConfigureCommand(MySqlCommand command, DbExecutionRequest request, DataConnectionDescriptor descriptor)
    {
        var timeout = request.CommandTimeoutSeconds ?? descriptor.DefaultCommandTimeoutSeconds;
        if (timeout is { } t)
        {
            command.CommandTimeout = t;
        }

        switch (request.ObjectType)
        {
            case DbObjectType.StoredProcedure:
                // The driver builds the CALL and round-trips OUT / INOUT parameters via session variables.
                command.CommandType = CommandType.StoredProcedure;
                command.CommandText = $"{request.Schema}.{request.ObjectName}";
                AddParameters(command, request.Parameters);
                break;

            case DbObjectType.ScalarFunction:
                command.CommandType = CommandType.Text;
                command.CommandText = $"SELECT {Quote(request.Schema)}.{Quote(request.ObjectName)}({ArgumentList(request.Parameters)}) AS `Value`";
                AddParameters(command, request.Parameters);
                break;

            case DbObjectType.TableValuedFunction:
                throw new NotSupportedException("MySQL does not support table-valued functions.");

            default:
                throw new NotSupportedException($"Unsupported object type '{request.ObjectType}'.");
        }
    }

    private static string ArgumentList(IReadOnlyList<WeirParameter> parameters) =>
        string.Join(", ", parameters
            .Where(p => p.Direction is WeirDirection.Input or WeirDirection.InputOutput)
            .Select(p => "@" + Strip(p.Name)));

    private static void AddParameters(MySqlCommand command, IReadOnlyList<WeirParameter> parameters)
    {
        foreach (var wp in parameters)
        {
            if (wp.DbType == WeirDbType.Structured)
            {
                throw new NotSupportedException(
                    "MySQL does not support table-valued parameters. Pass sets as JSON parameters instead.");
            }

            var parameter = new MySqlParameter
            {
                ParameterName = Strip(wp.Name),
                Direction = MapDirection(wp.Direction),
                MySqlDbType = MySqlDbTypeMapper.Map(wp.DbType),
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

    private static ParameterDirection MapDirection(WeirDirection direction) => direction switch
    {
        WeirDirection.Input => ParameterDirection.Input,
        WeirDirection.Output => ParameterDirection.Output,
        WeirDirection.InputOutput => ParameterDirection.InputOutput,
        // MySQL has no procedure RETURN value; treat it as an input.
        _ => ParameterDirection.Input,
    };

    private static string Quote(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    private static string Strip(string name) => name.StartsWith('@') ? name[1..] : name;
}
