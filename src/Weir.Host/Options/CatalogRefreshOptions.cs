namespace Weir.Host.Options;

/// <summary>
/// Endpoint-catalog refresh settings, bound from <c>Weir:ControlPlane</c>. In a multi-instance
/// (high-availability) deployment each instance must pick up metadata changes made through another
/// instance; a periodic reload does that. Single-node deployments can leave it disabled.
/// </summary>
public sealed class CatalogRefreshOptions
{
    /// <summary>
    /// How often, in seconds, to reload the endpoint catalog from the control-plane store. Zero
    /// disables periodic reload (recommended only for single-node deployments).
    /// </summary>
    public int ReloadSeconds { get; set; }
}
