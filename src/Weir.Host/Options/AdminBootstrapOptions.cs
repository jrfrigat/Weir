namespace Weir.Host.Options;

/// <summary>
/// Bootstrap credentials for the first admin account, bound from <c>Weir:Admin</c>. When no admin
/// exists yet and both values are present, the account is created on startup.
/// </summary>
public sealed class AdminBootstrapOptions
{
    /// <summary>Username of the bootstrap admin.</summary>
    public string? Username { get; set; }

    /// <summary>Password of the bootstrap admin (set via a secret, not committed config).</summary>
    public string? Password { get; set; }
}
