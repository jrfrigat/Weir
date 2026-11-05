using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Weir.Abstractions;

namespace Weir.Caching.Redis;

/// <summary>Registers the optional Redis provider without depending on the engine or host.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Replaces response caching with a dedicated reusable Redis connection.</summary>
    /// <param name="services">The host service collection, with cache settings and admission ports registered.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddWeirRedisResponseCache(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<RedisResponseCacheOptions>().Validate(static options => options.IsValid(),
            "Redis response caching needs a connection, an installation namespace and valid numeric bounds.").ValidateOnStart();
        services.AddSingleton<RedisResponseCacheConnection>();
        services.AddSingleton(sp => new RedisResponseCache(sp.GetRequiredService<RedisResponseCacheConnection>(),
            sp.GetRequiredService<IOptions<RedisResponseCacheOptions>>(), sp.GetRequiredService<IResponseCacheSettings>(),
            sp.GetRequiredService<IResponseBufferBudget>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RedisResponseCache>>()));
        services.Replace(ServiceDescriptor.Singleton<IResponseCache>(sp => sp.GetRequiredService<RedisResponseCache>()));
        return services;
    }

}
