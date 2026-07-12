using System.Data;
using Microsoft.Data.SqlClient.Server;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Connectors.SqlServer;

/// <summary>Builds the SqlClient value for a table-valued parameter from a <see cref="TableParameter"/>.</summary>
internal static class TableValuedParameters
{
    /// <summary>
    /// Returns the value to assign to a <see cref="SqlDbType.Structured"/> parameter. An empty table
    /// is sent as <see cref="DBNull.Value"/>, which SQL Server interprets as an empty TVP.
    /// Cell values in each row must align to <see cref="TableParameter.Columns"/> order.
    /// </summary>
    public static object BuildValue(TableParameter table) =>
        table.Rows.Count == 0 ? DBNull.Value : Enumerate(table);

    /// <summary>Yields one <see cref="SqlDataRecord"/> per row, aligned to the column metadata.</summary>
    /// <param name="table">The table parameter to enumerate.</param>
    /// <returns>The row records streamed to SqlClient.</returns>
    private static IEnumerable<SqlDataRecord> Enumerate(TableParameter table)
    {
        var meta = new SqlMetaData[table.Columns.Count];
        for (var i = 0; i < table.Columns.Count; i++)
        {
            meta[i] = ToMetaData(table.Columns[i]);
        }

        foreach (var row in table.Rows)
        {
            var record = new SqlDataRecord(meta);
            for (var i = 0; i < meta.Length; i++)
            {
                var value = i < row.Count ? row[i] : null;
                record.SetValue(i, value ?? DBNull.Value);
            }

            yield return record;
        }
    }

    /// <summary>Builds the SqlClient column metadata for one TVP column, honoring the introspected size/precision/scale.</summary>
    /// <param name="column">The column definition.</param>
    /// <returns>The column metadata.</returns>
    private static SqlMetaData ToMetaData(TvpColumn column)
    {
        var sqlType = SqlDbTypeMapper.Map(column.DbType);
        return sqlType switch
        {
            SqlDbType.NVarChar or SqlDbType.VarChar or SqlDbType.VarBinary or SqlDbType.Char or SqlDbType.NChar =>
                new SqlMetaData(column.Name, sqlType, column.Size is > 0 ? column.Size.Value : -1L),
            SqlDbType.Decimal =>
                new SqlMetaData(column.Name, sqlType, column.Precision ?? 18, column.Scale ?? 0),
            // Temporal types (Time / DateTime2 / DateTimeOffset) use the default fractional-seconds
            // scale; the server coerces each value to the actual table-type column definition on insert.
            _ => new SqlMetaData(column.Name, sqlType),
        };
    }
}
