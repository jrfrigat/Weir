using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Host.RequestLogging;

/// <summary>Queues data-plane request-log entries and persists them off the request hot path.</summary>
public interface IRequestLogSink
{
    /// <summary>Queues an entry. Returns immediately; the entry is dropped if the queue is full.</summary>
    /// <param name="entry">The request-log entry to record.</param>
    void Enqueue(RequestLogEntry entry);
}

/// <summary>
/// Background <see cref="IRequestLogSink"/>. The request-log observer enqueues entries synchronously
/// (a non-blocking channel write); a single background reader drains the queue and writes to the
/// control-plane store, so a slow or unavailable store never affects request latency. The queue is
/// bounded and drops the newest entry when full (counted, not silent), like the audit path.
/// </summary>
public sealed class RequestLogSink : BackgroundService, IRequestLogSink
{
    private readonly Channel<RequestLogEntry> _channel;
    private readonly IControlPlaneStore _store;
    private readonly ILogger<RequestLogSink> _logger;

    /// <summary>Cumulative number of entries dropped because the queue was full.</summary>
    private long _dropped;

    /// <summary>The dropped count last surfaced in a warning, so only new drops are reported.</summary>
    private long _lastReportedDrops;

    /// <summary>Creates the sink from the control-plane store and a logger.</summary>
    /// <param name="store">The control-plane store entries are written to.</param>
    /// <param name="logger">The logger.</param>
    public RequestLogSink(IControlPlaneStore store, ILogger<RequestLogSink> logger)
    {
        _store = store;
        _logger = logger;
        _channel = Channel.CreateBounded<RequestLogEntry>(
            new BoundedChannelOptions(10_000)
            {
                // Never block the request thread: if the writer outruns the store, drop the newest entry.
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <inheritdoc />
    public void Enqueue(RequestLogEntry entry)
    {
        // DropWrite makes TryWrite succeed even when it discards, reporting that through the callback
        // above - so a false here means the channel is closed, i.e. shutdown began. Count it, or the
        // last entries of a run would be the one kind of loss the counter cannot see.
        if (!_channel.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Stops accepting entries and lets the reader finish the queue before the host goes away, the same
    /// way <c>DataPlaneAuditor</c> does - two sinks side by side must not behave differently at
    /// shutdown. Without it every graceful restart discarded the tail of the request log.
    /// </summary>
    /// <param name="cancellationToken">The host's shutdown token; it bounds the drain.</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Completing the writer is what ends the read loop, which otherwise waits for more entries.
        // This is not optional: ExecuteAsync deliberately reads without the stopping token, so without
        // the completion here shutdown would hang until the host's timeout fired.
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Deliberately not reading with stoppingToken: that would abandon the queue the moment shutdown
        // starts. The loop ends when StopAsync completes the writer and the backlog is drained, and the
        // host's shutdown timeout still bounds how long that may take.
        await foreach (var entry in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                await _store.AppendRequestLogAsync(entry, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.RequestLogWriteFailed(_logger, ex);
            }

            var dropped = Interlocked.Read(ref _dropped);
            if (dropped - _lastReportedDrops >= 100)
            {
                Log.RequestLogEntriesDropped(_logger, dropped);
                _lastReportedDrops = dropped;
            }
        }
    }
}
