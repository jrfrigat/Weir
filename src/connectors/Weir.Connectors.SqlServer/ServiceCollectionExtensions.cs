using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Abstractions;

namespace Weir.Connectors.SqlServer;

/// <summary>DI helpers for the SQL Server connector.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server <see cref="IDbConnector"/>. Multiple connectors can coexist; the
    /// engine selects one by a connection's <see cref="DataConnectionDescriptor.Provider"/>.
    /// </summary>
    public static IServiceCollection AddWeirSqlServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbConnector, SqlServerConnector>());
        return services;
    }
}
