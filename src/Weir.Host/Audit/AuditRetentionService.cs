using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Abstractions;
using Weir.Core;

namespace Weir.Host.Audit;

/// <summary>
/// Periodically deletes audit entries older than the runtime <c>AuditRetentionDays</c> setting, so the
/// audit table cannot grow without bound. A retention of zero (the default) disables pruning.
/// </summary>
public sealed class AuditRetentionService : BackgroundService
{
    /// <summary>How often the retention sweep runs.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IControlPlaneStore _store;
    private readonly IRuntimeSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditRetentionService> _logger;

    /// <summary>Creates the retention service from its collaborators.</summary>
    /// <param name="store">The control-plane store to prune.</param>
    /// <param name="settings">Runtime settings carrying the retention window.</param>
    /// <param name="clock">Clock for the cut-off and the timer.</param>
    /// <param name="logger">The logger.</param>
    public AuditRetentionService(
        IControlPlaneStore store, IRuntimeSettings settings, TimeProvider clock, ILogger<AuditRetentionService> logger)
    {
        _store = store;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _clock);
        try
        {
            while (true)
            {
                await PruneOnceAsync(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Runs one prune pass: audit history (when retention is enabled) and stale login-throttle rows.</summary>
    /// <param name="stoppingToken">Cancellation token.</param>
    private async Task PruneOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var now = _clock.GetUtcNow();
            var days = _settings.Current.AuditRetentionDays;
            if (days > 0)
            {
                var deleted = await _store.PruneAuditAsync(now.AddDays(-days), stoppingToken);
                if (deleted > 0)
                {
                    Log.AuditPruned(_logger, deleted, days);
                }
            }

            var requestLogDays = _settings.Current.RequestLogRetentionDays;
            if (requestLogDays > 0)
            {
                var deleted = await _store.PruneRequestLogAsync(now.AddDays(-requestLogDays), stoppingToken);
                if (deleted > 0)
                {
                    Log.RequestLogPruned(_logger, deleted, requestLogDays);
                }
            }

            // Bound the login-throttle table: drop rows that are neither locked nor active in the last day.
            await _store.PruneLoginThrottleAsync(now.AddDays(-1), now, stoppingToken);

            // Bound the refresh-token table: drop expired and revoked tokens.
            await _store.PruneRefreshTokensAsync(now, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AuditPruneFailed(_logger, ex);
        }
    }
}
