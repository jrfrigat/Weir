using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Core;
using Weir.Host.Options;

namespace Weir.Host;

/// <summary>
/// Periodically reloads the endpoint catalog from the control-plane store so that, in a
/// multi-instance deployment, each instance picks up metadata changes made through another instance.
/// Disabled when the configured interval is zero (single-node deployments).
/// </summary>
public sealed class CatalogRefreshService : BackgroundService
{
    private readonly IEndpointCatalog _catalog;
    private readonly ILogger<CatalogRefreshService> _logger;
    private readonly TimeSpan _interval;

    /// <summary>Creates the refresh service from the catalog, options and a logger.</summary>
    /// <param name="catalog">The endpoint catalog to reload.</param>
    /// <param name="options">The refresh options.</param>
    /// <param name="logger">The logger.</param>
    public CatalogRefreshService(IEndpointCatalog catalog, IOptions<CatalogRefreshOptions> options, ILogger<CatalogRefreshService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _catalog = catalog;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Max(0, options.Value.ReloadSeconds));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _catalog.LoadAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.CatalogReloadFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
