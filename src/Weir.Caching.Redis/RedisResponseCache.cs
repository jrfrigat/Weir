using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Caching.Redis;

/// <summary>Stores complete immutable binary snapshots with atomic generation-fenced publication.</summary>
public sealed partial class RedisResponseCache : IVersionedResponseCache, IResponseCacheDiagnostics
{
    /// <summary>Fixed binary header followed by ASCII ETag and raw UTF-8 response bytes.</summary>
    private const int HeaderBytes = 16;
    /// <summary>Hard Redis string limit, independent of the optional application entry-size policy.</summary>
    private const int MaximumPackedBytes = 512 * 1024 * 1024;
    /// <summary>Bounds the index read even when its stored value is corrupt.</summary>
    private const int MaximumIndexChars = 128;

    /// <summary>Normalizes bounded opaque epochs and reads the matching bounded index atomically.</summary>
    private const string LookupScript = """
        local function epoch(key, candidate)
          local value = redis.call('GETRANGE', key, 0, 32)
          if string.len(value) ~= 32 or not string.match(value, '^[0-9a-f]+$') then
            value = candidate
            redis.call('SET', key, value, 'PX', ARGV[3])
          else redis.call('PEXPIRE', key, ARGV[3]) end
          return value
        end
        local g = epoch(KEYS[1], ARGV[1])
        local r = epoch(KEYS[2], ARGV[2])
        local p = redis.call('GETRANGE', KEYS[3], 0, 128)
        if string.sub(p, 1, 64) ~= g .. r then p = false end
        return {g, r, p}
        """;

    /// <summary>Publishes a snapshot and index together, without refreshing an already valid entry.</summary>
    private const string StoreScript = """
        if redis.call('GETRANGE', KEYS[1], 0, 32) ~= ARGV[1] or
           redis.call('GETRANGE', KEYS[2], 0, 32) ~= ARGV[2] then return 0 end
        local current = redis.call('GETRANGE', KEYS[3], 0, 63)
        if current == ARGV[1] .. ARGV[2] then return 0 end
        redis.call('SET', KEYS[4], ARGV[3] .. ARGV[4], 'PX', ARGV[5])
        redis.call('SET', KEYS[3], ARGV[6], 'PX', ARGV[5])
        redis.call('PEXPIRE', KEYS[1], ARGV[7])
        redis.call('PEXPIRE', KEYS[2], ARGV[7])
        return 1
        """;

    /// <summary>Removes only the corrupt index this lookup actually observed.</summary>
    private const string RepairScript = """
        if redis.call('GETRANGE', KEYS[1], 0, 128) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    /// <summary>The dedicated connection owner that can retire uncertain I/O.</summary>
    private readonly RedisResponseCacheConnection _connection;
    /// <summary>Restart-required safe and secret options; only safe fields are exposed.</summary>
    private readonly RedisResponseCacheOptions _options;
    /// <summary>Live entry-size policy without a dependency on Core.</summary>
    private readonly IResponseCacheSettings _settings;
    /// <summary>Admission shared with the engine and whole-body transports.</summary>
    private readonly IResponseBufferBudget _budget;
    /// <summary>Rate-limited safe backend diagnostics.</summary>
    private readonly ILogger<RedisResponseCache> _logger;
    /// <summary>Hashed installation namespace; its hash tag keeps script keys in one Redis Cluster slot.</summary>
    private readonly string _prefix;
    /// <summary>The namespace-wide opaque generation key.</summary>
    private readonly RedisKey _globalKey;
    /// <summary>Epochs outlive every snapshot even after traffic stops.</summary>
    private readonly long _epochMilliseconds;
    /// <summary>Failed lookups since startup.</summary>
    private long _lookupFailures;
    /// <summary>Failed stores since startup.</summary>
    private long _storeFailures;
    /// <summary>Invalidation faults that require a conservative namespace purge.</summary>
    private long _invalidationFaults;
    /// <summary>The latest invalidation fault covered by a confirmed recovery purge.</summary>
    private long _recoveredFault;
    /// <summary>Rejected or redundant stores.</summary>
    private long _skippedStores;
    /// <summary>Invalid binary/index values observed.</summary>
    private long _corruptEntries;
    /// <summary>Writes whose payloads are still retained.</summary>
    private int _pendingWrites;
    /// <summary>Bytes retained by pending provider writes.</summary>
    private long _pendingBytes;
    /// <summary>Whether the most recent backend operation succeeded.</summary>
    private int _available = 1;
    /// <summary>Next UTC tick at which a safe warning may be emitted.</summary>
    private long _nextWarning;
    /// <summary>Serializes only recovery purges, without queuing lookup callers.</summary>
    private readonly Lock _recoveryGate = new();
    /// <summary>Whether one lookup is already trying recovery.</summary>
    private bool _recovering;

    /// <summary>Caps actual metadata lookup jobs, including their bounded cancellation cleanup.</summary>
    private const int MaximumConcurrentLookups = 128;
    /// <summary>Serializes read/write admission without queuing callers.</summary>
    private readonly Lock _admissionGate = new();
    /// <summary>Metadata lookups whose actual I/O has not completed.</summary>
    private int _activeLookups;
    /// <summary>Earliest UTC tick at which data calls retry a failed backend.</summary>
    private long _retryAt;

    /// <summary>Creates a provider over a connection whose lifetime belongs to its caller.</summary>
    /// <param name="connection">Dedicated reusable connection owner.</param>
    /// <param name="options">Validated startup configuration.</param>
    /// <param name="settings">Live response-cache policy.</param>
    /// <param name="budget">Shared active allocation admission.</param>
    /// <param name="logger">Safe backend diagnostics.</param>
    public RedisResponseCache(RedisResponseCacheConnection connection, IOptions<RedisResponseCacheOptions> options,
        IResponseCacheSettings settings, IResponseBufferBudget budget, ILogger<RedisResponseCache> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(logger);
        if (!options.Value.IsValid()) throw new ArgumentException("Invalid Redis response-cache options.", nameof(options));
        _connection = connection;
        _options = options.Value;
        _settings = settings;
        _budget = budget;
        _logger = logger;
        _prefix = "weir:response:{" + Hash(_options.Namespace) + "}:v1:";
        _globalKey = _prefix + "generation";
        _epochMilliseconds = ((long)_options.MaximumTtlSeconds * 2 + 3600) * 1000;
    }

    /// <inheritdoc />
    public async ValueTask<ResponseCacheLookup> LookupAsync(string key, string scopePrefix, CancellationToken cancellationToken = default)
    {
        ValidateScope(key, scopePrefix);
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _retryAt)) return default;
        lock (_admissionGate) { if (_activeLookups >= MaximumConcurrentLookups) return default; _activeLookups++; }
        Lease<byte>? payload = null;
        IDisposable? admission = null;
        try
        {
            var connection = await _connection.GetAsync();
            var database = connection.GetDatabase(_options.Database);
            if (!await EnsureRecoveredAsync(database, connection, cancellationToken)) return default;
            var index = IndexKey(key);
            var raw = await AwaitRedisAsync(database.ScriptEvaluateAsync(LookupScript, [_globalKey, RouteKey(scopePrefix), index],
                [NewGeneration(), NewGeneration(), _epochMilliseconds]), connection);
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = (RedisResult[]?)raw;
            if (metadata is null || metadata.Length != 3) throw new InvalidDataException("Invalid cache metadata.");
            var global = (string?)metadata[0];
            var route = (string?)metadata[1];
            if (!IsGeneration(global) || !IsGeneration(route)) throw new InvalidDataException("Invalid cache generation.");
            var fence = new CacheWriteFence(scopePrefix, global + route);
            var pointer = (string?)metadata[2];
            if (string.IsNullOrEmpty(pointer)) { Succeeded(); return new(null, fence); }
            if (!TryParsePointer(pointer, out var snapshot, out var length) || !AdmitsPacked(length))
            {
                await RepairAsync(database, connection, index, pointer);
                return new(null, fence);
            }
            var payloadKey = PayloadKey(snapshot);
            // The unique snapshot key is immutable. Verify corrupt pointer lengths before renting the
            // full value, then reserve before GET; its size cannot change through this provider.
            var actual = await AwaitRedisAsync(database.StringLengthAsync(payloadKey), connection);
            if (actual != length)
            {
                await RepairAsync(database, connection, index, pointer);
                return new(null, fence);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!_budget.TryReserve(length, out admission)) return default;
            payload = await AwaitRedisAsync(database.StringGetLeaseAsync(payloadKey), connection);
            cancellationToken.ThrowIfCancellationRequested();
            if (payload is null) { Succeeded(); return new(null, fence); }
            if (payload.Length != length || !TryReadFrame(payload.Memory, out var response))
            {
                await RepairAsync(database, connection, index, pointer);
                return new(null, fence);
            }
            var lease = new CachedResponseLease(response, new ReadOwner(payload, admission!));
            payload = null;
            admission = null;
            Succeeded();
            return new(lease, fence);
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            Interlocked.Increment(ref _lookupFailures);
            Failed("lookup");
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }
        finally
        {
            try { try { payload?.Dispose(); } finally { admission?.Dispose(); } }
            finally { lock (_admissionGate) _activeLookups--; }
        }
    }

    /// <inheritdoc />
    public async ValueTask SetIfCurrentAsync(string key, CachedResponse entry, TimeSpan ttl, CacheWriteFence fence,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(key, fence.ScopePrefix);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        if (fence.Version is null || fence.Version.Length != 64 || !IsGeneration(fence.Version[..32]) ||
            !IsGeneration(fence.Version[32..]) || !AdmitsBody(entry.Bytes.Length) || !ValidETag(entry.ETag))
        { Interlocked.Increment(ref _skippedStores); return; }
        if (Interlocked.Read(ref _invalidationFaults) != Interlocked.Read(ref _recoveredFault))
        { Interlocked.Increment(ref _skippedStores); return; }
        var settings = _settings.Current;
        lock (_admissionGate)
        {
            if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _retryAt) ||
                _pendingWrites >= settings.ResponseCacheWriteMaxPendingCount ||
                entry.Bytes.Length > settings.ResponseCacheWriteMaxPendingBytes - _pendingBytes)
            { Interlocked.Increment(ref _skippedStores); return; }
            _pendingWrites++;
            _pendingBytes += entry.Bytes.Length;
        }
        try
        {
            var header = MakeHeader(entry);
            var packedLength = checked(header.Length + entry.Bytes.Length);
            // Reserve possible driver/network storage beside the source before command submission.
            if (!_budget.TryReserve(packedLength, out var reserved)) { Interlocked.Increment(ref _skippedStores); return; }
            using var admission = reserved;
            var connection = await _connection.GetAsync();
            var database = connection.GetDatabase(_options.Database);
            var snapshot = NewGeneration();
            var pointer = string.Concat(fence.Version, snapshot, ":", packedLength.ToString(CultureInfo.InvariantCulture));
            var ttlMs = Math.Max(1, Math.Min((long)Math.Ceiling(ttl.TotalMilliseconds), (long)_options.MaximumTtlSeconds * 1000));
            var stored = (long)await AwaitRedisAsync(database.ScriptEvaluateAsync(StoreScript,
                [_globalKey, RouteKey(fence.ScopePrefix), IndexKey(key), PayloadKey(snapshot)],
                [fence.Version[..32], fence.Version[32..], header, entry.Bytes, ttlMs, pointer, _epochMilliseconds]), connection);
            if (stored == 0) Interlocked.Increment(ref _skippedStores);
            Succeeded();
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            Interlocked.Increment(ref _storeFailures);
            Failed("store");
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_admissionGate) { _pendingWrites--; _pendingBytes -= entry.Bytes.Length; }
        }
    }

    /// <inheritdoc />
    public async ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var lookup = await LookupAsync(key, LegacyScope(key), cancellationToken);
        using var lease = lookup.Entry;
        return lease is null ? null : new CachedResponse(lease.Response.Bytes.ToArray(), lease.Response.ETag);
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string key, CachedResponse entry, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var lookup = await LookupAsync(key, LegacyScope(key), cancellationToken);
        using var previous = lookup.Entry;
        if (lookup.Fence is { } fence) await SetIfCurrentAsync(key, entry, ttl, fence, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return InvalidateAsync(_globalKey, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);
        // In-box route prefixes contain exactly two colons. Any broader/custom prefix safely evicts
        // the whole namespace rather than scanning keys or missing a matching nested scope.
        var routeScope = keyPrefix.StartsWith("weir:", StringComparison.Ordinal) && keyPrefix.EndsWith(':') &&
            keyPrefix.Count(static c => c == ':') == 2;
        return InvalidateAsync(routeScope ? RouteKey(keyPrefix) : _globalKey, cancellationToken);
    }

    /// <inheritdoc />
    public ResponseCacheStatus GetStatus() => new()
    {
        Provider = "Redis", Namespace = _options.Namespace,
        Database = _options.Database, OperationTimeoutMilliseconds = _options.OperationTimeoutMilliseconds,
        MaximumTtlSeconds = _options.MaximumTtlSeconds,
        Available = _connection.IsConnected && Volatile.Read(ref _available) != 0 &&
            Interlocked.Read(ref _invalidationFaults) == Interlocked.Read(ref _recoveredFault),
        LocalPayloadBytes = 0, LocalEntryCount = 0, MaxEntryBytes = _settings.Current.ResponseCacheMaxEntryBytes,
        LookupFailures = Interlocked.Read(ref _lookupFailures), StoreFailures = Interlocked.Read(ref _storeFailures),
        InvalidationFailures = Interlocked.Read(ref _invalidationFaults), SkippedStores = Interlocked.Read(ref _skippedStores),
        CorruptEntries = Interlocked.Read(ref _corruptEntries), PendingWrites = Volatile.Read(ref _pendingWrites),
        PendingWriteBytes = Interlocked.Read(ref _pendingBytes)
    };

    /// <summary>Checks idle availability and confirms pending invalidation recovery without data clients.</summary>
    /// <param name="cancellationToken">Host shutdown.</param>
    /// <returns>The bounded backend probe.</returns>
    public async ValueTask ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var connection = await _connection.GetAsync();
            var database = connection.GetDatabase(_options.Database);
            if (!await EnsureRecoveredAsync(database, connection, cancellationToken)) return;
            await AwaitRedisAsync(database.PingAsync(), connection);
            cancellationToken.ThrowIfCancellationRequested();
            Succeeded();
        }
        catch (Exception ex) when (IsBackendFault(ex)) { Failed("health"); }
    }

    /// <summary>Bounds a Redis command and retires its owned socket before releasing uncertain I/O.</summary>
    /// <typeparam name="T">The driver's result type.</typeparam>
    /// <param name="actual">The actual driver operation, never abandoned unobserved.</param>
    /// <param name="connection">The dedicated connection used by that operation.</param>
    /// <returns>The result whose ownership is transferred to its caller.</returns>
    private async Task<T> AwaitRedisAsync<T>(Task<T> actual, ConnectionMultiplexer connection)
    {
        try { return await actual.WaitAsync(TimeSpan.FromMilliseconds(_options.OperationTimeoutMilliseconds)); }
        catch (Exception ex) when (ex is TimeoutException or RedisConnectionException)
        {
            await _connection.RetireAsync(connection);
            try
            {
                var late = await actual;
                if (late is IDisposable owner) owner.Dispose();
            }
            catch (Exception) { }
            throw new TimeoutException("Redis response-cache I/O did not complete.");
        }
    }

    /// <summary>Changes an opaque epoch; a failed invalidation must not be reported as confirmed.</summary>
    /// <param name="epochKey">Route or installation epoch.</param>
    /// <param name="cancellationToken">The mutation lifetime.</param>
    /// <returns>The confirmed invalidation.</returns>
    private async ValueTask InvalidateAsync(RedisKey epochKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var connection = await _connection.GetAsync();
            var database = connection.GetDatabase(_options.Database);
            await AwaitRedisAsync(database.StringSetAsync(epochKey, NewGeneration(), TimeSpan.FromMilliseconds(_epochMilliseconds)), connection);
            Succeeded();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            Interlocked.Increment(ref _invalidationFaults);
            Failed("invalidation");
            throw new InvalidOperationException("Response cache invalidation could not be confirmed.");
        }
    }

    /// <summary>Repairs failed invalidations with one conservative purge before using recovered data.</summary>
    /// <param name="database">The operation's captured database handle.</param>
    /// <param name="connection">Its owned connection.</param>
    /// <param name="cancellationToken">The lookup lifetime.</param>
    /// <returns>Whether the provider may safely serve a cached generation.</returns>
    private async ValueTask<bool> EnsureRecoveredAsync(IDatabase database, ConnectionMultiplexer connection, CancellationToken cancellationToken)
    {
        var fault = Interlocked.Read(ref _invalidationFaults);
        if (fault == Interlocked.Read(ref _recoveredFault)) return true;
        lock (_recoveryGate) { if (_recovering) return false; _recovering = true; }
        try
        {
            await AwaitRedisAsync(database.StringSetAsync(_globalKey, NewGeneration(), TimeSpan.FromMilliseconds(_epochMilliseconds)), connection);
            Interlocked.Exchange(ref _recoveredFault, fault);
            Succeeded();
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Read(ref _invalidationFaults) == fault;
        }
        finally { lock (_recoveryGate) _recovering = false; }
    }

    /// <summary>Deletes only the observed corrupt index; snapshots expire independently.</summary>
    /// <param name="database">The operation's captured database handle.</param>
    /// <param name="connection">Its owned connection.</param>
    /// <param name="index">The bounded logical index key.</param>
    /// <param name="observed">The exact bounded value this lookup read.</param>
    /// <returns>The completed repair attempt.</returns>
    private async ValueTask RepairAsync(IDatabase database, ConnectionMultiplexer connection, RedisKey index, string observed)
    {
        Interlocked.Increment(ref _corruptEntries);
        await AwaitRedisAsync(database.ScriptEvaluateAsync(RepairScript, [index], [observed]), connection);
        Succeeded();
    }

    /// <summary>Checks provider and runtime size bounds before any complete value is fetched.</summary>
    /// <param name="length">Packed bytes from the bounded pointer.</param>
    /// <returns>Whether a complete fetch may be attempted.</returns>
    private bool AdmitsPacked(int length) => length >= HeaderBytes && length <= MaximumPackedBytes &&
        (_settings.Current.ResponseCacheMaxEntryBytes <= 0 || length - HeaderBytes <= _settings.Current.ResponseCacheMaxEntryBytes + 128);

    /// <summary>Checks the unencoded body policy before submitting a write.</summary>
    /// <param name="length">Body bytes.</param>
    /// <returns>Whether the body is eligible.</returns>
    private bool AdmitsBody(int length) => length <= MaximumPackedBytes - HeaderBytes - 128 &&
        (_settings.Current.ResponseCacheMaxEntryBytes <= 0 || length <= _settings.Current.ResponseCacheMaxEntryBytes);

    /// <summary>Validates the binary frame without deserializing its JSON payload.</summary>
    /// <param name="memory">The provider-owned packed value.</param>
    /// <param name="response">The borrowed response slice.</param>
    /// <returns>Whether its version, lengths and validator are valid.</returns>
    private bool TryReadFrame(ReadOnlyMemory<byte> memory, out CachedResponse response)
    {
        response = default;
        var span = memory.Span;
        if (span.Length < HeaderBytes || !span[..4].SequenceEqual("WRC1"u8)) return false;
        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var tagLength = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        if (!AdmitsBody(bodyLength) || bodyLength < 0 || tagLength is < 2 or > 128 ||
            (long)HeaderBytes + tagLength + bodyLength != span.Length || span[10..HeaderBytes].IndexOfAnyExcept((byte)0) >= 0) return false;
        var tag = span.Slice(HeaderBytes, tagLength);
        if (tag[0] != '"' || tag[^1] != '"') return false;
        foreach (var value in tag) if (value is < 32 or > 126) return false;
        response = new CachedResponse(memory[(HeaderBytes + tagLength)..], Encoding.ASCII.GetString(tag));
        return true;
    }

    /// <summary>Builds a small fixed header and ASCII validator separately from the response payload.</summary>
    /// <param name="entry">The immutable complete response.</param>
    /// <returns>Only the bounded frame prefix; payload concatenation happens on Redis.</returns>
    private static byte[] MakeHeader(CachedResponse entry)
    {
        var header = new byte[HeaderBytes + entry.ETag.Length];
        "WRC1"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), entry.Bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)entry.ETag.Length);
        Encoding.ASCII.GetBytes(entry.ETag, header.AsSpan(HeaderBytes));
        return header;
    }

    /// <summary>Parses only a bounded immutable snapshot identity and byte length.</summary>
    /// <param name="pointer">Generation-prefixed index contents.</param>
    /// <param name="snapshot">The immutable payload identity.</param>
    /// <param name="length">Packed payload bytes.</param>
    /// <returns>Whether the index is well-formed.</returns>
    private static bool TryParsePointer(string pointer, out string snapshot, out int length)
    {
        snapshot = string.Empty;
        length = 0;
        if (pointer.Length is < 98 or > MaximumIndexChars || pointer[96] != ':' ||
            !Guid.TryParseExact(pointer.AsSpan(64, 32), "N", out _) ||
            !int.TryParse(pointer.AsSpan(97), NumberStyles.None, CultureInfo.InvariantCulture, out length)) return false;
        snapshot = pointer.Substring(64, 32);
        return true;
    }

    /// <summary>Accepts only bounded printable strong validators.</summary>
    /// <param name="tag">The caller's precomputed validator.</param>
    /// <returns>Whether the validator fits the frame.</returns>
    private static bool ValidETag(string? tag) => tag is { Length: >= 2 and <= 128 } && tag[0] == '"' && tag[^1] == '"' &&
        tag.All(static value => value is >= ' ' and <= '~');

    /// <summary>Checks one provider-created opaque epoch.</summary>
    /// <param name="value">The epoch value.</param>
    /// <returns>Whether it is a complete non-repeating GUID representation.</returns>
    private static bool IsGeneration(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);
    /// <summary>Creates a fresh epoch or immutable snapshot identity.</summary>
    /// <returns>A fixed-length opaque generation.</returns>
    private static string NewGeneration() => Guid.NewGuid().ToString("N");
    /// <summary>Hashes metadata so Redis key names contain no request values or arbitrary route text.</summary>
    /// <param name="value">The logical metadata.</param>
    /// <returns>A fixed-length key component.</returns>
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    /// <summary>Builds one logical index key.</summary>
    /// <param name="key">The logical response key.</param>
    /// <returns>A namespaced bounded Redis key.</returns>
    private RedisKey IndexKey(string key) => _prefix + "index:" + Hash(key);
    /// <summary>Builds one route epoch key.</summary>
    /// <param name="scope">The invalidation prefix.</param>
    /// <returns>A namespaced epoch key.</returns>
    private RedisKey RouteKey(string scope) => _prefix + "route:" + Hash(scope);
    /// <summary>Builds an immutable binary snapshot key.</summary>
    /// <param name="snapshot">The validated snapshot identity.</param>
    /// <returns>A key in the namespace's script hash slot.</returns>
    private RedisKey PayloadKey(string snapshot) => _prefix + "payload:" + snapshot;
    /// <summary>Infers a scope only for the legacy convenience API.</summary>
    /// <param name="key">The logical key.</param>
    /// <returns>The prefix through its final colon, or an empty broad scope.</returns>
    private static string LegacyScope(string key) => key[..(key.LastIndexOf(':') + 1)];
    /// <summary>Rejects mismatched or excessive metadata before backend operations.</summary>
    /// <param name="key">The bounded logical key.</param>
    /// <param name="scope">Its invalidation prefix.</param>
    private static void ValidateScope(string key, string scope)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(scope);
        if (key.Length > 1024 || scope.Length > 1024 || !key.StartsWith(scope, StringComparison.Ordinal))
            throw new ArgumentException("The response cache key must belong to a bounded scope.", nameof(key));
    }
    /// <summary>Identifies backend failures without swallowing admission or request cancellation.</summary>
    /// <param name="exception">The observed exception.</param>
    /// <returns>Whether fail-open cache handling applies.</returns>
    private static bool IsBackendFault(Exception exception) => exception is RedisException or TimeoutException or InvalidDataException or ObjectDisposedException or InvalidOperationException;
    /// <summary>Marks the backend's latest operation successful.</summary>
    private void Succeeded() { Volatile.Write(ref _available, 1); Interlocked.Exchange(ref _retryAt, 0); }
    /// <summary>Records a safe, rate-limited warning without credentials, server messages or key data.</summary>
    /// <param name="operation">An internal fixed operation name.</param>
    private void Failed(string operation)
    {
        Volatile.Write(ref _available, 0);
        var now = DateTimeOffset.UtcNow.UtcTicks;
        Interlocked.Exchange(ref _retryAt, now + TimeSpan.TicksPerSecond);
        var previous = Interlocked.Read(ref _nextWarning);
        if (now < previous || Interlocked.CompareExchange(ref _nextWarning, now + TimeSpan.TicksPerSecond * 30, previous) != previous) return;
        LogBackendFailure(_logger, operation);
    }
    /// <summary>Emits a secret-free cache failure event.</summary>
    /// <param name="logger">The provider logger.</param>
    /// <param name="operation">The fixed operation name.</param>
    [LoggerMessage(1911, LogLevel.Warning, "Redis response cache {Operation} failed; cached data is unavailable or storage was skipped.")]
    private static partial void LogBackendFailure(ILogger logger, string operation);

    /// <summary>Releases the driver lease and its admission only after transmission ends.</summary>
    /// <param name="payload">Borrowed Redis storage.</param>
    /// <param name="admission">Reservation taken before GET.</param>
    private sealed class ReadOwner(Lease<byte> payload, IDisposable admission) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() { try { payload.Dispose(); } finally { admission.Dispose(); } }
    }
}
