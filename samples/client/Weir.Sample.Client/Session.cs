using Weir.Client;

namespace Weir.Sample.Client;

/// <summary>
/// A live connection to one Weir host, held for the whole session: the resolved URL and API key plus the
/// client package (<c>FrigaT.Weir.Client</c>) every command calls through. Going through the package is
/// the point - the sample is the example of what a caller writes, and the package is what a caller takes
/// instead of hand-rolling one - so running the sample exercises the package itself.
/// </summary>
internal sealed class Session : IDisposable
{
    /// <summary>The underlying HTTP client, carrying the base address and the API key header.</summary>
    private readonly HttpClient _http;

    /// <summary>The package client the commands call.</summary>
    private readonly WeirClient _client;

    /// <summary>Creates a session for a host.</summary>
    /// <param name="url">The resolved base URL.</param>
    /// <param name="apiKey">The resolved API key.</param>
    public Session(string url, string apiKey)
    {
        Url = url;
        ApiKey = apiKey;
        _http = CreateHttp(url, apiKey, maxConnections: 0);
        _client = new WeirClient(_http);
    }

    /// <summary>The host base URL (for display).</summary>
    public string Url { get; }

    /// <summary>The API key sent with every request.</summary>
    public string ApiKey { get; }

    /// <summary>The package client the commands call.</summary>
    public WeirClient Client => _client;

    /// <summary>
    /// The transport behind the package client. The streaming check times the bytes of a body as they
    /// arrive and the load test drives its own workers, so both need the transport itself - which the
    /// package hands over for exactly that ("for a caller that needs to reach past this one").
    /// </summary>
    public HttpClient Http => _http;

    /// <summary>Builds a route the package can call: the data plane lives under <c>/api</c>.</summary>
    /// <param name="route">The route as written on the command line, e.g. <c>widgets</c>.</param>
    /// <returns>The route relative to the gateway origin.</returns>
    public static string Api(string route) => "api/" + route.TrimStart('/');

    /// <summary>Creates a client with its own larger connection pool, for the load test.</summary>
    /// <param name="maxConnections">The connection-pool cap (the worker count).</param>
    /// <returns>A new client the caller must dispose.</returns>
    public HttpClient CreateHttp(int maxConnections) => CreateHttp(Url, ApiKey, maxConnections);

    /// <summary>Builds a session by resolving the URL and API key from the startup arguments.</summary>
    /// <param name="args">The parsed startup arguments.</param>
    /// <returns>The session.</returns>
    /// <exception cref="WeirCliException">No API key was supplied.</exception>
    public static Session Create(CliArgs args) => new(Connection.ResolveUrl(args), Connection.ResolveApiKey(args));

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>Builds an HTTP client pointed at the host and carrying the API key.</summary>
    /// <param name="url">The base URL.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="maxConnections">Connection-pool cap; 0 uses the default.</param>
    /// <returns>The client, which owns its handler.</returns>
    private static HttpClient CreateHttp(string url, string apiKey, int maxConnections)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        if (maxConnections > 0)
        {
            handler.MaxConnectionsPerServer = maxConnections;
        }

        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/"),
            Timeout = TimeSpan.FromSeconds(100),
        };
        http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return http;
    }
}
