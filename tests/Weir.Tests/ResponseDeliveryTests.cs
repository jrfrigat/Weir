using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.IO;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

// The delivery mode picks between writing rows out as they are read and building the whole envelope
// first. The difference is invisible in the bytes - both produce the same document - and shows only in
// when they arrive, so these tests watch the writes rather than the payload.
//
// It only decides anything for an endpoint that neither caches nor captures its result: both of those
// need the whole body before they can store or log it, so they buffer regardless. That is asserted too,
// because it is the part most likely to be "fixed" later by someone who reads the mode as absolute.
public class ResponseDeliveryTests
{
    /// <summary>Body limits refuse Full and cache-fill output before writing headers or bytes.</summary>
    /// <param name="cached">Whether this is a shared cache fill.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedBodyLimit_WritesNothing_AndReleases(bool cached)
    {
        WeirEngine? engine = null;
        await using var output = new WriteCountingStream();
        await Assert.ThrowsAsync<WeirBufferCapacityException>(() => ExecuteAsync(output,
            Delivery(ResponseDeliveryMode.Full), cached: cached,
            settings: new WeirSystemSettings { BufferedResponseMaxBytes = 100 }, configure: value => engine = value));
        Assert.Equal(0, output.Writes);
        Assert.Equal(0, engine!.ActiveBufferBytes);
    }

    /// <summary>Direct streaming does not acquire a complete response buffer.</summary>
    [Fact]
    public async Task Streaming_DoesNotUseBodyBudget()
    {
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, Delivery(ResponseDeliveryMode.Stream),
            settings: new WeirSystemSettings { ActiveResponseBufferMaxBytes = 1, BufferedResponseMaxBytes = 1 });
        Assert.True(output.Total > 100);
    }

    /// <summary>Cancellation cannot release memory while a transport still holds an awaited body write.</summary>
    /// <param name="cached">Whether the body is shared with a completed cache fill.</param>
    /// <returns>The completed lifetime check.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_RetainsBudgetUntilWriteUnwinds(bool cached)
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var output = new WriteCountingStream { WriteGate = gate.Task };
        WeirEngine? engine = null;
        var call = ExecuteAsync(output, Delivery(ResponseDeliveryMode.Full), cached: cached,
            configure: value => engine = value, cancellationToken: cancellation.Token);
        try
        {
            await output.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.True(engine!.ActiveBufferBytes > 0);
        }
        finally { gate.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(0, engine!.ActiveBufferBytes);
    }

    /// <summary>Multi-block Full bodies and bounded result capture never allocate a contiguous large pool buffer.</summary>
    /// <param name="capture">Whether result logging captures a prefix of the buffered body.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullAndCapture_DoNotConsolidatePooledBlocks(bool capture)
    {
        var largeBuffers = 0;
        var observer = new ResultObserver();
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, Delivery(ResponseDeliveryMode.Full),
            logging: new EndpointLogging { Enabled = capture, LogResult = capture },
            settings: new WeirSystemSettings { RequestLogEnabled = capture }, observer: observer,
            configure: engine =>
            {
                var manager = (RecyclableMemoryStreamManager)typeof(WeirEngine).GetField("_streams",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
                manager.LargeBufferCreated += (_, _) => Interlocked.Increment(ref largeBuffers);
            });
        Assert.True(output.Total > 128 * 1024);
        Assert.Equal(0, largeBuffers);
        using var json = JsonDocument.Parse(output.Bytes);
        Assert.Equal(3000, json.RootElement.GetProperty("data")[0].GetArrayLength());
        if (capture)
        {
            Assert.NotNull(observer.Captured);
            Assert.EndsWith("\n...[truncated]", observer.Captured, StringComparison.Ordinal);
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(observer.Captured), 1, 16 * 1024 + 15);
        }
        else Assert.Null(observer.Captured);
    }

    /// <summary>Observes the result text passed to the logging boundary.</summary>
    private sealed class ResultObserver : IWeirCallObserver
    {
        /// <summary>The captured result, if enabled.</summary>
        internal string? Captured { get; private set; }

        /// <inheritdoc />
        public ValueTask OnStartedAsync(WeirCallContext context) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask OnCompletedAsync(WeirCallContext context)
        {
            Captured = context.CapturedResult;
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask OnFailedAsync(WeirCallContext context, Exception exception) => ValueTask.CompletedTask;
    }

    /// <summary>A failure while preparing Full or a cache fill never emits a partial document.</summary>
    /// <param name="cached">Whether the failing request is a cache fill.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureBeforeFullCommit_WritesNothing(bool cached)
    {
        await using var output = new WriteCountingStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(output,
            Delivery(ResponseDeliveryMode.Full), cached: cached, failOnComplete: true));
        Assert.Equal(0, output.Writes);
    }

    [Fact]
    public async Task Stream_Writes_The_Body_Out_In_Chunks()
    {
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, Delivery(ResponseDeliveryMode.Stream));

        Assert.True(output.Writes > 1, $"expected chunked writes, got {output.Writes} of {output.Total} bytes");
    }

    /// <summary>Full, fresh cache fills and truncated fills all bound transport writes.</summary>
    /// <param name="cached">Whether this request fills the response cache.</param>
    /// <param name="maxRows">An optional row cap that exercises the uncached truncated handover.</param>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 2800)]
    public async Task Full_Hands_The_Completed_Body_Over_In_Bounded_Portions(bool cached, int maxRows)
    {
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, Delivery(ResponseDeliveryMode.Full),
            settings: new WeirSystemSettings { MaxRows = maxRows }, cached: cached);

        Assert.True(output.Writes > 1);
        Assert.InRange(output.MaximumWrite, 1, 256 * 1024);
        using var json = JsonDocument.Parse(output.Bytes);
        Assert.Equal(maxRows > 0, json.RootElement.GetProperty("truncated").GetBoolean());
    }

    /// <summary>A buffered response remains intact across network and pooled block boundaries.</summary>
    [Fact]
    public async Task A_Body_Past_One_Block_Survives_Bounded_Handover()
    {
        // Buffering completes before handover; chunk boundaries must not change the document.
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, Delivery(ResponseDeliveryMode.Full));

        Assert.True(output.Total > 128 * 1024, $"the body should span more than one block, was {output.Total} bytes");
        Assert.InRange(output.MaximumWrite, 1, 256 * 1024);

        using var document = JsonDocument.Parse(output.Bytes);
        var rows = document.RootElement.GetProperty("data")[0];
        Assert.Equal(3000, rows.GetArrayLength());
        Assert.Equal("row-2999-" + new string('x', 64), rows[2999].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Auto_Buffers_A_SingleRow_Endpoint_And_Streams_A_MultiRow_One()
    {
        // Auto reads the endpoint's declared shape: a small result buys atomic errors for nothing, and
        // a row-returning one is what streaming is for.
        await using var single = new WriteCountingStream();
        await ExecuteAsync(single, Delivery(ResponseDeliveryMode.Auto), ResultMode.SingleRow);
        Assert.InRange(single.MaximumWrite, 1, 256 * 1024);

        await using var many = new WriteCountingStream();
        await ExecuteAsync(many, Delivery(ResponseDeliveryMode.Auto), ResultMode.MultiRow);
        Assert.True(many.Writes > 1, $"expected a MultiRow endpoint to stream, got {many.Writes} write(s)");
    }

    [Fact]
    public async Task An_Endpoint_Without_A_Mode_Follows_The_System_Setting()
    {
        // Null is the default and means "whatever the system says", which is the whole point of having
        // the setting: change it once and every endpoint that has not opted out moves with it.
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, new DeliveryPolicy(), settings: new WeirSystemSettings { ResponseDeliveryMode = ResponseDeliveryMode.Full });

        Assert.InRange(output.MaximumWrite, 1, 256 * 1024);
    }

    [Fact]
    public async Task An_Endpoint_Mode_Beats_The_System_Setting()
    {
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, Delivery(ResponseDeliveryMode.Stream), settings: new WeirSystemSettings { ResponseDeliveryMode = ResponseDeliveryMode.Full });

        Assert.True(output.Writes > 1, $"the endpoint asked to stream but got {output.Writes} write(s)");
    }

    [Fact]
    public async Task Capturing_The_Result_Buffers_Even_When_The_Mode_Says_Stream()
    {
        // Not a conflict to resolve: there is nothing to write to the log until the body exists. The
        // mode is not overridden here so much as already satisfied.
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, Delivery(ResponseDeliveryMode.Stream),
            logging: new EndpointLogging { Enabled = true, LogResult = true });

        Assert.InRange(output.MaximumWrite, 1, 256 * 1024);
    }

    [Fact]
    public async Task A_Smaller_Flush_Threshold_Produces_More_Writes()
    {
        await using var coarse = new WriteCountingStream();
        await ExecuteAsync(coarse, new DeliveryPolicy { Mode = ResponseDeliveryMode.Stream, FlushBytes = 32 * 1024 });

        await using var fine = new WriteCountingStream();
        await ExecuteAsync(fine, new DeliveryPolicy { Mode = ResponseDeliveryMode.Stream, FlushBytes = 1024 });

        Assert.True(fine.Writes > coarse.Writes,
            $"a 1 KB threshold should chunk more finely than 32 KB, got {fine.Writes} vs {coarse.Writes} writes");
    }

    [Theory]
    [InlineData(ResponseDeliveryMode.Stream, ResultMode.MultiRow, false, false, true)]
    [InlineData(ResponseDeliveryMode.Auto, ResultMode.MultiRow, false, false, true)]
    [InlineData(ResponseDeliveryMode.Auto, ResultMode.SingleRow, false, false, false)]
    [InlineData(ResponseDeliveryMode.Full, ResultMode.MultiRow, false, false, false)]
    [InlineData(ResponseDeliveryMode.Stream, ResultMode.MultiRow, true, false, false)]
    [InlineData(ResponseDeliveryMode.Stream, ResultMode.MultiRow, false, true, false)]
    public void StreamsResponse_Agrees_With_What_The_Engine_Does(
        ResponseDeliveryMode mode, ResultMode resultMode, bool cached, bool capturesResult, bool expected)
    {
        // The host sends X-Accel-Buffering: no on exactly the responses this says will stream, so it must
        // follow the same rules the tests above pin on the engine: caching and result capture buffer
        // whatever the mode says, and Auto reads the declared result shape.
        var runtime = new FixedSettings(new WeirSystemSettings());
        using var cache = new MemoryResponseCache(runtime);
        using var engine = new WeirEngine(new ParameterBinder(), new SingleRegistry(), [new SizedConnector()], cache, [], runtime);

        var endpoint = new EndpointDefinition
        {
            Route = "orders/list",
            HttpMethod = "GET",
            ConnectionName = "default",
            ObjectName = "usp_list",
            ResultMode = resultMode,
            Delivery = Delivery(mode),
            Cache = cached ? new CachePolicy { Enabled = true, TtlSeconds = 60 } : new CachePolicy(),
            Logging = new EndpointLogging { Enabled = capturesResult, LogResult = capturesResult },
        };

        Assert.Equal(expected, engine.StreamsResponse(endpoint));
    }

    /// <summary>An endpoint delivery policy naming just the mode.</summary>
    /// <param name="mode">The mode to set.</param>
    /// <returns>The policy.</returns>
    private static DeliveryPolicy Delivery(ResponseDeliveryMode mode) => new() { Mode = mode };

    /// <summary>Runs one call through a real engine against a fake connector.</summary>
    /// <param name="output">Where the response is written.</param>
    /// <param name="delivery">The endpoint's delivery policy.</param>
    /// <param name="resultMode">The endpoint's declared result shape.</param>
    /// <param name="logging">The endpoint's logging policy.</param>
    /// <param name="settings">System settings to run under.</param>
    /// <param name="cached">Whether this request fills the response cache.</param>
    /// <param name="failOnComplete">Whether the database execution fails after its rows.</param>
    /// <param name="observer">An optional result observer.</param>
    /// <param name="configure">An optional observer of pool allocation events before execution.</param>
    /// <param name="cancellationToken">The client's lifetime.</param>
    private static async Task ExecuteAsync(
        Stream output,
        DeliveryPolicy delivery,
        ResultMode resultMode = ResultMode.MultiRow,
        EndpointLogging? logging = null,
        WeirSystemSettings? settings = null,
        bool cached = false,
        bool failOnComplete = false,
        IWeirCallObserver? observer = null,
        Action<WeirEngine>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var runtime = new FixedSettings(settings ?? new WeirSystemSettings());
        using var cache = new MemoryResponseCache(runtime);
        using var engine = new WeirEngine(
            new ParameterBinder(), new SingleRegistry(), [new SizedConnector { Rows = 3000, FailOnComplete = failOnComplete }], cache,
            observer is null ? [] : [observer], runtime);
        configure?.Invoke(engine);

        await engine.ExecuteAsync(
            new WeirInvocation
            {
                Endpoint = new EndpointDefinition
                {
                    Route = "orders/list",
                    HttpMethod = "GET",
                    ConnectionName = "default",
                    ObjectName = "usp_list",
                    ResultMode = resultMode,
                    Delivery = delivery,
                    Logging = logging ?? new EndpointLogging { Enabled = false },
                    Cache = new CachePolicy { Enabled = cached, TtlSeconds = 60 },
                },
                ApiKeyPrefix = "abc",
            },
            output, cancellationToken);
    }

    /// <summary>Counts writes and keeps the bytes, so a buffered body can be checked whole.</summary>
    private sealed class WriteCountingStream : Stream
    {
        /// <summary>An optional transport gate that deliberately outlives request cancellation.</summary>
        internal Task? WriteGate { get; init; }

        /// <summary>Signals when the transport has borrowed the body memory.</summary>
        internal TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Everything written so far.</summary>
        private readonly MemoryStream _received = new();

        /// <summary>How many separate writes reached the stream.</summary>
        public int Writes { get; private set; }

        /// <summary>The largest handover submitted to the destination.</summary>
        public int MaximumWrite { get; private set; }

        /// <summary>Total bytes written.</summary>
        public long Total { get; private set; }

        /// <summary>The bytes written, in order.</summary>
        public byte[] Bytes => _received.ToArray();

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override long Length => Total;

        /// <inheritdoc />
        public override long Position { get => Total; set => throw new NotSupportedException(); }

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => Note(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer) => Note(buffer);

        /// <inheritdoc />
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteEntered.TrySetResult();
            if (WriteGate is { } gate) await gate;
            Note(buffer.Span);
        }

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Note(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Records one write.</summary>
        /// <param name="bytes">The bytes of this write.</param>
        private void Note(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                return;
            }

            Writes++;
            MaximumWrite = Math.Max(MaximumWrite, bytes.Length);
            Total += bytes.Length;
            _received.Write(bytes);
        }
    }

    /// <summary>A result of a caller-chosen size, backed by DataTableReader so the engine sees a real DbDataReader.</summary>
    private sealed class FakeExecution : IDbExecution
    {
        private readonly DataTable _table;
        private readonly DbDataReader _reader;

        /// <summary>Whether completion must simulate a database failure.</summary>
        private readonly bool _failOnComplete;

        /// <summary>Builds a result with the given row count.</summary>
        /// <param name="rows">How many rows to return.</param>
        /// <param name="failOnComplete">Whether completion throws after the rows were consumed.</param>
        public FakeExecution(int rows, bool failOnComplete)
        {
            _failOnComplete = failOnComplete;
            _table = new DataTable();
            _table.Columns.Add("id", typeof(int));
            _table.Columns.Add("text", typeof(string));
            for (var i = 0; i < rows; i++)
            {
                _table.Rows.Add(i, $"row-{i}-{new string('x', 64)}");
            }

            _reader = _table.CreateDataReader();
        }

        /// <inheritdoc />
        public DbDataReader Reader => _reader;

        /// <inheritdoc />
        public IReadOnlyList<SqlMessage> Messages => [];

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Outputs => new Dictionary<string, object?>();

        /// <inheritdoc />
        public int? ReturnValue => null;

        /// <inheritdoc />
        public int RecordsAffected => 0;

        /// <inheritdoc />
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (_failOnComplete) throw new InvalidOperationException("Execution failed after its rows.");
            _reader.Close();
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _table.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A connector returning a fixed number of rows.</summary>
    private sealed class SizedConnector : IDbConnector
    {
        /// <summary>Rows each execution returns.</summary>
        public int Rows { get; set; } = 1;

        /// <summary>Whether the execution must fail when completing.</summary>
        public bool FailOnComplete { get; set; }

        /// <inheritdoc />
        public string ProviderName => "test";

        /// <inheritdoc />
        public Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDbExecution>(new FakeExecution(Rows, FailOnComplete));

        /// <inheritdoc />
        public Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DbObjectDescriptor>>([]);

        /// <inheritdoc />
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

        /// <inheritdoc />
        public IReadOnlyCollection<DataConnectionDescriptor> All => [Descriptor];

        /// <inheritdoc />
        public bool TryGet(string name, out DataConnectionDescriptor descriptor)
        {
            descriptor = Descriptor;
            return true;
        }

        /// <inheritdoc />
        public DataConnectionDescriptor Resolve(string name) => Descriptor;
    }

    /// <summary>Runtime settings fixed to a given snapshot.</summary>
    /// <param name="settings">The settings to serve.</param>
    private sealed class FixedSettings(WeirSystemSettings settings) : IRuntimeSettings
    {
        /// <inheritdoc />
        public WeirSystemSettings Current { get; } = settings;

        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task UpdateAsync(WeirSystemSettings updated, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
