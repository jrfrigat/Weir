using System.Text.Json;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class ParameterBinderTests
{
    private static WeirInvocation Invocation(EndpointDefinition endpoint, string? bodyJson)
    {
        JsonElement body = default;
        var hasBody = false;
        if (bodyJson is not null)
        {
            body = JsonDocument.Parse(bodyJson).RootElement;
            hasBody = true;
        }

        return new WeirInvocation { Endpoint = endpoint, Body = body, HasBody = hasBody };
    }

    private static EndpointDefinition Endpoint(params EndpointParameter[] parameters) => new()
    {
        Route = "x",
        ConnectionName = "default",
        ObjectName = "usp",
        Parameters = parameters,
    };

    [Fact]
    public void Binds_BodyParameter()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "id", DbType = WeirDbType.Int32 });
        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"id\":5}"));
        Assert.Single(result.Parameters);
        Assert.Equal(5, result.Parameters[0].Value);
        Assert.Equal("id", result.Parameters[0].Name);
    }

    [Fact]
    public void Missing_Required_Throws()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "id", DbType = WeirDbType.Int32, Required = true });
        Assert.Throws<WeirValidationException>(() => new ParameterBinder().Bind(Invocation(endpoint, "{}")));
    }

    [Fact]
    public void Default_Applied_When_Missing()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "flag", DbType = WeirDbType.String, DefaultValue = "def" });
        var result = new ParameterBinder().Bind(Invocation(endpoint, "{}"));
        Assert.Equal("def", result.Parameters[0].Value);
    }

    [Fact]
    public void UsesDbParameterName_When_Set()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "id", DbParameterName = "CustomerId", DbType = WeirDbType.Int32 });
        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"id\":7}"));
        Assert.Equal("CustomerId", result.Parameters[0].Name);
    }

    [Fact]
    public void Tvp_BuildsTable()
    {
        var endpoint = Endpoint(new EndpointParameter
        {
            Name = "items",
            DbType = WeirDbType.Structured,
            TypeName = "dbo.OrderItemType",
            TableColumns =
            [
                new TvpColumn { Name = "sku" },
                new TvpColumn { Name = "qty", DbType = WeirDbType.Int32 },
            ],
        });

        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"A1\",\"qty\":2}]}"));
        var parameter = result.Parameters[0];
        Assert.NotNull(parameter.Table);
        Assert.Single(parameter.Table!.Rows);
        Assert.Equal("A1", parameter.Table.Rows[0][0]);
        Assert.Equal(2, parameter.Table.Rows[0][1]);
    }

    [Fact]
    public void ValidationRegex_Rejects_BadValue()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "code", DbType = WeirDbType.String, ValidationRegex = "^[A-Z]{3}$" });
        Assert.Throws<WeirValidationException>(() => new ParameterBinder().Bind(Invocation(endpoint, "{\"code\":\"abc\"}")));
    }

    [Fact]
    public void Tvp_RecordsTokenForCacheKeying()
    {
        // A TVP must contribute a value to the cache-key map so an endpoint that varies by it keys on
        // the actual rows instead of colliding on NULL. Distinct row sets must yield distinct tokens.
        var endpoint = Endpoint(new EndpointParameter
        {
            Name = "items",
            DbType = WeirDbType.Structured,
            TypeName = "dbo.OrderItemType",
            TableColumns = [new TvpColumn { Name = "sku" }],
        });

        var a = new ParameterBinder().Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"A1\"}]}"));
        var b = new ParameterBinder().Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"B2\"}]}"));
        Assert.True(a.Values.ContainsKey("items"));
        Assert.NotEqual(a.Values["items"], b.Values["items"]);
    }

    [Fact]
    public void Tvp_ExceedingRowCap_Throws()
    {
        var endpoint = Endpoint(new EndpointParameter
        {
            Name = "items",
            DbType = WeirDbType.Structured,
            TypeName = "dbo.OrderItemType",
            TableColumns = [new TvpColumn { Name = "sku" }],
        });

        var binder = new ParameterBinder(new StubRuntimeSettings(new WeirSystemSettings { MaxTvpRows = 2 }));
        Assert.Throws<WeirValidationException>(() =>
            binder.Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"A\"},{\"sku\":\"B\"},{\"sku\":\"C\"}]}")));
    }

    /// <summary>Fixed <see cref="IRuntimeSettings"/> returning a preset snapshot, for cap tests.</summary>
    private sealed class StubRuntimeSettings(WeirSystemSettings settings) : IRuntimeSettings
    {
        public WeirSystemSettings Current { get; } = settings;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(WeirSystemSettings s, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
