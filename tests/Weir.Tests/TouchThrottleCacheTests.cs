using Weir.Abstractions;
using Xunit;

namespace Weir.Tests;

// The throttle sits on the authenticated hot path in all three control-plane stores, and its whole job is
// to keep a write off it. What it must not do in exchange is remember every key that has ever presented
// itself: that is a slow leak in a process that runs for months.
public class TouchThrottleCacheTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_Touch_Persists()
    {
        var cache = new TouchThrottleCache(Window);
        Assert.True(cache.ShouldPersist(Guid.NewGuid(), Start));
    }

    [Fact]
    public void Second_Touch_Inside_The_Window_Is_Dropped()
    {
        var cache = new TouchThrottleCache(Window);
        var id = Guid.NewGuid();

        Assert.True(cache.ShouldPersist(id, Start));
        Assert.False(cache.ShouldPersist(id, Start + TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void Touch_After_The_Window_Persists_Again()
    {
        var cache = new TouchThrottleCache(Window);
        var id = Guid.NewGuid();

        Assert.True(cache.ShouldPersist(id, Start));
        Assert.True(cache.ShouldPersist(id, Start + Window));
    }

    [Fact]
    public void Keys_Are_Throttled_Independently()
    {
        var cache = new TouchThrottleCache(Window);

        Assert.True(cache.ShouldPersist(Guid.NewGuid(), Start));
        Assert.True(cache.ShouldPersist(Guid.NewGuid(), Start));
    }

    [Fact]
    public void Forget_Lets_The_Next_Touch_Persist()
    {
        var cache = new TouchThrottleCache(Window);
        var id = Guid.NewGuid();

        Assert.True(cache.ShouldPersist(id, Start));
        cache.Forget(id);
        Assert.True(cache.ShouldPersist(id, Start + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Stale_Entries_Are_Swept_Once_The_Map_Grows()
    {
        // One-shot keys: each is touched once and never again. Without pruning the map keeps all of them
        // for the life of the process, which is the leak this guards.
        var cache = new TouchThrottleCache(Window);
        for (var i = 0; i < TouchThrottleCache.PruneThreshold + 1; i++)
        {
            cache.ShouldPersist(Guid.NewGuid(), Start);
        }

        Assert.True(cache.Count > TouchThrottleCache.PruneThreshold);

        // A touch far enough past the window that every entry above is beyond reuse triggers the sweep.
        cache.ShouldPersist(Guid.NewGuid(), Start + TimeSpan.FromMinutes(10));

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void A_Sweep_Keeps_Entries_That_Can_Still_Suppress_A_Write()
    {
        // Pruning must not become a second, sloppier throttle: an entry inside the window still has to
        // drop the next touch for its key.
        var cache = new TouchThrottleCache(Window);
        var live = Guid.NewGuid();
        cache.ShouldPersist(live, Start + TimeSpan.FromMinutes(10));

        for (var i = 0; i < TouchThrottleCache.PruneThreshold + 1; i++)
        {
            cache.ShouldPersist(Guid.NewGuid(), Start);
        }

        cache.ShouldPersist(Guid.NewGuid(), Start + TimeSpan.FromMinutes(10));

        Assert.False(cache.ShouldPersist(live, Start + TimeSpan.FromMinutes(10)));
    }
}
