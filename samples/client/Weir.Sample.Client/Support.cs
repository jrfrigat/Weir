using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Weir.Client;

namespace Weir.Sample.Client;

/// <summary>A user-facing error (bad arguments, a missing API key). Caught at the top level and printed cleanly.</summary>
internal sealed class WeirCliException : Exception
{
    /// <summary>Creates the exception with a message shown directly to the user.</summary>
    /// <param name="message">The user-facing message.</param>
    public WeirCliException(string message) : base(message)
    {
    }
}

/// <summary>Resolves the host URL and API key shared by every command, and builds a client from them.</summary>
internal static class Connection
{
    /// <summary>The default host URL when neither the option nor the environment variable is set.</summary>
    private const string DefaultUrl = "http://localhost:8080";

    /// <summary>Resolves the host base URL from <c>--url</c>, the <c>WEIR_URL</c> variable, or the default.</summary>
    /// <param name="args">Parsed arguments.</param>
    /// <returns>The base URL.</returns>
    public static string ResolveUrl(CliArgs args) =>
        args.Option("-u", "--url") ?? EnvOrNull("WEIR_URL") ?? DefaultUrl;

    /// <summary>Resolves the API key from <c>--api-key</c> or the <c>WEIR_API_KEY</c> variable.</summary>
    /// <param name="args">Parsed arguments.</param>
    /// <returns>The API key.</returns>
    /// <exception cref="WeirCliException">Neither the option nor the environment variable is set.</exception>
    public static string ResolveApiKey(CliArgs args) =>
        args.Option("-k", "--api-key") ?? EnvOrNull("WEIR_API_KEY")
        ?? throw new WeirCliException("No API key. Pass --api-key <KEY> or set the WEIR_API_KEY environment variable.");

    /// <summary>Returns a non-empty environment variable, or null.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value, or null when unset or blank.</returns>
    private static string? EnvOrNull(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>Invariant-culture number formatting, so output (and tests) stay locale-stable.</summary>
internal static class Fmt
{
    /// <summary>Formats an integer with thousands separators.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The formatted string.</returns>
    public static string N0(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Formats a number with one decimal place.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The formatted string.</returns>
    public static string N1(double value) => value.ToString("N1", CultureInfo.InvariantCulture);

    /// <summary>Formats a number with two decimal places.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The formatted string.</returns>
    public static string N2(double value) => value.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>Formats a byte count as a human-readable size (B, KB, MB, ...).</summary>
    /// <param name="bytes">The number of bytes.</param>
    /// <returns>The formatted size.</returns>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("N1", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

/// <summary>Shared helpers for rendering command output through Spectre.Console.</summary>
internal static class Output
{
    /// <summary>Options for pretty-printing the JSON envelope in the <c>call</c> command.</summary>
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>Renders a JSON value as a plain display string (scalars unquoted, null / absent as empty).</summary>
    /// <param name="value">The JSON value.</param>
    /// <returns>The display string.</returns>
    public static string Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText(),
    };

    /// <summary>Reads a named field from a JSON object row, or returns an empty string.</summary>
    /// <param name="row">The row object.</param>
    /// <param name="name">The field name.</param>
    /// <returns>The field value, or an empty string.</returns>
    public static string Field(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) ? Value(value) : string.Empty;

    /// <summary>Reads a field from the envelope's <c>output</c> object, or null when absent.</summary>
    /// <param name="result">The envelope.</param>
    /// <param name="name">The output field name.</param>
    /// <returns>The value, or null.</returns>
    public static string? OutputField(WeirResult result, string name)
    {
        // OutputValue<T> is an unconstrained T?, so a missing value comes back as default(JsonElement).
        var value = result.OutputValue<JsonElement>(name);
        return value.ValueKind == JsonValueKind.Undefined ? null : Value(value);
    }

    /// <summary>
    /// Renders a result set (rows of JSON objects) as a table, taking the column names and order from
    /// the first row (Weir preserves the SELECT column order). Prints a note when the set is empty.
    /// </summary>
    /// <param name="rows">The rows.</param>
    /// <param name="emptyMessage">The message shown when there are no rows.</param>
    public static void Rows(IReadOnlyList<JsonElement> rows, string emptyMessage)
    {
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(emptyMessage)}[/]");
            return;
        }

        var columns = rows[0].EnumerateObject().Select(property => property.Name).ToList();
        var table = new Table { Border = TableBorder.Rounded };
        foreach (var column in columns)
        {
            table.AddColumn(Markup.Escape(column));
        }

        foreach (var row in rows)
        {
            var cells = new string[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                cells[i] = Markup.Escape(Field(row, columns[i]));
            }

            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{Fmt.N0(rows.Count)} row(s).[/]");
    }

    /// <summary>Renders a JSON object's fields as a key/value panel (used for single rows and output params).</summary>
    /// <param name="obj">The object; a non-object renders an empty panel.</param>
    /// <param name="header">The panel header.</param>
    public static void KeyValues(JsonElement obj, string header)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                grid.AddRow($"[grey]{Markup.Escape(property.Name)}[/]", Markup.Escape(Value(property.Value)));
            }
        }

        AnsiConsole.Write(new Panel(grid) { Header = new PanelHeader(Markup.Escape(header)), Border = BoxBorder.Rounded });
    }

    /// <summary>Renders an envelope's output parameters as a key/value panel.</summary>
    /// <param name="output">The output values; null renders an empty panel.</param>
    /// <param name="header">The panel header.</param>
    public static void KeyValues(IReadOnlyDictionary<string, JsonElement>? output, string header)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        if (output is not null)
        {
            foreach (var (name, value) in output)
            {
                grid.AddRow($"[grey]{Markup.Escape(name)}[/]", Markup.Escape(Value(value)));
            }
        }

        AnsiConsole.Write(new Panel(grid) { Header = new PanelHeader(Markup.Escape(header)), Border = BoxBorder.Rounded });
    }

    /// <summary>Prints the problem a refused call came back with, and returns exit code 1.</summary>
    /// <param name="problem">The exception the package threw; its message is the problem's <c>detail</c>,
    /// which for a database failure is the SQL error text.</param>
    /// <returns>The process exit code (1).</returns>
    public static int Fail(WeirApiException problem)
    {
        AnsiConsole.MarkupLine($"[red]HTTP {(int)problem.Status}[/] - {Markup.Escape(problem.Title ?? "Request failed")}");
        if (!string.IsNullOrWhiteSpace(problem.Message))
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(problem.Message)}[/]");
        }

        return 1;
    }

    /// <summary>Pretty-prints the envelope inside a bordered panel.</summary>
    /// <param name="result">The envelope to render.</param>
    public static void Envelope(WeirResult result) =>
        AnsiConsole.Write(new Panel(Markup.Escape(JsonSerializer.Serialize(result, Pretty)))
        {
            Header = new PanelHeader("[green]envelope[/]"),
            Border = BoxBorder.Rounded,
        });
}

/// <summary>Calls the gateway through the client package and turns a refusal into a printed problem.</summary>
internal static class Gateway
{
    /// <summary>Runs a call, printing the problem and returning null when the gateway refused it.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="call">The call to make.</param>
    /// <returns>The result, or null when the call was refused.</returns>
    public static async Task<T?> TryAsync<T>(Func<Task<T>> call)
        where T : class
    {
        try
        {
            return await call();
        }
        catch (WeirApiException problem)
        {
            Output.Fail(problem);
            return null;
        }
    }

    /// <summary>Parses a JSON request body, or returns null when there is none.</summary>
    /// <param name="body">The JSON text as written on the command line, or null.</param>
    /// <returns>The parameters, or null.</returns>
    /// <exception cref="WeirCliException">The text is not valid JSON.</exception>
    public static JsonElement? ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            // Cloned, because the document that parsed it is disposed right away.
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new WeirCliException("The body is not valid JSON.");
        }
    }

    /// <summary>Sends one call by HTTP method name, with the parameters as an object.</summary>
    /// <param name="client">The package client.</param>
    /// <param name="method">The method name, as typed after <c>-X</c>.</param>
    /// <param name="path">The route relative to the gateway origin.</param>
    /// <param name="parameters">The request parameters, or null for none.</param>
    /// <returns>The envelope.</returns>
    /// <exception cref="WeirCliException">The method is not one the client can call.</exception>
    public static Task<WeirResult> SendAsync(WeirClient client, string method, string path, JsonElement? parameters) =>
        method switch
        {
            "GET" => client.GetAsync(path),
            "POST" => client.PostAsync(path, parameters),
            "PUT" => client.PutAsync(path, parameters),
            "PATCH" => client.PatchAsync(path, parameters),
            "DELETE" => client.DeleteAsync(path),
            _ => throw new WeirCliException($"Unsupported method '{method}'. Use GET, POST, PUT, PATCH or DELETE."),
        };
}
