using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.ControlPlane.Sqlite;
using Xunit;

namespace Weir.Tests;

// These tests open a real SQLite database, so they also verify that the patched SQLitePCLRaw
// (pinned to resolve NU1903) works correctly at runtime with Microsoft.Data.Sqlite.
public class SqliteControlPlaneStoreTests
{
    private static SqliteControlPlaneStore NewStore() => NewStore(out _);

    private static SqliteControlPlaneStore NewStore(out string connectionString)
    {
        var path = Path.Combine(Path.GetTempPath(), $"weir-test-{Guid.NewGuid():N}.db");
        connectionString = $"Data Source={path}";
        var options = Options.Create(new SqliteControlPlaneOptions { ConnectionString = connectionString });
        return new SqliteControlPlaneStore(options, TimeProvider.System);
    }

    [Fact]
    public async Task Initialize_Is_Idempotent()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.InitializeAsync();
        Assert.Empty(await store.GetEndpointsAsync());
    }

    [Fact]
    public async Task Migration_Checksum_Mismatch_Fails_Fast()
    {
        var store = NewStore(out var connectionString);
        await store.InitializeAsync();

        // Tamper with a recorded checksum, as an edited shipped migration or a doctored history would.
        await using (var conn = new SqliteConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE SchemaMigrations SET Checksum = 'tampered' WHERE Version = 1";
            await cmd.ExecuteNonQueryAsync();
        }

        var reopened = new SqliteControlPlaneStore(
            Options.Create(new SqliteControlPlaneOptions { ConnectionString = connectionString }), TimeProvider.System);
        await Assert.ThrowsAsync<ControlPlaneMigrationException>(() => reopened.InitializeAsync());
    }

    [Fact]
    public async Task Settings_Roundtrip_And_Overwrite()
    {
        var store = NewStore();
        await store.InitializeAsync();

        Assert.Null(await store.GetSettingsJsonAsync());

        await store.SaveSettingsJsonAsync("{\"maxRows\":10}");
        Assert.Equal("{\"maxRows\":10}", await store.GetSettingsJsonAsync());

        // A second save replaces the single settings row rather than inserting a duplicate.
        await store.SaveSettingsJsonAsync("{\"maxRows\":20}");
        Assert.Equal("{\"maxRows\":20}", await store.GetSettingsJsonAsync());
    }

    [Fact]
    public async Task PasswordChange_Bumps_TokenVersion()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var created = await store.CreateAdminAsync("op", "hash0", "Admin");
        var before = await store.FindAdminByIdAsync(created.Id);
        Assert.NotNull(before);
        Assert.Equal(0, before!.TokenVersion);

        await store.UpdateAdminPasswordAsync(created.Id, "hash1");

        var after = await store.FindAdminByIdAsync(created.Id);
        Assert.NotNull(after);
        Assert.Equal(1, after!.TokenVersion);
        Assert.Equal("hash1", after.PasswordHash);
    }

    [Fact]
    public async Task PruneAudit_Deletes_Only_Older_Entries()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        await store.AppendAuditAsync(new AuditEntry { Timestamp = now.AddDays(-10), Category = "old" });
        await store.AppendAuditAsync(new AuditEntry { Timestamp = now.AddDays(-1), Category = "recent" });

        var deleted = await store.PruneAuditAsync(now.AddDays(-5));
        Assert.Equal(1, deleted);

        var remaining = await store.QueryAuditAsync(new AuditQuery { Limit = 100 });
        Assert.Single(remaining);
        Assert.Equal("recent", remaining[0].Category);
    }

    [Fact]
    public async Task RequestLog_Roundtrip_Filter_And_Prune()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var endpointId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.AppendRequestLogAsync(new RequestLogEntry
        {
            Timestamp = now.AddDays(-10), EndpointId = endpointId, Route = "ping", HttpMethod = "GET",
            ObjectName = "sales.Ping", StatusCode = 200, Outcome = "ok", DurationMs = 1.2, RowsReturned = 1,
            Parameters = "{}", Result = "{\"data\":[]}",
        });
        await store.AppendRequestLogAsync(new RequestLogEntry
        {
            Timestamp = now, EndpointId = endpointId, Route = "ping", HttpMethod = "GET",
            ObjectName = "sales.Ping", StatusCode = 500, Outcome = "error", DurationMs = 90, Slow = true, Error = "boom",
        });
        await store.AppendRequestLogAsync(new RequestLogEntry
        {
            Timestamp = now, EndpointId = Guid.NewGuid(), Route = "other", HttpMethod = "POST", DurationMs = 5,
        });

        // Filter by endpoint returns only that endpoint's two rows, newest first.
        var byEndpoint = await store.QueryRequestLogAsync(new RequestLogQuery { EndpointId = endpointId, Limit = 50 });
        Assert.Equal(2, byEndpoint.Count);
        Assert.Equal("ping", byEndpoint[0].Route);
        Assert.Equal("{}", byEndpoint[^1].Parameters);

        // Slow-only and errors-only narrow to the flagged/failed row.
        var slow = await store.QueryRequestLogAsync(new RequestLogQuery { SlowOnly = true, Limit = 50 });
        Assert.Single(slow);
        Assert.True(slow[0].Slow);

        var errors = await store.QueryRequestLogAsync(new RequestLogQuery { ErrorsOnly = true, Limit = 50 });
        Assert.Single(errors);
        Assert.Equal("boom", errors[0].Error);

        // Pruning removes only the older row.
        var deleted = await store.PruneRequestLogAsync(now.AddDays(-5));
        Assert.Equal(1, deleted);
        Assert.Equal(2, (await store.QueryRequestLogAsync(new RequestLogQuery { Limit = 50 })).Count);
    }

    [Fact]
    public async Task Endpoint_Roundtrip()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var saved = await store.UpsertEndpointAsync(new EndpointDefinition
        {
            Route = "orders/get",
            ConnectionName = "default",
            ObjectName = "usp_Get",
            SuppressMessages = true,
            Logging = new EndpointLogging { LogParameters = true, LogResult = true, SlowThresholdPercent = 40 },
            Parameters = [new EndpointParameter { Name = "id", DbType = WeirDbType.Int32 }],
        });

        Assert.NotEqual(Guid.Empty, saved.Id);

        var all = await store.GetEndpointsAsync();
        Assert.Single(all);
        Assert.Equal("orders/get", all[0].Route);
        Assert.True(all[0].SuppressMessages);
        Assert.True(all[0].Logging.LogParameters);
        Assert.True(all[0].Logging.LogResult);
        Assert.Equal(40, all[0].Logging.SlowThresholdPercent);
        Assert.Single(all[0].Parameters);

        await store.DeleteEndpointAsync(saved.Id);
        Assert.Empty(await store.GetEndpointsAsync());
    }

    [Fact]
    public async Task ApiKey_Roundtrip()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.CreateApiKeyAsync(new ApiKeyCreate { Name = "key", Scopes = ["orders:read"] }, "hash-123", "wk_live_ab12");

        var record = await store.FindApiKeyByHashAsync("hash-123");
        Assert.NotNull(record);
        Assert.Equal("wk_live_ab12", record!.Prefix);
        Assert.Contains("orders:read", record.Scopes);
        Assert.True(record.Enabled);
    }

    [Fact]
    public async Task ApiKey_Grants_Are_Persisted()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var grants = new List<ApiKeyGrant>
        {
            new() { Connection = "sales", Schema = "dbo", ObjectName = "*" },
            new() { Connection = "sales", Schema = "reporting", ObjectName = "GetSummary" },
        };
        await store.CreateApiKeyAsync(new ApiKeyCreate { Name = "scoped", Grants = grants }, "hash-grants", "wk_live_g1");

        var record = await store.FindApiKeyByHashAsync("hash-grants");
        Assert.NotNull(record);
        Assert.Equal(2, record!.Grants.Count);
        Assert.True(record.Grants[0].Allows("sales", "dbo", "AnyProc"));
        Assert.False(record.Grants[0].Allows("sales", "reporting", "AnyProc"));
        Assert.True(record.Grants[1].Allows("sales", "reporting", "GetSummary"));
    }

    [Fact]
    public async Task Admin_Roundtrip()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.CreateAdminAsync("admin", "pwd-hash", AdminRoles.Admin);

        var found = await store.FindAdminByUsernameAsync("admin");
        Assert.NotNull(found);
        Assert.Equal("pwd-hash", found!.PasswordHash);
        Assert.Equal(AdminRoles.Admin, found.Role);
        Assert.True(found.Enabled);
    }

    [Fact]
    public async Task Admin_Viewer_Role_Is_Persisted()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.CreateAdminAsync("viewer", "pwd-hash", AdminRoles.Viewer);

        var found = await store.FindAdminByUsernameAsync("viewer");
        Assert.NotNull(found);
        Assert.Equal(AdminRoles.Viewer, found!.Role);
    }

    [Fact]
    public async Task Audit_Append_And_Query()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.AppendAuditAsync(new AuditEntry { Category = "admin.login", Actor = "admin", Outcome = "ok" });

        var entries = await store.QueryAuditAsync(new AuditQuery { Limit = 10 });
        Assert.Single(entries);
        Assert.Equal("admin.login", entries[0].Category);
    }

    [Fact]
    public async Task RefreshToken_Create_Find_Rotate_And_Prune()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var admin = await store.CreateAdminAsync("refresh-user", "hash", "Admin");
        var now = DateTimeOffset.UnixEpoch.AddDays(1);

        var id = Guid.NewGuid();
        await store.CreateRefreshTokenAsync(id, admin.Id, "hash-1", now.AddDays(14), now);

        var found = await store.FindRefreshTokenAsync("hash-1");
        Assert.NotNull(found);
        Assert.Equal(admin.Id, found!.AdminId);
        Assert.False(found.Revoked);

        // Rotation: revoking marks it revoked (and it no longer authorizes a refresh).
        await store.RevokeRefreshTokenAsync(id, now);
        Assert.True((await store.FindRefreshTokenAsync("hash-1"))!.Revoked);

        // A password change revokes all of an admin's refresh tokens.
        await store.CreateRefreshTokenAsync(Guid.NewGuid(), admin.Id, "hash-2", now.AddDays(14), now);
        await store.RevokeRefreshTokensForAdminAsync(admin.Id, now);
        Assert.True((await store.FindRefreshTokenAsync("hash-2"))!.Revoked);

        // Pruning removes revoked and expired rows.
        var deleted = await store.PruneRefreshTokensAsync(now.AddDays(30));
        Assert.True(deleted >= 2);
        Assert.Null(await store.FindRefreshTokenAsync("hash-1"));
    }

    [Fact]
    public async Task LoginThrottle_Locks_After_Threshold_And_Expires()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var lockout = TimeSpan.FromMinutes(5);
        const string client = "10.0.0.1";

        // Two failures below the threshold of three: not locked yet.
        await store.RecordLoginFailureAsync(client, maxFailures: 3, lockout, now);
        await store.RecordLoginFailureAsync(client, maxFailures: 3, lockout, now);
        Assert.False(await store.IsLoginLockedAsync(client, now));

        // Third failure trips the lockout.
        await store.RecordLoginFailureAsync(client, maxFailures: 3, lockout, now);
        Assert.True(await store.IsLoginLockedAsync(client, now));

        // The lockout expires after the window.
        Assert.False(await store.IsLoginLockedAsync(client, now + lockout + TimeSpan.FromSeconds(1)));

        // A successful sign-in clears the state.
        await store.ResetLoginThrottleAsync(client);
        Assert.False(await store.IsLoginLockedAsync(client, now));
    }

    [Fact]
    public async Task LoginThrottle_Disabled_When_MaxFailures_NonPositive()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UnixEpoch.AddDays(1);

        await store.RecordLoginFailureAsync("1.2.3.4", maxFailures: 0, TimeSpan.FromMinutes(5), now);
        Assert.False(await store.IsLoginLockedAsync("1.2.3.4", now));
    }

    [Fact]
    public async Task Audit_Keyset_Paging_Walks_Every_Row_Without_Overlap()
    {
        var store = NewStore();
        await store.InitializeAsync();

        for (var i = 0; i < 10; i++)
        {
            await store.AppendAuditAsync(new AuditEntry { Category = "endpoint.call", Actor = $"a{i}" });
        }

        // First page: newest 4 (descending id).
        var first = await store.QueryAuditAsync(new AuditQuery { Limit = 4 });
        Assert.Equal(4, first.Count);
        Assert.True(first[0].Id > first[3].Id);

        // Second page via keyset cursor: strictly older ids, no overlap with the first page.
        var second = await store.QueryAuditAsync(new AuditQuery { Limit = 4, AfterId = first[^1].Id });
        Assert.Equal(4, second.Count);
        Assert.True(second[0].Id < first[^1].Id);
        Assert.Empty(first.Select(e => e.Id).Intersect(second.Select(e => e.Id)));

        // Walking all pages visits all ten distinct rows.
        var all = new List<long>();
        long? cursor = null;
        while (true)
        {
            var page = await store.QueryAuditAsync(new AuditQuery { Limit = 4, AfterId = cursor });
            if (page.Count == 0)
            {
                break;
            }

            all.AddRange(page.Select(e => e.Id));
            cursor = page[^1].Id;
        }

        Assert.Equal(10, all.Distinct().Count());
    }
}
