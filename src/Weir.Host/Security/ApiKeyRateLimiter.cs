using System.Collections.Concurrent;
using Weir.Abstractions;
using Weir.Core;

namespace Weir.Host.Security;

/// <summary>Enforces per-API-key request rate limits.</summary>
public interface IApiKeyRateLimiter
{
    /// <summary>
    /// Records a request for the key and reports whether it is within the key's rate limit. A key with
    /// no configured limit falls back to the configured default; when neither is set the request passes.
    /// </summary>
    /// <param name="key">The authenticated key record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the request is allowed; false if the limit is exceeded.</returns>
    ValueTask<bool> TryAcquireAsync(ApiKeyRecord key, CancellationToken cancellationToken = default);

    /// <summary>The current per-key limit for a key (its own limit, or the configured default; 0 = unlimited).</summary>
    /// <param name="key">The authenticated key record.</param>
    /// <returns>The effective per-minute limit, or 0 when the request is unthrottled.</returns>
    int EffectiveLimit(ApiKeyRecord key);
}

/// <summary>
/// In-memory fixed-window rate limiter keyed by API key id. Enforces
/// <see cref="ApiKeyRecord.RateLimitPerMinute"/> (or the configured default) per one-minute window.
/// Per-instance; for a multi-instance deployment a distributed limiter would be required.
/// </summary>
public sealed class ApiKeyRateLimiter : IApiKeyRateLimiter
{
    /// <summary>Soft cap on tracked keys; exceeding it triggers a purge of stale windows.</summary>
    private const int MaxEntries = 50_000;

    private readonly ConcurrentDictionary<Guid, Window> _windows = new();
    private readonly TimeProvider _clock;

    /// <summary>Runtime settings; the default per-key limit is read from here on each request.</summary>
    private readonly IRuntimeSettings _settings;

    /// <summary>Creates the limiter over a clock and the runtime settings (for the default limit).</summary>
    /// <param name="clock">Time source used to compute the current minute window.</param>
    /// <param name="settings">Runtime settings carrying the default per-key limit.</param>
    public ApiKeyRateLimiter(TimeProvider clock, IRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _clock = clock;
        _settings = settings;
    }

    /// <inheritdoc />
    public int EffectiveLimit(ApiKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.RateLimitPerMinute is int configured && configured > 0
            ? configured
            : _settings.Current.DefaultApiKeyRateLimitPerMinute;
    }

    /// <inheritdoc />
    public ValueTask<bool> TryAcquireAsync(ApiKeyRecord key, CancellationToken cancellationToken = default)
    {
        var limit = EffectiveLimit(key);
        if (limit <= 0)
        {
            return ValueTask.FromResult(true);
        }

        var minute = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        if (_windows.Count >= MaxEntries)
        {
            Purge(minute);
        }

        var window = _windows.GetOrAdd(key.Id, static _ => new Window());
        lock (window)
        {
            if (window.Minute != minute)
            {
                window.Minute = minute;
                window.Count = 0;
            }

            window.Count++;
            return ValueTask.FromResult(window.Count <= limit);
        }
    }

    /// <summary>Removes windows that belong to an earlier minute, bounding memory growth over key churn.</summary>
    /// <param name="currentMinute">The current one-minute window.</param>
    private void Purge(long currentMinute)
    {
        foreach (var pair in _windows)
        {
            var window = pair.Value;
            lock (window)
            {
                if (window.Minute < currentMinute)
                {
                    _windows.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    /// <summary>A per-key counter for the current one-minute window.</summary>
    private sealed class Window
    {
        /// <summary>Unix minute the counter applies to.</summary>
        public long Minute;

        /// <summary>Requests counted in the current minute.</summary>
        public int Count;
    }
}
