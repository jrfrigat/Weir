namespace Weir.Host.Options;

/// <summary>
/// Plugin settings, bound from <c>Weir:Plugins</c>. Plugins let an operator extend a running Weir
/// image (for example with a third-party database connector) without rebuilding it: mount the
/// plugin assemblies and list their paths here. Loading a plugin runs third-party code in-process,
/// so only reference assemblies you trust.
/// </summary>
public sealed class WeirPluginOptions
{
    /// <summary>Absolute or relative paths to plugin assemblies (the entry <c>.dll</c> of each plugin).</summary>
    public IList<string> Paths { get; set; } = [];
}
