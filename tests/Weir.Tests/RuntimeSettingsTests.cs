using Microsoft.Extensions.Options;
using Weir.Contracts;
using Weir.ControlPlane.Sqlite;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class RuntimeSettingsTests
{
    private static SqliteControlPlaneStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"weir-settings-{Guid.NewGuid():N}.db");
        var options = Options.Create(new SqliteControlPlaneOptions { ConnectionString = $"Data Source={path}" });
        return new SqliteControlPlaneStore(options, TimeProvider.System);
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
}
