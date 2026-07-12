using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Weir.Host.Realtime;

/// <summary>
/// Real-time hub the admin dashboard connects to. It carries no client-callable methods - the server
/// pushes <see cref="Weir.Contracts.DashboardSnapshot"/> and connection-health updates. Authenticated
/// admins only; a viewer token authenticates but sees the same read-only stream.
/// </summary>
[Authorize]
public sealed class DashboardHub : Hub
{
    private readonly DashboardClientTracker _tracker;

    /// <summary>Creates the hub over the shared client tracker.</summary>
    /// <param name="tracker">Tracks how many dashboards are connected.</param>
    public DashboardHub(DashboardClientTracker tracker) => _tracker = tracker;

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        _tracker.Increment();
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _tracker.Decrement();
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Counts connected dashboard clients so the broadcaster can skip work when none are watching.</summary>
public sealed class DashboardClientTracker
{
    private int _count;

    /// <summary>Whether at least one dashboard is currently connected.</summary>
    public bool HasClients => Volatile.Read(ref _count) > 0;

    /// <summary>Records a new connection.</summary>
    public void Increment() => Interlocked.Increment(ref _count);

    /// <summary>Records a dropped connection.</summary>
    public void Decrement() => Interlocked.Decrement(ref _count);
}
