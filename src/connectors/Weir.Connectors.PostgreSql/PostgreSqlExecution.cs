using System.Data;
using System.Data.Common;
using Npgsql;
using Weir.Abstractions;
using Weir.Contracts;
using ParameterDirection = System.Data.ParameterDirection;

namespace Weir.Connectors.PostgreSql;

/// <summary>
/// A live PostgreSQL execution. Result sets are streamed from <see cref="Reader"/>; once the caller
/// has read them, <see cref="CompleteAsync"/> closes the reader and captures output / INOUT
/// parameters, the affected-row count and any notice messages raised during execution.
/// </summary>
internal sealed class PostgreSqlExecution : IDbExecution
{
    /// <summary>The open connection, disposed when the execution is disposed.</summary>
    private readonly NpgsqlConnection _connection;

    /// <summary>The command that produced the reader; holds the output/INOUT parameters.</summary>
    private readonly NpgsqlCommand _command;

    /// <summary>The live result reader.</summary>
    private readonly NpgsqlDataReader _reader;

    /// <summary>Captured notice messages, shared with the connector's notice handler.</summary>
    private readonly List<SqlMessage> _messages;

    /// <summary>The notice handler to detach on dispose.</summary>
    private readonly NoticeEventHandler _handler;

    /// <summary>Whether <see cref="CompleteAsync"/> has run.</summary>
    private bool _completed;

    /// <summary>Whether the execution has been disposed.</summary>
    private bool _disposed;

    /// <summary>Creates the execution handle over the open connection, command and reader.</summary>
    public PostgreSqlExecution(
        NpgsqlConnection connection,
        NpgsqlCommand command,
        NpgsqlDataReader reader,
        List<SqlMessage> messages,
        NoticeEventHandler handler)
    {
        _connection = connection;
        _command = command;
        _reader = reader;
        _messages = messages;
        _handler = handler;
        Outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public DbDataReader Reader => _reader;

    /// <inheritdoc />
    public IReadOnlyList<SqlMessage> Messages => _messages;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Outputs { get; private set; }

    /// <inheritdoc />
    // PostgreSQL functions and procedures do not have SQL Server's integer RETURN value.
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

        // Drain any result sets the caller left unread (e.g. after a row-cap truncation) so output /
        // INOUT parameters are populated; they are only valid once the reader has been fully consumed.
        if (!_reader.IsClosed)
        {
            try
            {
                while (await _reader.NextResultAsync(cancellationToken))
                {
                }
            }
            catch (OperationCanceledException)
            {
                // The caller cancelled (e.g. client disconnect); close without capturing outputs.
            }

            await _reader.CloseAsync();
        }

        RecordsAffected = _reader.RecordsAffected;

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (NpgsqlParameter parameter in _command.Parameters)
        {
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
            {
                outputs[parameter.ParameterName] = parameter.Value is DBNull ? null : parameter.Value;
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
        _connection.Notice -= _handler;

        if (!_reader.IsClosed)
        {
            await _reader.DisposeAsync();
        }

        await _command.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
