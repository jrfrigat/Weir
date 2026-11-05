using Microsoft.Extensions.Logging.Abstractions;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Diagnostics;
using Weir.Host.Realtime;
using Xunit;

namespace Weir.Tests;

/// <summary>Checks bounded gauge history, unknown values and sampling independent of dashboard clients.</summary>
public sealed class MemoryMetricsTests
{
    /// <summary>A newly created instance has no invented observations or zero-filled past history.</summary>
    [Fact]
    public void Startup_HasIdentityButNoInventedHistory()
    {
        var first = new MemoryMetrics(new Clock()).GetSnapshot();
        var second = new MemoryMetrics(new Clock()).GetSnapshot();
        Assert.NotEmpty(first.InstanceId);
        Assert.NotEqual(first.InstanceId, second.InstanceId);
        Assert.Empty(first.Points);
        Assert.Null(first.SampledAt);
        Assert.Null(first.Cache.LocalPayloadBytes);
    }

    /// <summary>The gauge ring stays fixed and preserves large byte values without an integer sum.</summary>
    [Fact]
    public void Ring_RetainsOnlyLast300Seconds_WithoutOverflow()
    {
        var clock = new Clock();
        var initial = clock.GetUtcNow();
        var metrics = new MemoryMetrics(clock);
        for (var second = 0; second < 1000; second++)
        {
            metrics.RecordSample(long.MaxValue, new ResponseCacheStatus { Provider = "Memory", LocalPayloadBytes = long.MaxValue / 2 }, 7, 0);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var snapshot = metrics.GetSnapshot();
        Assert.Equal(300, snapshot.Points.Count);
        Assert.Equal(initial.AddSeconds(700), snapshot.Points[0].Timestamp);
        Assert.Equal(initial.AddSeconds(999), snapshot.SampledAt);
        Assert.Equal(long.MaxValue, snapshot.Points[^1].ProcessWorkingSetBytes);
        Assert.Equal(long.MaxValue / 2, snapshot.Points[^1].LocalCachePayloadBytes);
        Assert.Equal(0, snapshot.ActiveResponseBufferBudgetBytes);
    }

    /// <summary>Missing seconds are unknown while a repeated second replaces its observation.</summary>
    [Fact]
    public void GapsAndDuplicateSeconds_DoNotBecomeZeroOrExtraPoints()
    {
        var clock = new Clock();
        var metrics = new MemoryMetrics(clock);
        metrics.RecordSample(1000, new ResponseCacheStatus { Provider = "Memory", LocalPayloadBytes = 0 }, 0, 1000);
        clock.Advance(TimeSpan.FromSeconds(3));
        metrics.RecordSample(2000, new ResponseCacheStatus { Provider = "Memory", LocalPayloadBytes = 50 }, 4, 1000);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        metrics.RecordSample(3000, new ResponseCacheStatus { Provider = "Memory", LocalPayloadBytes = 60 }, 5, 1000);
        var snapshot = metrics.GetSnapshot();
        Assert.Equal(4, snapshot.Points.Count);
        Assert.Equal(0, snapshot.Points[0].LocalCachePayloadBytes);
        Assert.Null(snapshot.Points[1].ProcessWorkingSetBytes);
        Assert.Null(snapshot.Points[2].LocalCachePayloadBytes);
        Assert.Equal(3000, snapshot.Points[^1].ProcessWorkingSetBytes);
        Assert.Equal(60, snapshot.Points[^1].LocalCachePayloadBytes);
        Assert.Equal(5, snapshot.ActiveResponseBufferBytes);
    }

    /// <summary>A backwards wall clock explicitly resets history rather than mixing future buckets.</summary>
    [Fact]
    public void BackwardsClock_ResetsHistoryWithinSameInstance()
    {
        var clock = new Clock();
        var metrics = new MemoryMetrics(clock);
        metrics.RecordSample(1000, new(), 0, 0);
        var identity = metrics.GetSnapshot().InstanceId;
        clock.Advance(TimeSpan.FromSeconds(-1));
        metrics.RecordSample(2000, new(), 0, 0);
        var reset = metrics.GetSnapshot();
        Assert.Single(reset.Points);
        Assert.Equal(identity, reset.InstanceId);
        Assert.Equal(1, reset.HistoryResetCount);
        Assert.Equal(2000, reset.Points[0].ProcessWorkingSetBytes);
    }

    /// <summary>Unknown custom statistics remain null, while a remote cache's local zero is real.</summary>
    [Fact]
    public void UnknownCustom_AndRedisLocalZero_AreDifferent()
    {
        var clock = new Clock();
        var metrics = new MemoryMetrics(clock);
        metrics.RecordSample(null, new ResponseCacheStatus(), 0, 0);
        Assert.Null(metrics.GetSnapshot().Points[0].LocalCachePayloadBytes);
        clock.Advance(TimeSpan.FromSeconds(1));
        metrics.RecordSample(1024, new ResponseCacheStatus { Provider = "Redis", LocalPayloadBytes = 0, LocalEntryCount = 0 }, 0, 0);
        Assert.Equal(0, metrics.GetSnapshot().Points[^1].LocalCachePayloadBytes);
        clock.Advance(TimeSpan.FromSeconds(1000));
        metrics.RecordSample(2048, new(), 0, 0);
        var advanced = metrics.GetSnapshot();
        Assert.Equal(300, advanced.Points.Count);
        Assert.All(advanced.Points.Take(299), point => Assert.Null(point.ProcessWorkingSetBytes));
    }

    /// <summary>The background beat advances without a tracker, client connection or data-plane request.</summary>
    /// <returns>The completed idle-sampling check.</returns>
    [Fact]
    public async Task BackgroundBeat_SamplesWhileNoDashboardOrRequestsExist()
    {
        var clock = new Clock();
        var metrics = new MemoryMetrics(clock);
        var reader = new Reader(() => clock.Advance(TimeSpan.FromSeconds(1)));
        using var service = new MemorySamplingService(metrics, reader, new Cache(), new Budget(), new Settings(), clock,
            NullLogger<MemorySamplingService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 250 && Volatile.Read(ref reader.Calls) < 2; attempt++) await Task.Delay(20);
            Assert.True(Volatile.Read(ref reader.Calls) >= 2);
        }
        finally { await service.StopAsync(CancellationToken.None); }
        var snapshot = metrics.GetSnapshot();
        Assert.True(snapshot.Points.Count >= 2);
        Assert.Equal(0, snapshot.Cache.LocalPayloadBytes);
        Assert.Equal(7, snapshot.ActiveResponseBufferBytes);
        Assert.Equal(999, snapshot.ActiveResponseBufferBudgetBytes);
    }

    /// <summary>A failed reader or custom provider produces only the affected gap.</summary>
    /// <param name="processFailure">Whether process reading, rather than cache diagnostics, fails.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SamplingFailure_DoesNotFailGatewayOrInventZeros(bool processFailure)
    {
        var clock = new Clock();
        var metrics = new MemoryMetrics(clock);
        using var service = new MemorySamplingService(metrics, new Reader(fail: processFailure), new Cache(!processFailure),
            new Budget(), new Settings(), clock, NullLogger<MemorySamplingService>.Instance);
        service.Capture();
        var point = metrics.GetSnapshot().Points[0];
        if (processFailure) { Assert.Null(point.ProcessWorkingSetBytes); Assert.Equal(0, point.LocalCachePayloadBytes); }
        else { Assert.Equal(1024, point.ProcessWorkingSetBytes); Assert.Null(point.LocalCachePayloadBytes); }
    }

    /// <summary>The operating-system reader reports resident bytes and releases its process handle.</summary>
    [Fact]
    public void ProcessReader_ReadsCurrentWorkingSet_AndDisposes()
    {
        using var reader = new ProcessMemoryReader();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.True(reader.ReadWorkingSetBytes() > 0);
        reader.Dispose();
        Assert.Null(reader.ReadWorkingSetBytes());
    }

    /// <summary>A deterministic UTC clock; its default timer still drives the idle background test.</summary>
    private sealed class Clock : TimeProvider
    {
        /// <summary>The current fake observation time.</summary>
        private DateTimeOffset _now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _now;
        /// <summary>Moves the observation clock without sampling.</summary>
        /// <param name="amount">The signed clock movement.</param>
        internal void Advance(TimeSpan amount) => _now += amount;
    }

    /// <summary>An observable reader without any operating-system dependency.</summary>
    /// <param name="beforeRead">Optional fake-clock update.</param>
    /// <param name="fail">Whether the reader is unavailable.</param>
    private sealed class Reader(Action? beforeRead = null, bool fail = false) : IProcessMemoryReader
    {
        /// <summary>The number of actual background reads.</summary>
        internal int Calls;
        /// <inheritdoc />
        public long? ReadWorkingSetBytes()
        {
            if (fail) throw new InvalidOperationException("Disposable reader failure.");
            beforeRead?.Invoke();
            Interlocked.Increment(ref Calls);
            return 1024;
        }
    }

    /// <summary>A provider that reports a genuine empty cache or unavailable custom diagnostics.</summary>
    /// <param name="fail">Whether its diagnostics fail.</param>
    private sealed class Cache(bool fail = false) : IResponseCacheDiagnostics
    {
        /// <inheritdoc />
        public ResponseCacheStatus GetStatus() => fail ? throw new InvalidOperationException("Disposable provider failure.") :
            new() { Provider = "Memory", LocalPayloadBytes = 0, LocalEntryCount = 0 };
    }

    /// <summary>Read-only admission for the sampler; sampling must never reserve memory through it.</summary>
    private sealed class Budget : IResponseBufferBudget
    {
        /// <inheritdoc />
        public long Used => 7;
        /// <inheritdoc />
        public IDisposable Reserve(long bytes) => throw new InvalidOperationException("Sampling must not reserve response storage.");
    }

    /// <summary>The live budget limit visible to the sampler.</summary>
    private sealed class Settings : IResponseCacheSettings
    {
        /// <inheritdoc />
        public WeirSystemSettings Current { get; } = new() { ActiveResponseBufferMaxBytes = 999 };
    }
}
