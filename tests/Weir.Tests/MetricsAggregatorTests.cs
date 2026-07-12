using Weir.Abstractions;
using Weir.Diagnostics;
using Xunit;

namespace Weir.Tests;

public class MetricsAggregatorTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static WeirCallContext Call(string route, double durationMs, string outcome) =>
        new() { Route = route, HttpMethod = "POST", DurationMs = durationMs, Outcome = outcome };

    [Fact]
    public async Task Records_Counts_And_Errors()
    {
        var aggregator = new InMemoryMetricsAggregator(new FixedClock(DateTimeOffset.UnixEpoch.AddDays(1)));

        var ok = Call("orders/get", 10, "ok");
        await aggregator.OnStartedAsync(ok);
        await aggregator.OnCompletedAsync(ok);

        var bad = Call("orders/get", 20, "error");
        await aggregator.OnStartedAsync(bad);
        await aggregator.OnFailedAsync(bad, new InvalidOperationException());

        var overview = aggregator.GetOverview();
        Assert.Equal(2, overview.TotalRequests);
        Assert.Equal(1, overview.TotalErrors);

        var endpoints = aggregator.GetEndpoints();
        Assert.Single(endpoints);
        Assert.Equal("orders/get", endpoints[0].Route);
        Assert.Equal(2, endpoints[0].Count);
        Assert.Equal(1, endpoints[0].Errors);
    }

    [Fact]
    public async Task TimeSeries_Has_Points()
    {
        var aggregator = new InMemoryMetricsAggregator(new FixedClock(DateTimeOffset.UnixEpoch.AddDays(1)));
        var call = Call("orders/get", 5, "ok");
        await aggregator.OnStartedAsync(call);
        await aggregator.OnCompletedAsync(call);

        var series = aggregator.GetTimeSeries("requests", null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15));
        Assert.NotEmpty(series.Points);
    }

    [Fact]
    public async Task Percentiles_Decay_Out_Of_The_Window()
    {
        var clock = new MovableClock(DateTimeOffset.UnixEpoch.AddDays(1));
        var aggregator = new InMemoryMetricsAggregator(clock);

        // A slow call now sits in a high latency bucket.
        var slow = Call("orders/get", 5000, "ok");
        await aggregator.OnStartedAsync(slow);
        await aggregator.OnCompletedAsync(slow);

        Assert.True(aggregator.GetOverview().P95LatencyMs >= 2000);

        // Advance beyond the percentile window (300s, EndpointStats.PercentileWindowSeconds); the slow
        // sample must decay out and the windowed p95 falls back to zero.
        clock.Now = clock.Now.AddSeconds(305);
        Assert.Equal(0, aggregator.GetOverview().P95LatencyMs);
    }
}
