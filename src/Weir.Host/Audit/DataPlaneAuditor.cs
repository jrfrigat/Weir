using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Host.Options;

namespace Weir.Host.Audit;

/// <summary>Queues data-plane audit entries and persists them off the request hot path.</summary>
public interface IDataPlaneAuditor
{
    /// <summary>Whether data-plane auditing is switched on.</summary>
    bool Enabled { get; }

    /// <summary>Queues an audit entry. Returns immediately; the entry is dropped if the queue is full.</summary>
    /// <param name="entry">The audit entry to record.</param>
    void Enqueue(AuditEntry entry);
}

/// <summary>
/// Background <see cref="IDataPlaneAuditor"/>. Data-plane handlers enqueue entries synchronously
/// (a non-blocking channel write); a single background reader drains the queue and writes to the
/// control-plane store, so a slow or unavailable store never affects request latency.
/// </summary>
public sealed class DataPlaneAuditor : BackgroundService, IDataPlaneAuditor
{
    private readonly Channel<AuditEntry> _channel;
    private readonly IControlPlaneStore _store;
    private readonly ILogger<DataPlaneAuditor> _logger;

    /// <summary>Cumulative number of audit entries dropped because the queue was full.</summary>
    private long _dropped;

    /// <summary>The dropped count last surfaced in a warning, so only new drops are reported.</summary>
    private long _lastReportedDrops;

    /// <summary>Creates the auditor from the control-plane store, options and a logger.</summary>
    /// <param name="store">The control-plane store audit entries are written to.</param>
    /// <param name="options">The audit options.</param>
    /// <param name="logger">The logger.</param>
    public DataPlaneAuditor(IControlPlaneStore store, IOptions<AuditOptions> options, ILogger<DataPlaneAuditor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _logger = logger;
        Enabled = options.Value.DataPlane;
        var capacity = options.Value.QueueCapacity <= 0 ? 10_000 : options.Value.QueueCapacity;
        _channel = Channel.CreateBounded<AuditEntry>(
            new BoundedChannelOptions(capacity)
            {
                // Never block the request thread: if the writer outruns the store, drop the newest entry.
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            },
            // Count drops so the loss is observable (reported off the hot path) instead of silent.
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>Cumulative number of audit entries dropped because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public bool Enabled { get; }

    /// <inheritdoc />
    public void Enqueue(AuditEntry entry)
    {
        if (Enabled)
        {
            _channel.Writer.TryWrite(entry);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _store.AppendAuditAsync(entry, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.AuditWriteFailed(_logger, ex);
                }

                // Surface any new drops in one warning per batch of 100, off the request hot path.
                var dropped = Interlocked.Read(ref _dropped);
                if (dropped - _lastReportedDrops >= 100)
                {
                    Log.AuditEntriesDropped(_logger, dropped);
                    _lastReportedDrops = dropped;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
