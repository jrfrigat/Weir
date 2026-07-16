using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Caching.Memory;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

// Covers the two properties of the cache-miss path that only show up under concurrency: a burst of
// callers for one cache key must cost exactly one database execution, and the response must not wait
// on the cache store.
public class WeirEngineCacheFillTests
{
    /// <summary>A connector whose execution blocks on a gate, so several callers can be held mid-flight at once.</summary>
    private sealed class GatedConnector : IDbConnector
    {
        private readonly TaskCompletionSource _gate;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executions;

        public GatedConnector(TaskCompletionSource gate) => _gate = gate;

        /// <summary>How many times the object was actually executed.</summary>
        public int Executions => Volatile.Read(ref _executions);

        /// <summary>Completes once the first caller is inside the execution and blocked on the gate.</summary>
        public Task Started => _started.Task;

        public string ProviderName => "test";

        public async Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executions);
            _started.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            return new FakeExecution();
        }

        public Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DbObjectDescriptor>>([]);

        public Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(string connectionName, string schema, string objectName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DbParameterDescriptor>>([]);
    }

    /// <summary>A result of a caller-chosen size, backed by DataTableReader so the engine sees a real DbDataReader.</summary>
    private sealed class FakeExecution : IDbExecution
    {
        private readonly DataTable _table;
        private readonly DbDataReader _reader;

        public FakeExecution(int rows = 1)
        {
            _table = new DataTable();
            _table.Columns.Add("id", typeof(int));
            _table.Columns.Add("text", typeof(string));
            for (var i = 0; i < rows; i++)
            {
                _table.Rows.Add(i, $"row-{i}-{new string('x', 64)}");
            }

            _reader = _table.CreateDataReader();
        }

        public DbDataReader Reader => _reader;

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            _reader.Close();
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<SqlMessage> Messages => [];

        public IReadOnlyDictionary<string, object?> Outputs => new Dictionary<string, object?>();

        public int? ReturnValue => null;

        public int RecordsAffected => 0;

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _table.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A cache that always misses and whose store never completes, standing in for a slow cache backend.</summary>
    private sealed class BlockingStoreCache : IResponseCache
    {
        private readonly TaskCompletionSource _storeGate;

        public BlockingStoreCache(TaskCompletionSource storeGate) => _storeGate = storeGate;

        /// <summary>Completes once a store has been attempted.</summary>
        public TaskCompletionSource StoreEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CachedResponse?>(null);

        public async ValueTask SetAsync(string key, CachedResponse entry, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            StoreEntered.TrySetResult();
            await _storeGate.Task;
        }

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>A connector returning however many rows it is currently told to, so body size can be varied per call.</summary>
    private sealed class SizedConnector : IDbConnector
    {
        /// <summary>Rows the next execution returns.</summary>
        public int Rows { get; set; } = 1;

        public string ProviderName => "test";

        public Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDbExecution>(new FakeExecution(Rows));

        public Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DbObjectDescriptor>>([]);

        public Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(string connectionName, string schema, string objectName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DbParameterDescriptor>>([]);
    }

    /// <summary>A cache that never hits and discards what it is given, so every call re-executes and re-buffers.</summary>
    private sealed class NeverHitsCache : IResponseCache
    {
        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CachedResponse?>(null);

        public ValueTask SetAsync(string key, CachedResponse entry, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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
    public async Task Concurrent_Callers_For_One_Cache_Key_Execute_The_Object_Once()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new GatedConnector(gate);
        using var cache = new MemoryResponseCache(new FixedSettings());
        using var engine = new WeirEngine(
            new ParameterBinder(), new SingleRegistry(), [connector], cache, [], new FixedSettings());

        const int callers = 20;
        var outputs = new MemoryStream[callers];
        var calls = new Task<WeirResponseMetadata>[callers];
        for (var i = 0; i < callers; i++)
        {
            outputs[i] = new MemoryStream();
            calls[i] = engine.ExecuteAsync(NewInvocation(), outputs[i]);
        }

        // Hold every caller until the first one is inside the database call. While it is, it owns the
        // fill for this key, so no other caller can start an execution of its own.
        await connector.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, connector.Executions);

        gate.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));

        // The point of the exercise: 20 simultaneous callers, one execution.
        Assert.Equal(1, connector.Executions);

        // And every caller got the same complete body, not an empty or partial one.
        var expected = outputs[0].ToArray();
        Assert.NotEmpty(expected);
        for (var i = 0; i < callers; i++)
        {
            Assert.Equal(expected, outputs[i].ToArray());
            Assert.Equal(expected.Length, outputs[i].Length);
            outputs[i].Dispose();
        }

        // Exactly one caller executed; the others were served the bytes it produced.
        Assert.Equal(callers - 1, results.Count(r => r.CacheHit));
        Assert.All(results, r => Assert.Equal(results[0].ETag, r.ETag));
    }

    /// <summary>
    /// The engine seeds each response buffer from the size the endpoint's last one came to, to avoid
    /// growing a large body through every intermediate size. That is a hint, not a promise: the body must
    /// come out identical whether the guess was absent, too low or too high.
    /// </summary>
    [Theory]
    [InlineData(1, 400)]    // first call has no hint at all, then a much larger body: guess too low
    [InlineData(400, 1)]    // large body first, then a tiny one: guess far too high
    [InlineData(400, 400)]  // steady state: the guess is right
    public async Task Buffered_Body_Is_Correct_Whatever_The_Size_Hint_Was(int firstRows, int secondRows)
    {
        var connector = new SizedConnector();
        using var engine = new WeirEngine(
            new ParameterBinder(), new SingleRegistry(), [connector], new NeverHitsCache(), [], new FixedSettings());

        // The cache never hits, so each call re-executes and re-buffers through the seeded path.
        connector.Rows = firstRows;
        using var first = new MemoryStream();
        await engine.ExecuteAsync(NewInvocation(), first);

        connector.Rows = secondRows;
        using var second = new MemoryStream();
        var metadata = await engine.ExecuteAsync(NewInvocation(), second);

        // Same endpoint, so the second call ran with the first call's size as its hint.
        var body = System.Text.Encoding.UTF8.GetString(second.ToArray());
        Assert.StartsWith("{\"data\":[[", body, StringComparison.Ordinal);
        Assert.EndsWith("}", body, StringComparison.Ordinal);
        Assert.Equal(secondRows, System.Text.RegularExpressions.Regex.Matches(body, "\"id\":").Count);
        Assert.Contains($"row-{secondRows - 1}-", body, StringComparison.Ordinal);

        // The ETag is computed over the payload, so a body padded by an oversized buffer would not match
        // a freshly hashed copy of the same bytes.
        Assert.Equal(ResponseETag.Compute(second.ToArray()), metadata.ETag);
        Assert.NotEqual(0, first.Length);
    }

    [Fact]
    public async Task Response_Is_Served_Without_Waiting_For_The_Cache_Store()
    {
        var executionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        executionGate.SetResult();
        var storeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new BlockingStoreCache(storeGate);
        using var engine = new WeirEngine(
            new ParameterBinder(), new SingleRegistry(), [new GatedConnector(executionGate)], cache, [], new FixedSettings());

        using var output = new MemoryStream();

        // The store never completes. If it sat on the response path this would hang, and the timeout
        // would fail the test rather than passing it on a technicality.
        var metadata = await engine.ExecuteAsync(NewInvocation(), output).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(0, output.Length);
        Assert.NotNull(metadata.ETag);
        Assert.False(metadata.CacheHit);

        // The store was still attempted, just behind the response.
        await cache.StoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        storeGate.SetResult();
    }
}
