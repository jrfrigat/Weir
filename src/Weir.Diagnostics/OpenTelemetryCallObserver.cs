using System.Diagnostics;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Diagnostics;

/// <summary>
/// An <see cref="IWeirCallObserver"/> that emits OpenTelemetry signals: one span per call via
/// <see cref="WeirInstruments.ActivitySource"/> and measurements on the request instruments. Export
/// (OTLP, Aspire, etc.) is configured by the host; this observer only produces the data.
/// </summary>
public sealed class OpenTelemetryCallObserver : IWeirCallObserver
{
    private const string ActivityItemKey = "weir.otel.activity";

    /// <inheritdoc />
    public ValueTask OnStartedAsync(WeirCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        WeirInstruments.ActiveRequests.Add(1, new KeyValuePair<string, object?>("weir.route", context.Route));

        var activity = WeirInstruments.ActivitySource.StartActivity("weir.call", ActivityKind.Server);
        if (activity is not null)
        {
            activity.SetTag("weir.route", context.Route);
            activity.SetTag("http.request.method", context.HttpMethod);
            activity.SetTag("weir.connection", context.ConnectionName);
            activity.SetTag("db.operation.name", context.ObjectName);
            context.Items[ActivityItemKey] = activity;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnCompletedAsync(WeirCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Record(context, exception: null);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFailedAsync(WeirCallContext context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        Record(context, exception);
        return ValueTask.CompletedTask;
    }

    /// <summary>Records instrument measurements and finalizes the span for a completed call.</summary>
    /// <param name="context">The call context.</param>
    /// <param name="exception">The failure, or null on success.</param>
    private static void Record(WeirCallContext context, Exception? exception)
    {
        WeirInstruments.ActiveRequests.Add(-1, new KeyValuePair<string, object?>("weir.route", context.Route));

        var tags = new TagList
        {
            { "weir.route", context.Route },
            { "weir.outcome", context.Outcome },
        };
        WeirInstruments.Requests.Add(1, tags);
        WeirInstruments.RequestDuration.Record(context.DurationMs, tags);

        var routeTag = new TagList { { "weir.route", context.Route } };
        if (context.DbDurationMs > 0)
        {
            WeirInstruments.DbDuration.Record(context.DbDurationMs, routeTag);
        }

        WeirInstruments.Rows.Record(context.RowsReturned, routeTag);
        if (context.CacheHit)
        {
            WeirInstruments.CacheHits.Add(1, routeTag);
        }
        else
        {
            WeirInstruments.CacheMisses.Add(1, routeTag);
        }

        if (context.DbError != DbErrorCategory.None)
        {
            WeirInstruments.DbErrors.Add(1, new TagList
            {
                { "weir.route", context.Route },
                { "weir.db.error_category", context.DbError.ToString() },
            });
        }

        if (context.Items.TryGetValue(ActivityItemKey, out var stored) && stored is Activity activity)
        {
            activity.SetTag("weir.rows", context.RowsReturned);
            activity.SetTag("weir.cache_hit", context.CacheHit);
            if (exception is null)
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Error, exception.Message);
                activity.SetTag("error.type", exception.GetType().FullName);
                if (context.DbError != DbErrorCategory.None)
                {
                    activity.SetTag("weir.db.error_category", context.DbError.ToString());
                }
            }

            activity.Dispose();
        }
    }
}
