using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Weir.Abstractions;

/// <summary>
/// The entry point of a Weir plugin. A plugin is an assembly that adds capabilities to the host -
/// most commonly a data-plane connector (an <see cref="IDbConnector"/>), but it may register any
/// service the engine resolves (a cache, a control-plane store, a call observer). The host discovers
/// plugin assemblies from configuration (<c>Weir:Plugins:Paths</c>), instantiates each
/// <see cref="IWeirPlugin"/> it finds, and calls <see cref="ConfigureServices"/> during startup.
/// The same type can be registered at compile time by calling <see cref="ConfigureServices"/> from a
/// custom host, so one implementation serves both the drop-in and the build-your-own models.
/// </summary>
/// <remarks>
/// Implementations must have a public parameterless constructor. Register connectors with
/// <c>TryAddEnumerable</c> so several can coexist; the engine selects one per connection by its
/// <see cref="DataConnectionDescriptor.Provider"/> matching <see cref="IDbConnector.ProviderName"/>.
/// </remarks>
public interface IWeirPlugin
{
    /// <summary>A short, stable name for the plugin, used in logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>Registers the plugin's services into the host's container.</summary>
    /// <param name="services">The service collection being built.</param>
    /// <param name="configuration">The host configuration, for reading the plugin's own settings.</param>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
