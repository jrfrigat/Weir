using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Host.Realtime;

/// <summary>
/// Pushes live dashboard data to connected admins over <see cref="DashboardHub"/>, replacing the
/// dashboard's HTTP polling. A cheap metrics snapshot (from the in-memory aggregator) is broadcast
/// every couple of seconds; connection health, which probes the databases, is broadcast on a slower
/// cadence. Both are skipped entirely when no dashboard is connected.
/// </summary>
public sealed class DashboardBroadcaster : BackgroundService
{
    /// <summary>How often the cheap metrics snapshot is pushed.</summary>
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(2);

    /// <summary>How often connection health (which opens database connections) is pushed.</summary>
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(15);

    private readonly IHubContext<DashboardHub> _hub;
    private readonly IMetricsAggregator _metrics;
    private readonly DashboardClientTracker _tracker;
    private readonly IDataConnectionRegistry _registry;
    private readonly IEnumerable<IDbConnector> _connectors;
    private readonly TimeProvider _clock;

    /// <summary>Creates the broadcaster from its collaborators.</summary>
    /// <param name="hub">The dashboard hub context.</param>
    /// <param name="metrics">The in-memory metrics aggregator.</param>
    /// <param name="tracker">The connected-client tracker.</param>
    /// <param name="registry">The data-connection registry (for health).</param>
    /// <param name="connectors">The registered connectors (for health probes).</param>
    /// <param name="clock">Clock for the broadcast timer and health timestamps.</param>
    public DashboardBroadcaster(
        IHubContext<DashboardHub> hub,
        IMetricsAggregator metrics,
        DashboardClientTracker tracker,
        IDataConnectionRegistry registry,
        IEnumerable<IDbConnector> connectors,
        TimeProvider clock)
    {
        _hub = hub;
        _metrics = metrics;
        _tracker = tracker;
        _registry = registry;
        _connectors = connectors;
        _clock = clock;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MetricsInterval, _clock);
        var sinceHealth = HealthInterval; // probe on the first tick that has clients
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!_tracker.HasClients)
                {
                    continue;
                }

                await BroadcastMetricsAsync(stoppingToken);

                sinceHealth += MetricsInterval;
                if (sinceHealth >= HealthInterval)
                {
                    sinceHealth = TimeSpan.Zero;
                    await BroadcastHealthAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Builds and pushes the metrics snapshot to all connected dashboards.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task BroadcastMetricsAsync(CancellationToken cancellationToken)
    {
        var snapshot = new DashboardSnapshot
        {
            Overview = _metrics.GetOverview(),
            Endpoints = _metrics.GetEndpoints(),
            Throughput = _metrics.GetTimeSeries("requests", null, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(15)),
            Latency = _metrics.GetTimeSeries("latency", null, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(15)),
        };

        await _hub.Clients.All.SendAsync("snapshot", snapshot, cancellationToken);
    }

    /// <summary>Probes each connection and pushes the health list to all connected dashboards.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task BroadcastHealthAsync(CancellationToken cancellationToken)
    {
        var byProvider = _connectors.ToDictionary(connector => connector.ProviderName, StringComparer.OrdinalIgnoreCase);
        var results = new List<ConnectionHealth>();
        foreach (var descriptor in _registry.All)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var healthy = false;
            string? error = null;
            if (byProvider.TryGetValue(descriptor.Provider, out var connector))
            {
                try
                {
                    await connector.ProbeAsync(descriptor.Name, cancellationToken);
                    healthy = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Kept for admins and redacted for viewers below: driver text discloses server /
                    // database / login names, and the HTTP route makes the same split.
                    error = ex.Message;
                }
            }
            else
            {
                error = $"No connector for provider '{descriptor.Provider}'.";
            }

            results.Add(new ConnectionHealth
            {
                Name = descriptor.Name,
                Provider = descriptor.Provider,
                Healthy = healthy,
                LatencyMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                Error = error,
                CheckedAt = _clock.GetUtcNow(),
            });
        }

        // Probe once, then say it two ways. A viewer still sees which connection is down and how slow it
        // is - only the driver's text is withheld, exactly as on GET /admin/api/connections/health.
        var redacted = results
            .Select(health => health.Error is null ? health : health with { Error = "unreachable" })
            .ToList();

        await _hub.Clients.Group(DashboardHub.AdminsGroup).SendAsync("health", results, cancellationToken);
        await _hub.Clients.Group(DashboardHub.ViewersGroup).SendAsync("health", redacted, cancellationToken);
    }
}
