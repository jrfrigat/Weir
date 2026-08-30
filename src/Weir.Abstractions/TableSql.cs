using System.Globalization;
using System.Text;
using Weir.Contracts;

namespace Weir.Abstractions;

/// <summary>
/// The handful of things that differ between engines when Weir composes a statement itself, rather
/// than calling something the database already declares. A connector supplies one of these and gets
/// the dictionary and import statements built for it, so the composition - and the rule that every
/// caller-supplied value is a bound parameter and never text - lives in one place instead of once per
/// provider.
/// </summary>
public abstract class SqlDialect
{
    /// <summary>Quotes an identifier so it cannot be read as anything but a name.</summary>
    /// <param name="identifier">The raw schema, table or column name.</param>
    /// <returns>The quoted identifier.</returns>
    public abstract string Quote(string identifier);

    /// <summary>Renders a reference to a bound parameter.</summary>
    /// <param name="name">The parameter name, without any prefix.</param>
    /// <returns>The parameter reference as it appears in the statement.</returns>
    public virtual string Parameter(string name) => "@" + name;

    /// <summary>
    /// The pattern-matching operator used for <see cref="DictionaryOperator.Contains"/> and
    /// <see cref="DictionaryOperator.StartsWith"/>. Engines whose default collation already ignores
    /// case use <c>LIKE</c>; the rest need their case-insensitive form, because a lookup that only
    /// matches the casing the user happened to type is not a search.
    /// </summary>
    public virtual string LikeOperator => "LIKE";

    /// <summary>
    /// Neutralizes the pattern metacharacters in a caller-supplied search term. Without it a search for
    /// "100%" matches every row beginning with "100" and one for "_" matches everything: the term stops
    /// being a term and becomes a pattern the caller did not know they were writing. The escaping is per
    /// engine and has no sensible default - SQL Server wraps a metacharacter in a character class, while
    /// PostgreSQL escapes it with a backslash and would read the brackets literally.
    /// </summary>
    /// <param name="term">The raw search term.</param>
    /// <returns>The term with its metacharacters neutralized.</returns>
    public abstract string EscapeLikeTerm(string term);

    /// <summary>Appends the paging clause to a statement that already carries its ordering.</summary>
    /// <param name="sql">The statement built so far.</param>
    /// <param name="skipParameter">Parameter reference holding the row offset.</param>
    /// <param name="takeParameter">Parameter reference holding the row count.</param>
    public abstract void AppendPaging(StringBuilder sql, string skipParameter, string takeParameter);

    /// <summary>
    /// Assembles the statement that writes one batch of rows. The rows arrive as a ready-made VALUES
    /// list of parameter references, because building that - and binding the values behind it - is the
    /// same everywhere; what differs is the statement wrapped around it.
    /// <para>
    /// The whole statement is the dialect's to build, not a clause appended to a fixed INSERT, because
    /// an upsert is not an insert with a suffix on every engine: PostgreSQL bolts
    /// <c>ON CONFLICT DO UPDATE</c> onto the insert, while SQL Server's is a <c>MERGE</c>, which puts
    /// the same VALUES list in a different place and reaches it under a source alias.
    /// </para>
    /// </summary>
    /// <param name="table">The quoted, schema-qualified target table.</param>
    /// <param name="columns">All target columns, quoted, in the order each row's cells are given.</param>
    /// <param name="valuesList">The row tuples, e.g. <c>(@w0, @w1), (@w2, @w3)</c>.</param>
    /// <param name="mode">What to do with a row whose key already exists.</param>
    /// <param name="keyColumns">The key columns, quoted. Non-empty when <paramref name="mode"/> is an upsert.</param>
    /// <returns>The statement text.</returns>
    public abstract string BuildWrite(
        string table,
        IReadOnlyList<string> columns,
        string valuesList,
        ImportMode mode,
        IReadOnlyList<string> keyColumns);

    /// <summary>
    /// The most parameters one command may carry. The batch size is lowered to fit this, since a batch
    /// that exceeds it fails at the driver rather than in the database and does so only once the data
    /// is wide enough - which is to say, in production and not in testing.
    /// </summary>
    public virtual int MaxParametersPerCommand => 2000;
}

/// <summary>A composed statement and the parameters it binds.</summary>
/// <param name="Text">The statement text. Every caller-supplied value in it is a parameter reference.</param>
/// <param name="Parameters">The parameters to bind, in no particular order.</param>
public readonly record struct SqlStatement(string Text, IReadOnlyList<WeirParameter> Parameters);

/// <summary>
/// Builds the statements behind dictionary and import endpoints. Identifiers come from endpoint
/// metadata, which only an admin can write, and are quoted on the way in; every value that came from
/// the request is bound as a parameter. Nothing a caller sends is ever concatenated into the text.
/// </summary>
public static class TableSql
{
    /// <summary>Prefix for the generated parameter names, kept clear of anything an endpoint declares.</summary>
    private const string ParameterPrefix = "w";

    /// <summary>
    /// Builds the SELECT behind a dictionary read: projection, filters, search, ordering and paging.
    /// </summary>
    /// <param name="dialect">The engine's dialect.</param>
    /// <param name="schema">Target schema.</param>
    /// <param name="objectName">Target table or view.</param>
    /// <param name="query">The resolved read.</param>
    /// <returns>The statement and its parameters.</returns>
    public static SqlStatement Select(SqlDialect dialect, string schema, string objectName, DictionaryQuery query)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new List<WeirParameter>();
        var sql = new StringBuilder("SELECT ");
        AppendProjection(dialect, sql, query.Policy.Columns);
        sql.Append(" FROM ").Append(Qualify(dialect, schema, objectName));
        AppendWhere(dialect, sql, query, parameters);
        AppendOrderBy(dialect, sql, query);

        if (query.Skip is { } skip && query.Take is { } take)
        {
            var skipName = Next(parameters);
            var takeName = Next(parameters, 1);
            parameters.Add(new WeirParameter { Name = skipName, DbType = WeirDbType.Int32, Value = skip });
            parameters.Add(new WeirParameter { Name = takeName, DbType = WeirDbType.Int32, Value = take });
            dialect.AppendPaging(sql, dialect.Parameter(skipName), dialect.Parameter(takeName));
        }

        return new SqlStatement(sql.ToString(), parameters);
    }

    /// <summary>
    /// Builds the COUNT behind <see cref="DictionaryPolicy.IncludeTotalCount"/>: the same filters and
    /// search as the read it accompanies, without its projection, ordering or page.
    /// </summary>
    /// <param name="dialect">The engine's dialect.</param>
    /// <param name="schema">Target schema.</param>
    /// <param name="objectName">Target table or view.</param>
    /// <param name="query">The resolved read.</param>
    /// <returns>The statement and its parameters.</returns>
    public static SqlStatement Count(SqlDialect dialect, string schema, string objectName, DictionaryQuery query)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new List<WeirParameter>();
        var sql = new StringBuilder("SELECT COUNT(*) FROM ").Append(Qualify(dialect, schema, objectName));
        AppendWhere(dialect, sql, query, parameters);
        return new SqlStatement(sql.ToString(), parameters);
    }

    /// <summary>
    /// Builds one INSERT carrying <paramref name="count"/> rows starting at <paramref name="offset"/>,
    /// as a multi-row VALUES list. Splitting a payload into batches is the caller's job, since only it
    /// knows whether the batches share a transaction.
    /// </summary>
    /// <param name="dialect">The engine's dialect.</param>
    /// <param name="schema">Target schema.</param>
    /// <param name="objectName">Target table.</param>
    /// <param name="payload">The rows and their columns.</param>
    /// <param name="offset">Index of the first row in this batch.</param>
    /// <param name="count">Number of rows in this batch.</param>
    /// <returns>The statement and its parameters.</returns>
    public static SqlStatement Insert(
        SqlDialect dialect, string schema, string objectName, ImportPayload payload, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(payload);

        var table = Qualify(dialect, schema, objectName);
        var quoted = payload.Columns.Select(c => dialect.Quote(c.Name)).ToArray();
        var parameters = new List<WeirParameter>(count * quoted.Length);

        var sql = new StringBuilder();
        for (var row = 0; row < count; row++)
        {
            if (row > 0)
            {
                sql.Append(", ");
            }

            sql.Append('(');
            var cells = payload.Rows[offset + row];
            for (var column = 0; column < payload.Columns.Count; column++)
            {
                if (column > 0)
                {
                    sql.Append(", ");
                }

                var definition = payload.Columns[column];
                var name = Next(parameters);
                parameters.Add(new WeirParameter
                {
                    Name = name,
                    DbType = definition.DbType,
                    Value = column < cells.Count ? cells[column] : null,
                    Size = definition.Size,
                    Precision = definition.Precision,
                    Scale = definition.Scale,
                });
                sql.Append(dialect.Parameter(name));
            }

            sql.Append(')');
        }

        var keys = payload.Mode == ImportMode.Upsert
            ? payload.KeyColumns.Select(dialect.Quote).ToArray()
            : [];

        return new SqlStatement(dialect.BuildWrite(table, quoted, sql.ToString(), payload.Mode, keys), parameters);
    }

    /// <summary>Builds the statement that empties the target table before a <see cref="ImportMode.Replace"/>.</summary>
    /// <param name="dialect">The engine's dialect.</param>
    /// <param name="schema">Target schema.</param>
    /// <param name="objectName">Target table.</param>
    /// <returns>The statement text.</returns>
    public static string DeleteAll(SqlDialect dialect, string schema, string objectName)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        return "DELETE FROM " + Qualify(dialect, schema, objectName);
    }

    /// <summary>
    /// How many rows one command may carry without exceeding the driver's parameter cap, given the
    /// column count and the endpoint's requested batch size.
    /// </summary>
    /// <param name="dialect">The engine's dialect.</param>
    /// <param name="columnCount">Columns per row.</param>
    /// <param name="requested">The endpoint's batch size; zero or less uses the default.</param>
    /// <returns>Rows per command, at least one.</returns>
    public static int BatchSize(SqlDialect dialect, int columnCount, int requested)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        const int defaultBatch = 500;
        var batch = requested > 0 ? requested : defaultBatch;
        if (columnCount <= 0)
        {
            return batch;
        }

        // One command must fit the driver's parameter cap. A single row that cannot fit is still sent
        // as one row: the failure then names the real problem (a table too wide for one statement)
        // rather than appearing as a batch that silently wrote nothing.
        var fits = dialect.MaxParametersPerCommand / columnCount;
        return Math.Max(1, Math.Min(batch, fits));
    }

    /// <summary>Writes the projected column list, or <c>*</c> when the policy names none.</summary>
    private static void AppendProjection(SqlDialect dialect, StringBuilder sql, IReadOnlyList<DictionaryColumn> columns)
    {
        if (columns.Count == 0)
        {
            sql.Append('*');
            return;
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append(dialect.Quote(columns[i].Name));
            if (!string.IsNullOrWhiteSpace(columns[i].Alias))
            {
                sql.Append(" AS ").Append(dialect.Quote(columns[i].Alias!));
            }
        }
    }

    /// <summary>Writes the WHERE clause from the supplied filters and the search text.</summary>
    private static void AppendWhere(
        SqlDialect dialect, StringBuilder sql, DictionaryQuery query, List<WeirParameter> parameters)
    {
        var terms = new List<string>();

        foreach (var filter in query.Filters)
        {
            var term = FilterTerm(dialect, filter, parameters);
            if (term is not null)
            {
                terms.Add(term);
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Search) && query.Policy.SearchColumns.Count > 0)
        {
            var name = Next(parameters);
            parameters.Add(new WeirParameter
            {
                Name = name,
                DbType = WeirDbType.String,
                Value = "%" + dialect.EscapeLikeTerm(query.Search) + "%",
            });

            var matches = query.Policy.SearchColumns
                .Select(column => $"{dialect.Quote(column)} {dialect.LikeOperator} {dialect.Parameter(name)}");
            terms.Add("(" + string.Join(" OR ", matches) + ")");
        }

        if (terms.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", terms);
        }
    }

    /// <summary>Renders one filter, binding its value; returns null when the filter contributes nothing.</summary>
    private static string? FilterTerm(SqlDialect dialect, DictionaryFilterValue filter, List<WeirParameter> parameters)
    {
        var column = dialect.Quote(filter.Filter.Column);
        var op = filter.Filter.Operator;

        if (op == DictionaryOperator.In)
        {
            var values = filter.Values;
            if (values is null || values.Count == 0)
            {
                // An IN over nothing matches nothing. Saying so explicitly beats dropping the filter,
                // which would widen the result to everything - the opposite of what was asked.
                return "1 = 0";
            }

            var references = new List<string>(values.Count);
            foreach (var value in values)
            {
                var name = Next(parameters);
                parameters.Add(new WeirParameter { Name = name, DbType = filter.Filter.DbType, Value = value });
                references.Add(dialect.Parameter(name));
            }

            return $"{column} IN ({string.Join(", ", references)})";
        }

        // A null compares with IS / IS NOT; = NULL is never true and would silently return nothing.
        if (filter.Value is null)
        {
            return op switch
            {
                DictionaryOperator.Equals => $"{column} IS NULL",
                DictionaryOperator.NotEquals => $"{column} IS NOT NULL",
                _ => null,
            };
        }

        var parameterName = Next(parameters);
        var bound = op switch
        {
            DictionaryOperator.Contains => "%" + dialect.EscapeLikeTerm(Text(filter.Value)) + "%",
            DictionaryOperator.StartsWith => dialect.EscapeLikeTerm(Text(filter.Value)) + "%",
            _ => filter.Value,
        };

        parameters.Add(new WeirParameter
        {
            Name = parameterName,
            DbType = op is DictionaryOperator.Contains or DictionaryOperator.StartsWith
                ? WeirDbType.String
                : filter.Filter.DbType,
            Value = bound,
        });

        var reference = dialect.Parameter(parameterName);
        return op switch
        {
            DictionaryOperator.Equals => $"{column} = {reference}",
            DictionaryOperator.NotEquals => $"{column} <> {reference}",
            DictionaryOperator.GreaterThan => $"{column} > {reference}",
            DictionaryOperator.GreaterOrEqual => $"{column} >= {reference}",
            DictionaryOperator.LessThan => $"{column} < {reference}",
            DictionaryOperator.LessOrEqual => $"{column} <= {reference}",
            DictionaryOperator.Contains or DictionaryOperator.StartsWith =>
                $"{column} {dialect.LikeOperator} {reference}",
            _ => $"{column} = {reference}",
        };
    }

    /// <summary>Writes the ORDER BY clause, if the read has one.</summary>
    private static void AppendOrderBy(SqlDialect dialect, StringBuilder sql, DictionaryQuery query)
    {
        if (query.OrderBy.Count == 0)
        {
            return;
        }

        sql.Append(" ORDER BY ");
        for (var i = 0; i < query.OrderBy.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append(dialect.Quote(query.OrderBy[i].Column));
            if (query.OrderBy[i].Descending)
            {
                sql.Append(" DESC");
            }
        }
    }

    /// <summary>Joins schema and object into one quoted, qualified name.</summary>
    private static string Qualify(SqlDialect dialect, string schema, string objectName) =>
        string.IsNullOrWhiteSpace(schema)
            ? dialect.Quote(objectName)
            : dialect.Quote(schema) + "." + dialect.Quote(objectName);

    /// <summary>Names the next generated parameter, offset by any that the caller is about to add.</summary>
    private static string Next(List<WeirParameter> parameters, int lookahead = 0) =>
        ParameterPrefix + (parameters.Count + lookahead).ToString(CultureInfo.InvariantCulture);

    /// <summary>Renders a filter value as the text a LIKE pattern is built from.</summary>
    private static string Text(object value) =>
        value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
