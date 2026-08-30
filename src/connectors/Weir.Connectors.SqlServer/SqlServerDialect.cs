using System.Text;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Connectors.SqlServer;

/// <summary>
/// The SQL Server dialect for the statements Weir composes itself - dictionary reads and imports.
/// Stateless, so one instance serves every call.
/// </summary>
internal sealed class SqlServerDialect : SqlDialect
{
    /// <summary>The shared instance.</summary>
    public static readonly SqlServerDialect Instance = new();

    /// <summary>Not constructed elsewhere; <see cref="Instance"/> is the only one.</summary>
    private SqlServerDialect()
    {
    }

    /// <inheritdoc />
    public override string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>
    /// SQL Server compares under the column's collation, which is case-insensitive on a default
    /// installation, so plain LIKE already searches the way a user expects. A case-sensitive column is
    /// the exception and is left alone deliberately: forcing a collation here would defeat any index
    /// on the column and turn a lookup into a scan.
    /// </summary>
    public override string LikeOperator => "LIKE";

    /// <inheritdoc />
    public override string EscapeLikeTerm(string term) => term
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);

    /// <inheritdoc />
    public override void AppendPaging(StringBuilder sql, string skipParameter, string takeParameter)
    {
        ArgumentNullException.ThrowIfNull(sql);
        sql.Append(" OFFSET ").Append(skipParameter).Append(" ROWS FETCH NEXT ")
           .Append(takeParameter).Append(" ROWS ONLY");
    }

    /// <summary>
    /// The driver refuses a command with more than 2100 parameters, and two of those are spoken for by
    /// the plumbing, so the batch is sized against a round number below it rather than the exact cap.
    /// </summary>
    public override int MaxParametersPerCommand => 2000;

    /// <inheritdoc />
    public override string BuildWrite(
        string table,
        IReadOnlyList<string> columns,
        string valuesList,
        ImportMode mode,
        IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(keyColumns);

        var columnList = string.Join(", ", columns);
        if (mode != ImportMode.Upsert || keyColumns.Count == 0)
        {
            return $"INSERT INTO {table} ({columnList}) VALUES {valuesList}";
        }

        // MERGE, not INSERT ... ON DUPLICATE: SQL Server has no upsert clause on INSERT. The rows go in
        // as an inline table so the whole batch is matched in one pass rather than one statement per row.
        var source = string.Join(", ", columns);
        var match = string.Join(" AND ", keyColumns.Select(key => $"t.{key} = s.{key}"));
        var updates = columns
            .Where(column => !keyColumns.Contains(column, StringComparer.Ordinal))
            .Select(column => $"t.{column} = s.{column}")
            .ToArray();
        var insertValues = string.Join(", ", columns.Select(column => $"s.{column}"));

        var sql = new StringBuilder()
            .Append("MERGE INTO ").Append(table).Append(" AS t USING (VALUES ").Append(valuesList)
            .Append(") AS s (").Append(source).Append(") ON ").Append(match);

        // Every column being a key leaves nothing to update: the match itself is the whole row, so the
        // upsert degenerates to "insert what is missing" and MERGE rejects an empty SET list.
        if (updates.Length > 0)
        {
            sql.Append(" WHEN MATCHED THEN UPDATE SET ").AppendJoin(", ", updates);
        }

        sql.Append(" WHEN NOT MATCHED THEN INSERT (").Append(columnList)
           .Append(") VALUES (").Append(insertValues).Append(");");

        return sql.ToString();
    }
}
