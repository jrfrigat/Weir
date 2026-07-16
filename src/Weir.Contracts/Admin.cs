namespace Weir.Contracts;

/// <summary>
/// The roles an admin account can hold. <see cref="Admin"/> may read and change everything;
/// <see cref="Viewer"/> may read (dashboard, endpoints, keys, audit) but not make changes.
/// </summary>
public static class AdminRoles
{
    /// <summary>Full read and write access.</summary>
    public const string Admin = "Admin";

    /// <summary>Read-only access.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Whether a role string names a known role.</summary>
    /// <param name="role">The role to check.</param>
    /// <returns>True if the role is <see cref="Admin"/> or <see cref="Viewer"/>.</returns>
    public static bool IsValid(string? role) =>
        role is Admin or Viewer;
}

/// <summary>Admin sign-in request.</summary>
public sealed record LoginRequest
{
    /// <summary>Admin username.</summary>
    public required string Username { get; init; }

    /// <summary>Admin password.</summary>
    public required string Password { get; init; }
}

/// <summary>Admin sign-in response carrying the issued access token and a refresh token.</summary>
public sealed record LoginResponse
{
    /// <summary>The signed JWT access token (short-lived).</summary>
    public required string Token { get; init; }

    /// <summary>When the access token expires.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The authenticated username.</summary>
    public required string Username { get; init; }

    /// <summary>
    /// The opaque refresh token used to obtain a fresh access token once this one expires. Long-lived
    /// and revocable; rotated on each use. Null only for legacy callers that do not issue refresh tokens.
    /// </summary>
    public string? RefreshToken { get; init; }

    /// <summary>When the refresh token expires.</summary>
    public DateTimeOffset? RefreshTokenExpiresAt { get; init; }
}

/// <summary>Request to exchange a refresh token for a new access token (and a rotated refresh token).</summary>
public sealed record RefreshRequest
{
    /// <summary>The opaque refresh token issued at sign-in or by a prior refresh.</summary>
    public required string RefreshToken { get; init; }
}

/// <summary>Request to revoke a refresh token on sign-out.</summary>
public sealed record LogoutRequest
{
    /// <summary>The refresh token to revoke. When null or unknown, sign-out is still treated as successful.</summary>
    public string? RefreshToken { get; init; }
}

/// <summary>Request to create a new admin account.</summary>
public sealed record CreateAdminRequest
{
    /// <summary>Username for the new account.</summary>
    public required string Username { get; init; }

    /// <summary>Initial password for the new account.</summary>
    public required string Password { get; init; }

    /// <summary>Role for the new account (see <see cref="AdminRoles"/>). Defaults to <see cref="AdminRoles.Admin"/>.</summary>
    public string Role { get; init; } = AdminRoles.Admin;
}

/// <summary>Request to change an admin account's password (admin resetting another account).</summary>
public sealed record ChangePasswordRequest
{
    /// <summary>The new password.</summary>
    public required string Password { get; init; }
}

/// <summary>Request for a signed-in admin to change their own password.</summary>
public sealed record ChangeOwnPasswordRequest
{
    /// <summary>The current password, verified before the change.</summary>
    public required string CurrentPassword { get; init; }

    /// <summary>The new password.</summary>
    public required string NewPassword { get; init; }
}

/// <summary>
/// Non-secret view of a personal access token. The token secret is never stored or returned after
/// creation - only a hash and a short identifying prefix. A token authenticates its owning admin to
/// the admin API (with that admin's role), for scripted or CI/CD access.
/// </summary>
public sealed record AdminTokenInfo
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Human-friendly label, e.g. <c>ci-deploy</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Short non-secret prefix used to recognize the token, e.g. <c>weadm_ab12cd</c>.</summary>
    public required string Prefix { get; init; }

    /// <summary>When the token was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Optional expiry; null means it never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the token was last used to authenticate.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>Request for a signed-in admin to create a personal access token for their own profile.</summary>
public sealed record AdminTokenCreate
{
    /// <summary>Human-friendly label for the token.</summary>
    public required string Name { get; init; }

    /// <summary>Optional expiry. Null creates a token that does not expire.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Result of creating a personal access token. The <see cref="PlainTextToken"/> is shown exactly once.</summary>
public sealed record AdminTokenCreated
{
    /// <summary>The non-secret view of the created token.</summary>
    public required AdminTokenInfo Info { get; init; }

    /// <summary>The full secret token. Returned only at creation time and never persisted in clear.</summary>
    public required string PlainTextToken { get; init; }
}

/// <summary>The signed-in admin's identity, for the account page.</summary>
public sealed record CurrentAdmin
{
    /// <summary>Username.</summary>
    public required string Username { get; init; }

    /// <summary>Role (see <see cref="AdminRoles"/>).</summary>
    public required string Role { get; init; }
}

/// <summary>Non-secret view of a configured data connection (never exposes the connection string).</summary>
public sealed record ConnectionInfo
{
    /// <summary>Logical connection name.</summary>
    public required string Name { get; init; }

    /// <summary>Provider key, e.g. SqlServer.</summary>
    public required string Provider { get; init; }
}

/// <summary>Request to change an admin's role.</summary>
public sealed record AdminRoleRequest
{
    /// <summary>The new role (see <see cref="AdminRoles"/>).</summary>
    public required string Role { get; init; }
}

/// <summary>Request to enable or disable an admin account.</summary>
public sealed record AdminEnabledRequest
{
    /// <summary>Whether the account should be enabled.</summary>
    public required bool Enabled { get; init; }
}
