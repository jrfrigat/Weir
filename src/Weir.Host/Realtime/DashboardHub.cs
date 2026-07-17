using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Weir.Contracts;

namespace Weir.Host.Realtime;

/// <summary>
/// Real-time hub the admin dashboard connects to. It carries no client-callable methods - the server
/// pushes <see cref="Weir.Contracts.DashboardSnapshot"/> and connection-health updates. Any
/// authenticated admin may connect; a viewer token authenticates too.
/// <para>
/// Because both roles share this stream, every connection joins a role group on connect and the
/// broadcaster sends per group: connection-health errors carry driver text that discloses server /
/// database / login names, which the HTTP route redacts for viewers, and a push has to redact it the
/// same way or it becomes the way around that route.
/// </para>
/// </summary>
[Authorize]
public sealed class DashboardHub : Hub
{
    /// <summary>Group of connections whose token carries the Admin role, cleared to see error detail.</summary>
    public const string AdminsGroup = "admins";

    /// <summary>Group of connections that authenticated without the Admin role, held to redacted detail.</summary>
    public const string ViewersGroup = "viewers";

    private readonly DashboardClientTracker _tracker;

    /// <summary>Creates the hub over the shared client tracker.</summary>
    /// <param name="tracker">Tracks how many dashboards are connected.</param>
    public DashboardHub(DashboardClientTracker tracker) => _tracker = tracker;

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        _tracker.Increment();
        var group = Context.User?.IsInRole(AdminRoles.Admin) == true ? AdminsGroup : ViewersGroup;
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _tracker.Decrement();
        // SignalR drops the connection's group memberships on disconnect, so there is nothing to undo.
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
