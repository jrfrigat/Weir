using Weir.Contracts;
using Weir.Host.Http;
using Xunit;

namespace Weir.Tests;

/// <summary>
/// Tests the off-hot-path access helper that decides whether a key's scopes and grants would be
/// allowed to call an endpoint (used for the endpoint filter and the key-scoped OpenAPI document).
/// </summary>
public class EndpointAccessTests
{
    private static EndpointDefinition Endpoint(string obj, string[]? requiredScopes = null, string schema = "dbo", string connection = "default") =>
        new()
        {
            Route = obj.ToLowerInvariant(),
            ConnectionName = connection,
            Schema = schema,
            ObjectName = obj,
            RequiredScopes = requiredScopes ?? [],
        };

    [Fact]
    public void No_Required_Scopes_And_No_Grants_Is_Accessible()
    {
        var endpoint = Endpoint("usp_GetCustomers");
        Assert.True(EndpointAccess.IsAccessibleBy(endpoint, [], []));
    }

    [Fact]
    public void Missing_Required_Scope_Is_Not_Accessible()
    {
        var endpoint = Endpoint("usp_CreateOrder", ["orders:write"]);
        Assert.False(EndpointAccess.IsAccessibleBy(endpoint, ["orders:read"], []));
    }

    [Fact]
    public void Held_Required_Scope_With_No_Grants_Is_Accessible()
    {
        var endpoint = Endpoint("usp_CreateOrder", ["orders:write"]);
        Assert.True(EndpointAccess.IsAccessibleBy(endpoint, ["orders:read", "orders:write"], []));
    }

    [Fact]
    public void Grant_Must_Match_The_Endpoint_Object()
    {
        var endpoint = Endpoint("usp_GetOrderById", ["orders:read"]);
        var grants = new[] { new ApiKeyGrant { Connection = "default", Schema = "dbo", ObjectName = "usp_CreateOrder" } };

        // Scope is held, but the single grant only covers a different object.
        Assert.False(EndpointAccess.IsAccessibleBy(endpoint, ["orders:read"], grants));
    }

    [Fact]
    public void Matching_Grant_And_Scope_Is_Accessible()
    {
        var endpoint = Endpoint("usp_CreateOrder", ["orders:write"]);
        var grants = new[] { new ApiKeyGrant { Connection = "default", Schema = "dbo", ObjectName = "usp_CreateOrder" } };

        Assert.True(EndpointAccess.IsAccessibleBy(endpoint, ["orders:write"], grants));
    }
}
