using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Weir.Contracts;
using Xunit;

namespace Weir.Tests;

// Demoting or disabling the last enabled admin locks everyone out for good: both routes are AdminOnly,
// so nobody is left who could undo it, and a restart does not rescue you either - the bootstrap account
// is only created when the admins table is empty, and a disabled row still fills it. The way back is
// editing the database by hand. These tests hold the guard that refuses the change in place.
public class LastAdminGuardTests : IClassFixture<LastAdminGuardTests.HostFactory>
{
    private readonly HostFactory _factory;

    public LastAdminGuardTests(HostFactory factory) => _factory = factory;

    [Fact]
    public async Task Disabling_The_Last_Admin_Is_Refused()
    {
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, "admin", "admin-password");
        var me = await WhoAsync(client, token, "admin");

        var response = await SendAsync(client, HttpMethod.Put, $"/admin/api/admins/{me.Id}/enabled", token,
            new AdminEnabledRequest { Enabled = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // And it really is still usable - a refusal that half-applied would be worse than none.
        var stillWorks = await SendAsync(client, HttpMethod.Get, "/admin/api/admins", token, content: null);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task Demoting_The_Last_Admin_To_Viewer_Is_Refused()
    {
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, "admin", "admin-password");
        var me = await WhoAsync(client, token, "admin");

        var response = await SendAsync(client, HttpMethod.Put, $"/admin/api/admins/{me.Id}/role", token,
            new AdminRoleRequest { Role = AdminRoles.Viewer });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task While_Another_Admin_Remains_A_Demotion_Goes_Through()
    {
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, "admin", "admin-password");

        // The guard protects the last admin, not role changes in general: while a second one is around
        // the change has to go through, or an admin could never be stepped down at all. The bootstrap
        // "admin" is left alone here - these tests share one database, and demoting it would pull the
        // AdminOnly rights out from under the other tests in the class.
        var created = await SendAsync(client, HttpMethod.Post, "/admin/api/admins", token,
            new CreateAdminRequest { Username = "second-admin", Password = "second-pass", Role = AdminRoles.Admin });
        created.EnsureSuccessStatusCode();

        var second = await WhoAsync(client, token, "second-admin");
        var response = await SendAsync(client, HttpMethod.Put, $"/admin/api/admins/{second.Id}/role", token,
            new AdminRoleRequest { Role = AdminRoles.Viewer });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Finds one admin by username.</summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="token">A bearer token.</param>
    /// <param name="username">The username to find.</param>
    /// <returns>The matching admin.</returns>
    private static async Task<AdminUserInfo> WhoAsync(HttpClient client, string token, string username)
    {
        var response = await SendAsync(client, HttpMethod.Get, "/admin/api/admins", token, content: null);
        response.EnsureSuccessStatusCode();
        var admins = await response.Content.ReadFromJsonAsync<List<AdminUserInfo>>();
        return admins!.Single(admin => admin.Username == username);
    }

    /// <summary>Signs in and returns the access token.</summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <returns>The access token.</returns>
    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/admin/api/auth/login",
            new LoginRequest { Username = username, Password = password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.Token;
    }

    /// <summary>Sends an authenticated request.</summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The route.</param>
    /// <param name="token">A bearer token.</param>
    /// <param name="content">An optional JSON body.</param>
    /// <returns>The response.</returns>
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
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"weir-lastadmin-{Guid.NewGuid():N}.db");

        /// <summary>Points the host at the throwaway database.</summary>
        /// <param name="builder">The host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Weir:Admin:Username", "admin");
            builder.UseSetting("Weir:Admin:Password", "admin-password");
            builder.UseSetting("Weir:Jwt:SigningKey", "last-admin-test-signing-key-0123456789");
        }

        /// <summary>Deletes the throwaway database.</summary>
        /// <param name="disposing">True when called from Dispose.</param>
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
