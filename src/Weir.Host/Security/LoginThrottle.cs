using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Host.Options;

namespace Weir.Host.Security;

/// <summary>
/// Tracks failed admin sign-ins and locks a client out after too many failures. Throttling is keyed
/// by a caller-supplied client identifier (the source IP), not the username, so an attacker who knows
/// a valid username cannot lock the real admin out by submitting bad passwords.
/// </summary>
public interface ILoginThrottle
{
    /// <summary>Whether the client is currently locked out.</summary>
    /// <param name="client">The client identifier (typically the source IP).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if further attempts should be rejected right now.</returns>
    Task<bool> IsLockedAsync(string client, CancellationToken cancellationToken = default);

    /// <summary>Records a failed sign-in, locking the client once the threshold is reached.</summary>
    /// <param name="client">The client identifier that failed to sign in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordFailureAsync(string client, CancellationToken cancellationToken = default);

    /// <summary>Clears the failure state for a client after a successful sign-in.</summary>
    /// <param name="client">The client identifier that signed in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResetAsync(string client, CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="ILoginThrottle"/> whose failure and lockout state lives in the control plane, so a
/// lockout survives a restart and is shared across every instance in an HA deployment. The threshold
/// and lockout window come from <see cref="AdminSecurityOptions"/>; the atomic read-modify-write lives
/// in the store.
/// </summary>
public sealed class PersistedLoginThrottle : ILoginThrottle
{
    /// <summary>The control-plane store that holds the throttle rows.</summary>
    private readonly IControlPlaneStore _store;

    /// <summary>Clock used for lockout timing.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Failure threshold before a lockout is applied; zero or less disables throttling.</summary>
    private readonly int _maxFailures;

    /// <summary>Duration of a lockout once the threshold is reached.</summary>
    private readonly TimeSpan _lockout;

    /// <summary>Creates the throttle from the store, bound options and a clock.</summary>
    /// <param name="store">The control-plane store.</param>
    /// <param name="options">The admin security options (threshold and lockout window).</param>
    /// <param name="clock">The time provider.</param>
    public PersistedLoginThrottle(IControlPlaneStore store, IOptions<AdminSecurityOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _clock = clock;
        _maxFailures = options.Value.MaxFailedLogins;
        _lockout = TimeSpan.FromMinutes(Math.Max(1, options.Value.LockoutMinutes));
    }

    /// <inheritdoc />
    public Task<bool> IsLockedAsync(string client, CancellationToken cancellationToken = default) =>
        _maxFailures <= 0
            ? Task.FromResult(false)
            : _store.IsLoginLockedAsync(client, _clock.GetUtcNow(), cancellationToken);

    /// <inheritdoc />
    public Task RecordFailureAsync(string client, CancellationToken cancellationToken = default) =>
        _maxFailures <= 0
            ? Task.CompletedTask
            : _store.RecordLoginFailureAsync(client, _maxFailures, _lockout, _clock.GetUtcNow(), cancellationToken);

    /// <inheritdoc />
    public Task ResetAsync(string client, CancellationToken cancellationToken = default) =>
        _store.ResetLoginThrottleAsync(client, cancellationToken);
}
