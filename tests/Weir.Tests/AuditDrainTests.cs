using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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
        var failures = new CapturingLogger<DataPlaneAuditor>();
        using var auditor = NewAuditor(store, failures);
        await auditor.StartAsync(CancellationToken.None);

        for (var i = 0; i < 200; i++)
        {
            auditor.Enqueue(NewEntry(i));
        }

        // Stop at once, without letting the reader get ahead: this is the redeploy case.
        await auditor.StopAsync(CancellationToken.None);

        // Check the two ways an entry can go missing before the count, and name them. A bare
        // "expected 200, got 0" cannot say whether nothing drained or every write threw, and the
        // writes throw into a swallowing catch - so without this the next failure is unreadable.
        Assert.True(
            auditor.WriteFailureCount == 0,
            $"{auditor.WriteFailureCount} audit writes failed: {failures.FirstError}");
        Assert.Equal(0, auditor.DroppedCount);

        var written = await store.QueryAuditAsync(new AuditQuery { Limit = 500 });
        Assert.Equal(200, written.Count);
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

    [Fact]
    public async Task Entries_Are_Written_Even_If_The_Read_Loop_Never_Ran()
    {
        var store = await NewStoreAsync();
        using var auditor = NewAuditor(store);

        // No StartAsync: this stands in for the race that made the drain flaky on a loaded machine.
        // BackgroundService schedules ExecuteAsync instead of running it inline, so a host that stops
        // before the scheduler reaches it cancels the work with the delegate never invoked - leaving
        // exactly this state, a full queue and a read loop that never existed.
        for (var i = 0; i < 50; i++)
        {
            auditor.Enqueue(NewEntry(i));
        }

        await auditor.StopAsync(CancellationToken.None);

        var written = await store.QueryAuditAsync(new AuditQuery { Limit = 100 });
        Assert.Equal(50, written.Count);
        Assert.Equal(0, auditor.DroppedCount);
        Assert.Equal(0, auditor.WriteFailureCount);
    }

    [Fact]
    public async Task An_Entry_That_Fails_To_Persist_Is_Counted_Rather_Than_Lost_Silently()
    {
        var path = NewDbPath();
        var store = await NewStoreAsync(path);
        var failures = new CapturingLogger<DataPlaneAuditor>();
        using var auditor = NewAuditor(store, failures);
        await auditor.StartAsync(CancellationToken.None);

        // Take the table away so every write throws. The drain catches and carries on by design - a
        // failing store must not stall the queue - and the point here is that carrying on is recorded.
        await using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            await conn.OpenAsync();
            await using var drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE Audit;";
            await drop.ExecuteNonQueryAsync();
        }

        auditor.Enqueue(NewEntry(1));
        auditor.Enqueue(NewEntry(2));
        await auditor.StopAsync(CancellationToken.None);

        // Neither entry was refused, so neither counts as dropped; both were accepted and then lost,
        // which is what the write-failure count is for.
        Assert.Equal(2, auditor.WriteFailureCount);
        Assert.Equal(0, auditor.DroppedCount);
        Assert.Contains("audit", failures.FirstError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds a path for a throwaway control-plane database.</summary>
    /// <returns>The file path.</returns>
    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"weir-audit-{Guid.NewGuid():N}.db");

    /// <summary>Opens a throwaway SQLite control plane.</summary>
    /// <param name="path">Where to put the database; a fresh path when omitted.</param>
    /// <returns>The initialized store.</returns>
    private static async Task<SqliteControlPlaneStore> NewStoreAsync(string? path = null)
    {
        var store = new SqliteControlPlaneStore(
            Options.Create(new SqliteControlPlaneOptions { ConnectionString = $"Data Source={path ?? NewDbPath()}" }),
            TimeProvider.System);
        await store.InitializeAsync();
        return store;
    }

    /// <summary>Builds an auditor with data-plane auditing on and a roomy queue.</summary>
    /// <param name="store">The store to write to.</param>
    /// <param name="logger">Where the auditor reports write failures; null discards them.</param>
    /// <returns>The auditor.</returns>
    private static DataPlaneAuditor NewAuditor(
        SqliteControlPlaneStore store, ILogger<DataPlaneAuditor>? logger = null) =>
        new(store, Options.Create(new AuditOptions { DataPlane = true, QueueCapacity = 10_000 }),
            logger ?? NullLogger<DataPlaneAuditor>.Instance);

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

/// <summary>
/// Keeps the first error a component logs, so a test can put the reason in its failure message. The
/// auditor writes inside a catch that swallows, so with a discarding logger the only evidence a run
/// leaves behind is a row count - which says nothing about why the rows are missing.
/// </summary>
/// <typeparam name="T">The component being logged for.</typeparam>
public sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>Guards the first-error fields against the auditor's reader thread.</summary>
    private readonly Lock _gate = new();

    /// <summary>The first error logged, message and exception, or null if none was.</summary>
    private string? _firstError;

    /// <summary>The first error logged, or a note that there was none.</summary>
    public string FirstError
    {
        get
        {
            lock (_gate)
            {
                return _firstError ?? "(no error was logged)";
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (logLevel < LogLevel.Warning)
        {
            return;
        }

        lock (_gate)
        {
            _firstError ??= $"{formatter(state, exception)} {exception}".Trim();
        }
    }
}
