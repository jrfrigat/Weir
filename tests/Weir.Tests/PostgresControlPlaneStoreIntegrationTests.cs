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
    }
}
