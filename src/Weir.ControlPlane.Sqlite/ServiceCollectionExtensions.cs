using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Abstractions;

namespace Weir.ControlPlane.Sqlite;

/// <summary>DI helpers for registering the SQLite control-plane store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SqliteControlPlaneStore"/> as the <see cref="IControlPlaneStore"/>.
    /// Call <see cref="IControlPlaneStore.InitializeAsync"/> once at startup to apply migrations.
    /// </summary>
    public static IServiceCollection AddWeirControlPlaneSqlite(
        this IServiceCollection services,
        Action<SqliteControlPlaneOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<SqliteControlPlaneOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IControlPlaneStore, SqliteControlPlaneStore>();
        return services;
    }
}
