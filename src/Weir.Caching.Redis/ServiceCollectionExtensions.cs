using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
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
        services.AddSingleton<CacheConnection>();
        services.AddSingleton(sp => new RedisResponseCache(sp.GetRequiredService<CacheConnection>().Multiplexer,
            sp.GetRequiredService<IOptions<RedisResponseCacheOptions>>(), sp.GetRequiredService<IResponseCacheSettings>(),
            sp.GetRequiredService<IResponseBufferBudget>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RedisResponseCache>>()));
        services.Replace(ServiceDescriptor.Singleton<IResponseCache>(sp => sp.GetRequiredService<RedisResponseCache>()));
        return services;
    }

    /// <summary>Owns the response cache's connection independently of other Redis subsystems.</summary>
    private sealed class CacheConnection : IDisposable
    {
        /// <summary>The one multiplexer reused by this cache provider.</summary>
        internal ConnectionMultiplexer Multiplexer { get; }

        /// <summary>Connects with bounded startup time and no disconnected command backlog.</summary>
        /// <param name="options">The validated secret configuration.</param>
        public CacheConnection(IOptions<RedisResponseCacheOptions> options)
        {
            ConfigurationOptions configuration;
            try { configuration = ConfigurationOptions.Parse(options.Value.ConnectionString); }
            catch (ArgumentException) { throw new InvalidOperationException("The Redis response-cache connection configuration is invalid."); }
            configuration.AbortOnConnectFail = false;
            configuration.ConnectRetry = 0;
            configuration.ConnectTimeout = options.Value.OperationTimeoutMilliseconds;
            configuration.AsyncTimeout = options.Value.OperationTimeoutMilliseconds;
            configuration.SyncTimeout = options.Value.OperationTimeoutMilliseconds;
            configuration.BacklogPolicy = BacklogPolicy.FailFast;
            configuration.ClientName = "weir-response-cache";
            try { Multiplexer = ConnectionMultiplexer.Connect(configuration); }
            catch (RedisException) { throw new InvalidOperationException("The Redis response-cache connection could not be initialized."); }
        }

        /// <inheritdoc />
        public void Dispose() => Multiplexer.Dispose();
    }
}
