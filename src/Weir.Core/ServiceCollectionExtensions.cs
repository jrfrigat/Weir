using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Abstractions;

namespace Weir.Core;

/// <summary>DI helpers for the Weir engine.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Weir engine (endpoint catalog, parameter binder, connection registry, memory
    /// response cache and <see cref="WeirEngine"/>). Register connectors (e.g. <c>AddWeirSqlServer</c>)
    /// and a control-plane store separately.
    /// </summary>
    public static IServiceCollection AddWeirCore(
        this IServiceCollection services,
        Action<WeirDataConnectionsOptions>? configureConnections = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<WeirDataConnectionsOptions>();
        if (configureConnections is not null)
        {
            options.Configure(configureConnections);
        }

        // Register data-plane limit options with defaults; the host may bind Weir:DataPlane over them.
        services.AddOptions<WeirDataPlaneOptions>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRuntimeSettings, RuntimeSettings>();
        services.TryAddSingleton<IDataConnectionRegistry, DataConnectionRegistry>();
        services.TryAddSingleton<IEndpointCatalog, EndpointCatalog>();
        services.TryAddSingleton<IParameterBinder, ParameterBinder>();

        // The shared memory cache is registered for other consumers (e.g. the host's API-key
        // authenticator); the response cache deliberately owns a private, size-bounded instance.
        services.AddMemoryCache();
        services.TryAddSingleton<IResponseCache, MemoryResponseCache>();

        services.TryAddSingleton<WeirEngine>();
        return services;
    }
}
