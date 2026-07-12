namespace Weir.Host.Options;

/// <summary>
/// Admin sign-in protection, bound from <c>Weir:Admin</c>. After too many failed logins for a
/// username the account is temporarily locked to slow down brute-force attempts. The lockout is
/// per-instance (in-memory).
/// </summary>
public sealed class AdminSecurityOptions
{
    /// <summary>Failed sign-ins before a username is locked out. Zero or less disables lockout.</summary>
    public int MaxFailedLogins { get; set; } = 5;

    /// <summary>How long a locked-out username stays locked, in minutes.</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Maximum personal access tokens one admin may hold at once. Zero or less means unlimited.</summary>
    public int MaxTokensPerAdmin { get; set; } = 20;

    /// <summary>When true, personal access tokens must carry an expiry; a never-expiring token is rejected.</summary>
    public bool RequireTokenExpiry { get; set; }
}
