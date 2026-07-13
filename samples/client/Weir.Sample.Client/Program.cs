using Spectre.Console;
using Weir.Sample.Client;

// Two modes:
//   interactive - no command given (just connection options, or nothing): open a REPL and keep it open.
//   one-shot    - a command given first (e.g. "list", "load"): run it once and exit, for scripting / CI.
// The URL and API key are read from the startup arguments (--url / --api-key) or the environment in
// both modes, so the interactive shell never re-asks for them.
var wantsHelp = args.Length > 0 && args[0] is "help" or "-h" or "--help";
if (wantsHelp)
{
    return HelpCommand.Run();
}

var interactive = args.Length == 0 || args[0].StartsWith('-');

try
{
    var session = Session.Create(new CliArgs(args));
    try
    {
        if (interactive)
        {
            return await InteractiveShell.RunAsync(session);
        }

        var command = args[0].ToLowerInvariant();
        return await Dispatch.RunAsync(command, session, new CliArgs(args[1..]));
    }
    finally
    {
        session.Dispose();
    }
}
catch (WeirCliException ex)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    return 2;
}
catch (HttpRequestException ex)
{
    AnsiConsole.MarkupLine($"[red]Cannot reach the Weir host:[/] {Markup.Escape(ex.Message)}");
    AnsiConsole.MarkupLine("[grey]Is the host running? Set --url or WEIR_URL to point at it.[/]");
    return 2;
}
catch (TaskCanceledException)
{
    AnsiConsole.MarkupLine("[red]The request timed out.[/]");
    return 2;
}
