using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Weir.Abstractions;

namespace Weir.Connectors.MySql;

/// <summary>
/// Plugin entry point for the MySQL connector. When this assembly is listed in
/// <c>Weir:Plugins:Paths</c>, the host loads it and calls <see cref="ConfigureServices"/>, which
/// registers the connector - the same registration a custom host would call via <c>AddWeirMySql</c>.
/// </summary>
public sealed class MySqlPlugin : IWeirPlugin
{
    /// <inheritdoc />
    public string Name => "Weir.Connectors.MySql";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddWeirMySql();
    }
}
