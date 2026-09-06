using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Host.Realtime;

/// <summary>
/// Pushes live dashboard data to connected admins over <see cref="DashboardHub"/>, which is what the
/// dashboard runs on - it polls nothing while the hub is up.
/// <para>
/// The socket carries changes rather than a heartbeat: the in-memory aggregator is read once a second
/// (cheap - no database is touched) and a snapshot is sent only when it differs from the one already on
/// the client's screen. An idle gateway therefore sends one keepalive every five seconds instead of a
/// full snapshot twice a second, and a busy one updates four times faster than the old fixed cadence
/// did. Everything stops entirely when no dashboard is connected.
/// </para>
/// <para>
/// Connection health is the exception and stays on a slow fixed cadence: it opens a database
/// connection per named connection, so it is a probe rather than a reading, and "has it changed" cannot
/// be answered without paying for it.
/// </para>
/// </summary>
public sealed class DashboardBroadcaster : BackgroundService
{
    /// <summary>How often the in-memory metrics are read to see whether anything moved.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long an unchanged dashboard may go without a message. It keeps the uptime clock on the
    /// ribbon honest and tells a connected client that the stream is alive rather than stuck.
    /// </summary>
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often connection health (which opens database connections) is pushed.</summary>
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(15);

    /// <summary>The window the dashboard's two sparklines cover.</summary>
    private static readonly TimeSpan ChartWindow = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Bucket width for those sparklines, and it is a readability setting rather than a resolution one.
    /// A series advances by exactly one whole bucket at a time, so the bucket IS the size of the step
    /// the line takes: at 15 seconds the five-minute window held twenty points and the line jumped a
    /// twentieth of its width four times a minute, which reads as a jerk. At five it holds sixty and
    /// moves a sixtieth at a time, which reads as a crawl - the same data, told in smaller steps.
    /// </summary>
    private static readonly TimeSpan ChartBucket = TimeSpan.FromSeconds(5);

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
        using var timer = new PeriodicTimer(PollInterval, _clock);
        var sinceHealth = HealthInterval; // probe on the first tick that has clients
        var sinceSnapshot = KeepaliveInterval;
        DashboardSnapshot? lastSent = null;
        var lastJoined = _tracker.Joined;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!_tracker.HasClients)
                {
                    // Nobody is looking. Forget what was last sent: the next client to arrive gets a
                    // snapshot rather than inheriting a comparison made against a screen that is gone.
                    lastSent = null;
                    continue;
                }

                sinceSnapshot += PollInterval;
                var joined = _tracker.Joined;
                var newClient = joined != lastJoined;
                lastJoined = joined;

                var snapshot = ReadSnapshot();
                if (newClient || sinceSnapshot >= KeepaliveInterval || !SameData(lastSent, snapshot))
                {
                    await _hub.Clients.All.SendAsync("snapshot", snapshot, stoppingToken);
                    lastSent = snapshot;
                    sinceSnapshot = TimeSpan.Zero;
                }

                sinceHealth += PollInterval;
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

    /// <summary>Reads the current metrics snapshot. Touches only in-memory state.</summary>
    /// <returns>The snapshot as the dashboard would draw it.</returns>
    private DashboardSnapshot ReadSnapshot() => new()
    {
        Overview = _metrics.GetOverview(),
        Endpoints = _metrics.GetEndpoints(),
        Throughput = _metrics.GetTimeSeries("requests", null, ChartWindow, ChartBucket),
        Latency = _metrics.GetTimeSeries("latency", null, ChartWindow, ChartBucket),
    };

    /// <summary>
    /// Whether two snapshots would draw the same dashboard. Uptime is deliberately left out of the
    /// comparison: it changes every second by definition, and letting it decide would mean the socket
    /// carries a clock rather than the data - which is the whole thing this is here to stop. The
    /// keepalive is what keeps the uptime honest.
    /// </summary>
    /// <param name="previous">The snapshot the clients already have, or null when they have none.</param>
    /// <param name="current">The snapshot just read.</param>
    /// <returns>True when sending <paramref name="current"/> would change nothing on screen.</returns>
    internal static bool SameData(DashboardSnapshot? previous, DashboardSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return previous is not null
            && SameOverview(previous.Overview, current.Overview)
            && SameEndpoints(previous.Endpoints, current.Endpoints)
            && SameSeries(previous.Throughput, current.Throughput)
            && SameSeries(previous.Latency, current.Latency);
    }

    /// <summary>Compares two overviews field by field, except for the uptime.</summary>
    /// <param name="a">One overview.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when every displayed value matches.</returns>
    private static bool SameOverview(MetricsOverview a, MetricsOverview b) =>
        a.TotalRequests == b.TotalRequests
        && a.TotalErrors == b.TotalErrors
        && a.RequestsPerSecond.Equals(b.RequestsPerSecond)
        && a.ErrorRate.Equals(b.ErrorRate)
        && a.CacheHitRatio.Equals(b.CacheHitRatio)
        && a.P50LatencyMs.Equals(b.P50LatencyMs)
        && a.P95LatencyMs.Equals(b.P95LatencyMs)
        && a.P99LatencyMs.Equals(b.P99LatencyMs)
        && a.ActiveRequests == b.ActiveRequests
        && SameEndpoints(a.TopSlow, b.TopSlow);

    /// <summary>Compares two endpoint-metric lists item by item.</summary>
    /// <param name="a">One list.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when both hold the same metrics in the same order.</returns>
    private static bool SameEndpoints(IReadOnlyList<EndpointMetrics> a, IReadOnlyList<EndpointMetrics> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            // EndpointMetrics is a record of scalars, so its own equality compares every field.
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Compares two time series point by point.</summary>
    /// <param name="a">One series.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when both hold the same points in the same order.</returns>
    private static bool SameSeries(TimeSeries a, TimeSeries b)
    {
        if (a.Points.Count != b.Points.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Points.Count; i++)
        {
            if (a.Points[i] != b.Points[i])
            {
                return false;
            }
        }

        return true;
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
