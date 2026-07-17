using System.Data;
using System.Data.Common;
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
    [Fact]
    public async Task Stream_Writes_The_Body_Out_In_Chunks()
    {
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, Delivery(ResponseDeliveryMode.Stream));

        Assert.True(output.Writes > 1, $"expected chunked writes, got {output.Writes} of {output.Total} bytes");
    }

    [Fact]
    public async Task Full_Hands_The_Body_Over_In_One_Piece()
    {
        await using var output = new WriteCountingStream();
        await ExecuteAsync(output, Delivery(ResponseDeliveryMode.Full));

        // Buffered first, then copied: the client sees the whole response or a clean error, never half.
        Assert.Equal(1, output.Writes);
    }

    [Fact]
    public async Task Auto_Buffers_A_SingleRow_Endpoint_And_Streams_A_MultiRow_One()
    {
        // Auto reads the endpoint's declared shape: a small result buys atomic errors for nothing, and
        // a row-returning one is what streaming is for.
        await using var single = new WriteCountingStream();
        await ExecuteAsync(single, Delivery(ResponseDeliveryMode.Auto), ResultMode.SingleRow);
        Assert.Equal(1, single.Writes);

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

        Assert.Equal(1, output.Writes);
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

        Assert.Equal(1, output.Writes);
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

    /// <summary>An endpoint delivery policy naming just the mode.</summary>
    /// <param name="mode">The mode to set.</param>
    /// <returns>The policy.</returns>
    private static DeliveryPolicy Delivery(ResponseDeliveryMode mode) => new() { Mode = mode };

    /// <summary>Runs one uncached call through a real engine against a fake connector.</summary>
    /// <param name="output">Where the response is written.</param>
    /// <param name="delivery">The endpoint's delivery policy.</param>
    /// <param name="resultMode">The endpoint's declared result shape.</param>
    /// <param name="logging">The endpoint's logging policy.</param>
    /// <param name="settings">System settings to run under.</param>
    private static async Task ExecuteAsync(
        Stream output,
        DeliveryPolicy delivery,
        ResultMode resultMode = ResultMode.MultiRow,
        EndpointLogging? logging = null,
        WeirSystemSettings? settings = null)
    {
        var runtime = new FixedSettings(settings ?? new WeirSystemSettings());
        using var cache = new MemoryResponseCache(runtime);
        using var engine = new WeirEngine(
            new ParameterBinder(), new SingleRegistry(), [new SizedConnector { Rows = 3000 }], cache, [], runtime);

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
                },
                ApiKeyPrefix = "abc",
            },
            output);
    }

    /// <summary>Counts writes without touching the bytes; a buffered response arrives as exactly one.</summary>
    private sealed class WriteCountingStream : Stream
    {
        /// <summary>How many separate writes reached the stream.</summary>
        public int Writes { get; private set; }

        /// <summary>Total bytes written.</summary>
        public long Total { get; private set; }

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
        public override void Write(byte[] buffer, int offset, int count) => Note(count);

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer) => Note(buffer.Length);

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Note(buffer.Length);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Note(count);
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
        /// <param name="count">Bytes in this write.</param>
        private void Note(int count)
        {
            if (count == 0)
            {
                return;
            }

            Writes++;
            Total += count;
        }
    }

    /// <summary>A result of a caller-chosen size, backed by DataTableReader so the engine sees a real DbDataReader.</summary>
    private sealed class FakeExecution : IDbExecution
    {
        private readonly DataTable _table;
        private readonly DbDataReader _reader;

        /// <summary>Builds a result with the given row count.</summary>
        /// <param name="rows">How many rows to return.</param>
        public FakeExecution(int rows)
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

        /// <inheritdoc />
        public string ProviderName => "test";

        /// <inheritdoc />
        public Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDbExecution>(new FakeExecution(Rows));

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
