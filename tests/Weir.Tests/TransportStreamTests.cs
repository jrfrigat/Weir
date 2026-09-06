using System.Text;
using Grpc.Core;
using Weir.Host.Grpc;
using Xunit;

namespace Weir.Tests;

// The two streams that carry an envelope onto a transport. Both cut a body into pieces, and both have
// to hand back exactly what went in: a result set that comes out one byte different, or one character
// mangled at a chunk boundary, is a defect no route-level test would catch.
public class TransportStreamTests
{
    [Fact]
    public async Task The_Grpc_Chunk_Stream_Reassembles_To_What_Was_Written()
    {
        var writer = new CollectingStreamWriter();
        await using var stream = new GrpcChunkStream(writer);
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"row\":\"value\"},", 8000)));

        // Written in odd-sized pieces, the way a row writer flushes: the chunk boundaries must not
        // depend on how the caller happened to divide its writes.
        for (var offset = 0; offset < payload.Length; offset += 777)
        {
            await stream.WriteAsync(payload.AsMemory(offset, Math.Min(777, payload.Length - offset)));
        }

        await stream.CompleteAsync(CancellationToken.None);

        Assert.True(writer.Chunks.Count > 1, "a payload larger than one chunk should be sent in several");
        Assert.Equal(payload, writer.Written);
        Assert.Equal(payload.Length, stream.BytesWritten);
    }

    [Fact]
    public async Task The_Grpc_Chunk_Stream_Sends_Nothing_For_An_Empty_Body()
    {
        var writer = new CollectingStreamWriter();
        await using var stream = new GrpcChunkStream(writer);

        await stream.CompleteAsync(CancellationToken.None);

        Assert.Empty(writer.Chunks);
        Assert.False(stream.HasStarted);
    }

    [Fact]
    public async Task The_Buffered_Stream_Keeps_Everything_For_The_Unary_Call()
    {
        await using var stream = new BufferedTransportStream();
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcdefghij", 5000)));

        await stream.WriteAsync(payload.AsMemory(0, 12345));
        await stream.WriteAsync(payload.AsMemory(12345));

        Assert.Equal(payload, stream.Written.ToArray());
        Assert.True(stream.HasStarted);
    }

    [Fact]
    public async Task The_WebSocket_Stream_Cuts_Fragments_On_Character_Boundaries()
    {
        var socket = new CollectingWebSocket();
        await using var stream = new Weir.Host.Http.WebSocketWriteStream(socket);

        // Cyrillic, so almost every character is two bytes and a naive cut at a fixed offset would land
        // inside one. The body is far larger than a fragment, so there are several cuts to get wrong.
        var body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"имя\":\"значение\"},", 4000)));
        await stream.BeginAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"body\":"), CancellationToken.None);
        await stream.WriteAsync(body.AsMemory());
        await stream.EndAsync(Encoding.UTF8.GetBytes(",\"status\":200}"), CancellationToken.None);

        Assert.True(socket.Fragments.Count > 1, "a body larger than one fragment should be sent in several");
        Assert.True(socket.Fragments[^1].EndOfMessage, "the last fragment must close the message");

        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        foreach (var fragment in socket.Fragments)
        {
            // Independently decodable: a reader that decodes each fragment as it arrives - which a
            // browser does - must not see a replacement character where two fragments meet.
            strict.GetString(fragment.Payload);
        }

        var expected = "{\"id\":1,\"body\":" + Encoding.UTF8.GetString(body) + ",\"status\":200}";
        Assert.Equal(expected, strict.GetString(socket.Written));
        Assert.Equal(body.Length, stream.BytesWritten);
    }

    /// <summary>A <see cref="WebSocket"/> that records the fragments it is asked to send.</summary>
    private sealed class CollectingWebSocket : System.Net.WebSockets.WebSocket
    {
        /// <summary>The fragments sent, in order.</summary>
        public List<(byte[] Payload, bool EndOfMessage)> Fragments { get; } = [];

        /// <summary>Every fragment concatenated - the message the caller reassembles.</summary>
        public byte[] Written => Fragments.SelectMany(f => f.Payload).ToArray();

        /// <inheritdoc />
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;

        /// <inheritdoc />
        public override string? CloseStatusDescription => null;

        /// <inheritdoc />
        public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;

        /// <inheritdoc />
        public override string? SubProtocol => null;

        /// <inheritdoc />
        public override void Abort()
        {
        }

        /// <inheritdoc />
        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        /// <inheritdoc />
        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        /// <inheritdoc />
        public override void Dispose()
        {
        }

        /// <inheritdoc />
        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Fragments.Add((buffer.ToArray(), endOfMessage));
            return Task.CompletedTask;
        }
    }

    /// <summary>An <see cref="IServerStreamWriter{T}"/> that keeps every chunk it is handed.</summary>
    private sealed class CollectingStreamWriter : IServerStreamWriter<InvokeChunk>
    {
        /// <summary>The chunks written, in order.</summary>
        public List<byte[]> Chunks { get; } = [];

        /// <summary>Every chunk concatenated - what the caller would reassemble.</summary>
        public byte[] Written => Chunks.SelectMany(c => c).ToArray();

        /// <inheritdoc />
        public WriteOptions? WriteOptions { get; set; }

        /// <inheritdoc />
        public Task WriteAsync(InvokeChunk message)
        {
            Chunks.Add(message.Data.ToByteArray());
            return Task.CompletedTask;
        }
    }
}
