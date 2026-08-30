using System.Text;
using Weir.Abstractions;
using Weir.Contracts;
using Xunit;

namespace Weir.Tests;

/// <summary>
/// Covers the statements Weir composes for dictionary and import endpoints. The dialect here is a
/// stand-in for a real connector's, so these assert the shared composition - what is parameterized,
/// what is quoted, how a batch is sized - rather than any one engine's syntax.
/// </summary>
public class TableSqlTests
{
    private sealed class TestDialect : SqlDialect
    {
        public override string Quote(string identifier) => "[" + identifier + "]";

        public override string EscapeLikeTerm(string term) => term.Replace("%", "[%]", StringComparison.Ordinal);

        public override void AppendPaging(StringBuilder sql, string skipParameter, string takeParameter) =>
            sql.Append(" OFFSET ").Append(skipParameter).Append(" ROWS FETCH NEXT ").Append(takeParameter).Append(" ROWS ONLY");

        public override int MaxParametersPerCommand => 10;

        public override string BuildWrite(
            string table, IReadOnlyList<string> columns, string valuesList, ImportMode mode, IReadOnlyList<string> keyColumns) =>
            mode == ImportMode.Upsert && keyColumns.Count > 0
                ? $"UPSERT {table} ({string.Join(", ", columns)}) VALUES {valuesList} ON {string.Join(",", keyColumns)}"
                : $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES {valuesList}";
    }

    private static readonly TestDialect Dialect = new();

    private static DictionaryQuery Query(
        DictionaryPolicy? policy = null,
        IReadOnlyList<DictionaryFilterValue>? filters = null,
        string? search = null,
        IReadOnlyList<DictionarySort>? orderBy = null,
        int? skip = null,
        int? take = null) => new()
        {
            Policy = policy ?? new DictionaryPolicy(),
            Filters = filters ?? [],
            Search = search,
            OrderBy = orderBy ?? [],
            Skip = skip,
            Take = take,
        };

    [Fact]
    public void Select_WithNoColumns_ProjectsEverything()
    {
        var statement = TableSql.Select(Dialect, "sales", "Products", Query());
        Assert.Equal("SELECT * FROM [sales].[Products]", statement.Text);
        Assert.Empty(statement.Parameters);
    }

    [Fact]
    public void Select_ProjectsAndAliasesNamedColumns()
    {
        var policy = new DictionaryPolicy
        {
            Columns = [new DictionaryColumn { Name = "Id" }, new DictionaryColumn { Name = "Name", Alias = "Label" }],
        };

        var statement = TableSql.Select(Dialect, "sales", "Products", Query(policy));
        Assert.Equal("SELECT [Id], [Name] AS [Label] FROM [sales].[Products]", statement.Text);
    }

    [Fact]
    public void Select_BindsFilterValuesAsParameters()
    {
        var filter = new DictionaryFilter { Column = "Stock", Operator = DictionaryOperator.GreaterThan, DbType = WeirDbType.Int32 };
        var statement = TableSql.Select(
            Dialect, "sales", "Products", Query(filters: [new DictionaryFilterValue { Filter = filter, Value = 10 }]));

        Assert.Contains("WHERE [Stock] > @w0", statement.Text, StringComparison.Ordinal);
        Assert.Equal(10, Assert.Single(statement.Parameters).Value);
    }

    [Fact]
    public void Select_NullEqualsBecomesIsNull()
    {
        var filter = new DictionaryFilter { Column = "Retired", Operator = DictionaryOperator.Equals };
        var statement = TableSql.Select(
            Dialect, "sales", "Products", Query(filters: [new DictionaryFilterValue { Filter = filter, Value = null }]));

        // "= NULL" is never true, so a caller filtering on null would silently get nothing back.
        Assert.Contains("[Retired] IS NULL", statement.Text, StringComparison.Ordinal);
        Assert.Empty(statement.Parameters);
    }

    [Fact]
    public void Select_EmptyInMatchesNothingRatherThanEverything()
    {
        var filter = new DictionaryFilter { Column = "Id", Operator = DictionaryOperator.In };
        var statement = TableSql.Select(
            Dialect, "sales", "Products", Query(filters: [new DictionaryFilterValue { Filter = filter, Values = [] }]));

        Assert.Contains("1 = 0", statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_InBindsOneParameterPerValue()
    {
        var filter = new DictionaryFilter { Column = "Id", Operator = DictionaryOperator.In, DbType = WeirDbType.Int32 };
        var statement = TableSql.Select(
            Dialect, "sales", "Products", Query(filters: [new DictionaryFilterValue { Filter = filter, Values = [1, 2, 3] }]));

        Assert.Contains("[Id] IN (@w0, @w1, @w2)", statement.Text, StringComparison.Ordinal);
        Assert.Equal(3, statement.Parameters.Count);
    }

    [Fact]
    public void Select_SearchCoversEveryDeclaredColumnAndEscapesWildcards()
    {
        var policy = new DictionaryPolicy { SearchColumns = ["Name", "Code"] };
        var statement = TableSql.Select(Dialect, "sales", "Products", Query(policy, search: "50%"));

        Assert.Contains("([Name] LIKE @w0 OR [Code] LIKE @w0)", statement.Text, StringComparison.Ordinal);

        // Without escaping, searching for "50%" would match everything starting with "50".
        Assert.Equal("%50[%]%", Assert.Single(statement.Parameters).Value);
    }

    [Fact]
    public void Select_PagesAfterOrdering()
    {
        var statement = TableSql.Select(
            Dialect, "sales", "Products",
            Query(orderBy: [new DictionarySort { Column = "Name", Descending = true }], skip: 20, take: 10));

        Assert.Equal(
            "SELECT * FROM [sales].[Products] ORDER BY [Name] DESC OFFSET @w0 ROWS FETCH NEXT @w1 ROWS ONLY",
            statement.Text);
        Assert.Equal(20, statement.Parameters[0].Value);
        Assert.Equal(10, statement.Parameters[1].Value);
    }

    [Fact]
    public void Count_KeepsTheFiltersAndDropsTheRest()
    {
        var policy = new DictionaryPolicy { Columns = [new DictionaryColumn { Name = "Id" }] };
        var filter = new DictionaryFilter { Column = "Stock", Operator = DictionaryOperator.Equals, DbType = WeirDbType.Int32 };
        var query = Query(policy, [new DictionaryFilterValue { Filter = filter, Value = 5 }],
            orderBy: [new DictionarySort { Column = "Id" }], skip: 0, take: 10);

        var statement = TableSql.Count(Dialect, "sales", "Products", query);

        Assert.Equal("SELECT COUNT(*) FROM [sales].[Products] WHERE [Stock] = @w0", statement.Text);
        Assert.DoesNotContain("ORDER BY", statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", statement.Text, StringComparison.Ordinal);
    }

    private static ImportPayload Payload(ImportMode mode = ImportMode.Insert, params IReadOnlyList<object?>[] rows) => new()
    {
        Columns = [new ImportColumn { Name = "Name" }, new ImportColumn { Name = "Price", DbType = WeirDbType.Decimal }],
        Rows = rows,
        Mode = mode,
        KeyColumns = mode == ImportMode.Upsert ? ["Name"] : [],
    };

    [Fact]
    public void Insert_BuildsOneMultiRowValuesList()
    {
        var payload = Payload(ImportMode.Insert, ["a", 1m], ["b", 2m]);
        var statement = TableSql.Insert(Dialect, "dbo", "Widgets", payload, 0, 2);

        Assert.Equal("INSERT INTO [dbo].[Widgets] ([Name], [Price]) VALUES (@w0, @w1), (@w2, @w3)", statement.Text);
        Assert.Equal(4, statement.Parameters.Count);
        Assert.Equal("a", statement.Parameters[0].Value);
        Assert.Equal(2m, statement.Parameters[3].Value);
    }

    [Fact]
    public void Insert_HonoursTheOffsetSoBatchesDoNotRepeatRows()
    {
        var payload = Payload(ImportMode.Insert, ["a", 1m], ["b", 2m], ["c", 3m]);
        var statement = TableSql.Insert(Dialect, "dbo", "Widgets", payload, 2, 1);

        Assert.Equal("INSERT INTO [dbo].[Widgets] ([Name], [Price]) VALUES (@w0, @w1)", statement.Text);
        Assert.Equal("c", statement.Parameters[0].Value);
    }

    [Fact]
    public void Insert_UpsertGoesThroughTheDialect()
    {
        var payload = Payload(ImportMode.Upsert, ["a", 1m]);
        var statement = TableSql.Insert(Dialect, "dbo", "Widgets", payload, 0, 1);

        Assert.StartsWith("UPSERT [dbo].[Widgets]", statement.Text, StringComparison.Ordinal);
        Assert.EndsWith("ON [Name]", statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_ShortRowBindsTheMissingCellsAsNull()
    {
        var payload = Payload(ImportMode.Insert, ["only-name"]);
        var statement = TableSql.Insert(Dialect, "dbo", "Widgets", payload, 0, 1);

        Assert.Equal(2, statement.Parameters.Count);
        Assert.Null(statement.Parameters[1].Value);
    }

    [Fact]
    public void BatchSize_FitsTheDriversParameterCap()
    {
        // The stand-in dialect allows 10 parameters, so 3 columns fit 3 rows per command even though
        // the endpoint asked for 500. Exceeding the cap fails at the driver, and only once the data is
        // wide enough - which is to say in production rather than in testing.
        Assert.Equal(3, TableSql.BatchSize(Dialect, columnCount: 3, requested: 500));
    }

    [Fact]
    public void BatchSize_KeepsTheEndpointsChoiceWhenItFits()
    {
        Assert.Equal(4, TableSql.BatchSize(Dialect, columnCount: 2, requested: 4));
    }

    [Fact]
    public void BatchSize_NeverDropsBelowOneRow()
    {
        // A table too wide for one command still sends one row, so the failure names the real problem.
        Assert.Equal(1, TableSql.BatchSize(Dialect, columnCount: 50, requested: 500));
    }

    [Fact]
    public void DeleteAll_TargetsTheQualifiedTable()
    {
        Assert.Equal("DELETE FROM [dbo].[Widgets]", TableSql.DeleteAll(Dialect, "dbo", "Widgets"));
    }

    [Fact]
    public void Quote_EscapesAnIdentifierRatherThanTrustingIt()
    {
        // Identifiers come from endpoint metadata, not from callers, but they still go through quoting:
        // that is what keeps a name with a delimiter in it from ending the identifier early.
        var statement = TableSql.Select(Dialect, "s", "t", Query(new DictionaryPolicy
        {
            Columns = [new DictionaryColumn { Name = "od d" }],
        }));

        Assert.Contains("[od d]", statement.Text, StringComparison.Ordinal);
    }
}
