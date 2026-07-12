namespace Weir.Host.Options;

/// <summary>
/// Rate-limiting settings, bound from <c>Weir:RateLimit</c>. By default the per-API-key limiter is
/// in-memory (per instance). Setting <see cref="RedisConnectionString"/> switches it to a distributed
/// Redis-backed limiter so the limit is shared across every instance in an HA deployment.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// StackExchange.Redis connection string (for example <c>localhost:6379</c>). When set, the data-plane
    /// per-key rate limiter counts requests in Redis so the limit applies across instances. Empty keeps
    /// the in-memory per-instance limiter.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>Key prefix for the limiter's Redis keys, so it can share a Redis instance with other apps.</summary>
    public string RedisKeyPrefix { get; set; } = "weir:ratelimit:";
}
