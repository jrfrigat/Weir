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

    /// <summary>A one-row result, backed by DataTableReader so the engine sees a real DbDataReader.</summary>
    private sealed class FakeExecution : IDbExecution
    {
        private readonly DataTable _table;
        private readonly DbDataReader _reader;

        public FakeExecution()
        {
            _table = new DataTable();
            _table.Columns.Add("id", typeof(int));
            _table.Rows.Add(1);
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
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var engine = new WeirEngine(
            new ParameterBinder(), new SingleRegistry(), [connector], new MemoryResponseCache(memory), [], new FixedSettings());

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
