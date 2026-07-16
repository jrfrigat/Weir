using Microsoft.Extensions.Options;
using Weir.Contracts;
using Weir.ControlPlane.Sqlite;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

// Route templates (orders/{id}) are an advertised endpoint feature: they are offered in the admin, and
// ParameterSource.Route exists to read what they capture. These cover resolving them and the precedence
// rules between a literal route and a template that would also match. They run against the real SQLite
// control plane rather than a stub, so the definitions travel the same path they do in production.
public class EndpointCatalogRouteTests
{
    private static EndpointDefinition Endpoint(string method, string route) => new()
    {
        Route = route,
        HttpMethod = method,
        ConnectionName = "default",
        ObjectName = "usp",
        Enabled = true,
    };

    private static async Task<EndpointCatalog> Catalog(params EndpointDefinition[] endpoints)
    {
        var path = Path.Combine(Path.GetTempPath(), $"weir-routes-{Guid.NewGuid():N}.db");
        var store = new SqliteControlPlaneStore(
            Options.Create(new SqliteControlPlaneOptions { ConnectionString = $"Data Source={path}" }),
            TimeProvider.System);
        await store.InitializeAsync();
        foreach (var endpoint in endpoints)
        {
            await store.UpsertEndpointAsync(endpoint);
        }

        var catalog = new EndpointCatalog(store);
        await catalog.LoadAsync();
        return catalog;
    }

    [Fact]
    public async Task Literal_Route_Resolves()
    {
        var catalog = await Catalog(Endpoint("GET", "ping"));

        Assert.True(catalog.TryResolve("GET", "ping", out var match));
        Assert.Equal("ping", match.Endpoint.Route);
        Assert.False(match.RouteValues.TryGet("anything", out _));
    }

    [Fact]
    public async Task Template_Route_Resolves_And_Captures_The_Segment()
    {
        var catalog = await Catalog(Endpoint("GET", "orders/{id}"));

        Assert.True(catalog.TryResolve("GET", "orders/123", out var match));
        Assert.Equal("orders/{id}", match.Endpoint.Route);
        Assert.True(match.RouteValues.TryGet("id", out var id));
        Assert.Equal("123", id);
    }

    [Fact]
    public async Task Capture_Is_Matched_Case_Insensitively_By_Name()
    {
        // The binder looks the capture up by the parameter's declared name, whose casing need not match
        // the template's.
        var catalog = await Catalog(Endpoint("GET", "orders/{OrderId}"));

        Assert.True(catalog.TryResolve("GET", "orders/7", out var match));
        Assert.True(match.RouteValues.TryGet("orderid", out var id));
        Assert.Equal("7", id);
    }

    [Fact]
    public async Task Literal_Wins_Over_A_Template_That_Would_Also_Match()
    {
        var catalog = await Catalog(Endpoint("GET", "orders/{id}"), Endpoint("GET", "orders/count"));

        Assert.True(catalog.TryResolve("GET", "orders/count", out var match));
        Assert.Equal("orders/count", match.Endpoint.Route);
    }

    [Fact]
    public async Task More_Specific_Template_Wins()
    {
        var catalog = await Catalog(Endpoint("GET", "orders/{id}/{part}"), Endpoint("GET", "orders/{id}/lines"));

        Assert.True(catalog.TryResolve("GET", "orders/9/lines", out var match));
        Assert.Equal("orders/{id}/lines", match.Endpoint.Route);
    }

    [Theory]
    [InlineData("orders")]          // too few segments
    [InlineData("orders/1/extra")]  // too many segments
    [InlineData("orders/")]         // trailing slash is trimmed, leaving too few segments
    [InlineData("invoices/1")]      // a literal segment that does not match
    [InlineData("")]                // no path at all
    public async Task Non_Matching_Paths_Do_Not_Resolve(string path)
    {
        var catalog = await Catalog(Endpoint("GET", "orders/{id}"));

        Assert.False(catalog.TryResolve("GET", path, out _));
    }

    [Fact]
    public async Task Empty_Segment_Does_Not_Satisfy_A_Capture()
    {
        // An interior empty segment is the one way a capture can be handed "": binding a parameter to
        // an empty string is never what the caller meant, so it must not match.
        var catalog = await Catalog(Endpoint("GET", "orders/{id}/lines"));

        Assert.False(catalog.TryResolve("GET", "orders//lines", out _));
        Assert.True(catalog.TryResolve("GET", "orders/7/lines", out _));
    }

    [Fact]
    public async Task Method_Is_Part_Of_The_Match()
    {
        var catalog = await Catalog(Endpoint("GET", "orders/{id}"));

        Assert.True(catalog.TryResolve("GET", "orders/1", out _));
        Assert.False(catalog.TryResolve("DELETE", "orders/1", out _));
    }

    [Fact]
    public async Task Surrounding_Slashes_Do_Not_Affect_The_Match()
    {
        var catalog = await Catalog(Endpoint("GET", "/orders/{id}/"));

        Assert.True(catalog.TryResolve("GET", "orders/42", out var match));
        Assert.True(match.RouteValues.TryGet("id", out var id));
        Assert.Equal("42", id);
    }

    [Fact]
    public async Task Captured_Value_Is_Not_Split_On_Its_Own_Content()
    {
        // A capture takes exactly one segment, so a value that looks like a path must not match.
        var catalog = await Catalog(Endpoint("GET", "orders/{id}"));

        Assert.False(catalog.TryResolve("GET", "orders/a/b", out _));
        Assert.True(catalog.TryResolve("GET", "orders/a-b", out var match));
        Assert.True(match.RouteValues.TryGet("id", out var id));
        Assert.Equal("a-b", id);
    }
}
