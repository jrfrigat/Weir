using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>
/// Stores immutable UTF-8 payloads with independent byte, entry-size and entry-count bounds.
/// Lock-free hits mark a second-chance CLOCK entry; mutations and accounting are synchronous,
/// so evicted payloads are not retained in background eviction callback queues.
/// </summary>
public sealed class MemoryResponseCache : IResponseCache, IDisposable
{
    /// <summary>Bounds retained key metadata independently of the payload budget.</summary>
    private const int MaxKeyChars = 1024;

    /// <summary>Bounds retained validator metadata; generated SHA-256 ETags fit comfortably.</summary>
    private const int MaxETagChars = 128;

    /// <summary>The live runtime configuration.</summary>
    private readonly IRuntimeSettings _settings;

    /// <summary>The TTL clock, replaceable by deterministic tests.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Serializes store, trim, expiry removal and invalidation.</summary>
    private readonly Lock _gate = new();

    /// <summary>The hit-path index, containing only admitted entries.</summary>
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Second-chance order, modified only under the mutation gate.</summary>
    private readonly LinkedList<Entry> _clock = new();

    /// <summary>The settings snapshot whose reductions have been applied.</summary>
    private WeirSystemSettings _appliedSettings;

    /// <summary>Exact admitted payload bytes, protected by the mutation gate.</summary>
    private long _payloadBytes;

    /// <summary>Admission refusals, protected by the mutation gate.</summary>
    private long _skippedEntries;

    /// <summary>Prefix invalidation stamp retained for compatibility with the engine.</summary>
    private long _purgeStamp;

    /// <summary>Whether this cache has released its stored entries.</summary>
    private bool _disposed;

    /// <summary>Creates the cache with the system TTL clock.</summary>
    /// <param name="settings">Runtime cache policy.</param>
    public MemoryResponseCache(IRuntimeSettings settings) : this(settings, TimeProvider.System) { }

    /// <summary>Creates the cache with an explicit TTL clock.</summary>
    /// <param name="settings">Runtime cache policy.</param>
    /// <param name="timeProvider">The clock used for expiration.</param>
    public MemoryResponseCache(IRuntimeSettings settings, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _settings = settings;
        _timeProvider = timeProvider;
        _appliedSettings = settings.Current;
    }

    /// <summary>The number of prefix invalidations, for detecting fills started before a purge.</summary>
    public long PurgeStamp => Interlocked.Read(ref _purgeStamp);

    /// <inheritdoc />
    public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        EnsureSettings();
        if (!_entries.TryGetValue(key, out var entry)) return ValueTask.FromResult<CachedResponse?>(null);
        if (entry.ExpiresAt <= _timeProvider.GetUtcNow().UtcTicks)
        {
            lock (_gate) RemoveEntry(entry);
            return ValueTask.FromResult<CachedResponse?>(null);
        }
        entry.Touch();
        return ValueTask.FromResult<CachedResponse?>(entry.Response);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, CachedResponse entry, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var settings = _settings.Current;
            ApplySettings(settings);
            var size = entry.Bytes.Length;
            if (key.Length > MaxKeyChars || entry.ETag is null || entry.ETag.Length > MaxETagChars ||
                (settings.ResponseCacheMaxEntryBytes > 0 && size > settings.ResponseCacheMaxEntryBytes) ||
                (settings.ResponseCacheMaxBytes > 0 && size > settings.ResponseCacheMaxBytes))
            {
                _skippedEntries++;
                return ValueTask.CompletedTask;
            }

            if (_entries.TryGetValue(key, out var previous)) RemoveEntry(previous);
            while ((settings.ResponseCacheMaxBytes > 0 && _payloadBytes + size > settings.ResponseCacheMaxBytes) ||
                (settings.ResponseCacheMaxEntries > 0 && _clock.Count >= settings.ResponseCacheMaxEntries))
                EvictOne();

            // A short slice must not retain a much larger array or a borrowed memory manager.
            if (!MemoryMarshal.TryGetArray(entry.Bytes, out var array) ||
                array.Array is null || array.Offset != 0 || array.Count != array.Array.Length)
                entry = new CachedResponse(entry.Bytes.ToArray(), entry.ETag);

            var now = _timeProvider.GetUtcNow().UtcTicks;
            var expires = now + Math.Min(ttl.Ticks, DateTimeOffset.MaxValue.UtcTicks - now);
            var stored = new Entry(key, entry, expires);
            stored.Node = _clock.AddLast(stored);
            _entries[key] = stored;
            _payloadBytes += size;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var entry)) RemoveEntry(entry);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var node = _clock.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Key.StartsWith(keyPrefix, StringComparison.Ordinal)) RemoveEntry(node.Value);
                node = next;
            }
            Interlocked.Increment(ref _purgeStamp);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns a coherent snapshot of cache-owned storage, excluding runtime object overhead.</summary>
    /// <returns>Payload bytes, retained entry count and admission refusals.</returns>
    internal (long PayloadBytes, int EntryCount, long SkippedEntries) GetStorageSnapshot()
    {
        EnsureSettings();
        lock (_gate) return (_payloadBytes, _clock.Count, _skippedEntries);
    }

    /// <summary>Applies changed bounds without taking a lock on an unchanged hit path.</summary>
    private void EnsureSettings()
    {
        var settings = _settings.Current;
        if (ReferenceEquals(Volatile.Read(ref _appliedSettings), settings)) return;
        lock (_gate) ApplySettings(_settings.Current);
    }

    /// <summary>Trims oversized and excess entries when runtime settings change; caller owns the gate.</summary>
    /// <param name="settings">The current immutable configuration snapshot.</param>
    private void ApplySettings(WeirSystemSettings settings)
    {
        if (ReferenceEquals(_appliedSettings, settings)) return;
        for (var node = _clock.First; node is not null;)
        {
            var next = node.Next;
            if (settings.ResponseCacheMaxEntryBytes > 0 && node.Value.Response.Bytes.Length > settings.ResponseCacheMaxEntryBytes)
                RemoveEntry(node.Value);
            node = next;
        }
        while ((settings.ResponseCacheMaxBytes > 0 && _payloadBytes > settings.ResponseCacheMaxBytes) ||
            (settings.ResponseCacheMaxEntries > 0 && _clock.Count > settings.ResponseCacheMaxEntries))
            EvictOne();
        Volatile.Write(ref _appliedSettings, settings);
    }

    /// <summary>Gives recently read entries one bounded second chance before evicting a victim.</summary>
    private void EvictOne()
    {
        var attempts = _clock.Count;
        while (attempts-- > 0 && _clock.First is { } node &&
            node.Value.ExpiresAt > _timeProvider.GetUtcNow().UtcTicks && node.Value.TakeSecondChance())
        {
            _clock.RemoveFirst();
            _clock.AddLast(node);
        }
        if (_clock.First is { } victim) RemoveEntry(victim.Value);
    }

    /// <summary>Removes only the same admitted entry, so a concurrent replacement cannot be removed by an expired reader.</summary>
    /// <param name="entry">The entry whose identity is being removed.</param>
    private void RemoveEntry(Entry entry)
    {
        if (!_entries.TryRemove(new KeyValuePair<string, Entry>(entry.Key, entry))) return;
        _clock.Remove(entry.Node!);
        _payloadBytes -= entry.Response.Bytes.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            _entries.Clear();
            _clock.Clear();
            _payloadBytes = 0;
        }
    }

    /// <summary>One immutable payload and its bounded replacement-policy metadata.</summary>
    /// <param name="key">The index key.</param>
    /// <param name="response">Owned immutable payload and validator.</param>
    /// <param name="expiresAt">Expiration in UTC ticks.</param>
    private sealed class Entry(string key, CachedResponse response, long expiresAt)
    {
        /// <summary>The bounded index key.</summary>
        internal string Key { get; } = key;

        /// <summary>The response, retained until both cache and in-flight readers release it.</summary>
        internal CachedResponse Response { get; } = response;

        /// <summary>Expiration on the cache's UTC clock.</summary>
        internal long ExpiresAt { get; } = expiresAt;

        /// <summary>The node owned by the replacement clock.</summary>
        internal LinkedListNode<Entry>? Node { get; set; }

        /// <summary>Whether a hit occurred since the last replacement sweep.</summary>
        private int _accessed;

        /// <summary>Marks a hit without repeatedly writing a shared hot cache line.</summary>
        internal void Touch()
        {
            if (Volatile.Read(ref _accessed) == 0) Volatile.Write(ref _accessed, 1);
        }

        /// <summary>Consumes one second chance during a bounded eviction sweep.</summary>
        /// <returns>Whether this entry was recently read.</returns>
        internal bool TakeSecondChance() => Interlocked.Exchange(ref _accessed, 0) != 0;
    }
}
