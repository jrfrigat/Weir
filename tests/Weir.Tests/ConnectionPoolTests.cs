using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using Weir.Abstractions;
using Weir.Connectors.PostgreSql;
using Weir.Connectors.SqlServer;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

// Pooling is stated once, in Weir's own terms, and each connector translates it onto its driver's
// keywords. Two things have to hold: an unset property must leave the connection string alone (both
// drivers already pool, and a "default" that quietly rewrote the string would change behaviour nobody
// asked to change), and a configuration that cannot mean anything must fail at startup rather than be
// half applied.
public class ConnectionPoolTests
{
    private const string SqlServerBase = "Server=localhost;Database=WeirDemo;Integrated Security=true";

    private const string PostgresBase = "Host=localhost;Database=weir;Username=weir";

    private static DataConnectionRegistry Registry(DataConnectionEntry entry, string name = "default")
    {
        var options = new WeirDataConnectionsOptions();
        options.Connections[name] = entry;
        return new DataConnectionRegistry(Options.Create(options));
    }

    private static DataConnectionEntry Entry(DataConnectionPoolEntry? pool = null) => new()
    {
        Provider = "SqlServer",
        ConnectionString = SqlServerBase,
        Pool = pool ?? new DataConnectionPoolEntry(),
    };

    [Fact]
    public void An_Unconfigured_Connection_Carries_An_Empty_Pool()
    {
        var descriptor = Registry(Entry()).Resolve("default");
        Assert.True(descriptor.Pool.IsEmpty);
    }

    [Fact]
    public void An_Empty_Pool_Leaves_The_Connection_String_Untouched()
    {
        // Byte-identical, not merely equivalent: round-tripping through a connection-string builder
        // reorders and re-cases keywords, which would make every diff of a running config a puzzle.
        Assert.Equal(SqlServerBase, SqlServerConnectionStrings.ApplyPool(SqlServerBase, new ConnectionPoolOptions()));
        Assert.Equal(PostgresBase, PostgreSqlConnectionStrings.ApplyPool(PostgresBase, new ConnectionPoolOptions()));
    }

    [Fact]
    public void SqlServer_Pool_Settings_Reach_The_Driver_Keywords()
    {
        var applied = SqlServerConnectionStrings.ApplyPool(SqlServerBase, new ConnectionPoolOptions
        {
            Enabled = true,
            MinSize = 2,
            MaxSize = 40,
            AcquireTimeoutSeconds = 12,
        });

        var builder = new SqlConnectionStringBuilder(applied);
        Assert.True(builder.Pooling);
        Assert.Equal(2, builder.MinPoolSize);
        Assert.Equal(40, builder.MaxPoolSize);
        Assert.Equal(12, builder.ConnectTimeout);
    }

    [Fact]
    public void Postgres_Pool_Settings_Reach_The_Driver_Keywords()
    {
        var applied = PostgreSqlConnectionStrings.ApplyPool(PostgresBase, new ConnectionPoolOptions
        {
            Enabled = true,
            MinSize = 3,
            MaxSize = 50,
            AcquireTimeoutSeconds = 9,
        });

        var builder = new NpgsqlConnectionStringBuilder(applied);
        Assert.True(builder.Pooling);
        Assert.Equal(3, builder.MinPoolSize);
        Assert.Equal(50, builder.MaxPoolSize);
        Assert.Equal(9, builder.Timeout);
    }

    [Fact]
    public void Pooling_Can_Be_Turned_Off()
    {
        // The mode the feature exists for besides sizing: a connection whose server holds per-session
        // state gets a fresh physical connection per request.
        Assert.False(new SqlConnectionStringBuilder(
            SqlServerConnectionStrings.ApplyPool(SqlServerBase, new ConnectionPoolOptions { Enabled = false })).Pooling);
        Assert.False(new NpgsqlConnectionStringBuilder(
            PostgreSqlConnectionStrings.ApplyPool(PostgresBase, new ConnectionPoolOptions { Enabled = false })).Pooling);
    }

    [Fact]
    public void A_Setting_Left_Null_Does_Not_Overwrite_The_Connection_String()
    {
        // The overlay only writes what it was told. A connection string that already sizes its pool keeps
        // that size when the configuration speaks about something else entirely.
        var applied = SqlServerConnectionStrings.ApplyPool(
            SqlServerBase + ";Max Pool Size=77", new ConnectionPoolOptions { MinSize = 1 });

        var builder = new SqlConnectionStringBuilder(applied);
        Assert.Equal(77, builder.MaxPoolSize);
        Assert.Equal(1, builder.MinPoolSize);
    }

    [Fact]
    public void A_Configured_Setting_Wins_Over_The_Connection_String()
    {
        var applied = SqlServerConnectionStrings.ApplyPool(
            SqlServerBase + ";Max Pool Size=77", new ConnectionPoolOptions { MaxSize = 20 });

        Assert.Equal(20, new SqlConnectionStringBuilder(applied).MaxPoolSize);
    }

    [Theory]
    [InlineData(-1, null, null, "MinSize")]
    [InlineData(null, 0, null, "MaxSize")]
    [InlineData(10, 5, null, "exceeds")]
    [InlineData(null, null, -5, "AcquireTimeoutSeconds")]
    public void An_Impossible_Pool_Fails_At_Startup(int? min, int? max, int? acquire, string expected)
    {
        var entry = Entry(new DataConnectionPoolEntry { MinSize = min, MaxSize = max, AcquireTimeoutSeconds = acquire });
        var error = Assert.Throws<WeirConfigurationException>(() => Registry(entry));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Contains("default", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sizing_An_Unpooled_Connection_Fails_At_Startup()
    {
        // Refused rather than ignored: it says the operator expects a pool that will not exist, and a
        // driver would silently drop half of what they wrote.
        var entry = Entry(new DataConnectionPoolEntry { Enabled = false, MaxSize = 10 });
        var error = Assert.Throws<WeirConfigurationException>(() => Registry(entry));
        Assert.Contains("Pool.Enabled is false", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_Pooling_Off_Alone_Is_Valid()
    {
        var descriptor = Registry(Entry(new DataConnectionPoolEntry { Enabled = false })).Resolve("default");
        Assert.False(descriptor.Pool.Enabled);
        Assert.False(descriptor.Pool.IsEmpty);
    }

    [Fact]
    public void The_Pool_Section_Binds_From_Configuration()
    {
        // The binding is the part most likely to break silently: a nested section that fails to bind
        // leaves every property null, which is indistinguishable from "not configured" - the pool would
        // simply never be applied and nothing would say so. These are the environment-variable style keys
        // a container passes, which is how this is set in a real deployment.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Weir:DataConnections:default:Provider"] = "SqlServer",
                ["Weir:DataConnections:default:ConnectionString"] = SqlServerBase,
                ["Weir:DataConnections:default:Pool:Enabled"] = "true",
                ["Weir:DataConnections:default:Pool:MinSize"] = "2",
                ["Weir:DataConnections:default:Pool:MaxSize"] = "40",
                ["Weir:DataConnections:default:Pool:AcquireTimeoutSeconds"] = "15",
            })
            .Build();

        var options = new WeirDataConnectionsOptions();
        configuration.GetSection("Weir:DataConnections").Bind(options.Connections);

        var descriptor = new DataConnectionRegistry(Options.Create(options)).Resolve("default");
        Assert.True(descriptor.Pool.Enabled);
        Assert.Equal(2, descriptor.Pool.MinSize);
        Assert.Equal(40, descriptor.Pool.MaxSize);
        Assert.Equal(15, descriptor.Pool.AcquireTimeoutSeconds);
    }

    [Fact]
    public void A_Connection_Without_A_Pool_Section_Binds_To_An_Empty_Pool()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Weir:DataConnections:default:Provider"] = "SqlServer",
                ["Weir:DataConnections:default:ConnectionString"] = SqlServerBase,
            })
            .Build();

        var options = new WeirDataConnectionsOptions();
        configuration.GetSection("Weir:DataConnections").Bind(options.Connections);

        Assert.True(new DataConnectionRegistry(Options.Create(options)).Resolve("default").Pool.IsEmpty);
    }
}
