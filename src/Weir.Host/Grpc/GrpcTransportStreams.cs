using System.Buffers;
using Google.Protobuf;
using Grpc.Core;
using Weir.Core;
using Weir.Host.Http;

namespace Weir.Host.Grpc;

/// <summary>
/// Collects a unary response under the same active allocation budget as the engine.
/// Contiguous growth reserves both old and new storage until the copy finishes.
/// </summary>
public sealed class BufferedTransportStream : TransportWriteStream
{
    /// <summary>The shared admission service; null only for legacy standalone callers.</summary>
    private readonly ResponseBufferBudget? _budget;

    /// <summary>The configured per-body bound.</summary>
    private readonly long _maximum;

    /// <summary>The current contiguous storage required by unary protobuf.</summary>
    private byte[] _buffer = [];

    /// <summary>The reservation for the current allocation.</summary>
    private IDisposable? _lease;

    /// <summary>Creates a unary buffer; the in-box service always supplies shared admission.</summary>
    /// <param name="budget">The shared active allocation budget.</param>
    /// <param name="maximum">Maximum logical body size; zero disables the bound.</param>
    public BufferedTransportStream(ResponseBufferBudget? budget = null, long maximum = 0)
    {
        _budget = budget;
        _maximum = maximum;
    }

    /// <summary>Everything written so far.</summary>
    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, checked((int)BytesWritten));

    /// <inheritdoc />
    public override ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var needed = checked(BytesWritten + buffer.Length);
        if ((_maximum > 0 && needed > _maximum) || needed > Array.MaxLength) throw new WeirBufferCapacityException();
        if (needed > _buffer.Length)
        {
            var capacity = (int)Math.Min(Array.MaxLength, Math.Max(needed, Math.Max(16384L, _buffer.LongLength * 2)));
            var lease = _budget?.Reserve(capacity);
            try
            {
                var replacement = GC.AllocateUninitializedArray<byte>(capacity);
                Written.Span.CopyTo(replacement);
                _buffer = replacement;
                _lease?.Dispose();
                _lease = lease;
            }
            catch { lease?.Dispose(); throw; }
        }
        buffer.Span.CopyTo(_buffer.AsSpan((int)BytesWritten));
        BytesWritten += buffer.Length;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
        // Nothing to flush: the buffer is the destination.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer = [];
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Streams a response envelope out as gRPC messages, one per chunk, in the order the rows were read.
/// Concatenating the chunks gives exactly the bytes the unary call would have returned - the split is a
/// property of the wire, not of the answer.
/// </summary>
public sealed class GrpcChunkStream : TransportWriteStream
{
    /// <summary>
    /// Chunk size. Large enough that a big result set is not turned into thousands of messages, small
    /// enough that the first rows leave while the reader is still working, and comfortably inside gRPC's
    /// default 4 MB receive limit so a caller never has to raise it.
    /// </summary>
    private const int ChunkBytes = 32 * 1024;

    private readonly IServerStreamWriter<InvokeChunk> _writer;
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
    private int _length;

    /// <summary>Creates the stream over a server-streaming writer.</summary>
    /// <param name="writer">The gRPC response stream.</param>
    public GrpcChunkStream(IServerStreamWriter<InvokeChunk> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <inheritdoc />
    public override async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (_length > 0)
        {
            await SendAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BytesWritten += buffer.Length;
        while (buffer.Length > 0)
        {
            var room = _buffer.Length - _length;
            if (room == 0)
            {
                await SendAsync(cancellationToken);
                continue;
            }

            var take = Math.Min(room, buffer.Length);
            buffer.Span[..take].CopyTo(_buffer.AsSpan(_length));
            _length += take;
            buffer = buffer[take..];
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
        // Nothing to do: a chunk is sent when the buffer fills or the envelope ends. Sending one per
        // Flush would put a message on the wire for every row the writer happens to flush after.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Sends what is buffered as one chunk message.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the message is written.</returns>
    private async ValueTask SendAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Copied rather than wrapped: the buffer is pooled and refilled the moment this returns, and
        // whether the message has finished with it by then is an implementation detail of the writer,
        // not a promise. A 32 KB copy per chunk is well under the large-object threshold and is not
        // where a streamed result set spends its time.
        var chunk = new InvokeChunk { Data = ByteString.CopyFrom(_buffer, 0, _length) };
        await _writer.WriteAsync(chunk, cancellationToken);
        _length = 0;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        base.Dispose(disposing);
    }
}
