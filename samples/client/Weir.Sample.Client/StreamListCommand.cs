using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Weir.Client;

namespace Weir.Sample.Client;

/// <summary>
/// The <c>stream-list</c> command: the <c>list</c> view of a route that streams. It calls the same endpoint
/// as <c>stream</c> (the demo's <c>GET /api/stream</c>), but instead of waiting for the whole body and then
/// printing when the bytes arrived, it parses the envelope as it arrives and prints each row the moment it
/// is complete - so the data is on screen while the procedure is still running. Every row carries the time
/// it arrived, and a marker is printed where a new batch begins.
/// </summary>
internal static class StreamListCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (the stream options; -r for any other route).</param>
    /// <returns>0 when the body streamed, 1 when it arrived buffered, 2 when the run was inconclusive.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var batches = args.IntOption(5, "--batches");
        var rows = args.IntOption(500, "--rows");
        var delayMs = args.IntOption(1000, "--delay");
        var gapMs = Math.Max(1, args.IntOption(Math.Clamp(delayMs / 3, 50, 250), "--gap"));

        var route = args.Option("-r", "--route")
            ?? string.Create(CultureInfo.InvariantCulture, $"stream?batches={batches}&rowsPerBatch={rows}&delayMs={delayMs}");
        var custom = args.Option("-r", "--route") is not null;

        AnsiConsole.MarkupLine($"[bold]Streaming list[/] [cyan]GET {Markup.Escape(session.Url)}/api/{Markup.Escape(route)}[/]");
        if (!custom)
        {
            AnsiConsole.MarkupLine($"[grey]Rows print as they arrive; the procedure sends {batches} batch(es) {delayMs} ms apart.[/]");
        }

        var start = Stopwatch.GetTimestamp();
        var printer = new RowPrinter();
        WeirApiException? failure = null;
        try
        {
            // The package's streaming API hands each row over the moment its bytes are complete, batch
            // boundary included, so this loop is the whole live view.
            await foreach (var item in session.Client.StreamEventsAsync(Session.Api(route)))
            {
                printer.Emit(item, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }
        catch (WeirApiException problem)
        {
            failure = problem;
        }

        var totalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AnsiConsole.WriteLine();

        if (failure is not null)
        {
            // The package reports a problem body before the stream starts and a body that ended inside a
            // value mid-stream; only the second carries an inner exception.
            if (failure.InnerException is null)
            {
                return Output.Fail(failure);
            }

            AnsiConsole.MarkupLine($"[red]Cut off:[/] {Fmt.N0(printer.RowCount)} row(s) arrived in {Fmt.N0((long)totalMs)} ms ({Markup.Escape(failure.Message)}).");
            AnsiConsole.MarkupLine("[grey]A proxy read timeout (nginx: proxy_read_timeout) turns a long pause into a closed connection, and a failure mid-stream aborts the response the same way.[/]");
            return 1;
        }

        if (printer.RowCount == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No rows arrived.[/] Check that the route returns data and that the endpoint's delivery mode is Stream.");
            return 2;
        }

        AnsiConsole.MarkupLine($"[grey]{Fmt.N0(printer.RowCount)} row(s) in {printer.SetCount} set(s); first row at {Fmt.N0((long)printer.FirstRowMs)} ms, done at {Fmt.N0((long)totalMs)} ms.[/]");
        return Verdict(printer.FirstRowMs, totalMs, gapMs);
    }

    /// <summary>Prints the streaming verdict and returns the exit code.</summary>
    /// <param name="firstRowMs">When the first row arrived, from the start of the request.</param>
    /// <param name="totalMs">The whole request's duration.</param>
    /// <param name="gapMs">The burst gap in use.</param>
    /// <returns>0 streamed, 1 buffered, 2 inconclusive.</returns>
    private static int Verdict(double firstRowMs, double totalMs, int gapMs)
    {
        if (totalMs < gapMs * 3)
        {
            AnsiConsole.MarkupLine($"[yellow]Inconclusive:[/] the response took {Fmt.N0((long)totalMs)} ms, too short to tell streaming from buffering. Raise --delay or --batches.");
            return 2;
        }

        if (firstRowMs < totalMs / 2)
        {
            AnsiConsole.MarkupLine($"[green]Streamed:[/] the first row arrived at {Fmt.N0((long)firstRowMs)} ms of {Fmt.N0((long)totalMs)} ms.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]Buffered:[/] the first row arrived at {Fmt.N0((long)firstRowMs)} ms of {Fmt.N0((long)totalMs)} ms - the body was held back before any of it reached you.");
        AnsiConsole.MarkupLine("[grey]Something buffered it: the endpoint's delivery mode (Stream), response caching, result logging, or a reverse proxy.[/]");
        return 1;
    }

    /// <summary>Prints rows as they complete and keeps the tallies the final verdict needs.</summary>
    private sealed class RowPrinter
    {
        /// <summary>The column names, taken from the first row; null until one arrives.</summary>
        private string[]? _columns;

        /// <summary>Rows printed so far.</summary>
        public long RowCount { get; private set; }

        /// <summary>Result sets (batches) seen so far.</summary>
        public int SetCount { get; private set; }

        /// <summary>When the first row arrived, in milliseconds from the start; -1 until one does.</summary>
        public double FirstRowMs { get; private set; } = -1;

        /// <summary>Prints one event from the package's stream, at the time it arrived.</summary>
        /// <param name="item">The event.</param>
        /// <param name="atMs">When it arrived, from the start of the request.</param>
        public void Emit(WeirStreamEvent item, double atMs)
        {
            if (item.Kind == WeirStreamEventKind.ResultSetStarted)
            {
                SetCount++;
                AnsiConsole.MarkupLine($"[grey]--- batch {SetCount} at {Fmt.N0((long)atMs)} ms ---[/]");
                return;
            }

            if (FirstRowMs < 0)
            {
                FirstRowMs = atMs;
            }

            using var row = JsonDocument.Parse(item.Json);
            if (_columns is null)
            {
                _columns = row.RootElement.ValueKind == JsonValueKind.Object
                    ? [.. row.RootElement.EnumerateObject().Select(property => property.Name)]
                    : [];
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(Join(_columns, null))}[/]");
            }

            AnsiConsole.MarkupLine($"[grey]{Fmt.N0((long)atMs),8} ms[/]  {Markup.Escape(Join(_columns!, row.RootElement))}");
            RowCount++;
        }

        /// <summary>Formats a line of padded cells: the header when <paramref name="row"/> is null, else the row's values.</summary>
        /// <param name="columns">The column names, which also set each cell's width.</param>
        /// <param name="row">The row, or null for the header line.</param>
        /// <returns>The formatted line.</returns>
        private static string Join(IReadOnlyList<string> columns, JsonElement? row) =>
            string.Join("  ", columns.Select(column => Pad(row is { } r ? Output.Field(r, column) : column, column)));

        /// <summary>Pads a value to a stable column width so the rows line up, cutting it short when it overruns.</summary>
        /// <param name="value">The cell value.</param>
        /// <param name="column">The column name, which sets the width.</param>
        /// <returns>The cell text.</returns>
        private static string Pad(string value, string column)
        {
            var width = Math.Clamp(column.Length, 6, 40);
            return (value.Length <= width ? value : value[..width]).PadRight(width);
        }
    }
}
