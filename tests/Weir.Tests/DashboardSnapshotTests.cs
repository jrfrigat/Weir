using Weir.Contracts;
using Weir.Host.Realtime;
using Xunit;

namespace Weir.Tests;

// The dashboard hub sends a snapshot only when it would change what is on screen, so an idle gateway
// stops pushing a full snapshot at every tick. What decides that is this comparison, and it has exactly
// one deliberate blind spot: the uptime, which changes every second by definition and would otherwise
// make every snapshot "different" and the whole thing pointless.
public class DashboardSnapshotTests
{
    [Fact]
    public void A_Client_With_Nothing_Yet_Is_Always_Sent_A_Snapshot()
    {
        Assert.False(DashboardBroadcaster.SameData(null, Snapshot()));
    }

    [Fact]
    public void Only_The_Clock_Moving_Is_Not_A_Change()
    {
        var previous = Snapshot(uptimeSeconds: 60);
        var current = Snapshot(uptimeSeconds: 61);

        Assert.True(DashboardBroadcaster.SameData(previous, current));
    }

    [Fact]
    public void A_Moved_Overview_Value_Is_A_Change()
    {
        var previous = Snapshot();
        var current = Snapshot(requestsPerSecond: 41);

        Assert.False(DashboardBroadcaster.SameData(previous, current));
    }

    [Fact]
    public void A_Moved_Endpoint_Value_Is_A_Change()
    {
        var previous = Snapshot();
        var current = Snapshot(endpointCount: 8);

        Assert.False(DashboardBroadcaster.SameData(previous, current));
    }

    [Fact]
    public void An_Endpoint_Appearing_Is_A_Change()
    {
        var previous = Snapshot();
        var current = Snapshot() with { Endpoints = [] };

        Assert.False(DashboardBroadcaster.SameData(previous, current));
    }

    [Fact]
    public void A_Chart_That_Advanced_Is_A_Change()
    {
        var previous = Snapshot();
        var current = Snapshot(newestBucketValue: 3);

        Assert.False(DashboardBroadcaster.SameData(previous, current));
    }

    /// <summary>Builds a snapshot, varying one value at a time.</summary>
    /// <param name="uptimeSeconds">Process uptime carried by the overview.</param>
    /// <param name="requestsPerSecond">Overview throughput.</param>
    /// <param name="endpointCount">Call count on the single endpoint.</param>
    /// <param name="newestBucketValue">Value of the newest throughput bucket.</param>
    /// <returns>The snapshot.</returns>
    private static DashboardSnapshot Snapshot(
        int uptimeSeconds = 60,
        double requestsPerSecond = 40,
        long endpointCount = 7,
        double newestBucketValue = 2)
    {
        var endpoint = new EndpointMetrics
        {
            Route = "orders/get",
            ObjectName = "dbo.usp_GetOrder",
            Count = endpointCount,
            P95LatencyMs = 12,
        };

        return new DashboardSnapshot
        {
            Overview = new MetricsOverview
            {
                TotalRequests = 100,
                RequestsPerSecond = requestsPerSecond,
                P95LatencyMs = 12,
                Uptime = TimeSpan.FromSeconds(uptimeSeconds),
                TopSlow = [endpoint],
            },
            Endpoints = [endpoint],
            Throughput = Series("requests", newestBucketValue),
            Latency = Series("latency", 5),
        };
    }

    /// <summary>Builds a two-point series whose newest bucket carries the given value.</summary>
    /// <param name="metric">Metric name.</param>
    /// <param name="newest">Value of the newest bucket.</param>
    /// <returns>The series.</returns>
    private static TimeSeries Series(string metric, double newest) => new()
    {
        Metric = metric,
        Points =
        [
            new MetricPoint { Timestamp = DateTimeOffset.UnixEpoch, Value = 1 },
            new MetricPoint { Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(15), Value = newest },
        ],
    };
}
