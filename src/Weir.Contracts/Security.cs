namespace Weir.Contracts;

/// <summary>
/// A resource grant on an API key: which procedures the key may call, addressed by connection
/// (a named connection is one server + database), schema and object. Any level set to <c>*</c> (or
/// left empty) matches everything at that level, so one grant can cover a whole connection, a whole
/// schema, or a single procedure. A key with no grants is unrestricted (still subject to scopes); a
/// key with one or more grants may call only endpoints that at least one of its grants matches.
/// </summary>
public sealed record ApiKeyGrant
{
    /// <summary>Named data connection (server + database), or <c>*</c> for any connection.</summary>
    public string Connection { get; init; } = "*";

    /// <summary>Schema, or <c>*</c> for any schema.</summary>
    public string Schema { get; init; } = "*";

    /// <summary>Object (procedure / function) name, or <c>*</c> for any object.</summary>
    public string ObjectName { get; init; } = "*";

    /// <summary>Whether this grant allows a call to the given connection, schema and object.</summary>
    /// <param name="connection">The endpoint's connection name.</param>
    /// <param name="schema">The endpoint's schema.</param>
    /// <param name="objectName">The endpoint's object name.</param>
    /// <returns>True if every level matches (a wildcard level always matches).</returns>
    public bool Allows(string connection, string schema, string objectName) =>
        LevelMatches(Connection, connection)
        && LevelMatches(Schema, schema)
        && LevelMatches(ObjectName, objectName);

    /// <summary>Matches one grant level: a wildcard or empty pattern matches any value.</summary>
    private static bool LevelMatches(string pattern, string value) =>
        string.IsNullOrEmpty(pattern) || pattern == "*" || string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Non-secret view of an API key, safe to show in the admin UI. The key material itself is never
/// stored or returned after creation - only a hash and a short identifying prefix.
/// </summary>
public sealed record ApiKeyInfo
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Human-friendly label for the key.</summary>
    public required string Name { get; init; }

    /// <summary>Short non-secret prefix used to recognize the key, e.g. <c>wk_live_ab12</c>.</summary>
    public required string Prefix { get; init; }

    /// <summary>Scopes granted to the key.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Resource grants limiting which procedures the key may call. Empty means unrestricted.</summary>
    public IReadOnlyList<ApiKeyGrant> Grants { get; init; } = [];

    /// <summary>Whether the key may authenticate.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Optional expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the key was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the key was last used to authenticate.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Optional per-key rate limit, in requests per minute.</summary>
    public int? RateLimitPerMinute { get; init; }
}

/// <summary>Request to create a new API key.</summary>
public sealed record ApiKeyCreate
{
    /// <summary>Human-friendly label.</summary>
    public required string Name { get; init; }

    /// <summary>Scopes to grant.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Resource grants limiting which procedures the key may call. Empty means unrestricted.</summary>
    public IReadOnlyList<ApiKeyGrant> Grants { get; init; } = [];

    /// <summary>Optional expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Optional per-key rate limit, in requests per minute.</summary>
    public int? RateLimitPerMinute { get; init; }
}

/// <summary>Result of creating an API key. The <see cref="PlainTextKey"/> is shown exactly once.</summary>
public sealed record ApiKeyCreated
{
    /// <summary>The non-secret view of the created key.</summary>
    public required ApiKeyInfo Info { get; init; }

    /// <summary>The full secret key. Returned only at creation time and never persisted in clear.</summary>
    public required string PlainTextKey { get; init; }
}

/// <summary>A named permission that can be attached to API keys and required by endpoints.</summary>
public sealed record Scope
{
    /// <summary>Scope name, e.g. <c>orders:read</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description shown in the admin UI.</summary>
    public string? Description { get; init; }
}

/// <summary>Non-secret view of an admin account.</summary>
public sealed record AdminUserInfo
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Login name.</summary>
    public required string Username { get; init; }

    /// <summary>Account role (see <see cref="AdminRoles"/>).</summary>
    public string Role { get; init; } = AdminRoles.Admin;

    /// <summary>Whether the account may sign in.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>When the account was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the account last signed in.</summary>
    public DateTimeOffset? LastLoginAt { get; init; }
}
