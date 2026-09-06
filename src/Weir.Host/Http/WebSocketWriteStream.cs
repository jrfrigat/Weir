using System.Buffers;
using System.Net.WebSockets;

namespace Weir.Host.Http;

/// <summary>
/// Streams one response envelope onto a WebSocket as a single text message, sent in fragments as the
/// rows arrive. The reader sees one message per call, whatever it cost to produce - a caller does not
/// have to reassemble anything or learn a chunk protocol.
/// <para>
/// The message is opened with a caller-supplied prefix (the correlation id and status, which have to
/// travel inside the payload because a WebSocket frame has no headers) and closed with its matching
/// suffix, so the whole thing is one JSON document written without ever holding it in memory.
/// </para>
/// </summary>
public sealed class WebSocketWriteStream : TransportWriteStream
{
    /// <summary>
    /// Fragment size. Large enough that a big result set is not chopped into thousands of frames, small
    /// enough that the first rows reach the caller while the reader is still going.
    /// </summary>
    private const int FragmentBytes = 16 * 1024;

    private readonly WebSocket _socket;
    private readonly byte[] _buffer;
    private int _length;

    /// <summary>Creates the stream over an accepted socket.</summary>
    /// <param name="socket">The socket to write to. Not owned: the session closes it.</param>
    public WebSocketWriteStream(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _buffer = ArrayPool<byte>.Shared.Rent(FragmentBytes);
    }

    /// <summary>Begins the message with a raw prefix, before any engine output.</summary>
    /// <param name="prefix">UTF-8 bytes to open the JSON document with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the prefix is buffered.</returns>
    public ValueTask BeginAsync(ReadOnlyMemory<byte> prefix, CancellationToken cancellationToken) =>
        WriteRawAsync(prefix, cancellationToken);

    /// <summary>Ends the message with a raw suffix and sends the final fragment.</summary>
    /// <param name="suffix">UTF-8 bytes to close the JSON document with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the last frame is on the wire.</returns>
    public async ValueTask EndAsync(ReadOnlyMemory<byte> suffix, CancellationToken cancellationToken)
    {
        await WriteRawAsync(suffix, cancellationToken);
        await SendAsync(_buffer.AsMemory(0, _length), endOfMessage: true, cancellationToken);
        _length = 0;
    }

    /// <summary>
    /// Does nothing, deliberately. The envelope is finished but the message is not: the suffix still has
    /// to close the object the prefix opened, and only the session knows what belongs there - so
    /// <see cref="EndAsync"/> is what ends the message.
    /// </summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public override ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BytesWritten += buffer.Length;
        await WriteRawAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
        // Nothing to do: a fragment is sent when the buffer fills or the message ends, and forcing one
        // per Flush would put a frame on the wire for every row the writer happens to flush after.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Buffers bytes, sending a fragment whenever the buffer is full.</summary>
    /// <param name="buffer">The bytes to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the bytes are buffered or sent.</returns>
    private async ValueTask WriteRawAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        while (buffer.Length > 0)
        {
            var room = _buffer.Length - _length;
            if (room == 0)
            {
                await FlushFragmentAsync(cancellationToken);
                continue;
            }

            var take = Math.Min(room, buffer.Length);
            buffer.Span[..take].CopyTo(_buffer.AsSpan(_length));
            _length += take;
            buffer = buffer[take..];
        }
    }

    /// <summary>
    /// Sends everything buffered as a non-final fragment, holding back an incomplete UTF-8 sequence.
    /// <para>
    /// The hold-back is the point: a text message is one UTF-8 document, and while the protocol permits
    /// a code point to straddle two fragments, plenty of readers - browsers included - decode each
    /// fragment as it arrives and turn a split sequence into a replacement character. Cutting on a
    /// character boundary costs at most three bytes of carry-over and makes every fragment independently
    /// decodable.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the fragment is sent.</returns>
    private async ValueTask FlushFragmentAsync(CancellationToken cancellationToken)
    {
        var send = SafeCut(_buffer.AsSpan(0, _length));
        if (send == 0)
        {
            // The whole buffer is one unfinished sequence, which cannot happen with a 16 KB buffer and a
            // 4-byte maximum, but growing the buffer is still the only correct answer if it ever does.
            send = _length;
        }

        await SendAsync(_buffer.AsMemory(0, send), endOfMessage: false, cancellationToken);

        var carry = _length - send;
        if (carry > 0)
        {
            _buffer.AsSpan(send, carry).CopyTo(_buffer);
        }

        _length = carry;
    }

    /// <summary>Sends one fragment.</summary>
    /// <param name="payload">The bytes to send.</param>
    /// <param name="endOfMessage">Whether this fragment closes the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the fragment is on the wire.</returns>
    private ValueTask SendAsync(ReadOnlyMemory<byte> payload, bool endOfMessage, CancellationToken cancellationToken) =>
        _socket.State == WebSocketState.Open
            ? _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage, cancellationToken)
            : ValueTask.CompletedTask;

    /// <summary>
    /// Finds the length of the longest prefix that ends on a UTF-8 character boundary.
    /// </summary>
    /// <param name="span">The buffered bytes.</param>
    /// <returns>How many bytes may be sent now.</returns>
    private static int SafeCut(ReadOnlySpan<byte> span)
    {
        // At most the last three bytes can belong to an unfinished sequence, so this walks back three
        // positions at the very most.
        for (var i = span.Length - 1; i >= 0 && i >= span.Length - 3; i--)
        {
            var b = span[i];
            if ((b & 0b1000_0000) == 0)
            {
                // A one-byte character: everything up to and including it is complete.
                return i + 1;
            }

            if ((b & 0b1100_0000) == 0b1000_0000)
            {
                // A continuation byte: keep walking back to find the lead byte it belongs to.
                continue;
            }

            // A lead byte: the sequence it starts is complete only if all of it is here.
            var needed = (b & 0b1111_0000) == 0b1111_0000 ? 4 : (b & 0b1110_0000) == 0b1110_0000 ? 3 : 2;
            return span.Length - i >= needed ? span.Length : i;
        }

        return span.Length;
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
