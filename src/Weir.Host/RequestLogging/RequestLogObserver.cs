using System.Collections.Concurrent;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;

namespace Weir.Host.RequestLogging;

/// <summary>
/// A call observer that records every data-plane call in the request log (when logging is enabled),
/// and flags a call "slow" when its duration exceeds its endpoint's rolling average by the configured
/// threshold percentage. It keeps a lightweight per-endpoint running average of successful, non-cached
/// calls so the slow decision needs no external stats source; that average is also stored on each entry
/// for context. Parameter and result capture is done upstream in the engine (opt-in per endpoint); this
/// observer only reads what the engine already populated on the call context.
/// </summary>
public sealed class RequestLogObserver : IWeirCallObserver
{
    /// <summary>Minimum samples before an endpoint's average is trusted enough to flag slow calls.</summary>
    private const int MinSamplesForSlow = 5;

    private readonly IRequestLogSink _sink;
    private readonly IRuntimeSettings _settings;
    private readonly TimeProvider _clock;

    /// <summary>Per-endpoint running average of successful, non-cached call durations.</summary>
    private readonly ConcurrentDictionary<Guid, RunningAverage> _averages = new();

    /// <summary>Creates the observer from the sink, runtime settings and a clock.</summary>
    /// <param name="sink">The request-log sink entries are queued to.</param>
    /// <param name="settings">Runtime settings (global switch and default slow threshold).</param>
    /// <param name="clock">Clock for entry timestamps.</param>
    public RequestLogObserver(IRequestLogSink sink, IRuntimeSettings settings, TimeProvider clock)
    {
        _sink = sink;
        _settings = settings;
        _clock = clock;
    }

    /// <inheritdoc />
    public ValueTask OnStartedAsync(WeirCallContext context) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnCompletedAsync(WeirCallContext context)
    {
        Record(context);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFailedAsync(WeirCallContext context, Exception exception)
    {
        Record(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>Builds and enqueues a request-log entry for the call, computing the slow flag.</summary>
    /// <param name="context">The completed (or failed) call context.</param>
    private void Record(WeirCallContext context)
    {
        var settings = _settings.Current;
        if (!settings.RequestLogEnabled || !context.LogRequests)
        {
            return;
        }

        var failed = string.Equals(context.Outcome, OutcomeCodes.Error, StringComparison.Ordinal) || context.StatusCode >= 400;

        // Only successful, non-cached executions inform the "typical" duration and can be flagged slow.
        double? average = null;
        var slow = false;
        if (context.EndpointId != Guid.Empty && !context.CacheHit && !failed)
        {
            var stats = _averages.GetOrAdd(context.EndpointId, static _ => new RunningAverage());
            var (priorAverage, priorCount) = stats.Snapshot();
            if (priorCount >= MinSamplesForSlow)
            {
                average = priorAverage;
                var thresholdPercent = context.SlowThresholdPercent ?? settings.SlowRequestThresholdPercent;
                if (thresholdPercent > 0 && context.DurationMs > priorAverage * (1 + thresholdPercent / 100.0))
                {
                    slow = true;
                }
            }

            stats.Add(context.DurationMs);
        }

        _sink.Enqueue(new RequestLogEntry
        {
            Timestamp = _clock.GetUtcNow(),
            EndpointId = context.EndpointId == Guid.Empty ? null : context.EndpointId,
            Route = context.Route,
            HttpMethod = context.HttpMethod,
            ConnectionName = context.ConnectionName,
            ObjectName = context.ObjectName,
            StatusCode = context.StatusCode,
            Outcome = context.Outcome,
            DurationMs = context.DurationMs,
            DbDurationMs = context.DbDurationMs > 0 ? context.DbDurationMs : null,
            RowsReturned = context.CacheHit ? null : context.RowsReturned,
            CacheHit = context.CacheHit,
            Slow = slow,
            AverageMs = average,
            ApiKeyPrefix = context.ApiKeyPrefix,
            Parameters = context.CapturedParameters,
            Result = context.CapturedResult,
            Error = context.Error,
        });
    }

    /// <summary>A thread-safe running mean of call durations for one endpoint.</summary>
    private sealed class RunningAverage
    {
        private readonly Lock _gate = new();
        private long _count;
        private double _sum;

        /// <summary>Returns the current average and sample count.</summary>
        /// <returns>The mean duration (zero when empty) and the number of samples.</returns>
        public (double Average, long Count) Snapshot()
        {
            lock (_gate)
            {
                return (_count == 0 ? 0 : _sum / _count, _count);
            }
        }

        /// <summary>Adds a sample to the running mean.</summary>
        /// <param name="durationMs">The call's duration in milliseconds.</param>
        public void Add(double durationMs)
        {
            lock (_gate)
            {
                _count++;
                _sum += durationMs;
            }
        }
    }
}
