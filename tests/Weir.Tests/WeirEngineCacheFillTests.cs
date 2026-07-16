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

        /// <summary>Completes if an execution is cancelled while waiting on the gate.</summary>
        public TaskCompletionSource ExecutionCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderName => "test";

        public async Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executions);
            _started.TrySetResult();
            try
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ExecutionCancelled.TrySetResult();
                throw;
            }

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

    private static WeirInvocation NewInvocation(bool coalesce = true) => new()
    {
        Endpoint = new EndpointDefinition
        {
            Route = "orders/list",
            HttpMethod = "GET",
            ConnectionName = "default",
            ObjectName = "usp_list",
            Cache = new CachePolicy { Enabled = true, TtlSeconds = 60, CoalesceRequests = coalesce },
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

    [Fact]
    public async Task Coalescing_Can_Be_Turned_Off_Per_Endpoint()
    {
        // The same burst as Concurrent_Callers_For_One_Cache_Key_Execute_The_Object_Once, differing only in
        // the endpoint's CoalesceRequests flag - so the two together pin what the flag actually decides.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new GatedConnector(gate);
        using var cache = new MemoryResponseCache(new FixedSettings());
        using var engine = new WeirEngine(
            new ParameterBinder(), new SingleRegistry(), [connector], cache, [], new FixedSettings());

        var invocation = NewInvocation(coalesce: false);
        const int callers = 20;
        var outputs = new MemoryStream[callers];
        var calls = new Task<WeirResponseMetadata>[callers];
        for (var i = 0; i < callers; i++)
        {
            outputs[i] = new MemoryStream();
            calls[i] = engine.ExecuteAsync(invocation, outputs[i]);
        }

        // Nobody waits on anybody: with the query held open, every caller is inside its own execution
        // rather than queued behind the first one. This is the stampede - opted into deliberately.
        await WaitUntil(() => connector.Executions == callers);

        gate.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(callers, connector.Executions);
        Assert.All(results, r => Assert.NotNull(r.ETag));
        Assert.All(results, r => Assert.False(r.CacheHit));
        foreach (var output in outputs)
        {
            Assert.NotEqual(0, output.Length);
            output.Dispose();
        }
    }

    [Fact]
    public async Task Leader_Disconnecting_Does_Not_Cancel_The_Work_Other_Callers_Are_Waiting_On()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new GatedConnector(gate);
        using var cache = new MemoryResponseCache(new FixedSettings());
        using var engine = new WeirEngine(
            new ParameterBinder(), new SingleRegistry(), [connector], cache, [], new FixedSettings());

        // The first caller claims the fill and starts the query.
        using var leaderAborted = new CancellationTokenSource();
        using var leaderOutput = new MemoryStream();
        var leader = engine.ExecuteAsync(NewInvocation(), leaderOutput, default, leaderAborted.Token);
        await connector.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // A second caller queues up behind it.
        using var waiterOutput = new MemoryStream();
        var waiter = engine.ExecuteAsync(NewInvocation(), waiterOutput);
        await WaitUntil(() => connector.Executions == 1 && !waiter.IsCompleted);

        // The first caller's client hangs up while the query is still running. That is its request ending,
        // not the query's: the second caller is still waiting for this answer.
        leaderAborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader).WaitAsync(TimeSpan.FromSeconds(10));

        gate.SetResult();
        var metadata = await waiter.WaitAsync(TimeSpan.FromSeconds(10));

        // The query the leader started ran to completion and served the caller behind it.
        Assert.Equal(1, connector.Executions);
        Assert.NotEqual(0, waiterOutput.Length);
        Assert.True(metadata.CacheHit);
        Assert.NotNull(metadata.ETag);
    }

    [Fact]
    public async Task Work_Is_Cancelled_Once_The_Last_Caller_Has_Gone()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new GatedConnector(gate);
        using var cache = new MemoryResponseCache(new FixedSettings());
        using var engine = new WeirEngine(
            new ParameterBinder(), new SingleRegistry(), [connector], cache, [], new FixedSettings());

        // Two callers on one fill: one owns it, one waits.
        using var leaderAborted = new CancellationTokenSource();
        using var waiterAborted = new CancellationTokenSource();
        using var leaderOutput = new MemoryStream();
        using var waiterOutput = new MemoryStream();
        var leader = engine.ExecuteAsync(NewInvocation(), leaderOutput, default, leaderAborted.Token);
        await connector.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var waiter = engine.ExecuteAsync(NewInvocation(), waiterOutput, default, waiterAborted.Token);
        await WaitUntil(() => connector.Executions == 1 && !waiter.IsCompleted);

        // Both clients hang up. With nobody left to serve, keeping the query alive would be pure waste, so
        // the fill drops it - the connector's execution sees the cancellation and never completes.
        leaderAborted.Cancel();
        waiterAborted.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter).WaitAsync(TimeSpan.FromSeconds(10));

        // The gate is never opened: if the execution had not been cancelled it would still be sitting on it.
        Assert.True(connector.ExecutionCancelled.Task.IsCompleted ||
            await System.Threading.Tasks.Task.WhenAny(connector.ExecutionCancelled.Task, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10))) == connector.ExecutionCancelled.Task);
        Assert.Equal(1, connector.Executions);
    }

    /// <summary>Spins until a condition holds, so a test never depends on a fixed sleep.</summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not hold within the timeout.");
            }

            await System.Threading.Tasks.Task.Delay(10);
        }
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
        Assert.Equal(secondRows, System.Text.RegularExpressions.Regex.Count(body, "\"id\":"));
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
