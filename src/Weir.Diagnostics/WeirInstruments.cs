using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Weir.Diagnostics;

/// <summary>
/// The shared OpenTelemetry primitives for Weir: a single <see cref="ActivitySource"/> for tracing
/// and a single <see cref="Meter"/> with the request instruments. The host subscribes to these by
/// name ("Weir") when configuring OpenTelemetry metrics and tracing.
/// </summary>
public static class WeirInstruments
{
    /// <summary>The instrumentation name used for both the activity source and the meter.</summary>
    public const string Name = "Weir";

    /// <summary>Activity source that emits one span per data-plane call.</summary>
    public static readonly ActivitySource ActivitySource = new(Name);

    /// <summary>Meter that owns all Weir request instruments.</summary>
    public static readonly Meter Meter = new(Name);

    /// <summary>Counts data-plane requests, tagged with route and outcome.</summary>
    public static readonly Counter<long> Requests =
        Meter.CreateCounter<long>("weir.requests", unit: "{request}", description: "Number of data-plane requests.");

    /// <summary>Total request duration in milliseconds.</summary>
    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("weir.request.duration", unit: "ms", description: "End-to-end request duration.");

    /// <summary>Time spent inside the database call, in milliseconds.</summary>
    public static readonly Histogram<double> DbDuration =
        Meter.CreateHistogram<double>("weir.db.duration", unit: "ms", description: "Database execution duration.");

    /// <summary>Counts responses served from the cache.</summary>
    public static readonly Counter<long> CacheHits =
        Meter.CreateCounter<long>("weir.cache.hits", unit: "{hit}", description: "Responses served from cache.");

    /// <summary>Counts responses that missed the cache.</summary>
    public static readonly Counter<long> CacheMisses =
        Meter.CreateCounter<long>("weir.cache.misses", unit: "{miss}", description: "Responses that missed the cache.");

    /// <summary>Number of rows returned across all result sets.</summary>
    public static readonly Histogram<long> Rows =
        Meter.CreateHistogram<long>("weir.rows", unit: "{row}", description: "Rows returned per request.");

    /// <summary>Currently in-flight requests.</summary>
    public static readonly UpDownCounter<long> ActiveRequests =
        Meter.CreateUpDownCounter<long>("weir.active_requests", unit: "{request}", description: "In-flight requests.");

    /// <summary>Counts classified database errors, tagged with route and category (timeout/deadlock/...).</summary>
    public static readonly Counter<long> DbErrors =
        Meter.CreateCounter<long>("weir.db.errors", unit: "{error}", description: "Classified database errors by category.");
}
