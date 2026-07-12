using System.Reflection;
using System.Runtime.Loader;

namespace Weir.Host.Plugins;

/// <summary>
/// Loads one plugin assembly and its private dependencies in isolation, while sharing the Weir
/// contract assemblies and the framework with the host. Sharing the contract assemblies is what
/// gives a plugin's <c>IDbConnector</c> the same type identity as the host's, so the engine can use
/// it; isolating the rest lets a plugin carry its own driver version without clashing with the host.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>Creates the load context for a plugin at the given path.</summary>
    /// <param name="pluginPath">Full path to the plugin's entry assembly.</param>
    public PluginLoadContext(string pluginPath)
        : base(name: $"WeirPlugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Resolve shared assemblies (Weir contracts, Microsoft.Extensions.*, the framework) from the
        // host's default context so types are identical on both sides of the plugin boundary.
        if (IsShared(assemblyName.Name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>Whether an assembly must be shared with the host rather than loaded per plugin.</summary>
    private static bool IsShared(string? name) =>
        name is "Weir.Abstractions" or "Weir.Contracts"
        || (name?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ?? false)
        || (name?.StartsWith("System.", StringComparison.Ordinal) ?? false);
}
