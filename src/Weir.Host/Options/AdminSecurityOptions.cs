namespace Weir.Host.Options;

/// <summary>
/// Admin sign-in protection, bound from <c>Weir:Admin</c>. After too many failed sign-ins from one
/// caller address that address is temporarily locked, which slows brute force down without letting a
/// bad password lock a real admin out. The lockout is persisted in the control plane, so it survives a
/// restart and is shared across instances.
/// <para>
/// Because it keys on the caller's address, it only works if Weir sees the real one: behind a reverse
/// proxy that means <c>Weir:Network:TrustedProxies</c> has to name the proxy (see
/// <see cref="NetworkOptions"/>), or every caller shares the proxy's address and one attacker locks
/// everyone out at once.
/// </para>
/// </summary>
public sealed class AdminSecurityOptions
{
    /// <summary>Failed sign-ins from one caller address before it is locked out. Zero or less disables lockout.</summary>
    public int MaxFailedLogins { get; set; } = 5;

    /// <summary>How long a locked-out caller address stays locked, in minutes.</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Maximum personal access tokens one admin may hold at once. Zero or less means unlimited.</summary>
    public int MaxTokensPerAdmin { get; set; } = 20;

    /// <summary>When true, personal access tokens must carry an expiry; a never-expiring token is rejected.</summary>
    public bool RequireTokenExpiry { get; set; }
}
