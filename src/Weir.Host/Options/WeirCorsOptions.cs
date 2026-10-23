using Weir.Contracts;

namespace Weir.Host.Options;

/// <summary>
/// The server-wide CORS policy for the data plane, bound from <c>Weir:Cors</c>. It is the default
/// every key falls back to: a key whose own <c>ApiKeyCorsPolicy</c> is null is answered with this.
/// <para>
/// <see cref="AllowedOrigins"/> is the setting this section started with, and it keeps its meaning
/// exactly: list the origins and browser clients from them may call the data plane. Everything else
/// here is new and optional, so a configuration file written before this existed behaves as it did.
/// </para>
/// </summary>
public sealed class WeirCorsOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Weir:Cors";

    /// <summary>
    /// Origins allowed to call the data plane from a browser, each a scheme, host and port - for
    /// example <c>https://app.example.com</c>. Empty (the default) leaves CORS off entirely.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = [];

    /// <summary>Methods the preflight answer permits. Empty answers with the data plane's own methods.</summary>
    public IList<string> AllowedMethods { get; } = [];

    /// <summary>
    /// Request headers the preflight answer permits. Empty echoes what the preflight asked for, which
    /// is what a browser client needs: it sends <c>X-Api-Key</c> and <c>Content-Type</c>.
    /// </summary>
    public IList<string> AllowedHeaders { get; } = [];

    /// <summary>Response headers the browser may read, beyond the CORS-safelisted ones.</summary>
    public IList<string> ExposedHeaders { get; } = [];

    /// <summary>Whether the browser may send credentials. Never answered together with <c>*</c>.</summary>
    public bool AllowCredentials { get; set; }

    /// <summary>How long the browser may cache a preflight answer. Null leaves the browser's default.</summary>
    public int? MaxAgeSeconds { get; set; }

    /// <summary>Whether a server-wide policy is configured at all.</summary>
    public bool Enabled => AllowedOrigins.Count > 0;

    /// <summary>
    /// This section as a policy, so the server default and a key's own policy are the same shape and
    /// the middleware has one kind of thing to apply. Returns null when nothing is configured, which
    /// is the same "states no policy" that a key's null means.
    /// </summary>
    /// <returns>The policy, or null when no origins are configured.</returns>
    public ApiKeyCorsPolicy? ToPolicy() => Enabled
        ? new ApiKeyCorsPolicy
        {
            AllowedOrigins = [.. AllowedOrigins],
            AllowedMethods = [.. AllowedMethods],
            AllowedHeaders = [.. AllowedHeaders],
            ExposedHeaders = [.. ExposedHeaders],
            AllowCredentials = AllowCredentials,
            MaxAgeSeconds = MaxAgeSeconds,
        }
        : null;

    /// <summary>
    /// Checks the section for the one combination that cannot work: a wildcard origin together with
    /// credentials. The specification forbids the pair and every browser refuses it, so a deployment
    /// configured that way would look permissive and allow nothing.
    /// </summary>
    /// <param name="error">Receives the problem, in operator-facing wording.</param>
    /// <returns>True when the section is usable.</returns>
    public bool Validate(out string? error)
    {
        error = null;
        if (!AllowCredentials)
        {
            return true;
        }

        foreach (var origin in AllowedOrigins)
        {
            if (origin == "*")
            {
                error = "Weir:Cors sets AllowCredentials together with the wildcard origin '*'. The CORS "
                    + "specification forbids that pair and browsers refuse it, so name the origins "
                    + "explicitly or turn AllowCredentials off.";
                return false;
            }
        }

        return true;
    }
}
