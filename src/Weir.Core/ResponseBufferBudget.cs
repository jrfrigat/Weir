using System.Buffers;
using Microsoft.IO;

namespace Weir.Core;

/// <summary>Reports that buffering a response would exceed an operator-configured memory bound.</summary>
public sealed class WeirBufferCapacityException : WeirException
{
    /// <summary>Creates a capacity refusal without exposing endpoint or parameter details.</summary>
    public WeirBufferCapacityException() : base("The response buffer budget is exhausted. Retry later or use a streaming endpoint.") { }
}

/// <summary>Accounts active pooled blocks and owned copies before allocation.</summary>
/// <param name="settings">The live configuration checked on each reservation.</param>
public sealed class ResponseBufferBudget(IRuntimeSettings settings)
{
    /// <summary>Serializes reservation changes.</summary>
    private readonly Lock _gate = new();

    /// <summary>Currently reserved bytes, including shared copies only once.</summary>
    private long _used;

    /// <summary>Gets the active reservation total.</summary>
    public long Used { get { lock (_gate) return _used; } }

    /// <summary>Reserves bytes before an allocation.</summary>
    /// <param name="bytes">The allocation size.</param>
    /// <returns>An ownership token released after its last consumer.</returns>
    public Lease Reserve(long bytes)
    {
        Add(bytes);
        return new Lease(this, bytes);
    }

    /// <summary>Checks and records an increase against the current global bound.</summary>
    /// <param name="bytes">Additional bytes.</param>
    private void Add(long bytes)
    {
        lock (_gate)
        {
            var limit = settings.Current.ActiveResponseBufferMaxBytes;
            if (bytes < 0 || (limit > 0 && bytes > limit - _used)) throw new WeirBufferCapacityException();
            _used = checked(_used + bytes);
        }
    }

    /// <summary>Releases an allocation after all asynchronous users have finished.</summary>
    /// <param name="bytes">Previously reserved bytes.</param>
    private void Release(long bytes) { lock (_gate) _used -= bytes; }

    /// <summary>Owns a reservation for one mutable buffer or immutable body.</summary>
    public sealed class Lease : IDisposable
    {
        /// <summary>The accounting service that admitted this allocation.</summary>
        private readonly ResponseBufferBudget _owner;

        /// <summary>The current reservation; only its buffer writer grows it.</summary>
        private long _bytes;

        /// <summary>Creates a token only after the accounting service admits its bytes.</summary>
        /// <param name="owner">The accounting service.</param>
        /// <param name="bytes">Initially reserved bytes.</param>
        internal Lease(ResponseBufferBudget owner, long bytes) { _owner = owner; _bytes = bytes; }

        /// <summary>Increases a reservation before the backing stream grows.</summary>
        /// <param name="bytes">The required capacity.</param>
        internal void GrowTo(long bytes)
        {
            if (bytes <= _bytes) return;
            _owner.Add(bytes - _bytes);
            _bytes = bytes;
        }

        /// <inheritdoc />
        public void Dispose() => _owner.Release(Interlocked.Exchange(ref _bytes, 0));
    }
}

/// <summary>A write-only segmented response stream that reserves each pooled block before growth.</summary>
internal sealed class BudgetedResponseBuffer : Stream
{
    /// <summary>The pool block size, shared with the engine's manager.</summary>
    internal const int BlockBytes = 128 * 1024;

    /// <summary>The underlying pooled storage.</summary>
    private readonly RecyclableMemoryStream _stream;

    /// <summary>The active capacity reservation.</summary>
    private readonly ResponseBufferBudget.Lease _lease;

    /// <summary>The per-body hard bound captured when buffering began.</summary>
    private readonly long _maximum;

    /// <summary>Whether the pooled storage has been returned.</summary>
    private bool _disposed;

    /// <summary>Reserves the first block before renting any storage.</summary>
    /// <param name="manager">The bounded free-buffer pool.</param>
    /// <param name="budget">The active allocation budget.</param>
    /// <param name="maximum">Maximum logical body size; zero disables this bound.</param>
    internal BudgetedResponseBuffer(RecyclableMemoryStreamManager manager, ResponseBufferBudget budget, long maximum)
    {
        _maximum = maximum;
        _lease = budget.Reserve(BlockBytes);
        try { _stream = manager.GetStream("weir-response"); }
        catch { _lease.Dispose(); throw; }
    }

    /// <inheritdoc />
    public override bool CanRead => false;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => !_disposed;
    /// <inheritdoc />
    public override long Length => _stream.Length;
    /// <inheritdoc />
    public override long Position { get => Length; set => throw new NotSupportedException(); }

    /// <summary>Returns segments whose lifetime is the lifetime of this buffer.</summary>
    /// <returns>The complete body without consolidating blocks.</returns>
    internal ReadOnlySequence<byte> GetReadOnlySequence() => _stream.GetReadOnlySequence();

    /// <summary>Copies the body after its caller has reserved the copy separately.</summary>
    /// <returns>The independently owned exact-length payload.</returns>
    internal byte[] ToArray() => _stream.ToArray();

    /// <summary>Checks the body limit and reserves rounded backing capacity.</summary>
    /// <param name="count">Bytes about to be written.</param>
    private void BeforeWrite(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var length = checked(Length + count);
        if (_maximum > 0 && length > _maximum) throw new WeirBufferCapacityException();
        _lease.GrowTo(checked((length + BlockBytes - 1) / BlockBytes * BlockBytes));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) { BeforeWrite(buffer.Length); _stream.Write(buffer); }
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _stream.Dispose(); }
            finally { _lease.Dispose(); }
        }
        base.Dispose(disposing);
    }
}
