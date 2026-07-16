using System.Security.Cryptography;
using System.Text;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

// Exercises the engine's cache-eligible response path: entity-tag generation on a cache hit and the
// pre-write 304 short-circuit when the caller's If-None-Match matches.
public class WeirEngineTests
{
    /// <summary>A response cache that always returns a fixed payload with a pre-computed ETag, simulating a warm cache.</summary>
    private sealed class HitCache(byte[] payload) : IResponseCache
    {
        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CachedResponse?>(new CachedResponse(payload, ETag: string.Concat("\"", Convert.ToHexString(SHA256.HashData(payload)), "\"")));

        public ValueTask<CachedResponse> SetAsync(string key, ReadOnlyMemory<byte> payload, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CachedResponse(payload, ETag: string.Concat("\"", Convert.ToHexString(SHA256.HashData(payload.Span)), "\"")));

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>A connector that is never executed on the cache-hit path; all members throw if reached.</summary>
    private sealed class UnusedConnector : IDbConnector
    {
        public string ProviderName => "test";

        public Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The connector must not run on a cache hit.");

        public Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DbObjectDescriptor>>([]);

        public Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(string connectionName, string schema, string objectName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DbParameterDescriptor>>([]);
    }

    /// <summary>A registry resolving every name to a single test descriptor.</summary>
    private sealed class SingleRegistry : IDataConnectionRegistry
    {
        private static readonly DataConnectionDescriptor Descriptor = new()
        {
            Name = "default",
            Provider = "test",
            ConnectionString = "n/a",
        };

        public bool TryGet(string name, out DataConnectionDescriptor descriptor)
        {
            descriptor = Descriptor;
            return true;
        }

        public DataConnectionDescriptor Resolve(string name) => Descriptor;

        public IReadOnlyCollection<DataConnectionDescriptor> All => [Descriptor];
    }

    /// <summary>Runtime settings fixed to defaults.</summary>
    private sealed class FixedSettings : IRuntimeSettings
    {
        public WeirSystemSettings Current { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(WeirSystemSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static WeirEngine NewEngine(byte[] cachedPayload) => new(
        new ParameterBinder(),
        new SingleRegistry(),
        [new UnusedConnector()],
        new HitCache(cachedPayload),
        [],
        new FixedSettings());

    private static WeirInvocation NewInvocation() => new()
    {
        Endpoint = new EndpointDefinition
        {
            Route = "orders/list",
            HttpMethod = "GET",
            ConnectionName = "default",
            ObjectName = "usp_list",
            Cache = new CachePolicy { Enabled = true, TtlSeconds = 60 },
        },
        ApiKeyPrefix = "abc",
    };

    [Fact]
    public async Task Cache_Hit_Sets_ETag_And_Writes_Body()
    {
        var payload = Encoding.UTF8.GetBytes("{\"data\":[[]]}");
        var engine = NewEngine(payload);

        WeirResponseMetadata head = default;
        using var output = new MemoryStream();
        var control = new WeirResponseControl { OnResponseHead = m => { head = m; return ValueTask.CompletedTask; } };
        var metadata = await engine.ExecuteAsync(NewInvocation(), output, control);

        Assert.True(metadata.CacheHit);
        Assert.True(metadata.Cacheable);
        Assert.False(metadata.NotModified);
        Assert.NotNull(metadata.ETag);
        Assert.Equal(60, metadata.MaxAgeSeconds);
        Assert.Equal(payload, output.ToArray());
        Assert.Equal(metadata.ETag, head.ETag); // header callback saw the same tag
    }

    [Fact]
    public async Task Matching_If_None_Match_Returns_304_Without_Body()
    {
        var payload = Encoding.UTF8.GetBytes("{\"data\":[[]]}");
        var engine = NewEngine(payload);

        // First call learns the entity tag.
        using var warm = new MemoryStream();
        var first = await engine.ExecuteAsync(NewInvocation(), warm);
        var etag = first.ETag!;

        // Second call presents that tag; the engine must not write a body.
        WeirResponseMetadata head = default;
        using var output = new MemoryStream();
        var control = new WeirResponseControl
        {
            IfNoneMatch = etag,
            OnResponseHead = m => { head = m; return ValueTask.CompletedTask; },
        };
        var metadata = await engine.ExecuteAsync(NewInvocation(), output, control);

        Assert.True(metadata.NotModified);
        Assert.Equal(0, output.Length);
        Assert.True(head.NotModified);
        Assert.Equal(etag, head.ETag);
    }

    [Fact]
    public async Task Wildcard_If_None_Match_Returns_304()
    {
        var payload = Encoding.UTF8.GetBytes("{\"data\":[[]]}");
        var engine = NewEngine(payload);

        using var output = new MemoryStream();
        var control = new WeirResponseControl { IfNoneMatch = "*" };
        var metadata = await engine.ExecuteAsync(NewInvocation(), output, control);

        Assert.True(metadata.NotModified);
        Assert.Equal(0, output.Length);
    }

    /// <summary>A connector that always fails and classifies its failure as a deadlock.</summary>
    private sealed class DeadlockConnector : IDbConnector
    {
        public string ProviderName => "test";

        public DbErrorCategory ClassifyError(Exception exception) => DbErrorCategory.Deadlock;

        public Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DbObjectDescriptor>>([]);

        public Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(string connectionName, string schema, string objectName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DbParameterDescriptor>>([]);
    }

    /// <summary>Captures the call context passed to <c>OnFailedAsync</c>.</summary>
    private sealed class CapturingObserver : IWeirCallObserver
    {
        public WeirCallContext? Failed { get; private set; }

        public ValueTask OnStartedAsync(WeirCallContext context) => ValueTask.CompletedTask;

        public ValueTask OnCompletedAsync(WeirCallContext context) => ValueTask.CompletedTask;

        public ValueTask OnFailedAsync(WeirCallContext context, Exception exception)
        {
            Failed = context;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Failure_Is_Classified_And_Observed()
    {
        var observer = new CapturingObserver();
        using var engine = new WeirEngine(
            new ParameterBinder(),
            new SingleRegistry(),
            [new DeadlockConnector()],
            new HitCache([]),
            [observer],
            new FixedSettings());

        var invocation = new WeirInvocation
        {
            Endpoint = new EndpointDefinition
            {
                Route = "orders/create",
                HttpMethod = "POST",
                ConnectionName = "default",
                ObjectName = "usp_create",
                // Cache disabled so the call reaches the (throwing) connector.
            },
            ApiKeyPrefix = "abc",
        };

        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(invocation, output));

        Assert.NotNull(observer.Failed);
        Assert.Equal(DbErrorCategory.Deadlock, observer.Failed!.DbError);
        Assert.Equal(500, observer.Failed.StatusCode);
    }
}
