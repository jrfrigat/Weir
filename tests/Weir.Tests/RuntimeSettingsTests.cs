using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Contracts;
using Weir.ControlPlane.Sqlite;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class RuntimeSettingsTests : IDisposable
{
    /// <summary>Throwaway databases this test opened; each is removed when the test ends.</summary>
    private readonly List<TempSqliteDatabase> _databases = [];

    /// <summary>Removes the databases this test created.</summary>
    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Opens a store on a fresh throwaway database.</summary>
    /// <returns>The store, not yet initialized.</returns>
    private SqliteControlPlaneStore NewStore()
    {
        var database = new TempSqliteDatabase("weir-settings");
        _databases.Add(database);
        return new SqliteControlPlaneStore(Options.Create(database.Options), TimeProvider.System);
    }

    [Fact]
    public async Task Seeds_From_Options_When_Nothing_Stored()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var settings = new RuntimeSettings(store, Options.Create(new WeirDataPlaneOptions { MaxRows = 5, MaxTvpRows = 7 }));
        await settings.InitializeAsync();

        Assert.Equal(5, settings.Current.MaxRows);
        Assert.Equal(7, settings.Current.MaxTvpRows);
    }

    [Fact]
    public async Task Update_Persists_And_Reloads_Across_Instances()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var first = new RuntimeSettings(store, Options.Create(new WeirDataPlaneOptions { MaxRows = 5 }));
        await first.InitializeAsync();
        await first.UpdateAsync(new WeirSystemSettings { MaxRows = 9, RequestTimeoutSeconds = 12 });
        Assert.Equal(9, first.Current.MaxRows);

        // A fresh instance over the same store loads the persisted values, overriding its seed.
        var second = new RuntimeSettings(store, Options.Create(new WeirDataPlaneOptions { MaxRows = 5 }));
        await second.InitializeAsync();
        Assert.Equal(9, second.Current.MaxRows);
        Assert.Equal(12, second.Current.RequestTimeoutSeconds);
    }

    [Fact]
    public async Task A_Corrupt_Stored_Document_Is_Reported_And_Leaves_The_Seed()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.SaveSettingsJsonAsync("{ not valid json");

        var logger = new CapturingLogger();
        var settings = new RuntimeSettings(store, Options.Create(new WeirDataPlaneOptions { MaxRows = 5 }), logger);
        await settings.InitializeAsync();

        // The broken document must not stop startup, but it must not pass silently either: the operator
        // has to be able to tell the seeded values from the stored ones.
        Assert.Equal(5, settings.Current.MaxRows);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>An <see cref="ILogger{TCategoryName}"/> that records every message it is handed, for asserting on the log.</summary>
    private sealed class CapturingLogger : ILogger<RuntimeSettings>
    {
        /// <summary>The captured entries, in the order they were written.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
