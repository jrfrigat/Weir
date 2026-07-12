using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Weir.Contracts;
using Xunit;

namespace Weir.Tests;

// Boots the real host in-memory and exercises role-based authorization end to end. This guards the
// regression where an authenticated Admin received 403 on every mutating admin route because the JWT
// "role" claim was being remapped and no longer matched the RequireRole policy.
public class AdminAuthorizationTests : IClassFixture<AdminAuthorizationTests.HostFactory>
{
    private readonly HostFactory _factory;

    public AdminAuthorizationTests(HostFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_Can_Call_An_AdminOnly_Route()
    {
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, "admin", "admin-password");

        var response = await SendAsync(client, HttpMethod.Post, "/admin/api/endpoints/import", token,
            new List<EndpointDefinition>());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_Can_Read_But_Not_Write()
    {
        var client = _factory.CreateClient();
        var adminToken = await LoginAsync(client, "admin", "admin-password");

        var created = await SendAsync(client, HttpMethod.Post, "/admin/api/admins", adminToken,
            new CreateAdminRequest { Username = "viewer1", Password = "viewer-pass", Role = AdminRoles.Viewer });
        created.EnsureSuccessStatusCode();

        var viewerToken = await LoginAsync(client, "viewer1", "viewer-pass");

        var read = await SendAsync(client, HttpMethod.Get, "/admin/api/endpoints", viewerToken, content: null);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var write = await SendAsync(client, HttpMethod.Post, "/admin/api/endpoints/import", viewerToken,
            new List<EndpointDefinition>());
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task PersonalAccessToken_Authenticates_An_AdminOnly_Route()
    {
        var client = _factory.CreateClient();
        var jwt = await LoginAsync(client, "admin", "admin-password");

        var createResponse = await SendAsync(client, HttpMethod.Post, "/admin/api/account/tokens", jwt,
            new AdminTokenCreate { Name = "ci-test" });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<AdminTokenCreated>();
        Assert.NotNull(created);
        Assert.StartsWith("weadm_", created!.PlainTextToken);

        // The token alone (no JWT) authenticates an AdminOnly route as its owning admin.
        var write = await SendAsync(client, HttpMethod.Post, "/admin/api/endpoints/import", created.PlainTextToken,
            new List<EndpointDefinition>());
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
    }

    [Fact]
    public async Task Revoked_PersonalAccessToken_Is_Rejected()
    {
        var client = _factory.CreateClient();
        var jwt = await LoginAsync(client, "admin", "admin-password");

        var created = await (await SendAsync(client, HttpMethod.Post, "/admin/api/account/tokens", jwt,
            new AdminTokenCreate { Name = "ci-revoke" })).Content.ReadFromJsonAsync<AdminTokenCreated>();
        Assert.NotNull(created);

        var tokens = await (await SendAsync(client, HttpMethod.Get, "/admin/api/account/tokens", jwt, content: null))
            .Content.ReadFromJsonAsync<List<AdminTokenInfo>>();
        var id = tokens!.Single(t => t.Name == "ci-revoke").Id;
        (await SendAsync(client, HttpMethod.Delete, $"/admin/api/account/tokens/{id}", jwt, content: null)).EnsureSuccessStatusCode();

        var afterRevoke = await SendAsync(client, HttpMethod.Get, "/admin/api/account/tokens", created!.PlainTextToken, content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task Endpoint_Sync_Accepts_Connection_Schema_Object_Filters()
    {
        var client = _factory.CreateClient();
        var jwt = await LoginAsync(client, "admin", "admin-password");

        // No endpoints match this specific procedure, so the filtered sync returns an empty array (200),
        // confirming the connection/schema/object query filters are accepted and applied.
        var response = await SendAsync(client, HttpMethod.Post,
            "/admin/api/endpoints/sync?connection=nope&schema=dbo&object=GetOrders", jwt, content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<EndpointSyncResult>>();
        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/admin/api/auth/login",
            new LoginRequest { Username = username, Password = password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.Token;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string token, object? content)
    {
        var request = new HttpRequestMessage(method, url) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        if (content is not null)
        {
            request.Content = JsonContent.Create(content);
        }

        return client.SendAsync(request);
    }

    /// <summary>Boots the host against a throwaway SQLite control plane with a fixed admin and signing key.</summary>
    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"weir-it-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Weir:Admin:Username", "admin");
            builder.UseSetting("Weir:Admin:Password", "admin-password");
            builder.UseSetting("Weir:Jwt:SigningKey", "integration-test-signing-key-0123456789");
        }

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
