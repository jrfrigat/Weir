using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Weir.Tests;

// An unknown API key is never cached, so every request bearing one queries the control-plane store.
// A flood of random keys therefore turns into a lookup per request - a database-exhaustion DoS that
// needs no valid credential. The flood guard caps how many unresolved keys one caller address may
// spend per minute; past that it is refused with 429 before the lookup, so the store sees a bounded
// number of misses per caller no matter how hard the caller pushes.
public class ApiKeyFloodGuardTests
{
    [Fact]
    public async Task A_Caller_Flooding_Unknown_Keys_Is_Refused_After_Its_Budget()
    {
        using var factory = new HostFactory(threshold: 3);
        var client = factory.CreateClient();

        // The budget is three: the first three unknown keys are ordinary failed auth (401), the fourth
        // is refused before a lookup (429). Distinct keys so nothing is served from the resolved cache.
        for (var i = 0; i < 3; i++)
        {
            var failed = await Attempt(client, $"bad-key-{i}");
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var blocked = await Attempt(client, "bad-key-over-budget");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("60", blocked.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task With_The_Guard_Disabled_The_Same_Flood_Only_Ever_Gets_401()
    {
        // Threshold zero disables the guard. This is the counter-check that the 429 above is the guard's
        // doing and not some other limiter: with it off, the identical flood stays 401 forever.
        using var factory = new HostFactory(threshold: 0);
        var client = factory.CreateClient();

        for (var i = 0; i < 8; i++)
        {
            var response = await Attempt(client, $"bad-key-{i}");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>Sends one data-plane request bearing the given API key.</summary>
    /// <param name="client">The client.</param>
    /// <param name="key">The raw API key to present.</param>
    /// <returns>The response.</returns>
    private static async Task<HttpResponseMessage> Attempt(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        return await client.SendAsync(request);
    }

    /// <summary>Boots the host against a throwaway SQLite control plane with a chosen failure budget.</summary>
    private sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"weir-flood-{Guid.NewGuid():N}.db");
        private readonly int _threshold;

        /// <summary>Creates the factory.</summary>
        /// <param name="threshold">The unresolved-key budget to configure; zero disables the guard.</param>
        internal HostFactory(int threshold) => _threshold = threshold;

        /// <summary>Points the host at the throwaway database and sets the budget.</summary>
        /// <param name="builder">The host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Weir:Admin:Username", "admin");
            builder.UseSetting("Weir:Admin:Password", "admin-password");
            builder.UseSetting("Weir:Jwt:SigningKey", "flood-guard-test-signing-key-0123456789");
            builder.UseSetting("Weir:DataPlane:ApiKeyFailureThreshold", _threshold.ToString(CultureInfo.InvariantCulture));
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
