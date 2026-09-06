namespace Weir.Host.Http;

/// <summary>
/// A write-only sink the engine can stream an envelope into on a transport that is not HTTP. The engine
/// writes bytes and knows nothing else, so a transport only has to say where those bytes go and when the
/// message ends.
/// <para>
/// It carries one extra thing an ordinary <see cref="Stream"/> cannot: <see cref="BytesWritten"/>. Once
/// the first byte is out, an error can no longer be turned into a clean failure response - the same
/// trade the HTTP path makes with <c>Response.HasStarted</c> - and the caller needs to know which side
/// of that line it is on.
/// </para>
/// </summary>
public abstract class TransportWriteStream : Stream
{
    /// <summary>How many bytes of the response body have been handed to the transport so far.</summary>
    public long BytesWritten { get; protected set; }

    /// <summary>Whether any part of the body has reached the caller, so a failure can no longer be reported cleanly.</summary>
    public bool HasStarted => BytesWritten > 0;

    /// <inheritdoc />
    public sealed override bool CanRead => false;

    /// <inheritdoc />
    public sealed override bool CanSeek => false;

    /// <inheritdoc />
    public sealed override bool CanWrite => true;

    /// <inheritdoc />
    public sealed override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public sealed override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    /// <summary>Finishes the message: flushes what is buffered and marks the response complete.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the transport has sent the last frame.</returns>
    public abstract ValueTask CompleteAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public sealed override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public sealed override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public sealed override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Synchronous writes are refused rather than bridged. Every writer on this path is async, and a
    /// blocking bridge here would be a deadlock waiting for the one caller that is not.
    /// </summary>
    /// <param name="buffer">Ignored.</param>
    /// <param name="offset">Ignored.</param>
    /// <param name="count">Ignored.</param>
    public sealed override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("This transport is asynchronous; use WriteAsync.");
}
