using Weir.Contracts;
using Weir.Host.Http;
using Xunit;

namespace Weir.Tests;

/// <summary>Tests the OpenAPI document builder, including the optional audience label for scoped documents.</summary>
public class OpenApiGeneratorTests
{
    private static EndpointDefinition Endpoint(string route, string method = "GET") =>
        new()
        {
            Route = route,
            HttpMethod = method,
            ConnectionName = "default",
            ObjectName = "usp_" + route,
        };

    [Fact]
    public void Generates_A_Path_Per_Endpoint()
    {
        var doc = OpenApiGenerator.Generate([Endpoint("customers"), Endpoint("orders", "POST")], "https://weir.example");

        var paths = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(doc["paths"]);
        Assert.Contains("/api/customers", paths.Keys);
        Assert.Contains("/api/orders", paths.Keys);
    }

    [Fact]
    public void Full_Document_Has_The_Plain_Title()
    {
        var doc = OpenApiGenerator.Generate([Endpoint("customers")], "https://weir.example");
        var info = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(doc["info"]);
        Assert.Equal("Weir data API", info["title"]);
    }

    [Fact]
    public void Audience_Is_Noted_In_The_Title()
    {
        var doc = OpenApiGenerator.Generate([Endpoint("orders")], "https://weir.example", "key \"checkout-service\"");
        var info = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(doc["info"]);
        Assert.Equal("Weir data API (key \"checkout-service\")", info["title"]);
        Assert.Contains("checkout-service", (string)info["description"]!);
    }
}
