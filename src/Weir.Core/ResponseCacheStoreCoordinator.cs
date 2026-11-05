using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>Retains an immutable filled body for another asynchronous consumer.</summary>
internal interface IResponsePayloadOwner : IDisposable
{
    /// <summary>Acquires another reference before its current owner can finish.</summary>
    void Retain();
}

/// <summary>Bounds unfinished stores by count and bytes while preserving actual provider ownership.</summary>
internal sealed partial class ResponseCacheStoreCoordinator : IDisposable, IAsyncDisposable
{
    /// <summary>The provider whose operations are bounded.</summary>
    private readonly IResponseCache _cache;
    /// <summary>Optional atomic invalidation fencing.</summary>
    private readonly IVersionedResponseCache? _versioned;
    /// <summary>Live admission and deadline policy.</summary>
    private readonly IRuntimeSettings _settings;
    /// <summary>Safe rate-limited diagnostics.</summary>
    private readonly ILogger _logger;
    /// <summary>Serializes admission and pending accounting.</summary>
    private readonly Lock _gate = new();
    /// <summary>Cancellation independent of any data-plane client.</summary>
    private readonly CancellationTokenSource _shutdown = new();
    /// <summary>Cached token that remains readable after the source is eventually disposed.</summary>
    private readonly CancellationToken _shutdownToken;
    /// <summary>Completes after shutdown when every actual provider operation has ended.</summary>
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Actual unfinished operations, including ones whose orchestration deadline expired.</summary>
    private int _pending;
    /// <summary>Payload bytes retained by those operations.</summary>
    private long _bytes;
    /// <summary>Whether shutdown has refused further stores.</summary>
    private bool _disposed;
    /// <summary>Stores refused before submission.</summary>
    private long _skipped;
    /// <summary>Provider failures not swallowed by the provider itself.</summary>
    private long _failures;
    /// <summary>Independent deadlines that expired.</summary>
    private long _timeouts;
    /// <summary>Earliest UTC tick for the next safe warning.</summary>
    private long _nextWarning;

    /// <summary>Creates admission for a single engine's cache stores.</summary>
    /// <param name="cache">The response-cache provider.</param>
    /// <param name="settings">Live store policy.</param>
    /// <param name="logger">Optional safe engine logger.</param>
    internal ResponseCacheStoreCoordinator(IResponseCache cache, IRuntimeSettings settings, ILogger? logger = null)
    {
        _cache = cache;
        _versioned = cache as IVersionedResponseCache;
        _settings = settings;
        _logger = logger ?? NullLogger.Instance;
        _shutdownToken = _shutdown.Token;
    }

    /// <summary>Starts an admitted operation and stops waiting at its own deadline.</summary>
    /// <param name="key">Bounded logical cache key.</param>
    /// <param name="entry">Complete immutable bytes.</param>
    /// <param name="ttl">Requested cache lifetime.</param>
    /// <param name="fence">Optional generation captured before execution.</param>
    /// <param name="owner">Body ownership retained until actual provider completion.</param>
    /// <returns>Completion or the orchestration deadline, without surfacing cache failures.</returns>
    internal Task StoreAsync(string key, CachedResponse entry, TimeSpan ttl, CacheWriteFence? fence, IResponsePayloadOwner owner)
    {
        var settings = _settings.Current;
        lock (_gate)
        {
            if (_disposed || (_versioned is not null && fence is null) || key.Length > 1024 || entry.ETag.Length > 128 ||
                (settings.ResponseCacheMaxEntryBytes > 0 && entry.Bytes.Length > settings.ResponseCacheMaxEntryBytes) ||
                _pending >= settings.ResponseCacheWriteMaxPendingCount ||
                entry.Bytes.Length > settings.ResponseCacheWriteMaxPendingBytes - _bytes)
            { Interlocked.Increment(ref _skipped); return Task.CompletedTask; }
            _pending++;
            _bytes += entry.Bytes.Length;
        }
        owner.Retain();
        var job = new StoreJob(settings.ResponseCacheWriteTimeoutMilliseconds, _shutdownToken);
        var actual = RunAsync();
        return actual.IsCompletedSuccessfully ? actual : AwaitDeadlineAsync(actual, job, settings.ResponseCacheWriteTimeoutMilliseconds);

        async Task RunAsync()
        {
            try
            {
                if (_versioned is { } versioned)
                    await versioned.SetIfCurrentAsync(key, entry, ttl, fence!.Value, job.Cancellation.Token);
                else await _cache.SetAsync(key, entry, ttl, job.Cancellation.Token);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                if (!_shutdown.IsCancellationRequested) TimedOut(job);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failures);
                Warn();
            }
            finally
            {
                var length = entry.Bytes.Length;
                entry = default;
                job.Cancellation.Dispose();
                try { owner.Dispose(); }
                finally
                {
                    lock (_gate)
                    {
                        _pending--;
                        _bytes -= length;
                        if (_disposed && _pending == 0) _drained.TrySetResult();
                    }
                }
            }
        }
    }

    /// <summary>Stops orchestration without releasing a provider's still-borrowed bytes.</summary>
    /// <param name="actual">The fully observed actual operation.</param>
    /// <param name="job">The independent cancellation state.</param>
    /// <param name="milliseconds">The orchestration deadline.</param>
    /// <returns>Bounded completion for fill registration cleanup.</returns>
    private async Task AwaitDeadlineAsync(Task actual, StoreJob job, int milliseconds)
    {
        try { await actual.WaitAsync(TimeSpan.FromMilliseconds(milliseconds), _shutdownToken); }
        catch (TimeoutException) { TimedOut(job); job.Cancel(); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { job.Cancel(); }
    }

    /// <summary>Records each deadline once even when timer and provider cancellation complete together.</summary>
    /// <param name="job">The operation whose deadline expired.</param>
    private void TimedOut(StoreJob job)
    {
        if (!job.MarkTimeout()) return;
        Interlocked.Increment(ref _timeouts);
        Warn();
    }

    /// <summary>Combines provider state with actual bounded store ownership.</summary>
    /// <param name="provider">The provider's own secret-free diagnostics.</param>
    /// <returns>A combined cache snapshot.</returns>
    internal ResponseCacheStatus GetStatus(ResponseCacheStatus provider)
    {
        lock (_gate) return provider with
        {
            PendingWrites = Math.Max(provider.PendingWrites, _pending), PendingWriteBytes = Math.Max(provider.PendingWriteBytes, _bytes),
            SkippedStores = provider.SkippedStores + Interlocked.Read(ref _skipped),
            StoreFailures = provider.StoreFailures + Interlocked.Read(ref _failures),
            StoreTimeouts = provider.StoreTimeouts + Interlocked.Read(ref _timeouts)
        };
    }

    /// <summary>Logs only a bounded-frequency event, without key, payload or exception details.</summary>
    private void Warn()
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var previous = Interlocked.Read(ref _nextWarning);
        if (now >= previous && Interlocked.CompareExchange(ref _nextWarning, now + TimeSpan.TicksPerSecond * 30, previous) == previous)
            LogStoreFailure(_logger);
    }

    /// <summary>Refuses new work and cancels independent stores without ending borrowed ownership early.</summary>
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        try { _shutdown.Cancel(); }
        catch (AggregateException) { Warn(); }
        lock (_gate) { if (_pending == 0) _drained.TrySetResult(); }
        _ = DisposeSourceAsync();
    }

    /// <summary>Disposes the source only after operations that might use its token have ended.</summary>
    /// <returns>The eventual cleanup.</returns>
    private async Task DisposeSourceAsync() { await _drained.Task; _shutdown.Dispose(); }

    /// <summary>Allows cooperative providers to drain while bounding host shutdown time.</summary>
    /// <returns>The bounded shutdown wait.</returns>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await _drained.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { Warn(); }
    }

    /// <summary>Emits a safe cache storage/deadline event.</summary>
    /// <param name="logger">The engine logger.</param>
    [LoggerMessage(1921, LogLevel.Warning, "A response cache store failed or exceeded its deadline; response delivery continues and retained writes remain bounded.")]
    private static partial void LogStoreFailure(ILogger logger);

    /// <summary>One independent deadline whose ownership remains with the actual provider operation.</summary>
    private sealed class StoreJob
    {
        /// <summary>Cancellation for the actual provider operation.</summary>
        internal CancellationTokenSource Cancellation { get; }
        /// <summary>Whether its timeout has already been counted.</summary>
        private int _timedOut;
        /// <summary>Creates a deadline independent of any HTTP caller.</summary>
        /// <param name="milliseconds">The configured deadline.</param>
        /// <param name="shutdown">The coordinator's shutdown token.</param>
        internal StoreJob(int milliseconds, CancellationToken shutdown)
        {
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            Cancellation.CancelAfter(milliseconds);
        }
        /// <summary>Marks timeout once.</summary>
        /// <returns>Whether this is its first timeout observation.</returns>
        internal bool MarkTimeout() => Interlocked.Exchange(ref _timedOut, 1) == 0;
        /// <summary>Cancels a still-running provider while tolerating simultaneous completion.</summary>
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
