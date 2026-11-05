using Microsoft.IO;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Weir.Host.Grpc;
using Xunit;

namespace Weir.Tests;

/// <summary>Verifies allocation admission and deterministic reservation cleanup.</summary>
public sealed class ResponseBufferBudgetTests
{
    /// <summary>Publishing a fill into cache does not double-charge its physical array during handover.</summary>
    /// <returns>The completed shared-allocation check.</returns>
    [Fact]
    public async Task FillAndCacheBorrow_ShareOnePhysicalReservation()
    {
        var settings = new MutableSettings(new WeirSystemSettings { ActiveResponseBufferMaxBytes = 8 });
        var budget = new ResponseBufferBudget(settings);
        var admission = budget.Reserve(8);
        var payload = new byte[8];
        using var fill = budget.AdoptPayload(payload, admission);
        using var cache = new MemoryResponseCache(settings, TimeProvider.System, budget);
        await cache.SetAsync("route:key", new CachedResponse(payload, "tag"), TimeSpan.FromMinutes(1));
        using var hit = (await cache.LookupAsync("route:key", "route:")).Entry!;
        Assert.Equal(8, budget.Used);
        fill.Dispose();
        Assert.Equal(8, budget.Used);
        hit.Dispose();
        Assert.Equal(0, budget.Used);
    }

    /// <summary>Borrowers share one reservation and keep evicted bytes visible to admission.</summary>
    /// <returns>The completed eviction-lifetime check.</returns>
    [Fact]
    public async Task BorrowedCachePayload_RemainsChargedAfterEviction()
    {
        var settings = new MutableSettings(new WeirSystemSettings { ActiveResponseBufferMaxBytes = 8 });
        var budget = new ResponseBufferBudget(settings);
        using var cache = new MemoryResponseCache(settings, TimeProvider.System, budget);
        await cache.SetAsync("route:key", new CachedResponse(new byte[8], "tag"), TimeSpan.FromMinutes(1));
        using var first = (await cache.LookupAsync("route:key", "route:")).Entry!;
        using var second = (await cache.LookupAsync("route:key", "route:")).Entry!;
        Assert.Equal(8, budget.Used);
        await cache.RemoveByPrefixAsync("route:");
        Assert.Equal(0, cache.GetStorageSnapshot().PayloadBytes);
        Assert.Equal(8, budget.Used);
        await cache.SetAsync("route:key", new CachedResponse(new byte[8], "new"), TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<WeirBufferCapacityException>(() => cache.LookupAsync("route:key", "route:").AsTask());
        first.Dispose();
        Assert.Equal(8, budget.Used);
        second.Dispose();
        second.Dispose();
        Assert.Equal(0, budget.Used);
        using var current = (await cache.LookupAsync("route:key", "route:")).Entry!;
        Assert.Equal("new", current.Response.ETag);
        Assert.Equal(8, budget.Used);
    }

    /// <summary>Unary gRPC growth charges old and new arrays together before copying.</summary>
    /// <returns>The completed admission check.</returns>
    [Fact]
    public async Task UnaryTransport_CannotBypassSharedBudget()
    {
        var budget = new ResponseBufferBudget(new MutableSettings(new WeirSystemSettings { ActiveResponseBufferMaxBytes = 32768 }));
        await using (var output = new BufferedTransportStream(budget, 32768))
        {
            await output.WriteAsync(new byte[16384]);
            Assert.Equal(16384, budget.Used);
            await Assert.ThrowsAsync<WeirBufferCapacityException>(() => output.WriteAsync(new byte[1]).AsTask());
            Assert.Equal(16384, output.BytesWritten);
            Assert.Equal(16384, budget.Used);
        }
        Assert.Equal(0, budget.Used);
    }

    /// <summary>The unary hard bound applies even when the engine streams into its transport.</summary>
    /// <returns>The completed body-limit check.</returns>
    [Fact]
    public async Task UnaryTransport_RejectsOversizeBeforeAllocation()
    {
        var budget = new ResponseBufferBudget(new MutableSettings(new WeirSystemSettings()));
        await using var output = new BufferedTransportStream(budget, 10);
        await Assert.ThrowsAsync<WeirBufferCapacityException>(() => output.WriteAsync(new byte[11]).AsTask());
        Assert.Equal(0, budget.Used);
        Assert.Equal(0, output.BytesWritten);
    }

    /// <summary>Growth is refused before a pooled stream can acquire another block.</summary>
    [Fact]
    public void Growth_ReservesBeforeAllocation_AndDisposeReleases()
    {
        var settings = new MutableSettings(new WeirSystemSettings { ActiveResponseBufferMaxBytes = 131072 });
        var budget = new ResponseBufferBudget(settings);
        var manager = new RecyclableMemoryStreamManager(new() { BlockSize = 131072 });
        using (var buffer = new BudgetedResponseBuffer(manager, budget, 0))
        {
            buffer.Write(new byte[131072]);
            Assert.Throws<WeirBufferCapacityException>(() => buffer.WriteByte(1));
            Assert.Equal(131072, buffer.Length);
            Assert.Equal(131072, budget.Used);
        }
        Assert.Equal(0, budget.Used);
    }

    /// <summary>A copy is charged beside its source and failed admission leaves accounting unchanged.</summary>
    [Fact]
    public void CopyReservation_AndRuntimeReduction_AreAccounted()
    {
        var settings = new MutableSettings(new WeirSystemSettings { ActiveResponseBufferMaxBytes = 262144 });
        var budget = new ResponseBufferBudget(settings);
        using var first = budget.Reserve(131072);
        using var copy = budget.Reserve(65536);
        Assert.Equal(196608, budget.Used);
        settings.Current = settings.Current with { ActiveResponseBufferMaxBytes = 65536 };
        Assert.Throws<WeirBufferCapacityException>(() => budget.Reserve(1));
        Assert.Equal(196608, budget.Used);
        first.Dispose();
        copy.Dispose();
        Assert.Equal(0, budget.Used);
        using var next = budget.Reserve(65536);
    }

    /// <summary>Distinct concurrent fills cannot reserve beyond the shared byte budget.</summary>
    [Fact]
    public void ConcurrentReservations_StayBounded()
    {
        var budget = new ResponseBufferBudget(new MutableSettings(new WeirSystemSettings { ActiveResponseBufferMaxBytes = 131072 }));
        var leases = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        Parallel.For(0, 100, _ =>
        {
            try { leases.Add(budget.Reserve(131072)); }
            catch (WeirBufferCapacityException) { }
        });
        Assert.Single(leases);
        Assert.Equal(131072, budget.Used);
        foreach (var lease in leases) lease.Dispose();
        Assert.Equal(0, budget.Used);
    }

    /// <summary>A live configuration for admission tests.</summary>
    /// <param name="settings">The initial limits.</param>
    private sealed class MutableSettings(WeirSystemSettings settings) : IRuntimeSettings
    {
        /// <inheritdoc />
        public WeirSystemSettings Current { get; set; } = settings;
        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <inheritdoc />
        public Task UpdateAsync(WeirSystemSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }
}
