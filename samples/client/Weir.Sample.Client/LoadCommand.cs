using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Spectre.Console;

namespace Weir.Sample.Client;

/// <summary>
/// The <c>load</c> command: drives a fixed number of concurrent workers against one endpoint, either
/// for a duration or for a fixed request count, and reports throughput and latency percentiles. It is a
/// convenience smoke / stress tool, not a substitute for a dedicated benchmarking rig (single client
/// process, wall-clock timing).
/// </summary>
internal static class LoadCommand
{
    /// <summary>Runs the load test.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments.</param>
    /// <returns>The process exit code (non-zero only when every request failed).</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var route = args.Option("-r", "--route") ?? "widgets";
        var method = new HttpMethod((args.Option("-X", "--method") ?? "GET").ToUpperInvariant());
        var body = args.Option("-b", "--body");
        var concurrency = Math.Max(1, args.IntOption(16, "-c", "--concurrency"));
        var warmup = Math.Max(0, args.IntOption(0, "-w", "--warmup"));

        // --requests wins over --duration when both are given; otherwise default to a 10s window.
        int? requests = args.Option("-n", "--requests") is not null ? args.IntOption(1, "-n", "--requests") : null;
        var byCount = requests.HasValue;
        var duration = byCount ? 0 : args.IntOption(10, "-d", "--duration");
        if (byCount && requests!.Value < 1)
        {
            throw new WeirCliException("--requests must be at least 1.");
        }

        if (!byCount && duration < 1)
        {
            throw new WeirCliException("--duration must be at least 1 second.");
        }

        using var client = session.CreateClient(concurrency);

        // A live (cursor-driven) display only works on a real terminal. When output is redirected
        // (a pipe, a file, CI), fall back to plain progress lines so the run does not crash.
        var interactive = !Console.IsOutputRedirected;

        AnsiConsole.MarkupLine($"[bold]Load test[/] [cyan]{method} {Markup.Escape(session.Url)}/api/{Markup.Escape(route)}[/]");
        AnsiConsole.MarkupLine($"[grey]concurrency={concurrency}, {(byCount ? $"requests={Fmt.N0(requests!.Value)}" : $"duration={duration}s")}, warmup={warmup}s[/]");

        // Preflight one request so a bad URL / key / route fails fast with a clear message instead of
        // producing a wall of identical errors in the results.
        using (var probe = await client.SendAsync(method, route, body, CancellationToken.None))
        {
            if (!probe.IsSuccess)
            {
                AnsiConsole.MarkupLine("[red]Preflight request failed - aborting load test.[/]");
                return Output.Fail(probe);
            }
        }

        if (warmup > 0)
        {
            using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(warmup));
            var warmupRun = RunWorkersAsync(client, method, route, body, concurrency, byCount: false, total: 0, stats: null, warmupCts.Token);
            if (interactive)
            {
                await AnsiConsole.Status().StartAsync($"warming up for {warmup}s...", async _ => await warmupRun);
            }
            else
            {
                AnsiConsole.MarkupLine($"[grey]warming up for {warmup}s...[/]");
                await warmupRun;
            }
        }

        var stats = new LoadStats();
        using var cts = byCount
            ? new CancellationTokenSource()
            : new CancellationTokenSource(TimeSpan.FromSeconds(duration));
        var clock = Stopwatch.StartNew();
        var run = RunWorkersAsync(client, method, route, body, concurrency, byCount, requests ?? 0, stats, cts.Token);
        await ShowProgressAsync(stats, clock, run, interactive);
        clock.Stop();

        var report = LoadReport.Compute(stats, clock.Elapsed);
        RenderReport(report, stats);
        return report.Failed > 0 && report.Successful == 0 ? 1 : 0;
    }

    /// <summary>
    /// Runs <paramref name="concurrency"/> workers that issue requests until the token is cancelled
    /// (duration mode) or the shared counter reaches <paramref name="total"/> (count mode). When
    /// <paramref name="stats"/> is null the run is a warm-up and results are discarded.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="route">The route.</param>
    /// <param name="body">The request body, or null.</param>
    /// <param name="concurrency">The number of concurrent workers.</param>
    /// <param name="byCount">True to stop after <paramref name="total"/> requests; false to run until cancelled.</param>
    /// <param name="total">The total request count in count mode.</param>
    /// <param name="stats">The stats to record into, or null to discard (warm-up).</param>
    /// <param name="token">Cancellation token (the duration window, or the caller's).</param>
    /// <returns>A task that completes when every worker has stopped.</returns>
    private static Task RunWorkersAsync(
        WeirClient client, HttpMethod method, string route, string? body, int concurrency,
        bool byCount, int total, LoadStats? stats, CancellationToken token)
    {
        var issued = 0;
        var workers = new Task[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                var latencies = stats is null ? null : new List<double>();
                while (!token.IsCancellationRequested)
                {
                    if (byCount && Interlocked.Increment(ref issued) > total)
                    {
                        break;
                    }

                    RequestOutcome outcome;
                    try
                    {
                        outcome = await client.MeasureAsync(method, route, body, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (stats is not null)
                    {
                        stats.Record(outcome);
                        latencies!.Add(outcome.ElapsedMs);
                    }
                }

                if (latencies is not null)
                {
                    stats!.MergeLatencies(latencies);
                }
            }, CancellationToken.None);
        }

        return Task.WhenAll(workers);
    }

    /// <summary>
    /// Shows progress until the run completes: a live, refreshing table on a real terminal, or periodic
    /// plain lines when output is redirected (where cursor control is unavailable).
    /// </summary>
    /// <param name="stats">The running stats.</param>
    /// <param name="clock">The elapsed-time clock.</param>
    /// <param name="run">The task that completes when every worker stops.</param>
    /// <param name="interactive">True when a live display is safe to use.</param>
    /// <returns>A task that completes when the run finishes.</returns>
    private static async Task ShowProgressAsync(LoadStats stats, Stopwatch clock, Task run, bool interactive)
    {
        if (interactive)
        {
            await AnsiConsole.Live(LiveTable(stats, clock.Elapsed))
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    while (!run.IsCompleted)
                    {
                        ctx.UpdateTarget(LiveTable(stats, clock.Elapsed));
                        ctx.Refresh();
                        await Task.WhenAny(run, Task.Delay(200));
                    }

                    ctx.UpdateTarget(LiveTable(stats, clock.Elapsed));
                    ctx.Refresh();
                });
        }
        else
        {
            while (!run.IsCompleted)
            {
                await Task.WhenAny(run, Task.Delay(1000));
                if (!run.IsCompleted)
                {
                    var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                    AnsiConsole.MarkupLine($"[grey]  {Fmt.N1(clock.Elapsed.TotalSeconds)}s: completed={Fmt.N0(stats.Completed)}, failed={Fmt.N0(stats.Failed)}, {Fmt.N1(stats.Completed / seconds)} req/s[/]");
                }
            }
        }

        await run;
    }

    /// <summary>Builds the live progress table shown while the test runs.</summary>
    /// <param name="stats">The running stats.</param>
    /// <param name="elapsed">Time elapsed so far.</param>
    /// <returns>The renderable table.</returns>
    private static Table LiveTable(LoadStats stats, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("elapsed");
        table.AddColumn("completed");
        table.AddColumn("failed");
        table.AddColumn("req/s");
        table.AddRow(
            $"{Fmt.N1(elapsed.TotalSeconds)}s",
            Fmt.N0(stats.Completed),
            stats.Failed > 0 ? $"[red]{Fmt.N0(stats.Failed)}[/]" : "0",
            $"[cyan]{Fmt.N1(stats.Completed / seconds)}[/]");
        return table;
    }

    /// <summary>Renders the final results table and the status-code breakdown.</summary>
    /// <param name="report">The computed report.</param>
    /// <param name="stats">The raw stats (for the status breakdown).</param>
    private static void RenderReport(LoadReport report, LoadStats stats)
    {
        var table = new Table { Border = TableBorder.Rounded, Title = new TableTitle("results") };
        table.AddColumn("metric");
        table.AddColumn(new TableColumn("value").RightAligned());
        table.AddRow("total requests", Fmt.N0(report.Total));
        table.AddRow("successful", $"[green]{Fmt.N0(report.Successful)}[/]");
        table.AddRow("failed", report.Failed > 0 ? $"[red]{Fmt.N0(report.Failed)}[/]" : "0");
        table.AddRow("duration", $"{Fmt.N2(report.ElapsedSeconds)} s");
        table.AddRow("throughput", $"[cyan]{Fmt.N1(report.RequestsPerSecond)}[/] req/s");
        table.AddRow("transferred", Fmt.Bytes(report.TotalBytes));
        table.AddRow("latency min / mean", $"{Fmt.N1(report.MinMs)} / {Fmt.N1(report.MeanMs)} ms");
        table.AddRow("latency p50 / p90", $"{Fmt.N1(report.P50)} / {Fmt.N1(report.P90)} ms");
        table.AddRow("latency p95 / p99", $"{Fmt.N1(report.P95)} / {Fmt.N1(report.P99)} ms");
        table.AddRow("latency max", $"{Fmt.N1(report.MaxMs)} ms");
        AnsiConsole.Write(table);

        var statuses = stats.StatusCounts.OrderBy(entry => entry.Key).ToList();
        if (statuses.Count == 0)
        {
            return;
        }

        var breakdown = new Table { Border = TableBorder.Rounded, Title = new TableTitle("status codes") };
        breakdown.AddColumn("status");
        breakdown.AddColumn(new TableColumn("count").RightAligned());
        foreach (var (code, count) in statuses)
        {
            var label = code == 0 ? "error (no response)" : code.ToString(CultureInfo.InvariantCulture);
            var color = code is >= 200 and < 400 ? "green" : code == 0 ? "red" : "yellow";
            breakdown.AddRow($"[{color}]{label}[/]", Fmt.N0(count));
        }

        AnsiConsole.Write(breakdown);
    }
}

/// <summary>Thread-safe counters and per-request latencies collected during a load test.</summary>
internal sealed class LoadStats
{
    /// <summary>Total completed (recorded) requests.</summary>
    private long _completed;

    /// <summary>Completed requests that failed (status 0, 4xx or 5xx).</summary>
    private long _failed;

    /// <summary>Total response bytes read.</summary>
    private long _bytes;

    /// <summary>Per-status-code counts.</summary>
    private readonly ConcurrentDictionary<int, long> _statusCounts = new();

    /// <summary>All recorded latencies (milliseconds), merged from per-worker lists.</summary>
    private readonly List<double> _latencies = [];

    /// <summary>Guards <see cref="_latencies"/> during merge and snapshot.</summary>
    private readonly Lock _latencyLock = new();

    /// <summary>Total completed (recorded) requests.</summary>
    public long Completed => Interlocked.Read(ref _completed);

    /// <summary>Completed requests that failed.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Total response bytes read.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Per-status-code counts (0 means the request failed before a response).</summary>
    public IReadOnlyDictionary<int, long> StatusCounts => _statusCounts;

    /// <summary>Records one completed request's outcome (counters and status; latency is per-worker).</summary>
    /// <param name="outcome">The request outcome.</param>
    public void Record(RequestOutcome outcome)
    {
        Interlocked.Increment(ref _completed);
        Interlocked.Add(ref _bytes, outcome.Bytes);
        if (outcome.Error is not null || outcome.StatusCode is 0 or >= 400)
        {
            Interlocked.Increment(ref _failed);
        }

        _statusCounts.AddOrUpdate(outcome.StatusCode, 1, static (_, count) => count + 1);
    }

    /// <summary>Merges a worker's collected latencies into the shared list.</summary>
    /// <param name="latencies">The worker's latencies.</param>
    public void MergeLatencies(List<double> latencies)
    {
        lock (_latencyLock)
        {
            _latencies.AddRange(latencies);
        }
    }

    /// <summary>Returns a sorted-copy-ready snapshot of every recorded latency.</summary>
    /// <returns>A copy of the latency samples.</returns>
    public double[] SnapshotLatencies()
    {
        lock (_latencyLock)
        {
            return [.. _latencies];
        }
    }
}

/// <summary>A computed load-test report: totals, throughput and latency percentiles (milliseconds).</summary>
/// <param name="Total">Total completed requests.</param>
/// <param name="Successful">Requests with a 2xx / 3xx status.</param>
/// <param name="Failed">Requests that failed (status 0, 4xx or 5xx).</param>
/// <param name="ElapsedSeconds">Wall-clock duration in seconds.</param>
/// <param name="RequestsPerSecond">Completed requests divided by the duration.</param>
/// <param name="MinMs">Minimum latency.</param>
/// <param name="MeanMs">Mean latency.</param>
/// <param name="P50">50th-percentile latency.</param>
/// <param name="P90">90th-percentile latency.</param>
/// <param name="P95">95th-percentile latency.</param>
/// <param name="P99">99th-percentile latency.</param>
/// <param name="MaxMs">Maximum latency.</param>
/// <param name="TotalBytes">Total response bytes read.</param>
internal readonly record struct LoadReport(
    long Total, long Successful, long Failed, double ElapsedSeconds, double RequestsPerSecond,
    double MinMs, double MeanMs, double P50, double P90, double P95, double P99, double MaxMs, long TotalBytes)
{
    /// <summary>Computes a report from the raw stats and the elapsed time.</summary>
    /// <param name="stats">The collected stats.</param>
    /// <param name="elapsed">The wall-clock duration.</param>
    /// <returns>The computed report.</returns>
    public static LoadReport Compute(LoadStats stats, TimeSpan elapsed)
    {
        var latencies = stats.SnapshotLatencies();
        Array.Sort(latencies);
        var total = stats.Completed;
        var failed = stats.Failed;
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return new LoadReport(
            total,
            total - failed,
            failed,
            elapsed.TotalSeconds,
            total / seconds,
            latencies.Length > 0 ? latencies[0] : 0,
            latencies.Length > 0 ? latencies.Average() : 0,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.90),
            Percentile(latencies, 0.95),
            Percentile(latencies, 0.99),
            latencies.Length > 0 ? latencies[^1] : 0,
            stats.Bytes);
    }

    /// <summary>Returns the nearest-rank percentile from a sorted array (empty -> 0).</summary>
    /// <param name="sorted">The latencies in ascending order.</param>
    /// <param name="quantile">The quantile in [0, 1].</param>
    /// <returns>The percentile value.</returns>
    private static double Percentile(double[] sorted, double quantile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(quantile * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
