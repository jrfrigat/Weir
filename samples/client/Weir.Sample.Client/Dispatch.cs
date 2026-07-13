using Spectre.Console;

namespace Weir.Sample.Client;

/// <summary>Maps a command name to its handler. Shared by one-shot mode and the interactive shell.</summary>
internal static class Dispatch
{
    /// <summary>Runs one command against a session.</summary>
    /// <param name="command">The command name (already lower-cased).</param>
    /// <param name="session">The active session (host URL, key and client).</param>
    /// <param name="args">The command's arguments (the command name already stripped).</param>
    /// <returns>The command's exit code.</returns>
    public static async Task<int> RunAsync(string command, Session session, CliArgs args) => command switch
    {
        // Widgets sample (endpoints.seed.json).
        "list" => await ListCommand.RunAsync(session, args),
        "get" => await GetCommand.RunAsync(session, args),
        "create" => await CreateCommand.RunAsync(session, args),
        "import" => await ImportCommand.RunAsync(session, args),

        // Demo / orders sample (weir-demo.endpoints.json).
        "products" => await ProductsCommand.RunAsync(session, args),
        "product" => await ProductCommand.RunAsync(session, args),
        "orders" => await OrdersCommand.RunAsync(session, args),
        "order" => await OrderCommand.RunAsync(session, args),
        "create-order" => await CreateOrderCommand.RunAsync(session, args),
        "customer-stats" => await CustomerStatsCommand.RunAsync(session, args),

        // General.
        "call" => await CallCommand.RunAsync(session, args),
        "load" => await LoadCommand.RunAsync(session, args),
        "help" or "-h" or "--help" => HelpCommand.Run(),
        _ => Unknown(command),
    };

    /// <summary>Prints an unknown-command message and returns exit code 2.</summary>
    /// <param name="command">The unrecognized command.</param>
    /// <returns>The exit code (2).</returns>
    private static int Unknown(string command)
    {
        AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'.[/] Type 'help' for the command list.");
        return 2;
    }
}
