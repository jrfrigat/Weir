using Microsoft.Data.SqlClient.Server;
using Weir.Abstractions;
using Weir.Connectors.SqlServer;
using Weir.Contracts;
using Xunit;

namespace Weir.Tests;

public class TableValuedParameterTests
{
    private static TableParameter Table(params IReadOnlyList<object?>[] rows) => new()
    {
        Columns = [new TvpColumn { Name = "Sku" }, new TvpColumn { Name = "Qty", DbType = WeirDbType.Int32 }],
        Rows = rows,
    };

    [Fact]
    public void EmptyTable_IsSentAsNull()
    {
        // SqlClient accepts exactly one representation of "TVP with no rows": a null value. DBNull is
        // rejected ("Table-valued parameters cannot have the value DBNull") and so is a record sequence
        // with no elements ("The enumeration of SqlDataRecord has no records"), so an endpoint with an
        // optional TVP failed outright whenever the caller left it out.
        Assert.Null(TableValuedParameters.BuildValue(Table()));
    }

    [Fact]
    public void PopulatedTable_YieldsOneRecordPerRow()
    {
        var value = TableValuedParameters.BuildValue(Table(["A1", 2], ["B2", 3]));

        var records = Assert.IsAssignableFrom<IEnumerable<SqlDataRecord>>(value).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal("A1", records[0].GetString(0));
        Assert.Equal(2, records[0].GetInt32(1));
        Assert.Equal("B2", records[1].GetString(0));
        Assert.Equal(3, records[1].GetInt32(1));
    }

    [Fact]
    public void NullCell_IsSentAsDbNull()
    {
        // Inside a row the mapping inverts: a missing cell is DBNull, because that is how SqlClient
        // writes a NULL into a column. Only the table as a whole uses a null value.
        var value = TableValuedParameters.BuildValue(Table(["A1", null]));

        var record = Assert.IsAssignableFrom<IEnumerable<SqlDataRecord>>(value).Single();
        Assert.True(record.IsDBNull(1));
    }
}
