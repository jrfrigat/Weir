using System.Text.Json;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>
/// Holds the current runtime-tunable system settings. Seeded from <c>appsettings.json</c> (via
/// <see cref="WeirDataPlaneOptions"/>) and overlaid with the persisted control-plane document on
/// <see cref="InitializeAsync"/>; components read <see cref="Current"/> on each use so an update from
/// the admin panel takes effect without a restart.
/// </summary>
public interface IRuntimeSettings
{
    /// <summary>The current settings snapshot. Reads are lock-free; each update swaps a new immutable value.</summary>
    WeirSystemSettings Current { get; }

    /// <summary>Loads the persisted settings document, overlaying it on the seeded defaults.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists and applies a new settings snapshot.</summary>
    /// <param name="settings">The new settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateAsync(WeirSystemSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IRuntimeSettings"/> backed by the control-plane store.</summary>
public sealed class RuntimeSettings : IRuntimeSettings
{
    /// <summary>JSON options for the persisted settings document.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The control-plane store the settings document is persisted in.</summary>
    private readonly IControlPlaneStore _store;

    /// <summary>The current snapshot; published atomically by reference on each update.</summary>
    private volatile WeirSystemSettings _current;

    /// <summary>Creates the service, seeding the snapshot from the bound data-plane options.</summary>
    /// <param name="store">The control-plane store.</param>
    /// <param name="options">The bound data-plane options used as the seed / default values.</param>
    public RuntimeSettings(IControlPlaneStore store, IOptions<WeirDataPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        var seed = options.Value;
        _current = new WeirSystemSettings
        {
            MaxRows = seed.MaxRows,
            RequestTimeoutSeconds = seed.RequestTimeoutSeconds,
            MaxTvpRows = seed.MaxTvpRows,
            DefaultApiKeyRateLimitPerMinute = seed.DefaultApiKeyRateLimitPerMinute,
            MaxConcurrentRequestsPerConnection = seed.MaxConcurrentRequestsPerConnection,
            CircuitBreakerFailureThreshold = seed.CircuitBreakerFailureThreshold,
            CircuitBreakerResetSeconds = seed.CircuitBreakerResetSeconds,
            ResponseCacheMaxBytes = seed.ResponseCacheMaxBytes,
        };
    }

    /// <inheritdoc />
    public WeirSystemSettings Current => _current;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var json = await _store.GetSettingsJsonAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<WeirSystemSettings>(json, Json);
            if (stored is not null)
            {
                _current = stored;
            }
        }
        catch (JsonException)
        {
            // A malformed document must not prevent startup; keep the seeded defaults.
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(WeirSystemSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _store.SaveSettingsJsonAsync(JsonSerializer.Serialize(settings, Json), cancellationToken);
        _current = settings;
    }
}
