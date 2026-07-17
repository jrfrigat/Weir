using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Weir.Tests;

// The data plane used to resolve the route before authenticating, so an anonymous caller could tell a
// real endpoint from an imaginary one by the status alone: 401 meant "exists", 404 meant "does not".
// That turns /api into a directory anyone can read without a key. Both must look the same until the
// caller proves it may know.
public class RouteEnumerationTests : IClassFixture<RouteEnumerationTests.HostFactory>
{
    private readonly HostFactory _factory;

    public RouteEnumerationTests(HostFactory factory) => _factory = factory;

    [Fact]
    public async Task An_Anonymous_Caller_Cannot_Tell_A_Real_Route_From_An_Imaginary_One()
    {
        var client = _factory.CreateClient();

        // "ping" is seeded by the demo control plane; the other cannot exist.
        var real = await client.GetAsync("/api/ping");
        var imaginary = await client.GetAsync("/api/does-not-exist-9f3a");

        Assert.Equal(HttpStatusCode.Unauthorized, real.StatusCode);
        Assert.Equal(real.StatusCode, imaginary.StatusCode);
    }

    [Fact]
    public async Task An_Anonymous_Caller_Is_Told_Nothing_By_The_Body_Either()
    {
        var client = _factory.CreateClient();

        var imaginary = await client.GetAsync("/api/does-not-exist-9f3a");
        var body = await imaginary.Content.ReadAsStringAsync();

        // A matching status but a body admitting "no endpoint is mapped to ..." would hand the answer
        // straight back. (The bodies are not compared outright - each carries its own traceId.)
        Assert.Contains("Unauthorized", body, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does-not-exist-9f3a", body, StringComparison.Ordinal);
    }

    /// <summary>Boots the host against a throwaway SQLite control plane.</summary>
    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"weir-routes-{Guid.NewGuid():N}.db");

        /// <summary>Points the host at the throwaway database.</summary>
        /// <param name="builder">The host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Weir:Admin:Username", "admin");
            builder.UseSetting("Weir:Admin:Password", "admin-password");
            builder.UseSetting("Weir:Jwt:SigningKey", "route-enum-test-signing-key-0123456789");
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
