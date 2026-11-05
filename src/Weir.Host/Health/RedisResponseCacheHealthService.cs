using Weir.Caching.Redis;

namespace Weir.Host.Health;

/// <summary>Restores cache availability and pending invalidations even when the gateway is idle.</summary>
public sealed class RedisResponseCacheHealthService : BackgroundService
{
    /// <summary>The optional response-cache provider, independent of the rate limiter.</summary>
    private readonly RedisResponseCache _cache;

    /// <summary>Creates the idle health probe.</summary>
    /// <param name="cache">The configured Redis response cache.</param>
    public RedisResponseCacheHealthService(RedisResponseCache cache) => _cache = cache;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _cache.ProbeAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
