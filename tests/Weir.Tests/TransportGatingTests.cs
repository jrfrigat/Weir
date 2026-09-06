using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Weir.Host.Security;
using Xunit;

namespace Weir.Tests;

// An endpoint now says which doors it answers on, and the doors have to agree with it. These run
// against the real host: the same catalog, the same authenticator and the same dispatcher a deployment
// uses, because the whole point of the setting is that one transport cannot quietly serve what another
// refuses.
public class TransportGatingTests : IClassFixture<TransportGatingTests.HostFactory>
{
    private readonly HostFactory _factory;

    public TransportGatingTests(HostFactory factory) => _factory = factory;

    [Fact]
    public async Task Http_Refuses_An_Endpoint_That_Does_Not_Name_It()
    {
        var key = await _factory.SeedAsync("grpc-only", EndpointTransports.Grpc);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var response = await client.GetAsync("/api/grpc-only");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not served over HTTP", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_Serves_An_Endpoint_That_Names_It_Alongside_Others()
    {
        var key = await _factory.SeedAsync("http-and-ws", EndpointTransports.Http | EndpointTransports.WebSocket);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var response = await client.GetAsync("/api/http-and-ws");

        // The connection is not configured in the test host, so the call gets as far as the engine and
        // fails there. That is the point: it was not turned away at the door.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_OpenApi_Document_Leaves_Out_What_Http_Does_Not_Serve()
    {
        await _factory.SeedAsync("openapi-grpc-only", EndpointTransports.Grpc);
        await _factory.SeedAsync("openapi-http", EndpointTransports.Http);
        var client = await _factory.CreateAdminClientAsync();

        var document = await client.GetStringAsync("/admin/api/openapi.json");

        Assert.Contains("/api/openapi-http", document, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/openapi-grpc-only", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_WebSocket_Handshake_Without_A_Key_Is_Refused()
    {
        var socketClient = _factory.Server.CreateWebSocketClient();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => socketClient.ConnectAsync(new Uri(_factory.Server.BaseAddress, "ws"), CancellationToken.None));
    }

    [Fact]
    public async Task A_WebSocket_Frame_Is_Refused_For_An_Endpoint_That_Does_Not_Name_It()
    {
        var key = await _factory.SeedAsync("http-only-ws-test", EndpointTransports.Http);
        using var socket = await _factory.ConnectAsync(key);

        var reply = await SendAsync(socket, """{"id":7,"route":"http-only-ws-test","method":"GET"}""");

        Assert.Equal(7, reply.GetProperty("id").GetInt32());
        Assert.Equal(404, reply.GetProperty("status").GetInt32());
        Assert.Contains("not served over websocket", reply.GetProperty("error").GetProperty("detail").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_WebSocket_Frame_Reaches_The_Engine_For_An_Endpoint_That_Names_It()
    {
        var key = await _factory.SeedAsync("ws-allowed", EndpointTransports.WebSocket);
        using var socket = await _factory.ConnectAsync(key);

        var reply = await SendAsync(socket, """{"id":"a","route":"ws-allowed","method":"GET"}""");

        // As on the HTTP path above: the test host has no data connection, so reaching the engine is
        // what success looks like here - anything but the door turning it away.
        Assert.Equal("a", reply.GetProperty("id").GetString());
        Assert.NotEqual(404, reply.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_Malformed_Frame_Is_Answered_Rather_Than_Closing_The_Session()
    {
        var key = await _factory.SeedAsync("ws-malformed", EndpointTransports.WebSocket);
        using var socket = await _factory.ConnectAsync(key);

        var first = await SendAsync(socket, "not json at all");
        Assert.Equal(400, first.GetProperty("status").GetInt32());

        // The session survives a bad frame: a client that mistypes one request does not lose the ones
        // it has not sent yet.
        var second = await SendAsync(socket, """{"id":2}""");
        Assert.Equal(400, second.GetProperty("status").GetInt32());
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task A_Client_That_Closes_Politely_Gets_The_Handshake_Back()
    {
        var key = await _factory.SeedAsync("ws-close", EndpointTransports.WebSocket);
        using var socket = await _factory.ConnectAsync(key);

        // The session ends on the caller's close frame, and the server owes it a close frame in reply.
        // Dropping the socket instead leaves this call throwing "closed without completing the close
        // handshake", which is how the defect showed up against a real client.
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    /// <summary>Sends one text frame and reads the whole reply message back as JSON.</summary>
    /// <param name="socket">The open session.</param>
    /// <param name="frame">The request frame.</param>
    /// <returns>The parsed reply.</returns>
    private static async Task<JsonElement> SendAsync(WebSocket socket, string frame)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[8192];
        var payload = new List<byte>(8192);
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            payload.AddRange(buffer.AsSpan(0, received.Count).ToArray());
        }
        while (!received.EndOfMessage);

        return JsonDocument.Parse(payload.ToArray()).RootElement.Clone();
    }

    /// <summary>Boots the host against a throwaway SQLite control plane and seeds endpoints and keys into it.</summary>
    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"weir-transport-{Guid.NewGuid():N}.db");

        /// <summary>Points the host at the throwaway database.</summary>
        /// <param name="builder">The host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Weir:Admin:Username", "admin");
            builder.UseSetting("Weir:Admin:Password", "admin-password");
            builder.UseSetting("Weir:Jwt:SigningKey", "transport-test-signing-key-0123456789");
        }

        /// <summary>
        /// Creates one endpoint on the given transports and one key that may call it, then reloads the
        /// catalog so the route is live.
        /// </summary>
        /// <param name="route">The route to create.</param>
        /// <param name="transports">The transports it answers on.</param>
        /// <returns>The plaintext API key.</returns>
        public async Task<string> SeedAsync(string route, EndpointTransports transports)
        {
            var store = Services.GetRequiredService<IControlPlaneStore>();
            await store.UpsertEndpointAsync(new EndpointDefinition
            {
                Route = route,
                HttpMethod = "GET",
                ConnectionName = "default",
                ObjectName = "usp_Nothing",
                Transports = transports,
            });

            await Services.GetRequiredService<IEndpointCatalog>().LoadAsync();

            var (plainText, prefix, hash) = ApiKeyGenerator.Generate();
            await store.CreateApiKeyAsync(new ApiKeyCreate { Name = $"key-{route}" }, hash, prefix);
            Services.GetRequiredService<IApiKeyAuthenticator>().Invalidate();
            return plainText;
        }

        /// <summary>Opens an authenticated data-plane WebSocket session.</summary>
        /// <param name="apiKey">The plaintext API key for the handshake.</param>
        /// <returns>The connected socket.</returns>
        public Task<WebSocket> ConnectAsync(string apiKey)
        {
            var client = Server.CreateWebSocketClient();
            client.ConfigureRequest = request => request.Headers["X-Api-Key"] = apiKey;
            return client.ConnectAsync(new Uri(Server.BaseAddress, "ws"), CancellationToken.None);
        }

        /// <summary>Signs in as the bootstrap admin and returns a client carrying the access token.</summary>
        /// <returns>An authorized admin client.</returns>
        public async Task<HttpClient> CreateAdminClientAsync()
        {
            var client = CreateClient();
            var response = await client.PostAsJsonAsync("/admin/api/auth/login",
                new LoginRequest { Username = "admin", Password = "admin-password" });
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
            return client;
        }

        /// <summary>Deletes the throwaway database.</summary>
        /// <param name="disposing">Whether managed resources are being released.</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try
            {
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp database file.
            }
        }
    }
}
