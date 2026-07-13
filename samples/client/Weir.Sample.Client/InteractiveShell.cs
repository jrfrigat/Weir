using System.Text;
using Spectre.Console;

namespace Weir.Sample.Client;

/// <summary>
/// The interactive REPL. It keeps the process open and reads command lines against a fixed session, so
/// the user can send request after request (the same commands as one-shot mode) without re-supplying the
/// URL and key or relaunching. A per-command error is printed and the loop continues; <c>exit</c> (or
/// end-of-input) leaves the shell.
/// </summary>
internal static class InteractiveShell
{
    /// <summary>Runs the shell until the user exits or input ends.</summary>
    /// <param name="session">The active session.</param>
    /// <returns>The process exit code (0).</returns>
    public static async Task<int> RunAsync(Session session)
    {
        AnsiConsole.MarkupLine($"[bold]weir-sample[/] interactive - connected to [cyan]{Markup.Escape(session.Url)}[/]");
        AnsiConsole.MarkupLine("[grey]Commands: list, get, create, import, call, load. Type 'help' for details, 'exit' to quit.[/]");
        AnsiConsole.WriteLine();

        while (true)
        {
            AnsiConsole.Markup("[green]weir[/][grey]>[/] ");
            var line = Console.ReadLine();
            if (line is null)
            {
                // End of input (Ctrl+D, or a piped script finished): leave the shell.
                break;
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                continue;
            }

            var command = tokens[0].ToLowerInvariant();
            if (command is "exit" or "quit" or "q")
            {
                break;
            }

            if (command is "clear" or "cls")
            {
                AnsiConsole.Clear();
                continue;
            }

            var args = new CliArgs(tokens.Skip(1).ToArray());
            try
            {
                await Dispatch.RunAsync(command, session, args);
            }
            catch (WeirCliException ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            }
            catch (HttpRequestException ex)
            {
                AnsiConsole.MarkupLine($"[red]Cannot reach the host:[/] {Markup.Escape(ex.Message)}");
            }
            catch (TaskCanceledException)
            {
                AnsiConsole.MarkupLine("[red]The request timed out.[/]");
            }

            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("[grey]bye.[/]");
        return 0;
    }

    /// <summary>
    /// Splits a command line into tokens on whitespace, honouring single- and double-quoted segments so
    /// a JSON body can be passed as one argument (for example <c>call widgets -X POST -b '{"a": 1}'</c>).
    /// </summary>
    /// <param name="line">The raw line.</param>
    /// <returns>The parsed tokens.</returns>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var inToken = false;
        foreach (var c in line)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                inToken = true;
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
