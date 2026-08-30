using System.Text.Json;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

/// <summary>
/// Covers what a caller may and may not ask a dictionary or import endpoint for. Most of these are
/// about the boundary: the request decides values, the endpoint decides which columns exist.
/// </summary>
public class TableRequestBinderTests
{
    private sealed class QueryValues : IValueSource
    {
        private readonly Dictionary<string, string> _values;

        public QueryValues(params (string Key, string Value)[] values) =>
            _values = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        public bool TryGet(string key, out string? value)
        {
            var found = _values.TryGetValue(key, out var text);
            value = text;
            return found;
        }
    }

    private static EndpointDefinition Dictionary(DictionaryPolicy policy) => new()
    {
        Route = "lookup",
        ConnectionName = "default",
        ObjectName = "Products",
        Schema = "sales",
        ObjectType = DbObjectType.Table,
        Operation = EndpointOperation.Dictionary,
        Dictionary = policy,
    };

    private static EndpointDefinition Import(ImportPolicy policy) => new()
    {
        Route = "load",
        ConnectionName = "default",
        ObjectName = "Widgets",
        ObjectType = DbObjectType.Table,
        Operation = EndpointOperation.Import,
        Import = policy,
    };

    private static WeirInvocation Invocation(EndpointDefinition endpoint, string? bodyJson = null, IValueSource? query = null)
    {
        JsonElement body = default;
        var hasBody = false;
        if (bodyJson is not null)
        {
            body = JsonDocument.Parse(bodyJson).RootElement;
            hasBody = true;
        }

        return new WeirInvocation
        {
            Endpoint = endpoint,
            Body = body,
            HasBody = hasBody,
            Query = query ?? EmptyValueSource.Instance,
        };
    }

    // ===== Dictionary reads =======================================================================

    [Fact]
    public void Dictionary_AppliesOnlyTheFiltersTheRequestSupplied()
    {
        var policy = new DictionaryPolicy
        {
            LabelColumn = "Name",
            Filters =
            [
                new DictionaryFilter { Column = "Stock", DbType = WeirDbType.Int32 },
                new DictionaryFilter { Column = "Category" },
            ],
        };

        var (query, _) = TableRequestBinder.BindDictionary(
            Invocation(Dictionary(policy), query: new QueryValues(("Stock", "5"))));

        var applied = Assert.Single(query.Filters);
        Assert.Equal("Stock", applied.Filter.Column);
        Assert.Equal(5, applied.Value);
    }

    [Fact]
    public void Dictionary_RefusesAMissingRequiredFilter()
    {
        var policy = new DictionaryPolicy
        {
            LabelColumn = "Name",
            Filters = [new DictionaryFilter { Column = "TenantId", Required = true }],
        };

        var ex = Assert.Throws<WeirValidationException>(
            () => TableRequestBinder.BindDictionary(Invocation(Dictionary(policy))));
        Assert.Contains("TenantId", ex.Errors.Keys);
    }

    [Fact]
    public void Dictionary_RefusesSearchWhenNoColumnIsSearchable()
    {
        // Ignoring it would let a client believe it had filtered and hand back the whole table as the
        // answer to its search.
        var policy = new DictionaryPolicy { LabelColumn = "Name" };

        var ex = Assert.Throws<WeirValidationException>(() => TableRequestBinder.BindDictionary(
            Invocation(Dictionary(policy), query: new QueryValues(("search", "abc")))));
        Assert.Contains("search", ex.Errors.Keys);
    }

    [Fact]
    public void Dictionary_TakesSearchWhenColumnsAreDeclared()
    {
        var policy = new DictionaryPolicy { LabelColumn = "Name", SearchColumns = ["Name"] };
        var (query, values) = TableRequestBinder.BindDictionary(
            Invocation(Dictionary(policy), query: new QueryValues(("search", "abc"))));

        Assert.Equal("abc", query.Search);
        Assert.Equal("abc", values["search"]);
    }

    [Fact]
    public void Dictionary_RefusesASortColumnTheEndpointDoesNotDeclare()
    {
        var policy = new DictionaryPolicy { LabelColumn = "Name", Columns = [new DictionaryColumn { Name = "Name" }] };

        var ex = Assert.Throws<WeirValidationException>(() => TableRequestBinder.BindDictionary(
            Invocation(Dictionary(policy), query: new QueryValues(("sort", "Secret")))));

        // The message names what IS sortable, so the caller is not left guessing.
        Assert.Contains("Name", ex.Errors["sort"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void Dictionary_AcceptsADeclaredSortColumnAndDirection()
    {
        var policy = new DictionaryPolicy { LabelColumn = "Name", Columns = [new DictionaryColumn { Name = "Price" }] };
        var (query, _) = TableRequestBinder.BindDictionary(Invocation(
            Dictionary(policy), query: new QueryValues(("sort", "price"), ("sortDir", "desc"))));

        var sort = Assert.Single(query.OrderBy);

        // The endpoint's spelling wins over the caller's, so the identifier in the statement is always
        // one the endpoint wrote.
        Assert.Equal("Price", sort.Column);
        Assert.True(sort.Descending);
    }

    [Fact]
    public void Dictionary_FallsBackToTheLabelColumnForOrdering()
    {
        var policy = new DictionaryPolicy { LabelColumn = "Name", ValueColumn = "Id" };
        var (query, _) = TableRequestBinder.BindDictionary(Invocation(Dictionary(policy)));

        Assert.Equal("Name", Assert.Single(query.OrderBy).Column);
    }

    [Fact]
    public void Dictionary_ClampsAnOversizedPageRatherThanRefusingIt()
    {
        var policy = new DictionaryPolicy { LabelColumn = "Name", DefaultPageSize = 25, MaxPageSize = 100 };
        var (query, _) = TableRequestBinder.BindDictionary(Invocation(
            Dictionary(policy), query: new QueryValues(("pageSize", "5000"), ("page", "3"))));

        Assert.Equal(100, query.Take);
        Assert.Equal(200, query.Skip);
    }

    [Fact]
    public void Dictionary_UsesTheDefaultPageWhenTheCallerAsksForNone()
    {
        var policy = new DictionaryPolicy { LabelColumn = "Name", DefaultPageSize = 25, MaxPageSize = 100 };
        var (query, _) = TableRequestBinder.BindDictionary(Invocation(Dictionary(policy)));

        Assert.Equal(25, query.Take);
        Assert.Equal(0, query.Skip);
    }

    [Fact]
    public void Dictionary_DoesNotPageWhenTheEndpointForbidsIt()
    {
        var policy = new DictionaryPolicy { LabelColumn = "Name", AllowPaging = false };
        var (query, _) = TableRequestBinder.BindDictionary(Invocation(
            Dictionary(policy), query: new QueryValues(("pageSize", "10"))));

        Assert.Null(query.Take);
        Assert.Null(query.Skip);
        Assert.False(query.IncludeTotalCount);
    }

    [Fact]
    public void Dictionary_SplitsACommaSeparatedInFilterFromTheQueryString()
    {
        var policy = new DictionaryPolicy
        {
            LabelColumn = "Name",
            Filters = [new DictionaryFilter { Column = "Id", Operator = DictionaryOperator.In, DbType = WeirDbType.Int32 }],
        };

        var (query, _) = TableRequestBinder.BindDictionary(Invocation(
            Dictionary(policy), query: new QueryValues(("Id", "1,2,3"))));

        Assert.Equal([1, 2, 3], Assert.Single(query.Filters).Values);
    }

    [Fact]
    public void Dictionary_ReadsAFilterUnderItsRequestKeyRatherThanItsColumn()
    {
        var policy = new DictionaryPolicy
        {
            LabelColumn = "Name",
            Filters = [new DictionaryFilter { Column = "CategoryId", ParameterName = "category", DbType = WeirDbType.Int32 }],
        };

        var (query, _) = TableRequestBinder.BindDictionary(Invocation(
            Dictionary(policy), query: new QueryValues(("category", "7"))));

        Assert.Equal(7, Assert.Single(query.Filters).Value);
    }

    // ===== Imports ================================================================================

    private static ImportPolicy TwoColumns(ImportMode mode = ImportMode.Insert, int maxRows = 0) => new()
    {
        Columns =
        [
            new ImportColumn { Name = "Name", Required = true },
            new ImportColumn { Name = "Price", DbType = WeirDbType.Decimal },
        ],
        Mode = mode,
        KeyColumns = mode == ImportMode.Upsert ? ["Name"] : [],
        MaxRows = maxRows,
    };

    [Fact]
    public void Import_CoercesEachCellToItsColumnType()
    {
        var payload = TableRequestBinder.BindImport(
            Invocation(Import(TwoColumns()), """{"rows":[{"Name":"a","Price":1.5},{"Name":"b","Price":2}]}"""), 0);

        Assert.Equal(2, payload.Rows.Count);
        Assert.Equal("a", payload.Rows[0][0]);
        Assert.Equal(1.5m, payload.Rows[0][1]);
    }

    [Fact]
    public void Import_AcceptsABareArrayBody()
    {
        var payload = TableRequestBinder.BindImport(
            Invocation(Import(TwoColumns()), """[{"Name":"a"}]"""), 0);

        Assert.Single(payload.Rows);
    }

    [Fact]
    public void Import_ReadsTheConfiguredRowsProperty()
    {
        var policy = TwoColumns() with { RowsProperty = "items" };
        var payload = TableRequestBinder.BindImport(
            Invocation(Import(policy), """{"items":[{"Name":"a"},{"Name":"b"}]}"""), 0);

        Assert.Equal(2, payload.Rows.Count);
    }

    [Fact]
    public void Import_RefusesARowMissingARequiredColumn()
    {
        var ex = Assert.Throws<WeirValidationException>(() => TableRequestBinder.BindImport(
            Invocation(Import(TwoColumns()), """{"rows":[{"Name":"a"},{"Price":2}]}"""), 0));

        // The key names the row, so a caller sending thousands can find the one that failed.
        Assert.Contains("rows[1]", ex.Errors.Keys);
    }

    [Fact]
    public void Import_ReadsAColumnFromItsSourceProperty()
    {
        var policy = new ImportPolicy
        {
            Columns = [new ImportColumn { Name = "FullName", SourceProperty = "name" }],
        };

        var payload = TableRequestBinder.BindImport(Invocation(Import(policy), """{"rows":[{"name":"a"}]}"""), 0);
        Assert.Equal("a", payload.Rows[0][0]);
    }

    [Fact]
    public void Import_UsesTheColumnDefaultForAnAbsentOptionalValue()
    {
        var policy = new ImportPolicy
        {
            Columns = [new ImportColumn { Name = "Source", DefaultValue = "bulk" }],
        };

        var payload = TableRequestBinder.BindImport(Invocation(Import(policy), """{"rows":[{}]}"""), 0);
        Assert.Equal("bulk", payload.Rows[0][0]);
    }

    [Fact]
    public void Import_RefusesMoreRowsThanTheSystemAllows()
    {
        var ex = Assert.Throws<WeirValidationException>(() => TableRequestBinder.BindImport(
            Invocation(Import(TwoColumns()), """{"rows":[{"Name":"a"},{"Name":"b"},{"Name":"c"}]}"""), 2));

        Assert.Contains("at most 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_TakesTheSmallerOfTheEndpointAndSystemLimits()
    {
        // The endpoint may tighten the system limit; it may not raise it.
        var ex = Assert.Throws<WeirValidationException>(() => TableRequestBinder.BindImport(
            Invocation(Import(TwoColumns(maxRows: 1)), """{"rows":[{"Name":"a"},{"Name":"b"}]}"""), 1000));

        Assert.Contains("at most 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesABodyThatCarriesNoRows()
    {
        Assert.Throws<WeirValidationException>(() => TableRequestBinder.BindImport(
            Invocation(Import(TwoColumns()), """{"nope":1}"""), 0));
    }

    [Fact]
    public void Import_RefusesAnUpsertWithNoKeyColumns()
    {
        var policy = TwoColumns(ImportMode.Upsert) with { KeyColumns = [] };

        Assert.Throws<WeirConfigurationException>(() => TableRequestBinder.BindImport(
            Invocation(Import(policy), """{"rows":[{"Name":"a"}]}"""), 0));
    }

    [Fact]
    public void Import_CarriesTheModeAndKeysToTheConnector()
    {
        var payload = TableRequestBinder.BindImport(
            Invocation(Import(TwoColumns(ImportMode.Upsert)), """{"rows":[{"Name":"a"}]}"""), 0);

        Assert.Equal(ImportMode.Upsert, payload.Mode);
        Assert.Equal("Name", Assert.Single(payload.KeyColumns));
    }
}
