using Microsoft.Extensions.Diagnostics.HealthChecks;
using Weir.Abstractions;

namespace Weir.Host.Health;

/// <summary>Health check that verifies the control-plane store is reachable.</summary>
public sealed class ControlPlaneHealthCheck : IHealthCheck
{
    private readonly IControlPlaneStore _store;

    /// <summary>Creates the health check.</summary>
    /// <param name="store">The control-plane store to probe.</param>
    public ControlPlaneHealthCheck(IControlPlaneStore store) => _store = store;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.GetScopesAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Control-plane store is unreachable.", ex);
        }
    }
}
