using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Abstractions;
using Weir.Host.Options;

namespace Weir.Host.Plugins;

/// <summary>Discovers and activates configured plugins during host startup.</summary>
public static class PluginLoader
{
    /// <summary>
    /// Loads each plugin assembly listed in <c>Weir:Plugins:Paths</c>, instantiates every
    /// <see cref="IWeirPlugin"/> it contains, and lets it register services. Failures are logged and
    /// skipped so one bad plugin cannot stop the host from starting.
    /// </summary>
    /// <param name="services">The service collection being built.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="logger">Logger for load progress and failures.</param>
    public static void LoadConfiguredPlugins(IServiceCollection services, IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection("Weir:Plugins").Get<WeirPluginOptions>() ?? new WeirPluginOptions();
        foreach (var path in options.Paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                LoadOne(path, services, configuration, logger);
            }
            catch (Exception ex)
            {
                Log.PluginLoadFailed(logger, ex, path);
            }
        }
    }

    /// <summary>Loads one plugin assembly and activates every plugin type it declares.</summary>
    private static void LoadOne(string path, IServiceCollection services, IConfiguration configuration, ILogger logger)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            Log.PluginNotFound(logger, fullPath);
            return;
        }

        var context = new PluginLoadContext(fullPath);
        var assembly = context.LoadFromAssemblyPath(fullPath);

        // GetTypes throws ReflectionTypeLoadException if any type fails to load (e.g. a missing optional
        // dependency), which would discard the whole assembly. Recover the types that did load instead.
        Type?[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types;
        }

        var pluginTypes = allTypes
            .Where(t => t is not null && typeof(IWeirPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => t!)
            .ToList();

        if (pluginTypes.Count == 0)
        {
            Log.PluginNoEntryPoint(logger, fullPath);
            return;
        }

        foreach (var type in pluginTypes)
        {
            var plugin = (IWeirPlugin)Activator.CreateInstance(type)!;
            plugin.ConfigureServices(services, configuration);
            Log.PluginLoaded(logger, plugin.Name, fullPath);
        }
    }
}
