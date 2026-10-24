using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Weir.Contracts;
using Weir.ControlPlane.PostgreSql;
using Xunit;

namespace Weir.Tests;

// Integration tests for the PostgreSQL control-plane store. They start a real PostgreSQL container,
// so they require Docker and are opt-in: set the environment variable WEIR_CONTAINER_TESTS=1 to run
// them (the CI release/build pipeline sets it). Without the flag they return immediately so a local
// "dotnet test" without Docker still succeeds.
public class PostgresControlPlaneStoreIntegrationTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("WEIR_CONTAINER_TESTS"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task Full_Roundtrip_Against_Real_Postgres()
    {
        if (!Enabled)
        {
            return;
        }

        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .Build();
        await container.StartAsync();

        var options = Options.Create(new PostgresControlPlaneOptions { ConnectionString = container.GetConnectionString() });
        var store = new PostgresControlPlaneStore(options, TimeProvider.System);

        // Idempotent: running the migrations twice must not fail.
        await store.InitializeAsync();
        await store.InitializeAsync();

        // Endpoints.
        var saved = await store.UpsertEndpointAsync(new EndpointDefinition
        {
            Route = "widgets",
            ConnectionName = "default",
            ObjectName = "GetWidgets",
        });
        Assert.NotEqual(Guid.Empty, saved.Id);

        var fetched = await store.GetEndpointAsync(saved.Id);
        Assert.NotNull(fetched);
        Assert.Equal("widgets", fetched!.Route);

        // API keys.
        var keyInfo = await store.CreateApiKeyAsync(
            new ApiKeyCreate { Name = "test", Scopes = ["widgets:read"] }, "hash-value", "wk_test");
        var record = await store.FindApiKeyByHashAsync("hash-value");
        Assert.NotNull(record);
        Assert.Equal(keyInfo.Id, record!.Id);
        Assert.Contains("widgets:read", record.Scopes);

        // Per-key mTLS and CORS policies. Checked against a real server database rather than only
        // against SQLite, because each provider spells the migration and the nullable column in its
        // own dialect, and a policy that writes but does not read back is a key that looks restricted
        // in the admin UI and is wide open on the request path.
        var policyKey = await store.CreateApiKeyAsync(
            new ApiKeyCreate
            {
                Name = "with-policies",
                Mtls = new ApiKeyMtlsPolicy { RequireCertificate = true, Thumbprints = ["AABBCC"] },
                Cors = new ApiKeyCorsPolicy { AllowedOrigins = ["https://app.example.com"], MaxAgeSeconds = 60 },
            },
            "hash-policies",
            "wk_test_pol");
        var policyRecord = await store.FindApiKeyByHashAsync("hash-policies");
        Assert.NotNull(policyRecord);
        Assert.True(policyRecord!.Mtls?.RequireCertificate);
        Assert.Equal(["AABBCC"], policyRecord.Mtls!.Thumbprints);
        Assert.Equal(["https://app.example.com"], policyRecord.Cors?.AllowedOrigins);
        Assert.Equal(60, policyRecord.Cors!.MaxAgeSeconds);

        // Null is the fact a key written before these columns existed records, so it has to read back
        // as null and not as an empty policy.
        Assert.Null(record.Mtls);
        Assert.Null(record.Cors);

        // Replacing the policies, and clearing them again.
        await store.UpdateApiKeyPoliciesAsync(policyKey.Id, null, new ApiKeyCorsPolicy());
        var cleared = await store.FindApiKeyByHashAsync("hash-policies");
        Assert.NotNull(cleared);
        Assert.Null(cleared!.Mtls);
        // Present but empty is a different fact from absent: it closes cross-origin access for this key.
        Assert.NotNull(cleared.Cors);
        Assert.True(cleared.Cors!.IsEmpty);

        // Admins with roles.
        await store.CreateAdminAsync("viewer", "pwd-hash", AdminRoles.Viewer);
        var admin = await store.FindAdminByUsernameAsync("viewer");
        Assert.NotNull(admin);
        Assert.Equal(AdminRoles.Viewer, admin!.Role);

        // Scopes.
        await store.UpsertScopeAsync(new Scope { Name = "widgets:read", Description = "Read widgets" });
        var scopes = await store.GetScopesAsync();
        Assert.Contains(scopes, s => s.Name == "widgets:read");

        // Audit.
        await store.AppendAuditAsync(new AuditEntry { Category = "endpoint.call", Actor = "wk_test", Outcome = "ok" });
        var entries = await store.QueryAuditAsync(new AuditQuery { Limit = 10 });
        Assert.Contains(entries, e => e.Category == "endpoint.call");

        // Cache purges (ON CONFLICT upsert, so a second purge of the same route moves the stamp rather
        // than failing on the primary key - the normal case, since a route is purged again and again).
        var first = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        await store.RecordCachePurgeAsync(["widgets", "orders/get"], first);
        var purges = await store.GetCachePurgesAsync();
        Assert.Equal(first, purges["widgets"]);
        Assert.Equal(first, purges["orders/get"]);

        var second = first.AddMinutes(5);
        await store.RecordCachePurgeAsync(["widgets"], second);
        purges = await store.GetCachePurgesAsync();
        Assert.Equal(second, purges["widgets"]);
        Assert.Equal(first, purges["orders/get"]);
    }
}
