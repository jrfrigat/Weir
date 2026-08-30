using System.Data.Common;
using Weir.Contracts;

namespace Weir.Abstractions;

/// <summary>
/// Runs an import against an open connection: optional transaction, optional clear-out, then the rows
/// in batches. Everything here is the same on every engine - what differs is the statement text, which
/// comes from the <see cref="SqlDialect"/>, and how a parameter is bound, which the connector supplies.
/// </summary>
public static class TableImport
{
    /// <summary>
    /// Writes one payload and returns the execution the response is built from.
    /// </summary>
    /// <param name="connection">An already-open connection. The caller owns it and disposes it.</param>
    /// <param name="dialect">The engine's dialect.</param>
    /// <param name="schema">Target schema.</param>
    /// <param name="objectName">Target table.</param>
    /// <param name="payload">The rows to write.</param>
    /// <param name="commandTimeoutSeconds">Per-command timeout, or null for the driver default.</param>
    /// <param name="bind">Binds one composed statement's parameters onto a command.</param>
    /// <param name="messages">Informational messages captured by the connector, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An execution carrying the row count and the per-batch summary.</returns>
    public static async Task<RowCountExecution> RunAsync(
        DbConnection connection,
        SqlDialect dialect,
        string schema,
        string objectName,
        ImportPayload payload,
        int? commandTimeoutSeconds,
        Action<DbCommand, IReadOnlyList<WeirParameter>> bind,
        IReadOnlyList<SqlMessage>? messages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(bind);

        // Replace empties the table first, so it is transactional whatever the endpoint asked for:
        // outside a transaction there would be a window in which the table is simply empty, and a
        // failure part-way through would leave it that way.
        var transactional = payload.Transactional || payload.Mode == ImportMode.Replace;
        DbTransaction? transaction = transactional
            ? await connection.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var deleted = 0;
            if (payload.Mode == ImportMode.Replace)
            {
                await using var clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = TableSql.DeleteAll(dialect, schema, objectName);
                if (commandTimeoutSeconds is { } clearTimeout)
                {
                    clear.CommandTimeout = clearTimeout;
                }

                deleted = await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            var batchSize = TableSql.BatchSize(dialect, payload.Columns.Count, payload.BatchSize);
            var written = 0;
            var batches = 0;

            for (var offset = 0; offset < payload.Rows.Count; offset += batchSize)
            {
                var count = Math.Min(batchSize, payload.Rows.Count - offset);
                var statement = TableSql.Insert(dialect, schema, objectName, payload, offset, count);

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement.Text;
                if (commandTimeoutSeconds is { } timeout)
                {
                    command.CommandTimeout = timeout;
                }

                bind(command, statement.Parameters);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);

                // A MERGE reports matched and inserted rows together, a plain INSERT reports what it
                // inserted, and a provider that reports nothing useful returns -1. Falling back to the
                // batch size keeps the count honest for the last case rather than reporting a negative.
                written += affected >= 0 ? affected : count;
                batches++;
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            var outputs = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["rows"] = payload.Rows.Count,
                ["written"] = written,
                ["batches"] = batches,
                ["mode"] = payload.Mode.ToString(),
            };

            if (payload.Mode == ImportMode.Replace)
            {
                outputs["deleted"] = deleted;
            }

            return new RowCountExecution(written, outputs, messages);
        }
        catch
        {
            if (transaction is not null)
            {
                // A rollback that itself fails must not replace the failure that caused it: the
                // original exception is the one that says what went wrong.
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch (DbException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
