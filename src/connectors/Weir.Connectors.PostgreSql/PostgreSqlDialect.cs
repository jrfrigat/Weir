using System.Text;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Connectors.PostgreSql;

/// <summary>
/// The PostgreSQL dialect for the statements Weir composes itself - dictionary reads and imports.
/// Stateless, so one instance serves every call.
/// </summary>
internal sealed class PostgreSqlDialect : SqlDialect
{
    /// <summary>The shared instance.</summary>
    public static readonly PostgreSqlDialect Instance = new();

    /// <summary>Not constructed elsewhere; <see cref="Instance"/> is the only one.</summary>
    private PostgreSqlDialect()
    {
    }

    /// <inheritdoc />
    public override string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// PostgreSQL compares text case-sensitively, so plain LIKE would only find the casing the user
    /// happened to type. ILIKE is the case-insensitive form and is what a lookup means by "matches".
    /// </summary>
    public override string LikeOperator => "ILIKE";

    /// <summary>
    /// Backslash is the default escape character for LIKE and ILIKE, so a metacharacter is neutralized
    /// by preceding it with one. The backslash itself goes first, or it would escape the escape added
    /// after it.
    /// </summary>
    public override string EscapeLikeTerm(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    /// <inheritdoc />
    public override void AppendPaging(StringBuilder sql, string skipParameter, string takeParameter)
    {
        ArgumentNullException.ThrowIfNull(sql);
        sql.Append(" LIMIT ").Append(takeParameter).Append(" OFFSET ").Append(skipParameter);
    }

    /// <summary>
    /// The wire protocol carries a 16-bit parameter count, so 65535 is the hard ceiling. The batch is
    /// sized well below it: a command that large spends longer being planned than executed.
    /// </summary>
    public override int MaxParametersPerCommand => 8000;

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
        var sql = new StringBuilder()
            .Append("INSERT INTO ").Append(table).Append(" (").Append(columnList)
            .Append(") VALUES ").Append(valuesList);

        if (mode != ImportMode.Upsert || keyColumns.Count == 0)
        {
            return sql.ToString();
        }

        var updates = columns
            .Where(column => !keyColumns.Contains(column, StringComparer.Ordinal))
            .Select(column => $"{column} = EXCLUDED.{column}")
            .ToArray();

        sql.Append(" ON CONFLICT (").AppendJoin(", ", keyColumns).Append(')');

        // Every column being a key leaves nothing to update, and DO UPDATE with an empty SET is a
        // syntax error. DO NOTHING is the same outcome: the row is already exactly what was sent.
        if (updates.Length == 0)
        {
            sql.Append(" DO NOTHING");
        }
        else
        {
            sql.Append(" DO UPDATE SET ").AppendJoin(", ", updates);
        }

        return sql.ToString();
    }
}
