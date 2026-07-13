using System.Globalization;
using System.Text.Json;
using Spectre.Console;

namespace Weir.Sample.Client;

// Commands for the richer demo database (samples/sqlserver/demo-database.sql, imported from
// samples/weir-demo.endpoints.json): products, orders and customers under the "sales" schema. They
// exercise a scalar/table-valued function, a table-valued parameter, multiple result sets, output
// parameters and a procedure return value.

/// <summary>The <c>products</c> command: GET <c>/api/products</c>, rendered as a table.</summary>
internal static class ProductsCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (unused).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        _ = args;
        using var response = await session.Client.SendAsync(HttpMethod.Get, "products", null, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        Output.Rows(response.FirstResultSet(), "No products.");
        return 0;
    }
}

/// <summary>The <c>product</c> command: GET <c>/api/products/by-id?id=N</c> (single row).</summary>
internal static class ProductCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (positional id).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var id = args.RequireIntPositional(0, "product <id>", "id");
        using var response = await session.Client.SendAsync(HttpMethod.Get, $"products/by-id?id={id}", null, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        var rows = response.FirstResultSet();
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Product {id} not found.[/]");
            return 0;
        }

        Output.KeyValues(rows[0], $"product {id}");
        return 0;
    }
}

/// <summary>The <c>orders</c> command: a customer's orders via the table-valued function.</summary>
internal static class OrdersCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (positional customer id).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var customerId = args.RequireIntPositional(0, "orders <customerId>", "customerId");
        using var response = await session.Client.SendAsync(HttpMethod.Get, $"customers/orders?id={customerId}", null, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        Output.Rows(response.FirstResultSet(), $"No orders for customer {customerId}.");
        return 0;
    }
}

/// <summary>The <c>order</c> command: an order header plus its line items (two result sets).</summary>
internal static class OrderCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (positional order id).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var orderId = args.RequireIntPositional(0, "order <orderId>", "orderId");
        using var response = await session.Client.SendAsync(HttpMethod.Get, $"orders/detail?orderId={orderId}", null, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        var sets = response.ResultSets();
        var header = sets.Count > 0 ? sets[0] : [];
        if (header.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Order {orderId} not found.[/]");
            return 0;
        }

        Output.KeyValues(header[0], $"order {orderId}");
        AnsiConsole.MarkupLine("[grey]line items:[/]");
        Output.Rows(sets.Count > 1 ? sets[1] : [], "No line items.");
        return 0;
    }
}

/// <summary>The <c>create-order</c> command: POST <c>/api/orders</c> with a table-valued parameter of items.</summary>
internal static class CreateOrderCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (positional customer id; repeatable --item ProductId:Quantity).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        const string usage = "create-order <customerId> --item ProductId:Quantity ...";
        var customerId = args.RequireIntPositional(0, usage, "customerId");
        var itemArgs = args.Options("-i", "--item");
        if (itemArgs.Count == 0)
        {
            throw new WeirCliException("Provide at least one --item ProductId:Quantity (repeatable), e.g. --item 1:2 --item 4:10");
        }

        var items = new List<object>(itemArgs.Count);
        foreach (var item in itemArgs)
        {
            var separator = item.LastIndexOf(':');
            if (separator <= 0 || separator == item.Length - 1)
            {
                throw new WeirCliException($"Bad --item '{item}'. Use ProductId:Quantity, e.g. 1:2");
            }

            var productId = ParseInt(item[..separator], $"product id in --item '{item}'");
            var quantity = ParseInt(item[(separator + 1)..], $"quantity in --item '{item}'");
            items.Add(new { ProductId = productId, Quantity = quantity });
        }

        var body = JsonSerializer.Serialize(new { customerId, items });
        using var response = await session.Client.SendAsync(HttpMethod.Post, "orders", body, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        var orderId = Output.OutputField(response, "orderId") ?? "(none)";
        var total = Output.OutputField(response, "total") ?? "(none)";
        var count = response.ReturnValue ?? Fmt.N0(itemArgs.Count);
        AnsiConsole.MarkupLine($"[green]Created order[/] [bold]{Markup.Escape(orderId)}[/] - total=[bold]{Markup.Escape(total)}[/], items=[bold]{Markup.Escape(count)}[/]");
        return 0;
    }

    /// <summary>Parses an integer from a TVP item component, or throws a usage error.</summary>
    /// <param name="text">The raw text.</param>
    /// <param name="what">A description of the value, used in the error message.</param>
    /// <returns>The parsed integer.</returns>
    /// <exception cref="WeirCliException">The text is not a valid integer.</exception>
    private static int ParseInt(string text, string what) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new WeirCliException($"Bad {what}.");
}

/// <summary>The <c>customer-stats</c> command: customer statistics returned only through output parameters.</summary>
internal static class CustomerStatsCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="session">The active session.</param>
    /// <param name="args">The command arguments (positional customer id).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(Session session, CliArgs args)
    {
        var customerId = args.RequireIntPositional(0, "customer-stats <customerId>", "customerId");
        using var response = await session.Client.SendAsync(HttpMethod.Get, $"customers/stats?customerId={customerId}", null, CancellationToken.None);
        if (!response.IsSuccess)
        {
            return Output.Fail(response);
        }

        Output.KeyValues(response.Output, $"customer {customerId} stats");
        return 0;
    }
}
