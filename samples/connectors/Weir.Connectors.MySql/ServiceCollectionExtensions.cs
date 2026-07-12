using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Abstractions;

namespace Weir.Connectors.MySql;

/// <summary>DI helpers for the sample MySQL connector.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MySQL <see cref="IDbConnector"/>. Multiple connectors can coexist; the engine
    /// selects one per connection by its <see cref="DataConnectionDescriptor.Provider"/> (use
    /// <c>MySql</c>). Call this from a custom host, or let the plugin loader call it via
    /// <see cref="MySqlPlugin"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddWeirMySql(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbConnector, MySqlDbConnector>());
        return services;
    }
}
