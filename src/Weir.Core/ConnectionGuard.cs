using System.Collections.Concurrent;

namespace Weir.Core;

/// <summary>
/// Per-connection resilience: a concurrency bulkhead and a circuit breaker for one data connection.
/// The bulkhead caps how many executions may run at once (fail-fast: a request over the limit is
/// rejected, not queued). The breaker trips after a run of failures and short-circuits further calls
/// until a reset window elapses, then lets a probe through. Both are driven by runtime settings read
/// on each call, so limits change without a restart. All state transitions are lock-guarded.
/// </summary>
internal sealed class ConnectionGuard : IDisposable
{
    /// <summary>Guards the semaphore swap and every breaker state transition.</summary>
    private readonly object _sync = new();

    /// <summary>The current bulkhead semaphore, or null when the connection is unlimited.</summary>
    private SemaphoreSlim? _semaphore;

    /// <summary>The concurrency limit <see cref="_semaphore"/> was built for (rebuilt when it changes).</summary>
    private int _limit;

    /// <summary>Consecutive failure count since the last success.</summary>
    private int _failures;

    /// <summary>Whether the breaker is currently open (short-circuiting).</summary>
    private bool _open;

    /// <summary>The <see cref="Environment.TickCount64"/> value at which the open window ends.</summary>
    private long _openUntilTick;

    /// <summary>
    /// Throws <see cref="WeirConnectionUnavailableException"/> when the breaker is open and still inside
    /// its reset window. Once the window elapses the call is allowed through as a probe; a probe success
    /// closes the breaker, a probe failure re-arms it.
    /// </summary>
    /// <param name="failureThreshold">The trip threshold; zero disables the breaker.</param>
    public void EnsureClosed(int failureThreshold)
    {
        if (failureThreshold <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_open && Environment.TickCount64 < _openUntilTick)
            {
                throw new WeirConnectionUnavailableException(
                    "The data connection is temporarily unavailable (circuit open). Retry shortly.");
            }
        }
    }

    /// <summary>
    /// Acquires a bulkhead permit without waiting. Returns a permit that releases on dispose; throws
    /// <see cref="WeirConnectionUnavailableException"/> when the connection is already at its limit.
    /// </summary>
    /// <param name="concurrencyLimit">The maximum concurrent executions; zero or less means unlimited.</param>
    /// <returns>A disposable permit to release when the execution completes.</returns>
    public ConnectionPermit Enter(int concurrencyLimit)
    {
        var semaphore = ResolveSemaphore(concurrencyLimit);
        if (semaphore is not null && !semaphore.Wait(0))
        {
            throw new WeirConnectionUnavailableException(
                "The data connection is at its concurrency limit. Retry shortly.");
        }

        return new ConnectionPermit(semaphore);
    }

    /// <summary>Records a successful execution, closing the breaker and clearing the failure run.</summary>
    public void RecordSuccess()
    {
        lock (_sync)
        {
            _failures = 0;
            _open = false;
        }
    }

    /// <summary>
    /// Records a failed execution. Trips the breaker once the failure run reaches the threshold, or
    /// re-arms the reset window when a probe (open-window) attempt fails.
    /// </summary>
    /// <param name="failureThreshold">The trip threshold; zero disables the breaker.</param>
    /// <param name="resetSeconds">How long the breaker stays open once tripped.</param>
    public void RecordFailure(int failureThreshold, int resetSeconds)
    {
        if (failureThreshold <= 0)
        {
            return;
        }

        lock (_sync)
        {
            _failures++;
            if (_open)
            {
                _openUntilTick = Environment.TickCount64 + (long)Math.Max(0, resetSeconds) * 1000L;
            }
            else if (_failures >= failureThreshold)
            {
                _open = true;
                _openUntilTick = Environment.TickCount64 + (long)Math.Max(0, resetSeconds) * 1000L;
            }
        }
    }

    /// <summary>Whether the breaker is currently open (for tests / diagnostics).</summary>
    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _open && Environment.TickCount64 < _openUntilTick;
            }
        }
    }

    /// <summary>
    /// Returns the bulkhead semaphore for the requested limit, rebuilding it when the limit changes.
    /// A limit of zero or less means unlimited and yields null (no gating).
    /// </summary>
    /// <param name="limit">The desired concurrency limit.</param>
    /// <returns>The semaphore, or null when unlimited.</returns>
    private SemaphoreSlim? ResolveSemaphore(int limit)
    {
        if (limit <= 0)
        {
            return null;
        }

        lock (_sync)
        {
            if (_semaphore is null || _limit != limit)
            {
                // Rebuild on a limit change. In-flight permits hold their own semaphore reference and
                // release it correctly; only new arrivals use the resized one.
                _semaphore = new SemaphoreSlim(limit, limit);
                _limit = limit;
            }

            return _semaphore;
        }
    }

    /// <summary>Disposes the bulkhead semaphore.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _semaphore?.Dispose();
            _semaphore = null;
        }
    }
}

/// <summary>A held bulkhead permit; releases its semaphore slot on dispose.</summary>
internal readonly struct ConnectionPermit(SemaphoreSlim? semaphore) : IDisposable
{
    /// <summary>The semaphore to release, or null when the connection was unlimited.</summary>
    private readonly SemaphoreSlim? _semaphore = semaphore;

    /// <summary>Releases the bulkhead slot back to the connection.</summary>
    public void Dispose() => _semaphore?.Release();
}

/// <summary>Keeps one <see cref="ConnectionGuard"/> per data-connection name, created on first use.</summary>
internal sealed class DataConnectionGuards : IDisposable
{
    /// <summary>The guards, keyed by connection name (case-insensitive).</summary>
    private readonly ConcurrentDictionary<string, ConnectionGuard> _guards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the guard for a connection, creating it on first request.</summary>
    /// <param name="connectionName">The data-connection name.</param>
    /// <returns>The connection's guard.</returns>
    public ConnectionGuard For(string connectionName) =>
        _guards.GetOrAdd(connectionName, static _ => new ConnectionGuard());

    /// <summary>Disposes every connection guard.</summary>
    public void Dispose()
    {
        foreach (var guard in _guards.Values)
        {
            guard.Dispose();
        }

        _guards.Clear();
    }
}
