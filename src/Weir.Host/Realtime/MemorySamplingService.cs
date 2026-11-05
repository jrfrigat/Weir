using Weir.Abstractions;
using Weir.Contracts;
using Weir.Diagnostics;

namespace Weir.Host.Realtime;

/// <summary>Samples process and local-cache memory even while no requests or dashboards are active.</summary>
public sealed partial class MemorySamplingService : BackgroundService
{
    /// <summary>The fixed-size gauge history.</summary>
    private readonly MemoryMetrics _metrics;
    /// <summary>The operating system's process working-set reader.</summary>
    private readonly IProcessMemoryReader _process;
    /// <summary>Provider-independent cache and pending-store state.</summary>
    private readonly IResponseCacheDiagnostics _cache;
    /// <summary>The shared active reservation budget.</summary>
    private readonly IResponseBufferBudget _budget;
    /// <summary>Current runtime limits, read only on the background beat.</summary>
    private readonly IResponseCacheSettings _settings;
    /// <summary>The clock shared with request metrics and broadcast timers.</summary>
    private readonly TimeProvider _clock;
    /// <summary>Safe diagnostics for an unavailable custom reader/provider.</summary>
    private readonly ILogger<MemorySamplingService> _logger;
    /// <summary>Whether a sampling failure has already been reported.</summary>
    private int _reportedFailure;

    /// <summary>Creates a sampler without any request or dashboard-client dependency.</summary>
    /// <param name="metrics">The bounded gauge history.</param>
    /// <param name="process">Resident process memory reader.</param>
    /// <param name="cache">Secret-free cache diagnostics.</param>
    /// <param name="budget">Shared allocation admission.</param>
    /// <param name="settings">Live configured limits.</param>
    /// <param name="clock">Shared sampling clock.</param>
    /// <param name="logger">Safe sampling diagnostics.</param>
    public MemorySamplingService(MemoryMetrics metrics, IProcessMemoryReader process, IResponseCacheDiagnostics cache,
        IResponseBufferBudget budget, IResponseCacheSettings settings, TimeProvider clock, ILogger<MemorySamplingService> logger)
    {
        _metrics = metrics; _process = process; _cache = cache; _budget = budget; _settings = settings; _clock = clock; _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(MemorySnapshot.SampleIntervalSeconds), _clock);
        try
        {
            Capture();
            while (await timer.WaitForNextTickAsync(stoppingToken)) Capture();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Captures one observation; unknown values become gaps instead of breaking the gateway.</summary>
    internal void Capture()
    {
        long? workingSet = null;
        ResponseCacheStatus cache = new();
        try { workingSet = _process.ReadWorkingSetBytes(); }
        catch (Exception) { ReportFailure(); }
        MemoryCapacity capacity = default;
        try { capacity = _process.ReadCapacity(); }
        catch (Exception) { ReportFailure(); }
        try { cache = _cache.GetStatus(); }
        catch (Exception) { ReportFailure(); }
        long active = -1;
        long limit = -1;
        try { active = _budget.Used; limit = _settings.Current.ActiveResponseBufferMaxBytes; }
        catch (Exception) { ReportFailure(); }
        _metrics.RecordSample(workingSet, cache, active, limit, capacity);
    }

    /// <summary>Reports a reader/provider failure once without arbitrary exception or configuration data.</summary>
    private void ReportFailure()
    {
        if (Interlocked.Exchange(ref _reportedFailure, 1) == 0) LogSamplingFailure(_logger);
    }

    /// <summary>Emits a safe background monitoring warning.</summary>
    /// <param name="logger">The sampler logger.</param>
    [LoggerMessage(1931, LogLevel.Warning, "A memory observation was unavailable; the dashboard will contain gaps for unknown values.")]
    private static partial void LogSamplingFailure(ILogger logger);
}
