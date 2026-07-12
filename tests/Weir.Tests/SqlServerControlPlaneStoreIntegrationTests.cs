using Microsoft.Extensions.Options;
using Testcontainers.MsSql;
using Weir.Contracts;
using Weir.ControlPlane.SqlServer;
using Xunit;

namespace Weir.Tests;

// Integration tests for the SQL Server control-plane store. They start a real SQL Server container, so
// they require Docker and are opt-in: set the environment variable WEIR_CONTAINER_TESTS=1 to run them
// (the CI release/build pipeline sets it). Without the flag they return immediately so a local
// "dotnet test" without Docker still succeeds.
public class SqlServerControlPlaneStoreIntegrationTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("WEIR_CONTAINER_TESTS"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task Full_Roundtrip_Against_Real_SqlServer()
    {
        if (!Enabled)
        {
            return;
        }

        await using var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();
        await container.StartAsync();

        var options = Options.Create(new SqlServerControlPlaneOptions { ConnectionString = container.GetConnectionString() });
        var store = new SqlServerControlPlaneStore(options, TimeProvider.System);

        // Idempotent: running the migrations twice must not fail (checksums are verified on re-run).
        await store.InitializeAsync();
        await store.InitializeAsync();

        // Endpoints (exercises the UPDATE; IF @@ROWCOUNT = 0 INSERT upsert).
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

        // Updating the same endpoint must not create a second row.
        await store.UpsertEndpointAsync(saved with { Description = "updated" });
        var all = await store.GetEndpointsAsync();
        Assert.Single(all);

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

        // Scopes (single-row upsert).
        await store.UpsertScopeAsync(new Scope { Name = "widgets:read", Description = "Read widgets" });
        var scopes = await store.GetScopesAsync();
        Assert.Contains(scopes, s => s.Name == "widgets:read");

        // Audit (identity insert + keyset/offset pagination on read).
        await store.AppendAuditAsync(new AuditEntry { Category = "endpoint.call", Actor = "wk_test", Outcome = "ok" });
        var entries = await store.QueryAuditAsync(new AuditQuery { Limit = 10 });
        Assert.Contains(entries, e => e.Category == "endpoint.call");

        // Runtime settings (single-row document upsert).
        await store.SaveSettingsJsonAsync("{\"maxRows\":100}");
        Assert.Equal("{\"maxRows\":100}", await store.GetSettingsJsonAsync());
    }
}
