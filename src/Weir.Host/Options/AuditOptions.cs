namespace Weir.Host.Options;

/// <summary>
/// Auditing settings, bound from <c>Weir:Audit</c>. Data-plane auditing is opt-in because it writes
/// one control-plane row per request; writes are queued and never block the request hot path.
/// </summary>
public sealed class AuditOptions
{
    /// <summary>Whether each data-plane call is recorded as an audit entry. Off by default.</summary>
    public bool DataPlane { get; set; }

    /// <summary>Bound of the in-memory audit queue; entries are dropped when full so requests never block.</summary>
    public int QueueCapacity { get; set; } = 10_000;
}
