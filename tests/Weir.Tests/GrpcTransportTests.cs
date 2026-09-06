using Grpc.Core;
using Grpc.Net.Client;
using Weir.Contracts;
using Weir.Host.Grpc;
using Xunit;

namespace Weir.Tests;

// The gRPC door, against the real host: same catalog, same authenticator, same dispatcher. What matters
// here is that it agrees with the other two doors about who may call what - a transport that answered
// something HTTP refuses would make the setting worthless.
public class GrpcTransportTests : IClassFixture<TransportGatingTests.HostFactory>
{
    private readonly TransportGatingTests.HostFactory _factory;

    public GrpcTransportTests(TransportGatingTests.HostFactory factory) => _factory = factory;

    [Fact]
    public async Task A_Call_Without_A_Key_Is_Unauthenticated()
    {
        var client = CreateClient();

        var error = await Assert.ThrowsAsync<RpcException>(
            () => client.InvokeAsync(new InvokeRequest { Route = "anything", Method = "GET" }).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Fact]
    public async Task Grpc_Refuses_An_Endpoint_That_Does_Not_Name_It()
    {
        var key = await _factory.SeedAsync("grpc-refused", EndpointTransports.Http);
        var client = CreateClient();

        var error = await Assert.ThrowsAsync<RpcException>(
            () => client.InvokeAsync(new InvokeRequest { Route = "grpc-refused", Method = "GET" }, Auth(key)).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
        Assert.Contains("not served over grpc", error.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Grpc_Serves_An_Endpoint_That_Names_It()
    {
        var key = await _factory.SeedAsync("grpc-allowed", EndpointTransports.Grpc);
        var client = CreateClient();

        var error = await Assert.ThrowsAsync<RpcException>(
            () => client.InvokeAsync(new InvokeRequest { Route = "grpc-allowed", Method = "GET" }, Auth(key)).ResponseAsync);

        // The call reaches the engine and fails at the database, which does not exist here - and that is
        // the proof that the door let it through. The exact failure is deliberately not asserted: it
        // depends on whether the machine running the tests happens to have a SQL Server listening, and
        // pinning it would make this test pass or fail for a reason it is not about.
        Assert.NotEqual(StatusCode.NotFound, error.StatusCode);
        Assert.DoesNotContain("not served over", error.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Streaming_Call_Is_Gated_By_The_Same_Setting()
    {
        var key = await _factory.SeedAsync("grpc-stream-refused", EndpointTransports.WebSocket);
        var client = CreateClient();

        using var call = client.InvokeStream(new InvokeRequest { Route = "grpc-stream-refused", Method = "GET" }, Auth(key));
        var error = await Assert.ThrowsAsync<RpcException>(
            () => call.ResponseStream.MoveNext(CancellationToken.None));

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task A_Call_Without_A_Route_Is_Rejected_Before_Anything_Else()
    {
        var key = await _factory.SeedAsync("grpc-no-route", EndpointTransports.Grpc);
        var client = CreateClient();

        var error = await Assert.ThrowsAsync<RpcException>(
            () => client.InvokeAsync(new InvokeRequest { Method = "GET" }, Auth(key)).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task A_Body_That_Is_Not_A_Json_Object_Is_Rejected()
    {
        var key = await _factory.SeedAsync("grpc-bad-body", EndpointTransports.Grpc);
        var client = CreateClient();

        var error = await Assert.ThrowsAsync<RpcException>(
            () => client.InvokeAsync(new InvokeRequest { Route = "grpc-bad-body", Method = "GET", Body = "[1,2,3]" }, Auth(key)).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    /// <summary>Builds a gateway client over the in-memory test server.</summary>
    /// <returns>The client.</returns>
    private WeirGateway.WeirGatewayClient CreateClient()
    {
        var channel = GrpcChannel.ForAddress(_factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = _factory.Server.CreateHandler(),
        });

        return new WeirGateway.WeirGatewayClient(channel);
    }

    /// <summary>Call metadata carrying the API key, the way a service caller sends it.</summary>
    /// <param name="apiKey">The plaintext key.</param>
    /// <returns>The metadata for the call.</returns>
    private static Metadata Auth(string apiKey) => new() { { "x-api-key", apiKey } };
}
