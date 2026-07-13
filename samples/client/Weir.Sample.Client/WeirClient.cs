using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Weir.Sample.Client;

/// <summary>
/// A minimal HTTP client for the Weir data plane. It calls <c>/api/{route}</c> with the API key in the
/// <c>X-Api-Key</c> header, exactly as any external consumer would. <see cref="SendAsync"/> parses the
/// JSON envelope for the interactive commands; <see cref="MeasureAsync"/> times a request and drains the
/// body without parsing, for the load test.
/// </summary>
internal sealed class WeirClient : IDisposable
{
    /// <summary>The underlying HTTP client (owns and disposes its handler).</summary>
    private readonly HttpClient _http;

    /// <summary>Creates a client targeting a Weir host.</summary>
    /// <param name="baseUrl">The host base URL, e.g. <c>http://localhost:8080</c>.</param>
    /// <param name="apiKey">The API key sent as <c>X-Api-Key</c>.</param>
    /// <param name="maxConnections">Connection-pool cap (0 uses the default); raised to the worker count for load tests.</param>
    /// <param name="timeout">Per-request timeout (defaults to 100 seconds).</param>
    public WeirClient(string baseUrl, string apiKey, int maxConnections = 0, TimeSpan? timeout = null)
    {
        var normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        if (maxConnections > 0)
        {
            handler.MaxConnectionsPerServer = maxConnections;
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(normalized),
            Timeout = timeout ?? TimeSpan.FromSeconds(100),
        };
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Sends one request and parses the JSON envelope (or problem+json body).</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="route">The route beneath <c>/api/</c> (may include a query string).</param>
    /// <param name="jsonBody">The request body, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed response; the caller must dispose it.</returns>
    public async Task<WeirResponse> SendAsync(HttpMethod method, string route, string? jsonBody, CancellationToken cancellationToken)
    {
        using var request = Build(method, route, jsonBody);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        }
        catch (JsonException)
        {
            document = JsonDocument.Parse("{}");
        }

        return new WeirResponse((int)response.StatusCode, raw, document);
    }

    /// <summary>Sends one request for load testing: times it and streams the body to a discard buffer.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="route">The route beneath <c>/api/</c> (may include a query string).</param>
    /// <param name="jsonBody">The request body, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token (cancelled when the load window ends).</param>
    /// <returns>The measured outcome.</returns>
    public async Task<RequestOutcome> MeasureAsync(HttpMethod method, string route, string? jsonBody, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            using var request = Build(method, route, jsonBody);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            long bytes = 0;
            var buffer = new byte[16 * 1024];
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    bytes += read;
                }
            }

            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new RequestOutcome((int)response.StatusCode, elapsed, bytes, null);
        }
        catch (OperationCanceledException)
        {
            // The load window ended (or the caller cancelled): propagate so the worker stops cleanly
            // without recording a half-finished request as a failure.
            throw;
        }
        catch (HttpRequestException ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new RequestOutcome(0, elapsed, 0, ex.GetType().Name);
        }
        catch (IOException ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new RequestOutcome(0, elapsed, 0, ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>Builds the request message for a route and optional JSON body.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="route">The route beneath <c>/api/</c>.</param>
    /// <param name="jsonBody">The request body, or null.</param>
    /// <returns>The request message.</returns>
    private static HttpRequestMessage Build(HttpMethod method, string route, string? jsonBody)
    {
        var request = new HttpRequestMessage(method, "api/" + route.TrimStart('/'));
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return request;
    }
}

/// <summary>The measured outcome of one load-test request.</summary>
/// <param name="StatusCode">The HTTP status, or 0 when the request failed before a response.</param>
/// <param name="ElapsedMs">The wall-clock duration in milliseconds.</param>
/// <param name="Bytes">The response body size in bytes.</param>
/// <param name="Error">The exception type name on failure, otherwise null.</param>
internal readonly record struct RequestOutcome(int StatusCode, double ElapsedMs, long Bytes, string? Error);
