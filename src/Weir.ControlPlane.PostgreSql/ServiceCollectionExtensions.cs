using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Abstractions;

namespace Weir.ControlPlane.PostgreSql;

/// <summary>DI helpers for registering the PostgreSQL control-plane store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresControlPlaneStore"/> as the <see cref="IControlPlaneStore"/>.
    /// Call <see cref="IControlPlaneStore.InitializeAsync"/> once at startup to apply migrations.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddWeirControlPlanePostgres(
        this IServiceCollection services,
        Action<PostgresControlPlaneOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<PostgresControlPlaneOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // Surface a clear message on first use rather than an opaque Npgsql error from an empty string.
        optionsBuilder.Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
            "Weir:ControlPlane:ConnectionString is required for the PostgreSQL control-plane store.");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IControlPlaneStore, PostgresControlPlaneStore>();
        return services;
    }
}
