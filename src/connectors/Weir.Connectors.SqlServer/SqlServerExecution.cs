using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Weir.Abstractions;
using Weir.Contracts;
using ParameterDirection = System.Data.ParameterDirection;

namespace Weir.Connectors.SqlServer;

/// <summary>
/// A live SQL Server execution. Result sets are streamed from <see cref="Reader"/>; once the caller
/// has read them, <see cref="CompleteAsync"/> closes the reader and captures output parameters, the
/// return value, affected-row count and any informational messages.
/// </summary>
internal sealed class SqlServerExecution : IDbExecution
{
    /// <summary>The open connection, disposed when the execution is disposed.</summary>
    private readonly SqlConnection _connection;

    /// <summary>The command that produced the reader; holds the output/return parameters.</summary>
    private readonly SqlCommand _command;

    /// <summary>The live result reader.</summary>
    private readonly SqlDataReader _reader;

    /// <summary>Captured informational messages, shared with the connector's info-message handler.</summary>
    private readonly List<SqlMessage> _messages;

    /// <summary>The info-message handler to detach on dispose.</summary>
    private readonly SqlInfoMessageEventHandler _handler;

    /// <summary>Whether <see cref="CompleteAsync"/> has run.</summary>
    private bool _completed;

    /// <summary>Whether the execution has been disposed.</summary>
    private bool _disposed;

    /// <summary>Creates the execution over the open connection, command and reader.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="command">The executed command.</param>
    /// <param name="reader">The live reader.</param>
    /// <param name="messages">Shared message list populated by the info-message handler.</param>
    /// <param name="handler">The info-message handler to detach on dispose.</param>
    public SqlServerExecution(
        SqlConnection connection,
        SqlCommand command,
        SqlDataReader reader,
        List<SqlMessage> messages,
        SqlInfoMessageEventHandler handler)
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
    public int? ReturnValue { get; private set; }

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

        // Output parameters, the return value and RecordsAffected are only populated once the entire
        // result stream has been consumed and the reader closed. Drain any result sets the caller left
        // unread (e.g. after a row-cap truncation) so those values are not silently lost.
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
        foreach (SqlParameter parameter in _command.Parameters)
        {
            switch (parameter.Direction)
            {
                case ParameterDirection.Output:
                case ParameterDirection.InputOutput:
                    outputs[Strip(parameter.ParameterName)] = parameter.Value is DBNull ? null : parameter.Value;
                    break;
                case ParameterDirection.ReturnValue:
                    ReturnValue = parameter.Value as int?;
                    break;
                default:
                    break;
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
        _connection.InfoMessage -= _handler;

        if (!_reader.IsClosed)
        {
            await _reader.DisposeAsync();
        }

        await _command.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>Removes a leading '@' from a parameter name so outputs are keyed by logical name.</summary>
    /// <param name="name">The driver parameter name.</param>
    /// <returns>The name without a leading '@'.</returns>
    private static string Strip(string name) => name.StartsWith('@') ? name[1..] : name;
}
