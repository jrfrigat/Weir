using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Weir.Client;

/// <summary>How a <see cref="WeirClient"/> reaches its gateway and identifies itself.</summary>
public sealed class WeirClientOptions
{
    /// <summary>The header a Weir gateway reads the API key from.</summary>
    public const string DefaultApiKeyHeader = "X-Api-Key";

    /// <summary>
    /// The gateway origin, e.g. <c>https://weir.internal</c>. Routes are relative to it, so it needs
    /// no <c>/api</c> suffix: the endpoint routes already carry theirs.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// The API key sent with every request. Leave it null when the caller supplies credentials another
    /// way - a delegating handler of its own, or a gateway that authenticates by network position.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>The header the key is sent in. Defaults to <see cref="DefaultApiKeyHeader"/>.</summary>
    public string ApiKeyHeader { get; set; } = DefaultApiKeyHeader;

    /// <summary>
    /// Per-request timeout. Generous by default because a Weir call is a database call: the gateway
    /// enforces its own timeout and answers with a problem body, which is a better failure than a
    /// client-side cancellation that says only that time ran out.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Whether to ask for compressed responses. On by default: a result set is JSON, which compresses
    /// heavily, and the gateway only compresses what is worth compressing.
    /// </summary>
    public bool AcceptCompression { get; set; } = true;
}

/// <summary>Registers a <see cref="WeirClient"/> and the <see cref="HttpClient"/> behind it.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The name of the registered <see cref="HttpClient"/>, for a caller that wants to add handlers.</summary>
    public const string HttpClientName = "Weir";

    /// <summary>
    /// Registers <see cref="WeirClient"/> against a named, pooled <see cref="HttpClient"/>. Returns the
    /// builder so the caller can add its own handlers - a retry policy, a tracing handler, a bearer
    /// token - on top.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the gateway address, the API key and the timeout.</param>
    /// <returns>The HTTP client builder, for further configuration.</returns>
    public static IHttpClientBuilder AddWeirClient(this IServiceCollection services, Action<WeirClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return services.AddWeirClientCore();
    }

    /// <summary>
    /// Registers <see cref="WeirClient"/> with options bound from configuration, e.g. a <c>Weir</c>
    /// section holding <c>BaseAddress</c> and <c>ApiKey</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration section to bind.</param>
    /// <returns>The HTTP client builder, for further configuration.</returns>
    public static IHttpClientBuilder AddWeirClient(
        this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<WeirClientOptions>(configuration);
        return services.AddWeirClientCore();
    }

    /// <summary>Shared registration: validates the options, then configures the client from them.</summary>
    private static IHttpClientBuilder AddWeirClientCore(this IServiceCollection services)
    {
        services.AddOptions<WeirClientOptions>()
            .Validate(
                options => options.BaseAddress is not null,
                "Weir client: BaseAddress is required. Set it to the gateway origin, e.g. https://weir.internal.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ApiKeyHeader),
                "Weir client: ApiKeyHeader must not be empty.")
            .ValidateOnStart();

        return services.AddHttpClient<WeirClient>(HttpClientName, (provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<WeirClientOptions>>().Value;
            http.BaseAddress = options.BaseAddress;
            http.Timeout = options.Timeout;

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation(options.ApiKeyHeader, options.ApiKey);
            }

            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (options.AcceptCompression)
            {
                http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
                http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            }
        });
    }
}
