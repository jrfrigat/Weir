using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Weir.Abstractions;
using Weir.Core;
using Weir.Host.Options;

namespace Weir.Host.Security;

/// <summary>
/// A distributed per-API-key rate limiter backed by Redis, so a limit is enforced across every instance
/// in an HA deployment rather than N times over (once per instance). Uses an atomic fixed-window counter:
/// one <c>INCR</c> per request against a per-key, per-minute key with a short TTL. If Redis is unreachable
/// the limiter fails open (allows the request) and logs a warning, favouring availability over a hard
/// limit - a rate limiter should not take the whole data plane down when its backend is briefly gone.
/// </summary>
public sealed class RedisApiKeyRateLimiter : IApiKeyRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _clock;
    private readonly IRuntimeSettings _settings;
    private readonly string _keyPrefix;
    private readonly ILogger<RedisApiKeyRateLimiter> _logger;

    /// <summary>Creates the limiter over a Redis connection, a clock and the runtime settings.</summary>
    /// <param name="redis">The shared Redis connection multiplexer.</param>
    /// <param name="clock">Time source used to compute the current minute window.</param>
    /// <param name="settings">Runtime settings carrying the default per-key limit.</param>
    /// <param name="options">Rate-limit options (the Redis key prefix).</param>
    /// <param name="logger">The logger, used when Redis is unavailable.</param>
    public RedisApiKeyRateLimiter(
        IConnectionMultiplexer redis, TimeProvider clock, IRuntimeSettings settings,
        IOptions<RateLimitOptions> options, ILogger<RedisApiKeyRateLimiter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _redis = redis;
        _clock = clock;
        _settings = settings;
        _keyPrefix = options.Value.RedisKeyPrefix;
        _logger = logger;
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
    public async ValueTask<bool> TryAcquireAsync(ApiKeyRecord key, CancellationToken cancellationToken = default)
    {
        var limit = EffectiveLimit(key);
        if (limit <= 0)
        {
            return true;
        }

        var minute = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        var redisKey = new RedisKey($"{_keyPrefix}{key.Id:N}:{minute}");

        try
        {
            var db = _redis.GetDatabase();
            // Atomically count this request in the current window; set the TTL only on the first hit so
            // the window key self-expires (a two-minute TTL tolerates minor clock skew across instances).
            var count = await db.StringIncrementAsync(redisKey);
            if (count == 1)
            {
                await db.KeyExpireAsync(redisKey, TimeSpan.FromMinutes(2));
            }

            return count <= limit;
        }
        catch (RedisException ex)
        {
            // Fail open: a transient Redis outage must not reject all traffic.
            Log.RateLimiterBackendUnavailable(_logger, ex);
            return true;
        }
    }
}
