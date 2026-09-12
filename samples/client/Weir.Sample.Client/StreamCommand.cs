using System.Diagnostics;
using System.Globalization;
using Spectre.Console;

namespace Weir.Sample.Client;

/// <summary>
/// The <c>stream</c> command: checks that a response really streams end to end. It calls an endpoint
/// that produces rows in batches with a pause between them (<c>sales.StreamBatches</c> from the demo
/// database, by default) and records when each piece of the body arrives. A streamed response arrives in
/// bursts spaced by the pause; a response that something held back - the endpoint's delivery mode, a
/// cache, result logging, or a reverse proxy buffering it - arrives in one burst at the end. Run it once
/// against Weir directly and once through the proxy, and compare.
/// </summary>
internal static class StreamCommand
{
    /// <summary>Runs the check.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments.</param>
    /// <returns>0 when the body streamed, 1 when it arrived buffered, 2 when the run was inconclusive.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var batches = args.IntOption(5, "--batches");
        var rows = args.IntOption(500, "--rows");
        var delayMs = args.IntOption(1000, "--delay");
        var gapMs = Math.Max(1, args.IntOption(Math.Clamp(delayMs / 3, 50, 250), "--gap"));
        var compress = args.Flag("--compress");

        // A custom route is taken as-is (with its own query string); the default one is the demo procedure.
        var route = args.Option("-r", "--route")
            ?? string.Create(CultureInfo.InvariantCulture, $"stream?batches={batches}&rowsPerBatch={rows}&delayMs={delayMs}");
        var custom = args.Option("-r", "--route") is not null;

        AnsiConsole.MarkupLine($"[bold]Streaming check[/] [cyan]GET {Markup.Escape(session.Url)}/api/{Markup.Escape(route)}[/]");
        if (!custom)
        {
            AnsiConsole.MarkupLine($"[grey]The procedure sends {batches} batch(es) of {rows} row(s), {delayMs} ms apart; a streamed body arrives in about {batches} burst(s).[/]");
        }

        var arrivals = new List<(double AtMs, int Bytes)>();
        var start = Stopwatch.GetTimestamp();
        using var response = await session.Client.OpenAsync(HttpMethod.Get, route, compress ? "br, gzip" : null, CancellationToken.None);
        var headersAtMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        var buffer = new byte[64 * 1024];
        Exception? cutOff = null;
        await using (var body = await response.Content.ReadAsStreamAsync(CancellationToken.None))
        {
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer, CancellationToken.None)) > 0)
                {
                    arrivals.Add((Stopwatch.GetElapsedTime(start).TotalMilliseconds, read));
                }
            }
            catch (IOException ex)
            {
                // The connection closed mid-body. Weir aborts a stream that fails after it started, and a
                // proxy does the same when it stops waiting for the next chunk - both look like this.
                cutOff = ex;
            }
        }

        var totalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        PrintHeaders(response, headersAtMs);

        if (cutOff is not null)
        {
            PrintBursts(GroupBursts(arrivals, gapMs));
            AnsiConsole.MarkupLine($"[red]Cut off:[/] the body stopped after {Fmt.Bytes(arrivals.Sum(a => (long)a.Bytes))} at {Fmt.N0((long)totalMs)} ms ({Markup.Escape(cutOff.Message)}).");
            AnsiConsole.MarkupLine("[grey]A proxy gives up when the gap between two chunks exceeds its read timeout (nginx: proxy_read_timeout, 60s by default), even though the response is still streaming. Raise it above the longest pause, or check Weir's log for a failure during the stream.[/]");
            return 1;
        }

        if (!response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLine($"[red]The call failed with HTTP {(int)response.StatusCode}; nothing to measure.[/]");
            return 2;
        }

        var bursts = GroupBursts(arrivals, gapMs);
        PrintBursts(bursts);
        return Verdict(bursts, arrivals, totalMs, gapMs);
    }

    /// <summary>A run of reads with no gap longer than the burst gap between them.</summary>
    /// <param name="StartMs">When the first read of the burst arrived, from the start of the request.</param>
    /// <param name="EndMs">When the last read of the burst arrived.</param>
    /// <param name="Bytes">Bytes received in the burst.</param>
    /// <param name="Reads">Reads that made up the burst.</param>
    private readonly record struct Burst(double StartMs, double EndMs, long Bytes, int Reads);

    /// <summary>Groups reads into bursts: a new burst starts after a silence longer than <paramref name="gapMs"/>.</summary>
    /// <param name="arrivals">Each read's arrival time and size, in order.</param>
    /// <param name="gapMs">The silence that separates two bursts.</param>
    /// <returns>The bursts, in order.</returns>
    private static List<Burst> GroupBursts(List<(double AtMs, int Bytes)> arrivals, int gapMs)
    {
        var bursts = new List<Burst>();
        foreach (var (atMs, bytes) in arrivals)
        {
            if (bursts.Count > 0 && atMs - bursts[^1].EndMs <= gapMs)
            {
                var last = bursts[^1];
                bursts[^1] = last with { EndMs = atMs, Bytes = last.Bytes + bytes, Reads = last.Reads + 1 };
            }
            else
            {
                bursts.Add(new Burst(atMs, atMs, bytes, 1));
            }
        }

        return bursts;
    }

    /// <summary>Prints the response headers that decide whether a body can stream.</summary>
    /// <param name="response">The response.</param>
    /// <param name="headersAtMs">When the headers arrived.</param>
    private static void PrintHeaders(HttpResponseMessage response, double headersAtMs)
    {
        var table = new Table { Border = TableBorder.Rounded, Title = new TableTitle("response") };
        table.AddColumn("header");
        table.AddColumn("value");
        table.AddRow("status", $"{(int)response.StatusCode} (HTTP/{response.Version})");
        table.AddRow("headers arrived", $"{Fmt.N0((long)headersAtMs)} ms");
        table.AddRow("Transfer-Encoding", Header(response, "Transfer-Encoding"));
        table.AddRow("Content-Length", response.Content.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "-");
        table.AddRow("Content-Encoding", Header(response, "Content-Encoding"));
        table.AddRow("X-Accel-Buffering", Header(response, "X-Accel-Buffering"));
        table.AddRow("Server / Via", $"{Header(response, "Server")} / {Header(response, "Via")}");
        AnsiConsole.Write(table);
    }

    /// <summary>Reads a response or content header as one display string.</summary>
    /// <param name="response">The response.</param>
    /// <param name="name">The header name.</param>
    /// <returns>The header value, or "-" when it is absent.</returns>
    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? Markup.Escape(string.Join(", ", values))
            : "-";

    /// <summary>Prints one row per burst.</summary>
    /// <param name="bursts">The bursts.</param>
    private static void PrintBursts(List<Burst> bursts)
    {
        var table = new Table { Border = TableBorder.Rounded, Title = new TableTitle("body arrival") };
        table.AddColumn("burst");
        table.AddColumn(new TableColumn("at ms").RightAligned());
        table.AddColumn(new TableColumn("bytes").RightAligned());
        table.AddColumn(new TableColumn("reads").RightAligned());
        for (var i = 0; i < bursts.Count; i++)
        {
            table.AddRow(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                Fmt.N0((long)bursts[i].StartMs),
                Fmt.Bytes(bursts[i].Bytes),
                bursts[i].Reads.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }

    /// <summary>Prints and returns the verdict.</summary>
    /// <param name="bursts">The bursts.</param>
    /// <param name="arrivals">The individual reads.</param>
    /// <param name="totalMs">The whole request's duration.</param>
    /// <param name="gapMs">The burst gap in use.</param>
    /// <returns>0 streamed, 1 buffered, 2 inconclusive.</returns>
    private static int Verdict(List<Burst> bursts, List<(double AtMs, int Bytes)> arrivals, double totalMs, int gapMs)
    {
        if (arrivals.Count == 0 || totalMs < gapMs * 3)
        {
            AnsiConsole.MarkupLine($"[yellow]Inconclusive:[/] the response took {Fmt.N0((long)totalMs)} ms, too short to tell streaming from buffering. Raise --delay or --batches.");
            return 2;
        }

        var firstByteMs = arrivals[0].AtMs;
        if (bursts.Count > 1 && firstByteMs < totalMs / 2)
        {
            AnsiConsole.MarkupLine($"[green]Streamed:[/] the first bytes arrived at {Fmt.N0((long)firstByteMs)} ms of {Fmt.N0((long)totalMs)} ms, in {bursts.Count} bursts.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]Buffered:[/] the body arrived in {bursts.Count} burst(s), the first at {Fmt.N0((long)firstByteMs)} ms of {Fmt.N0((long)totalMs)} ms.");
        AnsiConsole.MarkupLine("[grey]Something between the database and this client held it back. On the Weir side: the endpoint's delivery mode (Stream), response caching, or result logging, all of which buffer. In front of Weir: a reverse proxy buffering the response - for nginx, see proxy_buffering and X-Accel-Buffering in docs/en/deployment.md.[/]");
        return 1;
    }
}
