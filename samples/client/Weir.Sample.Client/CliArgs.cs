using System.Globalization;

namespace Weir.Sample.Client;

/// <summary>
/// A tiny command-line parser used by every sample command. It splits raw tokens into positional
/// arguments, valued options (<c>--name value</c>, <c>--name=value</c> or <c>-n value</c>) and boolean
/// flags. A valued option consumes the following token unless that token itself starts with '-', so
/// option values must not begin with a dash (none of the sample commands need one).
/// </summary>
internal sealed class CliArgs
{
    /// <summary>Positional arguments, in order.</summary>
    private readonly List<string> _positionals = [];

    /// <summary>Valued options keyed by their name (including the leading dashes); repeated options accumulate.</summary>
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names seen as bare boolean flags.</summary>
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses the command's tokens (the arguments after the command name).</summary>
    /// <param name="tokens">The raw tokens to parse.</param>
    public CliArgs(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!IsOption(token))
            {
                _positionals.Add(token);
                continue;
            }

            var equals = token.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                Add(token[..equals], token[(equals + 1)..]);
            }
            else if (i + 1 < tokens.Count && !IsOption(tokens[i + 1]))
            {
                Add(token, tokens[++i]);
            }
            else
            {
                _flags.Add(token);
            }
        }
    }

    /// <summary>The number of positional arguments.</summary>
    public int PositionalCount => _positionals.Count;

    /// <summary>Returns the positional at <paramref name="index"/>, or null when it is absent.</summary>
    /// <param name="index">Zero-based positional index.</param>
    /// <returns>The value, or null.</returns>
    public string? Positional(int index) => index < _positionals.Count ? _positionals[index] : null;

    /// <summary>Reads a required positional as an integer, throwing a usage error when it is missing or invalid.</summary>
    /// <param name="index">Zero-based positional index.</param>
    /// <param name="usage">The usage string shown when the positional is missing.</param>
    /// <param name="name">The value's name, used in the "not a valid ..." message.</param>
    /// <returns>The parsed integer.</returns>
    /// <exception cref="WeirCliException">The positional is missing or not an integer.</exception>
    public int RequireIntPositional(int index, string usage, string name)
    {
        var text = Positional(index) ?? throw new WeirCliException($"Usage: {usage}");
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new WeirCliException($"'{text}' is not a valid {name}.");
    }

    /// <summary>Returns the last value supplied for any of <paramref name="names"/>, or null.</summary>
    /// <param name="names">The accepted names for the option (aliases).</param>
    /// <returns>The value, or null when the option was not given.</returns>
    public string? Option(params string[] names)
    {
        foreach (var name in names)
        {
            if (_options.TryGetValue(name, out var values) && values.Count > 0)
            {
                return values[^1];
            }
        }

        return null;
    }

    /// <summary>Returns every value supplied for any of <paramref name="names"/> (for repeated options).</summary>
    /// <param name="names">The accepted names for the option (aliases).</param>
    /// <returns>All values, in order; empty when the option was not given.</returns>
    public IReadOnlyList<string> Options(params string[] names)
    {
        var result = new List<string>();
        foreach (var name in names)
        {
            if (_options.TryGetValue(name, out var values))
            {
                result.AddRange(values);
            }
        }

        return result;
    }

    /// <summary>Whether any of <paramref name="names"/> was supplied as a boolean flag.</summary>
    /// <param name="names">The accepted names for the flag (aliases).</param>
    /// <returns>True if the flag was present.</returns>
    public bool Flag(params string[] names) => names.Any(_flags.Contains);

    /// <summary>Reads an integer option, returning <paramref name="fallback"/> when it is absent.</summary>
    /// <param name="fallback">The value used when the option is not given.</param>
    /// <param name="names">The accepted names for the option (aliases).</param>
    /// <returns>The parsed integer, or the fallback.</returns>
    /// <exception cref="WeirCliException">The option was given but is not a valid integer.</exception>
    public int IntOption(int fallback, params string[] names)
    {
        var raw = Option(names);
        if (raw is null)
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new WeirCliException($"'{raw}' is not a valid integer for {names[0]}.");
    }

    /// <summary>Whether a token names an option (starts with '-' and is more than just "-").</summary>
    /// <param name="token">The token to test.</param>
    /// <returns>True when the token is an option name.</returns>
    private static bool IsOption(string token) => token.Length > 1 && token[0] == '-';

    /// <summary>Records a valued option under its name.</summary>
    /// <param name="name">The option name (with dashes).</param>
    /// <param name="value">The option value.</param>
    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            values = [];
            _options[name] = values;
        }

        values.Add(value);
    }
}
