using Spectre.Console;

namespace Weir.Sample.Client;

/// <summary>
/// The <c>demo</c> command: a guided tour of the richer demo endpoints that finishes with the streaming
/// check. It runs each step in turn - a multi-row list, a single row, a table-valued-parameter insert
/// with output parameters and a return value, a two-result-set call, output-only statistics, and finally
/// the streaming procedure - so one command shows the whole surface, and the last thing it shows is a
/// procedure whose rows reach the client batch by batch.
/// <para>
/// Every step is independent: one that fails (a missing demo endpoint, an absent order) prints its error
/// and the tour continues, so a partially configured host still demonstrates what it can. The command's
/// exit code is the streaming step's verdict, which is what the demo exists to show.
/// </para>
/// </summary>
internal static class DemoCommand
{
    /// <summary>Runs the tour.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (optional positional customer id; stream options are passed through).</param>
    /// <returns>The streaming step's exit code (0 streamed, 1 buffered, 2 inconclusive).</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var customerId = args.Positional(0) ?? "1";
        AnsiConsole.MarkupLine($"[bold]Demo mode[/] - a tour of the demo endpoints, ending with the streaming check.");
        AnsiConsole.MarkupLine("[grey]Products, one product, an order built from a table-valued parameter, its detail, output-only stats, then GET /api/stream.[/]");
        AnsiConsole.WriteLine();

        await StepAsync("products (multi-row list)", () => ProductsCommand.RunAsync(session, new CliArgs([])));
        await StepAsync("product 1 (single row)", () => ProductCommand.RunAsync(session, new CliArgs(["1"])));
        await StepAsync(
            $"create-order (table-valued parameter)",
            () => CreateOrderCommand.RunAsync(session, new CliArgs([customerId, "--item", "1:2", "--item", "4:10"])));
        await StepAsync("order 1 (two result sets)", () => OrderCommand.RunAsync(session, new CliArgs(["1"])));
        await StepAsync(
            $"customer-stats {customerId} (output parameters)",
            () => CustomerStatsCommand.RunAsync(session, new CliArgs([customerId])));

        // The finale: the streaming step's exit code is this command's exit code.
        return await StepAsync(
            "stream (batches of rows from a procedure)",
            () => StreamCommand.RunAsync(session, new CliArgs([])));
    }

    /// <summary>Runs one tour step under a heading, catching failures so the tour continues.</summary>
    /// <param name="title">The step heading.</param>
    /// <param name="step">The step to run.</param>
    /// <returns>The step's exit code, or 1 when the step failed.</returns>
    private static async Task<int> StepAsync(string title, Func<Task<int>> step)
    {
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(title)}[/]") { Justification = Justify.Left });
        try
        {
            return await step();
        }
        catch (WeirCliException ex)
        {
            AnsiConsole.MarkupLine($"[red]Skipped:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Skipped:[/] cannot reach the host - {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            AnsiConsole.WriteLine();
        }
    }
}
