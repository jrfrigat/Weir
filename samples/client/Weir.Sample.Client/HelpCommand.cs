using Spectre.Console;

namespace Weir.Sample.Client;

/// <summary>The <c>help</c> command: prints usage, the command list and examples.</summary>
internal static class HelpCommand
{
    /// <summary>Prints the help text.</summary>
    /// <returns>The process exit code (0).</returns>
    public static int Run()
    {
        AnsiConsole.MarkupLine("[bold]weir-sample[/] - a CLI client and load tester for a running Weir host.");
        AnsiConsole.MarkupLine("[grey]Calls the 'widgets' sample endpoints (samples/sqlserver/schema.sql, samples/endpoints.seed.json).[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Interactive:[/] weir-sample --url <URL> --api-key <KEY>   (opens a prompt; type commands, 'exit' to quit)");
        AnsiConsole.MarkupLine("[bold]One-shot:[/]    weir-sample <command> [[options]]              (run one command and exit)");
        AnsiConsole.WriteLine();

        var widgets = new Table { Border = TableBorder.Rounded, Title = new TableTitle("widgets sample (endpoints.seed.json)") };
        widgets.AddColumn("command");
        widgets.AddColumn("description");
        widgets.AddRow("list", "List all widgets (GET /api/widgets).");
        widgets.AddRow("get <id>", "Get one widget by id (GET /api/widgets/by-id).");
        widgets.AddRow("create <name> <price>", "Create a widget (POST /api/widgets); shows the output id.");
        widgets.AddRow("import --item Name:Price ...", "Bulk-insert widgets via a table-valued parameter.");
        AnsiConsole.Write(widgets);

        var demo = new Table { Border = TableBorder.Rounded, Title = new TableTitle("demo / orders sample (weir-demo.endpoints.json)") };
        demo.AddColumn("command");
        demo.AddColumn("description");
        demo.AddRow("products", "List all products (GET /api/products).");
        demo.AddRow("product <id>", "Get one product by id (GET /api/products/by-id).");
        demo.AddRow("orders <customerId>", "A customer's orders (table-valued function).");
        demo.AddRow("order <orderId>", "Order header plus line items (two result sets).");
        demo.AddRow("create-order <customerId> --item PID:QTY ...", "Create an order via a table-valued parameter.");
        demo.AddRow("customer-stats <customerId>", "Customer order count / total (output parameters).");
        AnsiConsole.Write(demo);

        var general = new Table { Border = TableBorder.Rounded, Title = new TableTitle("general") };
        general.AddColumn("command");
        general.AddColumn("description");
        general.AddRow("call <route> [[-X M]] [[-b JSON]]", "Call any endpoint and print the raw envelope.");
        general.AddRow("load [[options]]", "Load-test an endpoint (throughput and latency percentiles).");
        general.AddRow("help", "Show this help.");
        general.AddRow("clear", "Clear the screen (interactive only).");
        general.AddRow("exit", "Leave the interactive shell.");
        AnsiConsole.Write(general);

        var options = new Table { Border = TableBorder.Rounded, Title = new TableTitle("connection (all commands)") };
        options.AddColumn("option");
        options.AddColumn("description");
        options.AddRow("-u, --url <URL>", "Host base URL. Env: WEIR_URL. Default: http://localhost:8080");
        options.AddRow("-k, --api-key <KEY>", "API key for the X-Api-Key header. Env: WEIR_API_KEY");
        AnsiConsole.Write(options);

        var load = new Table { Border = TableBorder.Rounded, Title = new TableTitle("load options") };
        load.AddColumn("option");
        load.AddColumn("description");
        load.AddRow("-r, --route <ROUTE>", "Route to hit (default: widgets).");
        load.AddRow("-X, --method <METHOD>", "HTTP method (default: GET).");
        load.AddRow("-b, --body <JSON>", "Request body for POST/PUT.");
        load.AddRow("-c, --concurrency <N>", "Concurrent workers (default: 16).");
        load.AddRow("-d, --duration <SECONDS>", "Run for a time window (default: 10).");
        load.AddRow("-n, --requests <N>", "Run a fixed request count (overrides --duration).");
        load.AddRow("-w, --warmup <SECONDS>", "Warm-up window, results discarded (default: 0).");
        AnsiConsole.Write(load);

        AnsiConsole.MarkupLine("[bold]Examples:[/]");
        AnsiConsole.MarkupLine("[grey]  weir-sample --url http://localhost:8080 --api-key weir_...   # interactive shell[/]");
        AnsiConsole.MarkupLine("[grey]  widgets:  list | get 1 | create Bolt 1.50[/]");
        AnsiConsole.MarkupLine("[grey]  orders:   products | create-order 1 --item 1:2 --item 4:10 | order 1 | orders 1[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]  export WEIR_URL=http://localhost:8080; export WEIR_API_KEY=weir_...   # then one-shot:[/]");
        AnsiConsole.MarkupLine("[grey]  weir-sample list[/]");
        AnsiConsole.MarkupLine("[grey]  weir-sample import --item Nut:0.25 --item Washer:0.10[/]");
        AnsiConsole.MarkupLine("[grey]  weir-sample load --route widgets -c 32 -d 15[/]");
        return 0;
    }

    /// <summary>Prints an unknown-command error, then the help, and returns exit code 2.</summary>
    /// <param name="command">The unrecognized command.</param>
    /// <returns>The process exit code (2).</returns>
    public static int Unknown(string command)
    {
        AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'.[/]");
        AnsiConsole.WriteLine();
        Run();
        return 2;
    }
}
