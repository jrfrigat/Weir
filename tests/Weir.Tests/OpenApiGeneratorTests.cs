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

    /// <summary>Digs the 200 response schema out of the generated document for one endpoint.</summary>
    private static IReadOnlyDictionary<string, object?> ResponseSchema(EndpointDefinition endpoint)
    {
        var doc = OpenApiGenerator.Generate([endpoint], "https://weir.example");
        var paths = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(doc["paths"]);
        var path = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(paths["/api/" + endpoint.Route]);
        var operation = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(path[endpoint.HttpMethod.ToLowerInvariant()]);
        var responses = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(operation["responses"]);
        var ok = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(responses["200"]);
        var content = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(ok["content"]);
        var json = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(content["application/json"]);
        return Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(json["schema"]);
    }

    [Fact]
    public void Endpoint_Without_Output_Parameters_References_The_Shared_Envelope()
    {
        var schema = ResponseSchema(Endpoint("customers"));
        Assert.Contains("$ref", schema.Keys);
        Assert.DoesNotContain("allOf", schema.Keys);
    }

    [Fact]
    public void Output_Parameters_Are_Described_In_The_Response_Schema()
    {
        // An OUTPUT parameter is deliberately absent from the request schema, so the response is the only
        // place a generated client can learn it exists. It used to be an untyped "output": {} there.
        var endpoint = Endpoint("orders", "POST") with
        {
            Parameters =
            [
                new EndpointParameter { Name = "customerId", DbType = WeirDbType.Int32 },
                new EndpointParameter { Name = "total", DbType = WeirDbType.Decimal, Direction = ParameterDirection.Output },
                new EndpointParameter { Name = "note", DbType = WeirDbType.String, Direction = ParameterDirection.InputOutput },
            ],
        };

        var schema = ResponseSchema(endpoint);
        var allOf = Assert.IsAssignableFrom<IReadOnlyList<object>>(schema["allOf"]);
        Assert.Equal(2, allOf.Count);

        // The shared envelope stays referenced rather than copied, so the two cannot drift apart.
        var envelope = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(allOf[0]);
        Assert.Contains("$ref", envelope.Keys);

        var narrowed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(allOf[1]);
        var properties = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(narrowed["properties"]);
        var output = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(properties["output"]);
        var outputProperties = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(output["properties"]);

        Assert.Equal(["note", "total"], outputProperties.Keys.OrderBy(k => k, StringComparer.Ordinal));

        // Typed, not just named: this is the whole point of describing them.
        var total = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(outputProperties["total"]);
        Assert.Equal("number", total["type"]);

        // The procedure may leave any of them unset, and the envelope's output is null when it sets none.
        Assert.Equal(true, output["nullable"]);
        Assert.Equal(true, total["nullable"]);
    }

    [Fact]
    public void Output_Parameters_Stay_Out_Of_The_Request_Body()
    {
        // The response schema now names them, which must not leak back into the request: an OUTPUT
        // parameter is the procedure's to write, and asking a caller for one would be wrong.
        var endpoint = Endpoint("orders", "POST") with
        {
            Parameters =
            [
                new EndpointParameter { Name = "customerId", DbType = WeirDbType.Int32 },
                new EndpointParameter { Name = "total", DbType = WeirDbType.Decimal, Direction = ParameterDirection.Output },
            ],
        };

        var doc = OpenApiGenerator.Generate([endpoint], "https://weir.example");
        var paths = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(doc["paths"]);
        var path = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(paths["/api/orders"]);
        var operation = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(path["post"]);
        var body = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(operation["requestBody"]);
        var content = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(body["content"]);
        var json = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(content["application/json"]);
        var schema = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(json["schema"]);
        var properties = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(schema["properties"]);

        Assert.Equal(["customerId"], properties.Keys);
    }
}
