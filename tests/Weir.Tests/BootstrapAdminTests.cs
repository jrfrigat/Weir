using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Contracts;
using Weir.ControlPlane.Sqlite;
using Weir.Host;
using Weir.Host.Options;
using Weir.Host.Security;
using Xunit;

namespace Weir.Tests;

// H-3: every exit of the bootstrap-admin check must leave a log entry, because a host with no admin and
// no explanation looks exactly like a typo in the setting name. The tests drive the real check against a
// real SQLite control plane rather than a fake, so the store's own reads are part of what is verified.
public class BootstrapAdminTests : IDisposable
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

    /// <summary>Opens and initializes a store on a fresh throwaway database.</summary>
    /// <returns>The initialized store.</returns>
    private async Task<SqliteControlPlaneStore> NewStoreAsync()
    {
        var database = new TempSqliteDatabase("weir-bootstrap");
        _databases.Add(database);
        var store = new SqliteControlPlaneStore(Options.Create(database.Options), TimeProvider.System);
        await store.InitializeAsync();
        return store;
    }

    [Fact]
    public async Task Admins_Already_Present_Skip_Bootstrap_And_Say_So()
    {
        var store = await NewStoreAsync();
        await store.CreateAdminAsync("existing", PasswordHasher.Hash("a-strong-password", 1), AdminRoles.Admin);
        var logger = new CapturingLogger();

        await WeirStartup.BootstrapAdminAsync(
            store, new AdminBootstrapOptions { Username = "bootstrap", Password = "a-strong-password" }, 1, logger);

        // The existing account is untouched and the skip is on record, not silent.
        Assert.Single(await store.GetAdminsAsync());
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Rejected_Password_With_No_Admins_Is_Logged_As_An_Error()
    {
        var store = await NewStoreAsync();
        var logger = new CapturingLogger();

        await WeirStartup.BootstrapAdminAsync(
            store, new AdminBootstrapOptions { Username = "bootstrap", Password = "short" }, 1, logger);

        // No admin exists and the host keeps running, so the entry it is left with has to shout.
        Assert.Empty(await store.GetAdminsAsync());
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Missing_Credentials_Are_Noted_And_Create_Nothing()
    {
        var store = await NewStoreAsync();
        var logger = new CapturingLogger();

        await WeirStartup.BootstrapAdminAsync(store, new AdminBootstrapOptions(), 1, logger);

        Assert.Empty(await store.GetAdminsAsync());
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task A_Valid_Password_Creates_The_Admin()
    {
        var store = await NewStoreAsync();
        var logger = new CapturingLogger();

        await WeirStartup.BootstrapAdminAsync(
            store, new AdminBootstrapOptions { Username = "bootstrap", Password = "a-strong-password" }, 1, logger);

        var admin = Assert.Single(await store.GetAdminsAsync());
        Assert.Equal("bootstrap", admin.Username);
    }

    /// <summary>An <see cref="ILogger"/> that records every message it is handed, for asserting on the log.</summary>
    private sealed class CapturingLogger : ILogger
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
