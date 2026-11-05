using System.Data;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using StackExchange.Redis;
using Weir.Abstractions;
using Weir.Caching.Redis;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

/// <summary>Exercises real Redis binary snapshots; enabled by WEIR_CONTAINER_TESTS=1.</summary>
/// <param name="fixture">A disposable loopback Redis server.</param>
public sealed class RedisResponseCacheIntegrationTests(RedisResponseCacheIntegrationTests.RedisFixture fixture)
    : IClassFixture<RedisResponseCacheIntegrationTests.RedisFixture>
{
    /// <summary>One logical route shared by the protocol tests.</summary>
    private const string Scope = "weir:orders/list:";
    /// <summary>One logical key whose vary-by portion is already opaque.</summary>
    private const string Key = Scope + "0123456789ABCDEF";
    /// <summary>Whether Docker-backed checks were requested.</summary>
    private static bool Enabled => Environment.GetEnvironmentVariable("WEIR_CONTAINER_TESTS") == "1";

    /// <summary>Different provider instances share hits, atomic invalidation and installation isolation.</summary>
    /// <returns>The completed cross-instance check.</returns>
    [Fact]
    public async Task CrossInstance_FencesPurge_AndIsolatesNamespaces()
    {
        if (!Enabled) return;
        var settings = new Settings();
        var budget = new ResponseBufferBudget(settings);
        var identity = "fixture-" + Guid.NewGuid().ToString("N");
        var first = NewCache(identity, settings, budget);
        var second = NewCache(identity, settings, budget);
        var isolated = NewCache(identity + "-other", settings, budget);
        var before = await first.LookupAsync(Key, Scope);
        await second.RemoveByPrefixAsync(Scope);
        await first.SetIfCurrentAsync(Key, Entry("stale"), TimeSpan.FromMinutes(1), before.Fence!.Value);
        Assert.Null((await second.LookupAsync(Key, Scope)).Entry);
        var fresh = await first.LookupAsync(Key, Scope);
        await first.SetIfCurrentAsync(Key, Entry("fresh"), TimeSpan.FromMinutes(1), fresh.Fence!.Value);
        using (var hit = (await second.LookupAsync(Key, Scope)).Entry!)
        {
            Assert.Equal("fresh", Encoding.UTF8.GetString(hit.Response.Bytes.Span));
            Assert.Equal(Entry("fresh").ETag, hit.Response.ETag);
            Assert.True(budget.Used > hit.Response.Bytes.Length);
        }
        Assert.Equal(0, budget.Used);
        Assert.Null((await isolated.LookupAsync(Key, Scope)).Entry);
        await isolated.RemoveByPrefixAsync(Scope);
        using var preserved = (await first.LookupAsync(Key, Scope)).Entry;
        Assert.NotNull(preserved);
        Assert.Equal(0, first.GetStatus().LocalPayloadBytes);
        Assert.True(first.GetStatus().Available);
    }

    /// <summary>Snapshots and indexes expire together without touching a driver or JSON serializer.</summary>
    /// <returns>The completed expiry check.</returns>
    [Fact]
    public async Task Ttl_ExpiresCompleteBinaryEntry()
    {
        if (!Enabled) return;
        var settings = new Settings();
        var cache = NewCache("ttl-" + Guid.NewGuid().ToString("N"), settings, new ResponseBufferBudget(settings));
        var lookup = await cache.LookupAsync(Key, Scope);
        await cache.SetIfCurrentAsync(Key, Entry("ttl"), TimeSpan.FromSeconds(1), lookup.Fence!.Value);
        using (var hit = (await cache.LookupAsync(Key, Scope)).Entry) Assert.NotNull(hit);
        await Task.Delay(1200);
        Assert.Null((await cache.LookupAsync(Key, Scope)).Entry);
    }

    /// <summary>Invalid payloads are refused and repaired, and policy reductions prevent oversized reads.</summary>
    /// <returns>The completed corruption and admission check.</returns>
    [Fact]
    public async Task CorruptFrame_RepairsIndex_AndOversizePolicySkips()
    {
        if (!Enabled) return;
        var settings = new Settings();
        var budget = new ResponseBufferBudget(settings);
        var identity = "corrupt-" + Guid.NewGuid().ToString("N");
        var cache = NewCache(identity, settings, budget);
        var lookup = await cache.LookupAsync(Key, Scope);
        await cache.SetIfCurrentAsync(Key, Entry("original"), TimeSpan.FromMinutes(1), lookup.Fence!.Value);
        var server = fixture.Connection.GetServer(fixture.Connection.GetEndPoints()[0]);
        var snapshot = server.Keys(pattern: "weir:response:*:v1:payload:*")
            .Single(key => ((string)key!).Contains(NamespaceHash(identity), StringComparison.Ordinal));
        var database = fixture.Connection.GetDatabase();
        var length = await database.StringLengthAsync(snapshot);
        await database.StringSetAsync(snapshot, new byte[(int)length]);
        var repaired = await cache.LookupAsync(Key, Scope);
        Assert.Null(repaired.Entry);
        Assert.Equal(1, cache.GetStatus().CorruptEntries);
        Assert.Equal(0, budget.Used);
        await cache.SetIfCurrentAsync(Key, Entry("restored"), TimeSpan.FromMinutes(1), repaired.Fence!.Value);
        using (var hit = (await cache.LookupAsync(Key, Scope)).Entry) Assert.NotNull(hit);
        settings.Current = settings.Current with { ResponseCacheMaxEntryBytes = 1 };
        Assert.Null((await cache.LookupAsync(Key, Scope)).Entry);
        await cache.SetIfCurrentAsync(Key, Entry("oversized"), TimeSpan.FromMinutes(1), repaired.Fence.Value);
        Assert.True(cache.GetStatus().SkippedStores > 0);
        Assert.Equal(0, budget.Used);
    }

    /// <summary>A corrupt advertised size cannot trigger an unreserved complete payload fetch.</summary>
    /// <returns>The completed pointer-validation check.</returns>
    [Fact]
    public async Task CorruptPointerLength_IsRejectedBeforeBorrowing()
    {
        if (!Enabled) return;
        var settings = new Settings();
        var budget = new ResponseBufferBudget(settings);
        var identity = "pointer-" + Guid.NewGuid().ToString("N");
        var cache = NewCache(identity, settings, budget);
        var lookup = await cache.LookupAsync(Key, Scope);
        await cache.SetIfCurrentAsync(Key, Entry(new string('x', 1024 * 1024)), TimeSpan.FromMinutes(1), lookup.Fence!.Value);
        var server = fixture.Connection.GetServer(fixture.Connection.GetEndPoints()[0]);
        var index = server.Keys(pattern: "weir:response:*:v1:index:*")
            .Single(key => ((string)key!).Contains(NamespaceHash(identity), StringComparison.Ordinal));
        var database = fixture.Connection.GetDatabase();
        var pointer = (string)(await database.StringGetAsync(index))!;
        await database.StringSetAsync(index, pointer[..(pointer.LastIndexOf(':') + 1)] + "100");
        Assert.Null((await cache.LookupAsync(Key, Scope)).Entry);
        Assert.Equal(0, budget.Used);
        Assert.Equal(1, cache.GetStatus().CorruptEntries);
    }

    /// <summary>The real engine bypasses the database on a Redis hit and emits a bodyless conditional response.</summary>
    /// <returns>The completed engine-provider integration check.</returns>
    [Fact]
    public async Task Engine_HitAnd304_DoNotExecuteDatabase_AndReleaseLease()
    {
        if (!Enabled) return;
        var settings = new Settings();
        var budget = new ResponseBufferBudget(settings);
        var cache = NewCache("engine-" + Guid.NewGuid().ToString("N"), settings, budget);
        var connector = new Connector();
        using var engine = new WeirEngine(new ParameterBinder(), new Registry(), [connector], cache, [], settings, budget);
        var invocation = new WeirInvocation
        {
            Endpoint = new EndpointDefinition
            {
                Route = "orders/list", HttpMethod = "GET", ConnectionName = "default", ObjectName = "fixture",
                Cache = new CachePolicy { Enabled = true, TtlSeconds = 60 }
            }
        };
        using var initial = new MemoryStream();
        var result = await engine.ExecuteAsync(invocation, initial);
        for (var attempt = 0; attempt < 200 && engine.DisposedFillCount == 0; attempt++) await Task.Delay(10);
        Assert.Equal(1, engine.DisposedFillCount);
        using var repeated = new MemoryStream();
        Assert.True((await engine.ExecuteAsync(invocation, repeated)).CacheHit);
        Assert.Equal(initial.ToArray(), repeated.ToArray());
        using var conditional = new MemoryStream();
        var notModified = await engine.ExecuteAsync(invocation, conditional, new WeirResponseControl { IfNoneMatch = result.ETag });
        Assert.True(notModified.NotModified);
        Assert.Equal(0, conditional.Length);
        Assert.Equal(1, connector.Executions);
        Assert.Equal(0, budget.Used);
        using var canceled = new CancellationTokenSource();
        using var interrupted = new CancelingStream(canceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(invocation, interrupted, default, canceled.Token));
        Assert.Equal(0, budget.Used);
        Assert.Equal(1, connector.Executions);
    }

    /// <summary>The in-box host exposes safe Redis settings and never returns connection configuration.</summary>
    /// <returns>The completed composition and redaction check.</returns>
    [Fact]
    public async Task Host_SelectsRedis_AndRedactsConnectionSettings()
    {
        if (!Enabled) return;
        await using var factory = new TransportGatingTests.HostFactory();
        var identity = "host-" + Guid.NewGuid().ToString("N");
        const string marker = "private-connection-marker";
        var connectionOptions = ConfigurationOptions.Parse(fixture.Connection.Configuration);
        connectionOptions.ClientName = marker;
        await using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Weir:ResponseCache:Provider", "Redis");
            builder.UseSetting("Weir:ResponseCache:Redis:Namespace", identity);
            builder.UseSetting("Weir:ResponseCache:Redis:ConnectionString", connectionOptions.ToString());
        });
        using var client = configured.CreateClient();
        var json = await SettingsJsonAsync(client);
        Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        var backend = document.RootElement.GetProperty("responseCache");
        Assert.Equal("Redis", backend.GetProperty("provider").GetString());
        Assert.Equal(identity, backend.GetProperty("namespace").GetString());
        Assert.True(backend.GetProperty("available").GetBoolean());
    }

    /// <summary>Memory remains usable without Redis even when unused Redis settings are invalid.</summary>
    /// <returns>The completed default-backend check.</returns>
    [Fact]
    public async Task Host_DefaultMemory_DoesNotConnectRedis()
    {
        await using var factory = new TransportGatingTests.HostFactory();
        await using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Weir:ResponseCache:Provider", "Memory");
            builder.UseSetting("Weir:ResponseCache:Redis:ConnectionString", "unused-invalid-private-configuration");
        });
        using var client = configured.CreateClient();
        var json = await SettingsJsonAsync(client);
        Assert.DoesNotContain("unused-invalid-private-configuration", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Memory", document.RootElement.GetProperty("responseCache").GetProperty("provider").GetString());
    }

    /// <summary>Signs in only to the isolated factory and reads its settings view.</summary>
    /// <param name="client">The disposable host client.</param>
    /// <returns>The secret-free settings JSON.</returns>
    private static async Task<string> SettingsJsonAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/admin/api/auth/login", new { username = "admin", password = "admin-password" });
        login.EnsureSuccessStatusCode();
        using var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.RootElement.GetProperty("token").GetString());
        return await client.GetStringAsync("/admin/api/settings");
    }

    /// <summary>Cancels after a transport borrows one portion of a real Redis lease.</summary>
    /// <param name="cancellation">The request lifetime to end.</param>
    private sealed class CancelingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Creates a provider over the shared disposable connection with its own namespace.</summary>
    /// <param name="identity">The isolated installation identity.</param>
    /// <param name="settings">Live application policy.</param>
    /// <param name="budget">Shared active admission.</param>
    /// <returns>The provider, which does not own the fixture connection.</returns>
    private RedisResponseCache NewCache(string identity, Settings settings, ResponseBufferBudget budget) =>
        new(fixture.Connection, Options.Create(new RedisResponseCacheOptions
        {
            ConnectionString = fixture.Connection.Configuration, Namespace = identity
        }), settings, budget, NullLogger<RedisResponseCache>.Instance);

    /// <summary>Builds an immutable body with the actual engine's strong validator.</summary>
    /// <param name="text">The test body.</param>
    /// <returns>The complete binary entry.</returns>
    private static CachedResponse Entry(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new(bytes, ResponseETag.Compute(bytes));
    }

    /// <summary>Locates deliberately corrupted protocol keys without exposing any real installation data.</summary>
    /// <param name="identity">The disposable namespace.</param>
    /// <returns>The documented installation hash tag.</returns>
    private static string NamespaceHash(string identity) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    /// <summary>Mutable settings shared by the engine and provider.</summary>
    private sealed class Settings : IRuntimeSettings
    {
        /// <inheritdoc />
        public WeirSystemSettings Current { get; set; } = new();
        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <inheritdoc />
        public Task UpdateAsync(WeirSystemSettings settings, CancellationToken cancellationToken = default) { Current = settings; return Task.CompletedTask; }
    }

    /// <summary>A metadata-only registry for the engine integration check.</summary>
    private sealed class Registry : IDataConnectionRegistry
    {
        /// <summary>The one fake data connection; no real database is contacted.</summary>
        private static readonly DataConnectionDescriptor Descriptor = new() { Name = "default", Provider = "fixture", ConnectionString = "unused" };
        /// <inheritdoc />
        public DataConnectionDescriptor Resolve(string name) => Descriptor;
        /// <inheritdoc />
        public bool TryGet(string name, out DataConnectionDescriptor descriptor) { descriptor = Descriptor; return true; }
        /// <inheritdoc />
        public IReadOnlyCollection<DataConnectionDescriptor> All => [Descriptor];
    }

    /// <summary>A connector that makes every accidental database execution observable.</summary>
    private sealed class Connector : IDbConnector
    {
        /// <summary>The number of actual executions.</summary>
        internal int Executions;
        /// <inheritdoc />
        public string ProviderName => "fixture";
        /// <inheritdoc />
        public Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Executions);
            return Task.FromResult<IDbExecution>(new Execution());
        }
        /// <inheritdoc />
        public Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <inheritdoc />
        public Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DbObjectDescriptor>>([]);
        /// <inheritdoc />
        public Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(string connectionName, string schema, string objectName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DbParameterDescriptor>>([]);
    }

    /// <summary>A real reader over a large fixture row, independent of Redis.</summary>
    private sealed class Execution : IDbExecution
    {
        /// <summary>The source table owned by this execution.</summary>
        private readonly DataTable _table = new();
        /// <inheritdoc />
        public DbDataReader Reader { get; }
        /// <summary>Creates a body spanning several transport writes.</summary>
        public Execution()
        {
            _table.Columns.Add("text", typeof(string));
            _table.Rows.Add(new string('x', 1024 * 1024));
            Reader = _table.CreateDataReader();
        }
        /// <inheritdoc />
        public IReadOnlyList<SqlMessage> Messages => [];
        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Outputs => new Dictionary<string, object?>();
        /// <inheritdoc />
        public int? ReturnValue => null;
        /// <inheritdoc />
        public int RecordsAffected => 0;
        /// <inheritdoc />
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) { Reader.Close(); return ValueTask.CompletedTask; }
        /// <inheritdoc />
        public ValueTask DisposeAsync() { Reader.Dispose(); _table.Dispose(); return ValueTask.CompletedTask; }
    }

    /// <summary>Owns a fresh Redis container and its reusable test connection.</summary>
    public sealed class RedisFixture : IAsyncLifetime
    {
        /// <summary>The disposable container, absent when integration checks are disabled.</summary>
        private IContainer? _container;
        /// <summary>The connection shared only by this fixture's providers.</summary>
        public ConnectionMultiplexer Connection { get; private set; } = null!;
        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            if (!Enabled) return;
            _container = new ContainerBuilder("redis:8.2-alpine").WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
                .WithCreateParameterModifier(parameters =>
                {
                    foreach (var bindings in parameters.HostConfig!.PortBindings!.Values)
                        foreach (var binding in bindings) binding.HostIP = "127.0.0.1";
                }).Build();
            await _container.StartAsync();
            Connection = await ConnectionMultiplexer.ConnectAsync($"{_container.Hostname}:{_container.GetMappedPublicPort(6379)},abortConnect=false,asyncTimeout=1000");
        }
        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            if (Connection is not null) await Connection.DisposeAsync();
            if (_container is not null) await _container.DisposeAsync();
        }
    }
}
