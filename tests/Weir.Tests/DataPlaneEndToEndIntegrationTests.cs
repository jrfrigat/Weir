using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Testcontainers.PostgreSql;
using Weir.Abstractions;
using Weir.Connectors.PostgreSql;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

// Connector-execution and data-plane end-to-end tests against a real PostgreSQL database in a
// Testcontainers container. They require Docker and are opt-in (WEIR_CONTAINER_TESTS=1); without the
// flag they return immediately so a local "dotnet test" without Docker still succeeds.
public class DataPlaneEndToEndIntegrationTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("WEIR_CONTAINER_TESTS"), "1", StringComparison.Ordinal);

    /// <summary>A registry resolving "default" to the container's connection string.</summary>
    private sealed class TestRegistry(string connectionString) : IDataConnectionRegistry
    {
        private readonly DataConnectionDescriptor _descriptor = new()
        {
            Name = "default",
            Provider = "PostgreSql",
            ConnectionString = connectionString,
        };

        public bool TryGet(string name, out DataConnectionDescriptor descriptor)
        {
            descriptor = _descriptor;
            return true;
        }

        public DataConnectionDescriptor Resolve(string name) => _descriptor;

        public IReadOnlyCollection<DataConnectionDescriptor> All => [_descriptor];
    }

    /// <summary>Runtime settings fixed to defaults.</summary>
    private sealed class FixedSettings : IRuntimeSettings
    {
        public WeirSystemSettings Current { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(WeirSystemSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task SeedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE OR REPLACE FUNCTION get_widgets(p_min int) RETURNS TABLE(id int, name text) AS $$ " +
            "SELECT * FROM (VALUES (1,'a'),(2,'b'),(3,'c')) AS t(id, name) WHERE id >= p_min ORDER BY id $$ LANGUAGE sql;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static EndpointDefinition WidgetsEndpoint(bool cache) => new()
    {
        Route = "widgets",
        HttpMethod = "GET",
        ConnectionName = "default",
        ObjectType = DbObjectType.TableValuedFunction,
        Schema = "public",
        ObjectName = "get_widgets",
        Cache = cache ? new CachePolicy { Enabled = true, TtlSeconds = 60, VaryByParameters = ["p_min"] } : new CachePolicy(),
        Parameters = [new EndpointParameter { Name = "p_min", DbType = WeirDbType.Int32, Source = ParameterSource.Body, Required = true }],
    };

    [Fact]
    public async Task Connector_Executes_Function_And_Streams_Rows()
    {
        if (!Enabled)
        {
            return;
        }

        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        await SeedAsync(container.GetConnectionString());

        var connector = new PostgreSqlConnector(new TestRegistry(container.GetConnectionString()));
        var request = new DbExecutionRequest
        {
            ConnectionName = "default",
            Schema = "public",
            ObjectName = "get_widgets",
            ObjectType = DbObjectType.TableValuedFunction,
            Parameters = [new WeirParameter { Name = "p_min", DbType = WeirDbType.Int32, Value = 2 }],
        };

        await using var execution = await connector.ExecuteAsync(request);
        var count = 0;
        while (await execution.Reader.ReadAsync())
        {
            count++;
        }

        await execution.CompleteAsync();
        Assert.Equal(2, count); // ids 2 and 3
    }

    [Fact]
    public async Task Engine_Streams_Envelope_And_Serves_From_Cache()
    {
        if (!Enabled)
        {
            return;
        }

        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        await SeedAsync(container.GetConnectionString());

        var settings = new FixedSettings();
        var cache = new MemoryResponseCache(new MemoryCache(new MemoryCacheOptions()));
        using var engine = new WeirEngine(
            new ParameterBinder(settings),
            new TestRegistry(container.GetConnectionString()),
            [new PostgreSqlConnector(new TestRegistry(container.GetConnectionString()))],
            cache,
            [],
            settings);

        var endpoint = WidgetsEndpoint(cache: true);
        using var body = JsonDocument.Parse("{\"p_min\":2}");
        var invocation = new WeirInvocation
        {
            Endpoint = endpoint,
            Body = body.RootElement,
            HasBody = true,
            ApiKeyPrefix = "test",
        };

        using var first = new MemoryStream();
        var meta1 = await engine.ExecuteAsync(invocation, first);
        Assert.False(meta1.CacheHit);
        Assert.True(meta1.Cacheable);

        using var document = JsonDocument.Parse(first.ToArray());
        var rows = document.RootElement.GetProperty("data")[0];
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(2, rows[0].GetProperty("id").GetInt32());

        // A second identical call is served from the cache (same bytes, same ETag).
        using var second = new MemoryStream();
        var meta2 = await engine.ExecuteAsync(invocation, second);
        Assert.True(meta2.CacheHit);
        Assert.Equal(meta1.ETag, meta2.ETag);
        Assert.Equal(first.ToArray(), second.ToArray());
    }
}
