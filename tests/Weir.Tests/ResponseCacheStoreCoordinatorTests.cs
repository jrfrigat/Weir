using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

/// <summary>Checks bounded storage admission independently of a provider's cancellation cooperation.</summary>
public sealed class ResponseCacheStoreCoordinatorTests
{
    /// <summary>A deadline releases orchestration while actual memory and count remain charged.</summary>
    /// <returns>The completed late-ownership check.</returns>
    [Fact]
    public async Task Deadline_RetainsActualOwnerAndAdmissionUntilLateCompletion()
    {
        var settings = new Settings(new WeirSystemSettings
        {
            ResponseCacheWriteMaxPendingCount = 1, ResponseCacheWriteMaxPendingBytes = 8,
            ResponseCacheWriteTimeoutMilliseconds = 100
        });
        var budget = new ResponseBufferBudget(settings);
        var cache = new GatedCache(cooperative: false);
        await using var stores = new ResponseCacheStoreCoordinator(cache, settings);
        var bytes = Enumerable.Repeat((byte)1, 8).ToArray();
        using var owner = new Owner(bytes, budget.Reserve(8));
        await stores.StoreAsync("key", new CachedResponse(bytes, "tag"), TimeSpan.FromMinutes(1), null, owner).WaitAsync(TimeSpan.FromSeconds(5));
        owner.Dispose();
        var timedOut = stores.GetStatus(new ResponseCacheStatus());
        Assert.Equal(1, timedOut.StoreTimeouts);
        Assert.Equal(1, timedOut.PendingWrites);
        Assert.Equal(8, timedOut.PendingWriteBytes);
        Assert.Equal(8, budget.Used);
        using var refused = new Owner([], budget.Reserve(0));
        await stores.StoreAsync("other", new CachedResponse(ReadOnlyMemory<byte>.Empty, "tag"), TimeSpan.FromMinutes(1), null, refused);
        Assert.Equal(1, cache.Calls);
        Assert.Equal(1, stores.GetStatus(new ResponseCacheStatus()).SkippedStores);
        cache.Gate.TrySetResult();
        await UntilAsync(() => stores.GetStatus(new ResponseCacheStatus()).PendingWrites == 0);
        Assert.True(cache.ReadValid);
        Assert.Equal(0, budget.Used);
        Assert.Equal(1, owner.Releases);
    }

    /// <summary>Payload and empty-entry metadata limits are independent.</summary>
    /// <param name="length">The admitted payload size, including the empty-body case.</param>
    /// <returns>The completed admission check.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task CountAndByteBounds_RefuseBeforeProviderInvocation(int length)
    {
        var settings = new Settings(new WeirSystemSettings
        {
            ResponseCacheWriteMaxPendingCount = length == 0 ? 1 : 64, ResponseCacheWriteMaxPendingBytes = 8
        });
        var cache = new GatedCache(cooperative: false);
        await using var stores = new ResponseCacheStoreCoordinator(cache, settings);
        using var firstOwner = new Owner(new byte[length]);
        using var secondOwner = new Owner(new byte[1]);
        var first = stores.StoreAsync("first", new CachedResponse(firstOwner.Bytes, "tag"), TimeSpan.FromMinutes(1), null, firstOwner);
        await stores.StoreAsync("second", new CachedResponse(secondOwner.Bytes, "tag"), TimeSpan.FromMinutes(1), null, secondOwner);
        Assert.Equal(1, cache.Calls);
        Assert.Equal(length, stores.GetStatus(new ResponseCacheStatus()).PendingWriteBytes);
        cache.Gate.TrySetResult();
        await first;
        Assert.Equal(0, stores.GetStatus(new ResponseCacheStatus()).PendingWrites);
    }

    /// <summary>Shutdown cancels a cooperative store and drains without recording a deadline failure.</summary>
    /// <returns>The completed shutdown check.</returns>
    [Fact]
    public async Task Shutdown_DrainsCooperativeStoreAndOwner()
    {
        var settings = new Settings(new WeirSystemSettings());
        var budget = new ResponseBufferBudget(settings);
        var cache = new GatedCache(cooperative: true);
        var stores = new ResponseCacheStoreCoordinator(cache, settings);
        using var owner = new Owner(new byte[8], budget.Reserve(8));
        var call = stores.StoreAsync("key", new CachedResponse(owner.Bytes, "tag"), TimeSpan.FromMinutes(1), null, owner);
        owner.Dispose();
        await stores.DisposeAsync();
        await call;
        Assert.True(cache.Canceled);
        Assert.Equal(0, stores.GetStatus(new ResponseCacheStatus()).PendingWrites);
        Assert.Equal(0, stores.GetStatus(new ResponseCacheStatus()).StoreTimeouts);
        Assert.Equal(0, budget.Used);
    }

    /// <summary>Provider errors are observed and release ownership without failing response delivery.</summary>
    /// <returns>The completed failure cleanup check.</returns>
    [Fact]
    public async Task Failure_IsObservedAndReleasesAdmission()
    {
        var settings = new Settings(new WeirSystemSettings());
        var cache = new GatedCache(cooperative: false) { Fail = true };
        cache.Gate.SetResult();
        await using var stores = new ResponseCacheStoreCoordinator(cache, settings);
        using var owner = new Owner([1]);
        await stores.StoreAsync("key", new CachedResponse(owner.Bytes, "tag"), TimeSpan.FromMinutes(1), null, owner);
        owner.Dispose();
        Assert.Equal(1, owner.Releases);
        Assert.Equal(1, stores.GetStatus(new ResponseCacheStatus()).StoreFailures);
        Assert.Equal(0, stores.GetStatus(new ResponseCacheStatus()).PendingWrites);
    }

    /// <summary>Waits for actual completion rather than making timing assertions about scheduling.</summary>
    /// <param name="condition">The observable lifecycle condition.</param>
    /// <returns>The bounded wait.</returns>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>A refcounted body that detects reuse before actual provider completion.</summary>
    /// <param name="bytes">Owned payload storage.</param>
    /// <param name="admission">Optional physical allocation reservation.</param>
    private sealed class Owner(byte[] bytes, IDisposable? admission = null) : IResponsePayloadOwner
    {
        /// <summary>The immutable payload until its last owner finishes.</summary>
        internal byte[] Bytes { get; } = bytes;
        /// <summary>Number of final releases.</summary>
        internal int Releases;
        /// <summary>Current independent owners.</summary>
        private int _references = 1;
        /// <summary>Whether this test's original ownership already ended.</summary>
        private int _originalDisposed;
        /// <inheritdoc />
        public void Retain() => Interlocked.Increment(ref _references);
        /// <inheritdoc />
        public void Dispose()
        {
            // Test callers explicitly release once; using performs no additional ownership release.
            if (Volatile.Read(ref _references) == 0) return;
            if (Interlocked.Decrement(ref _references) != 0) return;
            if (Interlocked.Exchange(ref _originalDisposed, 1) != 0) return;
            Array.Fill(Bytes, (byte)255);
            admission?.Dispose();
            Interlocked.Increment(ref Releases);
        }
    }

    /// <summary>A cache whose completion and cancellation behavior can be controlled independently.</summary>
    /// <param name="cooperative">Whether it observes its store token.</param>
    private sealed class GatedCache(bool cooperative) : IResponseCache
    {
        /// <summary>Releases the actual store operation.</summary>
        internal TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Actual provider invocations.</summary>
        internal int Calls;
        /// <summary>Whether borrowed bytes were still valid when the operation completed.</summary>
        internal bool ReadValid;
        /// <summary>Whether cancellation reached the provider.</summary>
        internal bool Canceled;
        /// <summary>Whether completion should simulate a backend failure.</summary>
        internal bool Fail { get; init; }
        /// <inheritdoc />
        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult<CachedResponse?>(null);
        /// <inheritdoc />
        public async ValueTask SetAsync(string key, CachedResponse entry, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            try { if (cooperative) await Gate.Task.WaitAsync(cancellationToken); else await Gate.Task; }
            catch (OperationCanceledException) { Canceled = true; throw; }
            ReadValid = entry.Bytes.Span.IndexOf((byte)255) < 0;
            if (Fail) throw new IOException("Disposable backend failure.");
        }
        /// <inheritdoc />
        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        /// <inheritdoc />
        public ValueTask RemoveByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>The explicit runtime policy for admission tests.</summary>
    /// <param name="settings">Configured bounds.</param>
    private sealed class Settings(WeirSystemSettings settings) : IRuntimeSettings
    {
        /// <inheritdoc />
        public WeirSystemSettings Current => settings;
        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <inheritdoc />
        public Task UpdateAsync(WeirSystemSettings update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
