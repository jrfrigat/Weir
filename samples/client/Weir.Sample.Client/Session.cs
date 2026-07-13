namespace Weir.Sample.Client;

/// <summary>
/// A live connection to one Weir host: the resolved URL and API key, plus a shared HTTP client the
/// interactive commands reuse across requests. Created once (from the startup arguments) and held for
/// the whole session, so the user does not re-supply the URL and key on every command.
/// </summary>
internal sealed class Session : IDisposable
{
    /// <summary>The shared client used by the interactive (non-load) commands.</summary>
    private readonly WeirClient _client;

    /// <summary>Creates a session for a host.</summary>
    /// <param name="url">The resolved base URL.</param>
    /// <param name="apiKey">The resolved API key.</param>
    public Session(string url, string apiKey)
    {
        Url = url;
        ApiKey = apiKey;
        _client = new WeirClient(url, apiKey);
    }

    /// <summary>The host base URL (for display).</summary>
    public string Url { get; }

    /// <summary>The API key sent with every request.</summary>
    public string ApiKey { get; }

    /// <summary>The shared client for one-off requests (default connection pool).</summary>
    public WeirClient Client => _client;

    /// <summary>Creates a dedicated client sized for a load test (its own larger connection pool).</summary>
    /// <param name="maxConnections">The connection-pool cap (the worker count).</param>
    /// <returns>A new client the caller must dispose.</returns>
    public WeirClient CreateClient(int maxConnections) => new(Url, ApiKey, maxConnections);

    /// <summary>Builds a session by resolving the URL and API key from the startup arguments.</summary>
    /// <param name="args">The parsed startup arguments.</param>
    /// <returns>The session.</returns>
    /// <exception cref="WeirCliException">No API key was supplied.</exception>
    public static Session Create(CliArgs args) => new(Connection.ResolveUrl(args), Connection.ResolveApiKey(args));

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
