using System.Globalization;
using System.Text.Json;
using Spectre.Console;

namespace Weir.Sample.Client;

/// <summary>The <c>list</c> command: GET <c>/api/widgets</c> and render the rows as a table.</summary>
internal static class ListCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (unused).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        _ = args;
        using var response = await session.Client.SendAsync(HttpMethod.Get, "widgets", null, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        var rows = response.FirstResultSet();
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No widgets yet.[/] Create one: [bold]create <name> <price>[/]");
            return 0;
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Id");
        table.AddColumn("Name");
        table.AddColumn(new TableColumn("Price").RightAligned());
        table.AddColumn("CreatedAt");
        foreach (var row in rows)
        {
            table.AddRow(
                Markup.Escape(Output.Field(row, "Id")),
                Markup.Escape(Output.Field(row, "Name")),
                Markup.Escape(Output.Field(row, "Price")),
                Markup.Escape(Output.Field(row, "CreatedAt")));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{Fmt.N0(rows.Count)} widget(s).[/]");
        return 0;
    }
}

/// <summary>The <c>get</c> command: GET <c>/api/widgets/by-id?id=N</c> and render the single row.</summary>
internal static class GetCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (positional id).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var idText = args.Positional(0) ?? throw new WeirCliException("Usage: get <id>");
        if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            throw new WeirCliException($"'{idText}' is not a valid id.");
        }

        using var response = await session.Client.SendAsync(HttpMethod.Get, $"widgets/by-id?id={id}", null, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        var rows = response.FirstResultSet();
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Widget {id} not found.[/]");
            return 0;
        }

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        foreach (var property in rows[0].EnumerateObject())
        {
            grid.AddRow($"[grey]{Markup.Escape(property.Name)}[/]", Markup.Escape(Output.Value(property.Value)));
        }

        AnsiConsole.Write(new Panel(grid) { Header = new PanelHeader($"widget {id}"), Border = BoxBorder.Rounded });
        return 0;
    }
}

/// <summary>The <c>create</c> command: POST <c>/api/widgets</c> and report the new id (output parameter).</summary>
internal static class CreateCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (positional name and price).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var name = args.Positional(0) ?? throw new WeirCliException("Usage: create <name> <price>");
        var priceText = args.Positional(1) ?? throw new WeirCliException("Usage: create <name> <price>");
        if (!decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
        {
            throw new WeirCliException($"'{priceText}' is not a valid price.");
        }

        var body = JsonSerializer.Serialize(new { name, price });
        using var response = await session.Client.SendAsync(HttpMethod.Post, "widgets", body, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        var newId = response.Output.ValueKind == JsonValueKind.Object && response.Output.TryGetProperty("newId", out var value)
            ? Output.Value(value)
            : "(none)";
        AnsiConsole.MarkupLine($"[green]Created[/] widget [bold]{Markup.Escape(name)}[/] -> newId=[bold]{Markup.Escape(newId)}[/]");
        return 0;
    }
}

/// <summary>The <c>import</c> command: bulk-insert widgets through a table-valued parameter.</summary>
internal static class ImportCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (repeatable --item Name:Price).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var items = args.Options("-i", "--item");
        if (items.Count == 0)
        {
            throw new WeirCliException("Provide at least one --item \"Name:Price\" (repeatable), e.g. --item Nut:0.25 --item Washer:0.10");
        }

        var rows = new List<object>(items.Count);
        foreach (var item in items)
        {
            var separator = item.LastIndexOf(':');
            if (separator <= 0 || separator == item.Length - 1)
            {
                throw new WeirCliException($"Bad --item '{item}'. Use Name:Price, e.g. Nut:0.25");
            }

            if (!decimal.TryParse(item[(separator + 1)..], NumberStyles.Number, CultureInfo.InvariantCulture, out var itemPrice))
            {
                throw new WeirCliException($"Bad price in --item '{item}'.");
            }

            rows.Add(new { Name = item[..separator], Price = itemPrice });
        }

        var body = JsonSerializer.Serialize(new { items = rows });
        using var response = await session.Client.SendAsync(HttpMethod.Post, "widgets/import", body, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        var resultRows = response.FirstResultSet();
        var imported = resultRows.Count > 0 ? Output.Field(resultRows[0], "Imported") : Fmt.N0(rows.Count);
        AnsiConsole.MarkupLine($"[green]Imported[/] [bold]{Markup.Escape(imported)}[/] widget(s).");
        return 0;
    }
}

/// <summary>The <c>call</c> command: call any route with an optional body and print the raw envelope.</summary>
internal static class CallCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (positional route; -X method; -b body or -f file).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var route = args.Positional(0) ?? throw new WeirCliException("Usage: call <route> [-X METHOD] [-b JSON | -f FILE]");
        var method = new HttpMethod((args.Option("-X", "--method") ?? "GET").ToUpperInvariant());

        var body = args.Option("-b", "--body");
        var bodyFile = args.Option("-f", "--body-file");
        if (bodyFile is not null)
        {
            if (!File.Exists(bodyFile))
            {
                throw new WeirCliException($"Body file not found: {bodyFile}");
            }

            body = await File.ReadAllTextAsync(bodyFile, CancellationToken.None);
        }

        using var response = await session.Client.SendAsync(method, route, body, CancellationToken.None);
        Output.Envelope(response);
        return response.IsSuccess ? 0 : 1;
    }
}
