using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Weir.Contracts;

namespace Weir.Host.Realtime;

/// <summary>
/// Where a stored audit entry is handed so live consumers can see it: the host pushes it to the dashboard
/// hub, and a component that only records audit rows takes a no-op implementation. Extracted so the audit
/// writer and the admin API depend on the act of publishing rather than on SignalR itself.
/// </summary>
public interface IAuditPublisher
{
    /// <summary>Hands one stored entry to the live consumers.</summary>
    /// <param name="entry">The stored audit entry.</param>
    /// <returns>A task that completes when the entry has been handed over.</returns>
    Task PublishAsync(AuditEntry entry);
}

/// <summary>
/// Pushes a newly stored audit entry to connected dashboards over <see cref="DashboardHub"/>, which is
/// what turns the admin audit page into a live feed instead of one the operator has to refresh.
/// <para>
/// Publishing is best-effort and must never affect the audit write that produced the entry: with no
/// dashboard connected the call returns at once, and a delivery that fails is logged and swallowed
/// rather than thrown back into the audit writer.
/// </para>
/// </summary>
public sealed class AuditPublisher : IAuditPublisher
{
    /// <summary>Name of the hub event that carries one audit entry.</summary>
    public const string EventName = "audit";

    private readonly IHubContext<DashboardHub> _hub;
    private readonly DashboardClientTracker _tracker;
    private readonly ILogger<AuditPublisher> _logger;

    /// <summary>Creates the publisher from its collaborators.</summary>
    /// <param name="hub">The dashboard hub context.</param>
    /// <param name="tracker">Says whether any dashboard is connected.</param>
    /// <param name="logger">Logger for a delivery that failed.</param>
    public AuditPublisher(IHubContext<DashboardHub> hub, DashboardClientTracker tracker, ILogger<AuditPublisher> logger)
    {
        _hub = hub;
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>Hands one stored entry to every connected dashboard, if any is connected.</summary>
    /// <param name="entry">The stored audit entry.</param>
    /// <returns>A task that completes when the entry has been handed to SignalR.</returns>
    public async Task PublishAsync(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_tracker.HasClients)
        {
            return;
        }

        try
        {
            await _hub.Clients.All.SendAsync(EventName, entry);
        }
        catch (Exception ex)
        {
            // The entry is already stored. A feed that could not be delivered is not a reason to fail the
            // audit write that produced it, and the next entry - or a page reload - carries the same news.
            Log.AuditPublishFailed(_logger, ex);
        }
    }
}
