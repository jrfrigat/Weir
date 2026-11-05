using System.Diagnostics;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Weir.Caching.Redis;

/// <summary>Owns a dedicated cache connection and retires uncertain I/O without affecting other Redis users.</summary>
public sealed class RedisResponseCacheConnection : IDisposable, IAsyncDisposable
{
    /// <summary>Bounds connection changes and permits only one reconnect attempt.</summary>
    private readonly Lock _gate = new();
    /// <summary>Validated secret options, never exposed through diagnostics.</summary>
    private readonly ConfigurationOptions _configuration;
    /// <summary>The operation/connect wait bound.</summary>
    private readonly int _timeoutMilliseconds;
    /// <summary>The currently usable connection, if initialized.</summary>
    private ConnectionMultiplexer? _current;
    /// <summary>At most one connection attempt.</summary>
    private Task<ConnectionMultiplexer>? _connecting;
    /// <summary>At most one retirement that must precede replacement.</summary>
    private Task? _retiring;
    /// <summary>Whether the owner has stopped accepting new connections.</summary>
    private bool _disposed;

    /// <summary>Begins one bounded connection attempt without requiring Redis for host availability.</summary>
    /// <param name="options">Validated dedicated response-cache settings.</param>
    public RedisResponseCacheConnection(IOptions<RedisResponseCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Value.IsValid()) throw new ArgumentException("Invalid Redis response-cache options.", nameof(options));
        try { _configuration = ConfigurationOptions.Parse(options.Value.ConnectionString); }
        catch (ArgumentException) { throw new InvalidOperationException("The Redis response-cache connection configuration is invalid."); }
        _timeoutMilliseconds = options.Value.OperationTimeoutMilliseconds;
        _configuration.AbortOnConnectFail = false;
        _configuration.ConnectRetry = 0;
        _configuration.ConnectTimeout = _timeoutMilliseconds;
        _configuration.AsyncTimeout = _timeoutMilliseconds;
        _configuration.SyncTimeout = _timeoutMilliseconds;
        _configuration.BacklogPolicy = BacklogPolicy.FailFast;
        _configuration.ClientName = "weir-response-cache";
        _connecting = ConnectAsync();
    }

    /// <summary>Gets current connectivity without starting work or exposing endpoints.</summary>
    public bool IsConnected
    {
        get
        {
            var current = Volatile.Read(ref _current);
            if (current is not null) return current.IsConnected;
            var attempt = Volatile.Read(ref _connecting);
            return attempt is { IsCompletedSuccessfully: true } && attempt.Result.IsConnected;
        }
    }

    /// <summary>Obtains the single owned connection with a bounded initialization wait.</summary>
    /// <returns>The connection used by one operation.</returns>
    internal async Task<ConnectionMultiplexer> GetAsync()
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            Task? retiring;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                retiring = _retiring;
            }
            if (retiring is not null) await retiring.WaitAsync(Remaining(started));
            Task<ConnectionMultiplexer> attempt;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_current is { } ready) return ready;
                if (_connecting is { IsFaulted: true }) _ = _connecting.Exception;
                if (_connecting is null || _connecting.IsFaulted || _connecting.IsCanceled) _connecting = ConnectAsync();
                attempt = _connecting;
            }
            var connection = await attempt.WaitAsync(Remaining(started));
            lock (_gate)
            {
                if (_disposed) { connection.Dispose(); throw new ObjectDisposedException(nameof(RedisResponseCacheConnection)); }
                // Another waiter may have published and retired this attempt while we resumed. Only
                // the still-current initialization may publish; otherwise join its replacement.
                if (!ReferenceEquals(_connecting, attempt)) continue;
                _current ??= connection;
                return _current;
            }
        }
    }

    /// <summary>Keeps repeated generation changes inside one initialization wait budget.</summary>
    /// <param name="started">The initial monotonic timestamp.</param>
    /// <returns>The remaining wait, or a timeout when the total budget expired.</returns>
    private TimeSpan Remaining(long started)
    {
        var remaining = TimeSpan.FromMilliseconds(_timeoutMilliseconds) - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Redis response-cache initialization did not complete.");
        return remaining;
    }

    /// <summary>Closes uncertain queued commands before their callers may release borrowed input bytes.</summary>
    /// <param name="failed">The connection used by the failed operation.</param>
    /// <returns>Completion of local retirement, independent of a remote server reply.</returns>
    internal Task RetireAsync(ConnectionMultiplexer failed)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, failed)) return _retiring ?? Task.CompletedTask;
            _current = null;
            _connecting = null;
            _retiring = RetireCoreAsync(failed);
            return _retiring;
        }
    }

    /// <summary>Performs local connection teardown outside the state lock, then starts one replacement.</summary>
    /// <param name="failed">The retired connection.</param>
    /// <returns>The teardown completion.</returns>
    private async Task RetireCoreAsync(ConnectionMultiplexer failed)
    {
        await Task.Yield();
        failed.Dispose();
        lock (_gate) { if (!_disposed) _connecting = ConnectAsync(); }
    }

    /// <summary>Creates a new multiplexer while redacting initialization failures.</summary>
    /// <returns>The one reconnect attempt.</returns>
    private async Task<ConnectionMultiplexer> ConnectAsync()
    {
        try { return await ConnectionMultiplexer.ConnectAsync(_configuration.Clone()); }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        { throw new InvalidOperationException("The Redis response-cache connection could not be initialized."); }
    }

    /// <summary>Stops new operations and closes any established socket immediately.</summary>
    public void Dispose()
    {
        ConnectionMultiplexer? current;
        Task<ConnectionMultiplexer>? connecting;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            current = _current;
            _current = null;
            connecting = _connecting;
        }
        current?.Dispose();
        if (connecting is not null) _ = DisposeAttemptAsync(connecting, current);
    }

    /// <summary>Cleans a connection that finishes initialization after disposal.</summary>
    /// <param name="attempt">The already bounded connection attempt.</param>
    /// <param name="disposed">The connection already disposed synchronously.</param>
    /// <returns>The observed late cleanup.</returns>
    private static async Task DisposeAttemptAsync(Task<ConnectionMultiplexer> attempt, ConnectionMultiplexer? disposed)
    {
        try { var connection = await attempt; if (!ReferenceEquals(connection, disposed)) connection.Dispose(); }
        catch (Exception) { }
    }

    /// <summary>Awaits bounded connection initialization cleanup during host shutdown.</summary>
    /// <returns>The bounded cleanup wait.</returns>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task<ConnectionMultiplexer>? connecting;
        Task? retiring;
        lock (_gate) { connecting = _connecting; retiring = _retiring; }
        try
        {
            if (retiring is not null) await retiring.WaitAsync(TimeSpan.FromSeconds(5));
            if (connecting is not null) await DisposeAttemptAsync(connecting, null).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException) { }
    }
}
