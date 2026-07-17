using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weir.ControlPlane.Sqlite;
using Weir.Contracts;
using Weir.Host.Audit;
using Weir.Host.Options;
using Xunit;

namespace Weir.Tests;

// Audit is a compliance record, and the queue that keeps it off the request thread used to be thrown
// away on every graceful shutdown: the reader awaited on the host's stopping token, so the moment
// shutdown began it abandoned whatever was still queued. That loss was also the one kind the drop
// counter could not see - it only counts a full queue - so a redeploy quietly took the tail of the
// audit with it and reported nothing.
public class AuditDrainTests
{
    [Fact]
    public async Task Entries_Queued_Before_Shutdown_Are_Written()
    {
        var store = await NewStoreAsync();
        using var auditor = NewAuditor(store);
        await auditor.StartAsync(CancellationToken.None);

        for (var i = 0; i < 200; i++)
        {
            auditor.Enqueue(NewEntry(i));
        }

        // Stop at once, without letting the reader get ahead: this is the redeploy case.
        await auditor.StopAsync(CancellationToken.None);

        var written = await store.QueryAuditAsync(new AuditQuery { Limit = 500 });
        Assert.Equal(200, written.Count);
        Assert.Equal(0, auditor.DroppedCount);
    }

    [Fact]
    public async Task An_Entry_Arriving_After_Shutdown_Is_Counted_As_Dropped()
    {
        var store = await NewStoreAsync();
        using var auditor = NewAuditor(store);
        await auditor.StartAsync(CancellationToken.None);
        await auditor.StopAsync(CancellationToken.None);

        // The channel is closed by now. The entry cannot be kept - but it must not vanish unrecorded,
        // which is exactly what made the shutdown loss invisible before.
        auditor.Enqueue(NewEntry(1));

        Assert.Equal(1, auditor.DroppedCount);
        Assert.Empty(await store.QueryAuditAsync(new AuditQuery { Limit = 10 }));
    }

    /// <summary>Opens a throwaway SQLite control plane.</summary>
    /// <returns>The initialized store.</returns>
    private static async Task<SqliteControlPlaneStore> NewStoreAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"weir-audit-{Guid.NewGuid():N}.db");
        var store = new SqliteControlPlaneStore(
            Options.Create(new SqliteControlPlaneOptions { ConnectionString = $"Data Source={path}" }),
            TimeProvider.System);
        await store.InitializeAsync();
        return store;
    }

    /// <summary>Builds an auditor with data-plane auditing on and a roomy queue.</summary>
    /// <param name="store">The store to write to.</param>
    /// <returns>The auditor.</returns>
    private static DataPlaneAuditor NewAuditor(SqliteControlPlaneStore store) =>
        new(store, Options.Create(new AuditOptions { DataPlane = true, QueueCapacity = 10_000 }),
            NullLogger<DataPlaneAuditor>.Instance);

    /// <summary>Builds one audit entry.</summary>
    /// <param name="i">A discriminator for the actor.</param>
    /// <returns>The entry.</returns>
    private static AuditEntry NewEntry(int i) => new()
    {
        Category = "data.call",
        Actor = $"key-{i}",
        Outcome = "ok",
        Timestamp = DateTimeOffset.UnixEpoch,
    };
}
