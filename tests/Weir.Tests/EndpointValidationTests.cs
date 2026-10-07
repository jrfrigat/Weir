using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

/// <summary>
/// Covers the checks that run before an endpoint is stored. Each one stands for a call that would
/// otherwise have failed at run time, on a request that did nothing wrong.
/// </summary>
public class EndpointValidationTests
{
    private static EndpointDefinition Endpoint(
        EndpointOperation operation,
        DbObjectType objectType,
        DictionaryPolicy? dictionary = null,
        ImportPolicy? import = null) => new()
        {
            Route = "x",
            ConnectionName = "default",
            ObjectName = "Thing",
            ObjectType = objectType,
            Operation = operation,
            Dictionary = dictionary,
            Import = import,
        };

    [Fact]
    public void Invoke_AgainstAProcedureIsFine()
    {
        Assert.Empty(EndpointValidation.Validate(Endpoint(EndpointOperation.Invoke, DbObjectType.StoredProcedure)));
    }

    [Fact]
    public void Invoke_AgainstATableIsRefused()
    {
        var errors = EndpointValidation.Validate(Endpoint(EndpointOperation.Invoke, DbObjectType.Table));
        Assert.Contains("ObjectType", errors.Keys);
    }

    [Fact]
    public void Dictionary_NeedsAPolicy()
    {
        var errors = EndpointValidation.Validate(Endpoint(EndpointOperation.Dictionary, DbObjectType.Table));
        Assert.Contains("Dictionary", errors.Keys);
    }

    [Fact]
    public void Dictionary_AgainstAProcedureIsRefused()
    {
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Dictionary, DbObjectType.StoredProcedure,
            dictionary: new DictionaryPolicy { LabelColumn = "Name" }));

        Assert.Contains("ObjectType", errors.Keys);
    }

    [Fact]
    public void Dictionary_OverAViewIsFine()
    {
        Assert.Empty(EndpointValidation.Validate(Endpoint(
            EndpointOperation.Dictionary, DbObjectType.View,
            dictionary: new DictionaryPolicy { LabelColumn = "Name" })));
    }

    [Fact]
    public void Dictionary_PagingWithoutAnOrderIsRefused()
    {
        // Paging an unordered result set returns an arbitrary subset that can differ between two
        // identical calls, so page 2 may repeat or skip rows from page 1.
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Dictionary, DbObjectType.Table,
            dictionary: new DictionaryPolicy { AllowPaging = true }));

        Assert.Contains("OrderBy", errors.Keys);
    }

    [Fact]
    public void Dictionary_UnpagedNeedsNoOrder()
    {
        Assert.Empty(EndpointValidation.Validate(Endpoint(
            EndpointOperation.Dictionary, DbObjectType.Table,
            dictionary: new DictionaryPolicy { AllowPaging = false })));
    }

    [Fact]
    public void Dictionary_DefaultPageLargerThanTheMaximumIsRefused()
    {
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Dictionary, DbObjectType.Table,
            dictionary: new DictionaryPolicy { LabelColumn = "Name", DefaultPageSize = 500, MaxPageSize = 100 }));

        Assert.Contains("DefaultPageSize", errors.Keys);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("page")]
    [InlineData("pageSize")]
    [InlineData("sort")]
    [InlineData("sortDir")]
    [InlineData("__sort")]
    [InlineData("Page")]
    [InlineData("SEARCH")]
    public void Dictionary_FilterNamedAfterARequestFormatKeyIsRefused(string name)
    {
        // The binder writes its own values under these names, so a filter using one would lose its value -
        // and with it the only thing that told two cache keys apart.
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Dictionary, DbObjectType.Table,
            dictionary: new DictionaryPolicy
            {
                LabelColumn = "Name",
                Filters = [new DictionaryFilter { Column = "Category", ParameterName = name }],
            }));

        Assert.Contains("Filters", errors.Keys);
        Assert.Contains(name, errors["Filters"][0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dictionary_FilterNameThatOnlyResemblesARequestKeyIsFine()
    {
        // The refusal above must not be so broad that it catches names that merely read similarly.
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Dictionary, DbObjectType.Table,
            dictionary: new DictionaryPolicy
            {
                LabelColumn = "Name",
                Filters =
                [
                    new DictionaryFilter { Column = "Category", ParameterName = "category" },
                    new DictionaryFilter { Column = "SearchText", ParameterName = "query" },
                    new DictionaryFilter { Column = "PageNo", ParameterName = "pageNumber" },
                ],
            }));

        Assert.Empty(errors);
    }

    [Fact]
    public void Import_NeedsAPolicy()
    {
        var errors = EndpointValidation.Validate(Endpoint(EndpointOperation.Import, DbObjectType.Table));
        Assert.Contains("Import", errors.Keys);
    }

    [Fact]
    public void Import_IntoAViewIsRefused()
    {
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Import, DbObjectType.View,
            import: new ImportPolicy { Columns = [new ImportColumn { Name = "Name" }] }));

        Assert.Contains("ObjectType", errors.Keys);
    }

    [Fact]
    public void Import_NeedsAtLeastOneColumn()
    {
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Import, DbObjectType.Table, import: new ImportPolicy()));

        Assert.Contains("Columns", errors.Keys);
    }

    [Fact]
    public void Import_UpsertNeedsKeyColumns()
    {
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Import, DbObjectType.Table,
            import: new ImportPolicy
            {
                Columns = [new ImportColumn { Name = "Name" }],
                Mode = ImportMode.Upsert,
            }));

        Assert.Contains("KeyColumns", errors.Keys);
    }

    [Fact]
    public void Import_UpsertKeyMustBeOneOfTheImportedColumns()
    {
        // A key the payload never carries matches nothing, so every row would insert and the upsert
        // would behave as a plain insert until the first constraint violation.
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Import, DbObjectType.Table,
            import: new ImportPolicy
            {
                Columns = [new ImportColumn { Name = "Name" }],
                Mode = ImportMode.Upsert,
                KeyColumns = ["Id"],
            }));

        Assert.Contains("KeyColumns", errors.Keys);
    }

    [Fact]
    public void Import_WellFormedUpsertIsFine()
    {
        Assert.Empty(EndpointValidation.Validate(Endpoint(
            EndpointOperation.Import, DbObjectType.Table,
            import: new ImportPolicy
            {
                Columns = [new ImportColumn { Name = "Name" }, new ImportColumn { Name = "Price" }],
                Mode = ImportMode.Upsert,
                KeyColumns = ["Name"],
            })));
    }

    [Fact]
    public void Import_NegativeLimitsAreRefused()
    {
        var errors = EndpointValidation.Validate(Endpoint(
            EndpointOperation.Import, DbObjectType.Table,
            import: new ImportPolicy
            {
                Columns = [new ImportColumn { Name = "Name" }],
                BatchSize = -1,
                MaxRows = -5,
            }));

        Assert.Contains("BatchSize", errors.Keys);
        Assert.Contains("MaxRows", errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Cache_EnabledWithANonPositiveTtlIsRefused(int ttl)
    {
        // A non-positive TTL reaches the response cache's options and makes it throw on a live request.
        var endpoint = Endpoint(EndpointOperation.Invoke, DbObjectType.StoredProcedure) with
        {
            Cache = new CachePolicy { Enabled = true, TtlSeconds = ttl },
        };

        Assert.Contains("Cache.TtlSeconds", EndpointValidation.Validate(endpoint).Keys);
    }

    [Fact]
    public void Cache_EnabledWithATtlAboveTheCapIsRefused()
    {
        var endpoint = Endpoint(EndpointOperation.Invoke, DbObjectType.StoredProcedure) with
        {
            Cache = new CachePolicy { Enabled = true, TtlSeconds = (int)CacheBounds.TtlSeconds.Max + 1 },
        };

        Assert.Contains("Cache.TtlSeconds", EndpointValidation.Validate(endpoint).Keys);
    }

    [Fact]
    public void Cache_EnabledWithAnInRangeTtlIsFine()
    {
        var endpoint = Endpoint(EndpointOperation.Invoke, DbObjectType.StoredProcedure) with
        {
            Cache = new CachePolicy { Enabled = true, TtlSeconds = 60 },
        };

        Assert.Empty(EndpointValidation.Validate(endpoint));
    }

    [Fact]
    public void Cache_DisabledWithAnUnsetTtlIsFine()
    {
        // Every seed file stores ttlSeconds 0 with the cache off, and nothing reads it in that state.
        var endpoint = Endpoint(EndpointOperation.Invoke, DbObjectType.StoredProcedure) with
        {
            Cache = new CachePolicy { Enabled = false, TtlSeconds = 0 },
        };

        Assert.Empty(EndpointValidation.Validate(endpoint));
    }
}
