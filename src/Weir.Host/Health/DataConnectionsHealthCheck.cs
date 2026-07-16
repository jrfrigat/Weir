using Microsoft.Extensions.Diagnostics.HealthChecks;
using Weir.Abstractions;

namespace Weir.Host.Health;

/// <summary>
/// Probes every configured data connection (a lightweight round-trip via the matching connector).
/// Reports Degraded, not Unhealthy, if a target database is unreachable - Weir itself is still up.
/// </summary>
public sealed class DataConnectionsHealthCheck : IHealthCheck
{
    private readonly IDataConnectionRegistry _registry;
    private readonly Dictionary<string, IDbConnector> _connectors;

    /// <summary>Creates the health check.</summary>
    /// <param name="registry">The data-connection registry.</param>
    /// <param name="connectors">The registered connectors, indexed by provider.</param>
    public DataConnectionsHealthCheck(IDataConnectionRegistry registry, IEnumerable<IDbConnector> connectors)
    {
        _registry = registry;
        _connectors = connectors.ToDictionary(connector => connector.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var anyUnhealthy = false;

        foreach (var descriptor in _registry.All)
        {
            if (!_connectors.TryGetValue(descriptor.Provider, out var connector))
            {
                data[descriptor.Name] = $"no connector for provider '{descriptor.Provider}'";
                anyUnhealthy = true;
                continue;
            }

            try
            {
                await connector.ProbeAsync(descriptor.Name, cancellationToken);
                data[descriptor.Name] = "healthy";
            }
            catch (Exception)
            {
                data[descriptor.Name] = "unhealthy";
                anyUnhealthy = true;
            }
        }

        return anyUnhealthy
            ? HealthCheckResult.Degraded("One or more data connections are unreachable.", data: data)
            : HealthCheckResult.Healthy("All data connections are reachable.", data);
    }
}
