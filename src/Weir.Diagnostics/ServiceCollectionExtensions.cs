using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Abstractions;

namespace Weir.Diagnostics;

/// <summary>DI helpers for Weir telemetry.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory metrics aggregator and the OpenTelemetry observer. The aggregator is
    /// exposed as both <see cref="IMetricsAggregator"/> (read by the admin API) and an
    /// <see cref="IWeirCallObserver"/> (fed by the engine). The host still wires up OTLP export by
    /// subscribing to the "Weir" meter and activity source.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddWeirDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InMemoryMetricsAggregator>();
        services.TryAddSingleton<IMetricsAggregator>(sp => sp.GetRequiredService<InMemoryMetricsAggregator>());

        // The aggregator is also fed as an observer. A factory descriptor cannot be de-duplicated by
        // TryAddEnumerable, so register it with AddSingleton (AddWeirDiagnostics is called once).
        services.AddSingleton<IWeirCallObserver>(sp => sp.GetRequiredService<InMemoryMetricsAggregator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWeirCallObserver, OpenTelemetryCallObserver>());

        return services;
    }
}
