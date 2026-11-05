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
    /// <summary>Large cached bodies are copied exactly through bounded writes, and 304 writes nothing.</summary>
    [Fact]
    public async Task LargeCacheHit_IsBounded_AndConditionalHitWritesNothing()
    {
        var payload = Encoding.UTF8.GetBytes("{\"data\":[[\"" + new string('x', 1024 * 1024) + "\"]]}");
        using var engine = NewEngine(payload);
        using var output = new LimitedWriteStream();
        var result = await engine.ExecuteAsync(NewInvocation(), output);
        Assert.Equal(payload, output.ToArray());
        Assert.True(output.Writes > 1);
        Assert.InRange(output.MaximumWrite, 1, 256 * 1024);

        using var conditional = new LimitedWriteStream();
        var metadata = await engine.ExecuteAsync(NewInvocation(), conditional,
            new WeirResponseControl { IfNoneMatch = result.ETag });
        Assert.True(metadata.NotModified);
        Assert.Equal(0, conditional.Writes);
    }

    /// <summary>Cancellation after one network portion prevents the remaining body from being copied.</summary>
    [Fact]
    public async Task LargeCacheHit_StopsAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var engine = NewEngine(new byte[1024 * 1024]);
        using var output = new LimitedWriteStream(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ExecuteAsync(NewInvocation(), output, default, cancellation.Token));
        Assert.Equal(1, output.Writes);
        Assert.Equal(256 * 1024, output.Length);
    }

    /// <summary>Observes transport handovers and optionally cancels after the first completed write.</summary>
    /// <param name="cancelAfterFirst">The cancellation source to signal, when requested.</param>
    private sealed class LimitedWriteStream(CancellationTokenSource? cancelAfterFirst = null) : MemoryStream
    {
        /// <summary>Number of submitted writes.</summary>
        internal int Writes { get; private set; }

        /// <summary>Largest submitted portion.</summary>
        internal int MaximumWrite { get; private set; }

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            MaximumWrite = Math.Max(MaximumWrite, buffer.Length);
            Write(buffer.Span);
            if (Writes == 1) cancelAfterFirst?.Cancel();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A response cache that always returns a fixed payload with a pre-computed ETag, simulating a warm cache.</summary>
    private sealed class HitCache(byte[] payload) : IResponseCache
    {
        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CachedResponse?>(new CachedResponse(payload, ETag: string.Concat("\"", Convert.ToHexString(SHA256.HashData(payload)), "\"")));

        public ValueTask SetAsync(string key, CachedResponse entry, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

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
