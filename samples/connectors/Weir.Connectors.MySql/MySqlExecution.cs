using System.Data;
using System.Data.Common;
using MySqlConnector;
using Weir.Abstractions;
using Weir.Contracts;
using ParameterDirection = System.Data.ParameterDirection;

namespace Weir.Connectors.MySql;

/// <summary>
/// A live MySQL execution. Result sets are streamed from <see cref="Reader"/>; once the caller has
/// read them, <see cref="CompleteAsync"/> closes the reader and captures output / INOUT parameters
/// and the affected-row count. MySQL procedures have no integer RETURN value, and MySQL surfaces no
/// informational messages through the driver, so those are empty.
/// </summary>
internal sealed class MySqlExecution : IDbExecution
{
    private readonly MySqlConnection _connection;
    private readonly MySqlCommand _command;
    private readonly MySqlDataReader _reader;
    private bool _completed;
    private bool _disposed;

    /// <summary>Creates the execution handle over the open connection, command and reader.</summary>
    public MySqlExecution(MySqlConnection connection, MySqlCommand command, MySqlDataReader reader)
    {
        _connection = connection;
        _command = command;
        _reader = reader;
        Outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public DbDataReader Reader => _reader;

    /// <inheritdoc />
    public IReadOnlyList<SqlMessage> Messages => [];

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Outputs { get; private set; }

    /// <inheritdoc />
    public int? ReturnValue => null;

    /// <inheritdoc />
    public int RecordsAffected { get; private set; }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;

        if (!_reader.IsClosed)
        {
            await _reader.CloseAsync();
        }

        RecordsAffected = _reader.RecordsAffected;

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (MySqlParameter parameter in _command.Parameters)
        {
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
            {
                outputs[Strip(parameter.ParameterName)] = parameter.Value is DBNull ? null : parameter.Value;
            }
        }

        Outputs = outputs;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_reader.IsClosed)
        {
            await _reader.DisposeAsync();
        }

        await _command.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static string Strip(string name) => name.StartsWith('@') ? name[1..] : name;
}
