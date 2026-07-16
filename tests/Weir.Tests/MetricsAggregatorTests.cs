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

    // A clock that advances one second for every fixed number of reads. It gives a concurrency test a
    // deterministic number of second roll-overs without sleeping. The tick counter is interlocked: a
    // plain mutable DateTimeOffset field (as MovableClock has) is too wide to be written atomically, so
    // it cannot be advanced while other threads read it.
    private sealed class TickingClock(DateTimeOffset start, int readsPerSecond) : TimeProvider
    {
        private long _reads;

        public override DateTimeOffset GetUtcNow() =>
            start.AddSeconds(Interlocked.Increment(ref _reads) / readsPerSecond);
    }

    private static WeirCallContext Call(string route, double durationMs, string outcome) =>
        new() { Route = route, HttpMethod = "POST", DurationMs = durationMs, Outcome = outcome };

    // Runs body on dedicated threads that all start together, so the recorders genuinely overlap rather
    // than being serialized by a busy thread pool.
    private static void RunConcurrently(int threads, Action body)
    {
        using var barrier = new Barrier(threads);
        var workers = new Thread[threads];
        Exception? failure = null;

        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    body();
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
            });
            workers[t].Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        Assert.Null(failure);
    }

    // Sums a per-second metric across every one-second bucket of the ring. Unlike the aggregator's
    // lifetime counters (plain interlocked longs), this reads the ring slots that Record writes
    // lock-free, so it is what a lost increment would actually show up in.
    private static long RingTotal(InMemoryMetricsAggregator aggregator, string metric) =>
        aggregator
            .GetTimeSeries(metric, null, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(1))
            .Points.Sum(point => (long)point.Value);

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

    [Fact]
    public void Concurrent_Records_Are_Counted_Exactly()
    {
        const int threads = 8;
        const int callsPerThread = 2_000;
        const int rounds = 16;
        const int total = threads * callsPerThread;

        for (var round = 0; round < rounds; round++)
        {
            // A fixed clock puts every call in the same second, so all threads race to claim the very
            // same ring slot and then hammer it. Claiming a slot is the roll-over path: if a slot's
            // second could ever be published before its counters were cleared, or if two threads could
            // both clear it, the ring totals below would drift low. A fresh aggregator per round means
            // every round re-runs that contended claim.
            var aggregator = new InMemoryMetricsAggregator(new FixedClock(DateTimeOffset.UnixEpoch.AddDays(1)));

            RunConcurrently(threads, () =>
            {
                for (var i = 0; i < callsPerThread; i++)
                {
                    aggregator.Record(new WeirCallContext
                    {
                        Route = "orders/get",
                        HttpMethod = "POST",
                        DurationMs = 5,
                        CacheHit = i % 4 == 0,
                        Outcome = i % 2 == 0 ? "error" : "ok",
                    });
                }
            });

            var overview = aggregator.GetOverview();
            Assert.Equal(total, overview.TotalRequests);
            Assert.Equal(total / 2, overview.TotalErrors);

            var endpoint = Assert.Single(aggregator.GetEndpoints());
            Assert.Equal(total, endpoint.Count);
            Assert.Equal(total / 2, endpoint.Errors);

            // These come off the ring's per-second slots rather than the lifetime counters: they are only
            // exact if not one increment was lost to, or double counted by, the slot claim.
            Assert.Equal(total, RingTotal(aggregator, "requests"));
            Assert.Equal(total / 2, RingTotal(aggregator, "errors"));
            Assert.Equal(0.5, overview.ErrorRate);
            Assert.Equal(0.25, overview.CacheHitRatio);
        }
    }

    [Fact]
    public void Concurrent_Records_Are_Counted_Exactly_Across_Second_Rollovers()
    {
        const int threads = 8;
        const int callsPerThread = 4_000;
        const int total = threads * callsPerThread;

        // One second per 250 recorded calls, so the run spans about 128 seconds. That is well inside the
        // ring's 300s capacity, so no slot is reused and nothing may legitimately fall out of the window,
        // yet it forces ~128 roll-overs that threads hit concurrently - some still recording second S
        // while others have already moved on to S+1.
        var aggregator = new InMemoryMetricsAggregator(new TickingClock(DateTimeOffset.UnixEpoch.AddDays(1), 250));

        RunConcurrently(threads, () =>
        {
            for (var i = 0; i < callsPerThread; i++)
            {
                aggregator.Record(new WeirCallContext
                {
                    Route = "orders/get",
                    HttpMethod = "POST",
                    DurationMs = 5,
                    Outcome = i % 2 == 0 ? "error" : "ok",
                });
            }
        });

        Assert.Equal(total, aggregator.GetOverview().TotalRequests);
        Assert.Equal(total, RingTotal(aggregator, "requests"));
        Assert.Equal(total / 2, RingTotal(aggregator, "errors"));
    }
}
