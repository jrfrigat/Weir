using Weir.Core;
using Xunit;

namespace Weir.Tests;

// Exercises the per-connection bulkhead (fail-fast concurrency limit) and the circuit breaker
// (trip after consecutive failures, close on a probe success).
public class ConnectionGuardTests
{
    [Fact]
    public void Bulkhead_Rejects_Beyond_Limit_And_Recovers_On_Release()
    {
        using var guard = new ConnectionGuard();

        var first = guard.Enter(1);
        // The single slot is taken; a second entry must fail fast.
        Assert.Throws<WeirConnectionUnavailableException>(() => guard.Enter(1));

        first.Dispose(); // release the slot
        using var second = guard.Enter(1); // now succeeds
    }

    [Fact]
    public void Zero_Limit_Means_Unlimited()
    {
        using var guard = new ConnectionGuard();
        // No exception no matter how many permits are outstanding.
        var a = guard.Enter(0);
        var b = guard.Enter(0);
        var c = guard.Enter(0);
        a.Dispose();
        b.Dispose();
        c.Dispose();
    }

    [Fact]
    public void Breaker_Trips_After_Threshold_And_Blocks()
    {
        using var guard = new ConnectionGuard();
        const int threshold = 3;

        // Below the threshold the breaker stays closed.
        guard.RecordFailure(threshold, resetSeconds: 60);
        guard.RecordFailure(threshold, resetSeconds: 60);
        guard.EnsureClosed(threshold); // still closed

        guard.RecordFailure(threshold, resetSeconds: 60); // third failure trips it
        Assert.True(guard.IsOpen);
        Assert.Throws<WeirConnectionUnavailableException>(() => guard.EnsureClosed(threshold));
    }

    [Fact]
    public void Success_Resets_Failure_Run()
    {
        using var guard = new ConnectionGuard();
        const int threshold = 2;

        guard.RecordFailure(threshold, resetSeconds: 60);
        guard.RecordSuccess(); // clears the run
        guard.RecordFailure(threshold, resetSeconds: 60); // only one failure since reset
        guard.EnsureClosed(threshold); // still closed
        Assert.False(guard.IsOpen);
    }

    [Fact]
    public void Probe_Allowed_After_Reset_Window_Then_Success_Closes()
    {
        using var guard = new ConnectionGuard();
        const int threshold = 1;

        // reset of 0 means the open window elapses immediately, so a probe is allowed on the next call.
        guard.RecordFailure(threshold, resetSeconds: 0);
        guard.EnsureClosed(threshold); // probe allowed (does not throw)
        guard.RecordSuccess();
        Assert.False(guard.IsOpen);
    }

    [Fact]
    public void Disabled_Breaker_Never_Trips()
    {
        using var guard = new ConnectionGuard();
        for (var i = 0; i < 100; i++)
        {
            guard.RecordFailure(failureThreshold: 0, resetSeconds: 60);
        }

        guard.EnsureClosed(0); // disabled: no throw
        Assert.False(guard.IsOpen);
    }
}
