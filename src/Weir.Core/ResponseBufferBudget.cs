using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.IO;
using Weir.Abstractions;

namespace Weir.Core;

/// <summary>Reports that buffering a response would exceed an operator-configured memory bound.</summary>
public sealed class WeirBufferCapacityException : WeirException
{
    /// <summary>Creates a capacity refusal without exposing endpoint or parameter details.</summary>
    public WeirBufferCapacityException() : base("The response buffer budget is exhausted. Retry later or use a streaming endpoint.") { }
}

/// <summary>Accounts active pooled blocks and owned copies before allocation.</summary>
/// <param name="settings">The live configuration checked on each reservation.</param>
public sealed class ResponseBufferBudget(IRuntimeSettings settings) : IResponseBufferBudget
{
    /// <summary>Shares admission for legacy constructors composed over the same runtime service.</summary>
    private static readonly ConditionalWeakTable<IRuntimeSettings, ResponseBufferBudget> SharedBudgets = new();

    /// <summary>Gets the common admission instance for one runtime service.</summary>
    /// <param name="settings">The application's runtime configuration.</param>
    /// <returns>The shared budget without retaining abandoned runtime services.</returns>
    internal static ResponseBufferBudget Shared(IRuntimeSettings settings) =>
        SharedBudgets.GetValue(settings, static value => new ResponseBufferBudget(value));
    /// <summary>Serializes reservation changes.</summary>
    private readonly Lock _gate = new();

    /// <summary>Currently reserved bytes, including shared copies only once.</summary>
    private long _used;

    /// <summary>Tracks shared GC-owned payloads without keeping discarded cache bodies alive.</summary>
    private readonly ConditionalWeakTable<byte[], PayloadAllocation> _payloads = new();

    /// <summary>Transfers a newly allocated exact payload reservation to shared ownership.</summary>
    /// <param name="payload">Fresh immutable storage, never previously registered.</param>
    /// <param name="reservation">Admission taken before allocating the array; ownership transfers here.</param>
    /// <returns>The initial asynchronous consumer's ownership token.</returns>
    internal IDisposable AdoptPayload(byte[] payload, Lease reservation)
    {
        var allocation = new PayloadAllocation(this, payload.Length, reservation);
        _payloads.Add(payload, allocation);
        return new PayloadOwner(allocation);
    }

    /// <summary>Charges one physical payload once across fill consumers and cache-hit borrowers.</summary>
    /// <param name="payload">The immutable owned payload.</param>
    /// <returns>An ownership token retained until the last asynchronous reader finishes.</returns>
    internal IDisposable BorrowPayload(ReadOnlyMemory<byte> payload)
    {
        if (!MemoryMarshal.TryGetArray(payload, out var array) || array.Array is null ||
            array.Offset != 0 || array.Count != array.Array.Length) return Reserve(payload.Length);
        if (!_payloads.TryGetValue(array.Array, out var allocation))
            allocation = _payloads.GetValue(array.Array, key => new PayloadAllocation(this, key.Length, null));
        return allocation.Acquire();
    }

    /// <summary>Shares one admitted array across otherwise independent consumers.</summary>
    /// <param name="budget">The shared admission service.</param>
    /// <param name="bytes">The physical array length, without retaining the weak-table key.</param>
    /// <param name="initial">Admission transferred from the fill, or null for an idle cache entry.</param>
    private sealed class PayloadAllocation(ResponseBufferBudget budget, int bytes, Lease? initial)
    {
        /// <summary>Serializes acquisition and release for this array only.</summary>
        private readonly Lock _gate = new();
        /// <summary>The current reservation, absent while no asynchronous consumer owns it.</summary>
        private Lease? _lease = initial;
        /// <summary>Asynchronous owners, independent of whether the cache still stores the array.</summary>
        private int _owners = initial is null ? 0 : 1;

        /// <summary>Retains the array without charging another copy of its bytes.</summary>
        /// <returns>A token for this consumer.</returns>
        internal PayloadOwner Acquire()
        {
            lock (_gate)
            {
                if (_owners == 0) _lease = budget.Reserve(bytes);
                _owners++;
                return new PayloadOwner(this);
            }
        }

        /// <summary>Releases admission when no asynchronous reader still borrows this array.</summary>
        internal void Release()
        {
            lock (_gate)
            {
                if (--_owners == 0) { _lease!.Dispose(); _lease = null; }
            }
        }
    }

    /// <summary>An idempotent reference to one tracked payload allocation.</summary>
    /// <param name="allocation">The allocation whose owner count was already increased.</param>
    private sealed class PayloadOwner(PayloadAllocation allocation) : IDisposable
    {
        /// <summary>The allocation until this consumer ends.</summary>
        private PayloadAllocation? _allocation = allocation;
        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref _allocation, null)?.Release();
    }

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

    /// <inheritdoc />
    IDisposable IResponseBufferBudget.Reserve(long bytes) => Reserve(bytes);

    /// <inheritdoc />
    bool IResponseBufferBudget.TryReserve(long bytes, out IDisposable? reservation)
    {
        try { reservation = Reserve(bytes); return true; }
        catch (WeirBufferCapacityException) { reservation = null; return false; }
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
