using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Abstractions;

namespace Weir.Connectors.PostgreSql;

/// <summary>DI helpers for the PostgreSQL connector.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL <see cref="IDbConnector"/>. Multiple connectors can coexist; the
    /// engine selects one by a connection's <see cref="DataConnectionDescriptor.Provider"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddWeirPostgreSql(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbConnector, PostgreSqlConnector>());
        return services;
    }
}
