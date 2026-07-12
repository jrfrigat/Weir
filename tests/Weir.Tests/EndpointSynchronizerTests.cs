using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class EndpointSynchronizerTests
{
    private static EndpointDefinition Endpoint(params EndpointParameter[] parameters) => new()
    {
        Id = Guid.NewGuid(),
        Route = "widgets",
        HttpMethod = "POST",
        ConnectionName = "default",
        ObjectName = "CreateWidget",
        Parameters = parameters,
    };

    [Fact]
    public void Adds_New_Database_Parameters_As_Body()
    {
        var endpoint = Endpoint();
        var db = new[] { new DbParameterDescriptor { Name = "Name", DbType = WeirDbType.String, Direction = ParameterDirection.Input } };

        var (updated, result) = EndpointSynchronizer.Merge(endpoint, db);

        Assert.Equal("updated", result.Status);
        Assert.Contains("Name", result.Added);
        var added = Assert.Single(updated.Parameters);
        Assert.Equal(ParameterSource.Body, added.Source);
        Assert.Equal(WeirDbType.String, added.DbType);
    }

    [Fact]
    public void Removes_Parameters_The_Database_No_Longer_Declares()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "gone", DbType = WeirDbType.Int32 });

        var (updated, result) = EndpointSynchronizer.Merge(endpoint, []);

        Assert.Equal("updated", result.Status);
        Assert.Contains("gone", result.Removed);
        Assert.Empty(updated.Parameters);
    }

    [Fact]
    public void Updates_Type_But_Preserves_Binding_Config()
    {
        var endpoint = Endpoint(new EndpointParameter
        {
            Name = "id",
            DbParameterName = "Id",
            Source = ParameterSource.Query,
            Required = true,
            ValidationRegex = "^[0-9]+$",
            DbType = WeirDbType.String, // stale type - the DB now says Int32
        });
        var db = new[] { new DbParameterDescriptor { Name = "Id", DbType = WeirDbType.Int32, Direction = ParameterDirection.Input } };

        var (updated, result) = EndpointSynchronizer.Merge(endpoint, db);

        Assert.Equal("updated", result.Status);
        Assert.Contains("Id", result.Updated);
        var param = Assert.Single(updated.Parameters);
        Assert.Equal(WeirDbType.Int32, param.DbType);       // shape synced
        Assert.Equal(ParameterSource.Query, param.Source);   // config preserved
        Assert.True(param.Required);
        Assert.Equal("^[0-9]+$", param.ValidationRegex);
    }

    [Fact]
    public void Reports_Unchanged_When_Nothing_Differs()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "Name", DbType = WeirDbType.String, Direction = ParameterDirection.Input });
        var db = new[] { new DbParameterDescriptor { Name = "Name", DbType = WeirDbType.String, Direction = ParameterDirection.Input } };

        var (_, result) = EndpointSynchronizer.Merge(endpoint, db);

        Assert.Equal("unchanged", result.Status);
        Assert.Empty(result.Added);
        Assert.Empty(result.Updated);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Preserves_Tvp_Columns_On_Matched_Structured_Parameter()
    {
        var columns = new List<TvpColumn> { new() { Name = "Name", DbType = WeirDbType.String, Ordinal = 0 } };
        var endpoint = Endpoint(new EndpointParameter
        {
            Name = "items",
            DbParameterName = "Items",
            DbType = WeirDbType.Structured,
            TypeName = "dbo.WidgetImportType",
            TableColumns = columns,
        });
        // The database reports the TVP but the introspection carries no column detail this time.
        var db = new[] { new DbParameterDescriptor { Name = "Items", DbType = WeirDbType.Structured, Direction = ParameterDirection.Input } };

        var (updated, _) = EndpointSynchronizer.Merge(endpoint, db);

        var param = Assert.Single(updated.Parameters);
        Assert.Equal(WeirDbType.Structured, param.DbType);
        Assert.Equal("dbo.WidgetImportType", param.TypeName);
        Assert.NotNull(param.TableColumns);
        Assert.Single(param.TableColumns!);
    }
}
