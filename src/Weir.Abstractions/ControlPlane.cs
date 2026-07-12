using Weir.Contracts;

namespace Weir.Abstractions;

/// <summary>
/// Raised by a control-plane store when a write violates a uniqueness constraint (for example two
/// endpoints sharing the same method and route). The host maps this to HTTP 409 Conflict.
/// </summary>
public sealed class ControlPlaneConflictException : Exception
{
    /// <summary>Creates the conflict exception.</summary>
    /// <param name="message">A human-readable description of the conflict.</param>
    /// <param name="innerException">The underlying provider exception, if any.</param>
    public ControlPlaneConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised at startup when an already-applied migration's recorded checksum does not match the shipped
/// migration script - evidence that a shipped migration was edited (which is forbidden) or the schema
/// history was tampered with. Startup fails fast rather than applying an inconsistent schema.
/// </summary>
public sealed class ControlPlaneMigrationException : Exception
{
    /// <summary>Creates the migration exception.</summary>
    /// <param name="message">A human-readable description of the mismatch.</param>
    public ControlPlaneMigrationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Persistence port for Weir's control-plane metadata: endpoints, API keys, scopes, admin users and
/// audit. Implementations are provider-specific (SQLite today) and own their own schema/migrations.
/// </summary>
public interface IControlPlaneStore
{
    /// <summary>Creates or upgrades the store's schema. Idempotent; safe to call on every startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    // ----- Endpoints -------------------------------------------------------------------------

    /// <summary>Returns every endpoint definition.</summary>
    Task<IReadOnlyList<EndpointDefinition>> GetEndpointsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a single endpoint, or null if not found.</summary>
    Task<EndpointDefinition?> GetEndpointAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates an endpoint and returns the stored value.</summary>
    Task<EndpointDefinition> UpsertEndpointAsync(EndpointDefinition endpoint, CancellationToken cancellationToken = default);

    /// <summary>Deletes an endpoint by id.</summary>
    Task DeleteEndpointAsync(Guid id, CancellationToken cancellationToken = default);

    // ----- API keys --------------------------------------------------------------------------

    /// <summary>Returns non-secret views of all API keys.</summary>
    Task<IReadOnlyList<ApiKeyInfo>> GetApiKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds the full key record (including scopes and status) by its stored hash.</summary>
    Task<ApiKeyRecord?> FindApiKeyByHashAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>Persists a new API key given its precomputed hash and identifying prefix.</summary>
    Task<ApiKeyInfo> CreateApiKeyAsync(ApiKeyCreate create, string hash, string prefix, CancellationToken cancellationToken = default);

    /// <summary>Disables an API key by id.</summary>
    Task RevokeApiKeyAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Records the last-used timestamp for a key (best-effort, may be throttled by the store).</summary>
    Task TouchApiKeyAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken = default);

    // ----- Scopes ----------------------------------------------------------------------------

    /// <summary>Returns all defined scopes.</summary>
    Task<IReadOnlyList<Scope>> GetScopesAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates a scope.</summary>
    Task UpsertScopeAsync(Scope scope, CancellationToken cancellationToken = default);

    /// <summary>Deletes a scope by name.</summary>
    Task DeleteScopeAsync(string name, CancellationToken cancellationToken = default);

    // ----- Admin users -----------------------------------------------------------------------

    /// <summary>Finds an admin account (including its password hash) by username.</summary>
    Task<AdminUserRecord?> FindAdminByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Finds an admin account by id, for re-checking a bearer token's validity on each request.</summary>
    /// <param name="id">The admin id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminUserRecord?> FindAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns non-secret views of all admin accounts.</summary>
    Task<IReadOnlyList<AdminUserInfo>> GetAdminsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates an admin account with a precomputed password hash and role.</summary>
    Task<AdminUserInfo> CreateAdminAsync(string username, string passwordHash, string role, CancellationToken cancellationToken = default);

    /// <summary>Replaces an admin account's password hash.</summary>
    Task UpdateAdminPasswordAsync(Guid id, string passwordHash, CancellationToken cancellationToken = default);

    /// <summary>Records the last-login timestamp for an admin account.</summary>
    Task TouchAdminLoginAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken = default);

    // ----- Admin access tokens ---------------------------------------------------------------

    /// <summary>Returns non-secret views of an admin's personal access tokens.</summary>
    /// <param name="adminId">The owning admin's id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AdminTokenInfo>> GetAdminTokensAsync(Guid adminId, CancellationToken cancellationToken = default);

    /// <summary>Persists a new personal access token for an admin, given its precomputed hash and prefix.</summary>
    /// <param name="adminId">The owning admin's id.</param>
    /// <param name="name">Human-friendly label.</param>
    /// <param name="expiresAt">Optional expiry.</param>
    /// <param name="hash">Stored one-way hash of the token secret.</param>
    /// <param name="prefix">Identifying non-secret prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminTokenInfo> CreateAdminTokenAsync(
        Guid adminId, string name, DateTimeOffset? expiresAt, string hash, string prefix, CancellationToken cancellationToken = default);

    /// <summary>Finds the full token record (with its owning admin's identity and role) by stored hash.</summary>
    /// <param name="hash">The stored hash to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminTokenRecord?> FindAdminTokenByHashAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>Revokes (deletes) an admin's own token by id.</summary>
    /// <param name="id">The token id.</param>
    /// <param name="adminId">The owning admin's id; a token owned by another admin is not revoked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RevokeAdminTokenAsync(Guid id, Guid adminId, CancellationToken cancellationToken = default);

    /// <summary>Records the last-used timestamp for a token (best-effort, may be throttled by the store).</summary>
    /// <param name="id">The token id.</param>
    /// <param name="usedAt">When it was used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TouchAdminTokenAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken = default);

    // ----- Audit -----------------------------------------------------------------------------

    /// <summary>Appends an audit entry.</summary>
    Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Queries the audit log.</summary>
    Task<IReadOnlyList<AuditEntry>> QueryAuditAsync(AuditQuery query, CancellationToken cancellationToken = default);

    /// <summary>Deletes audit entries older than the given cut-off, bounding the audit table's growth.</summary>
    /// <param name="olderThan">Entries with a timestamp strictly before this are deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of entries deleted.</returns>
    Task<int> PruneAuditAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);

    // ----- Request log -----------------------------------------------------------------------

    /// <summary>Appends a data-plane request-log entry.</summary>
    /// <param name="entry">The request-log entry to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendRequestLogAsync(RequestLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Queries the data-plane request log (newest first, keyset-paged).</summary>
    /// <param name="query">The filter and paging cursor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching entries, most recent first.</returns>
    Task<IReadOnlyList<RequestLogEntry>> QueryRequestLogAsync(RequestLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Deletes request-log entries older than the given cut-off, bounding the table's growth.</summary>
    /// <param name="olderThan">Entries with a timestamp strictly before this are deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of entries deleted.</returns>
    Task<int> PruneRequestLogAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);

    // ----- Login throttle --------------------------------------------------------------------

    /// <summary>
    /// Whether a client is currently locked out from signing in. Persisting the lockout in the control
    /// plane means it survives a restart and is shared across every instance in an HA deployment.
    /// </summary>
    /// <param name="client">The client identifier (typically the source IP).</param>
    /// <param name="now">The current time, compared against the stored lockout expiry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if sign-in attempts should be rejected right now.</returns>
    Task<bool> IsLoginLockedAsync(string client, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically records a failed sign-in for a client and applies a lockout once the failure count
    /// reaches <paramref name="maxFailures"/>. A no-op when <paramref name="maxFailures"/> is not positive.
    /// </summary>
    /// <param name="client">The client identifier that failed to sign in.</param>
    /// <param name="maxFailures">Failures allowed before a lockout is applied.</param>
    /// <param name="lockout">Lockout duration once the threshold is reached.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordLoginFailureAsync(string client, int maxFailures, TimeSpan lockout, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Clears a client's failure/lockout state after a successful sign-in.</summary>
    /// <param name="client">The client identifier that signed in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResetLoginThrottleAsync(string client, CancellationToken cancellationToken = default);

    /// <summary>Deletes throttle rows that are neither locked nor recently active, bounding the table.</summary>
    /// <param name="staleBefore">Rows last active before this and not currently locked are removed.</param>
    /// <param name="now">The current time, used to keep currently-locked rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> PruneLoginThrottleAsync(DateTimeOffset staleBefore, DateTimeOffset now, CancellationToken cancellationToken = default);

    // ----- Refresh tokens --------------------------------------------------------------------

    /// <summary>Stores a new (hashed) admin refresh token.</summary>
    /// <param name="id">The token's stable identifier.</param>
    /// <param name="adminId">The owning admin account.</param>
    /// <param name="hash">The token's stored hash (the raw token is never persisted).</param>
    /// <param name="expiresAt">When the refresh token expires.</param>
    /// <param name="now">The creation time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateRefreshTokenAsync(Guid id, Guid adminId, string hash, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Looks up a refresh token by its hash.</summary>
    /// <param name="hash">The stored hash of the presented token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token record, or null when no token matches.</returns>
    Task<RefreshTokenRecord?> FindRefreshTokenAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>Revokes a single refresh token (for example on rotation or sign-out).</summary>
    /// <param name="id">The token id.</param>
    /// <param name="now">The revocation time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RevokeRefreshTokenAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Revokes every refresh token for an admin (for example on a password change).</summary>
    /// <param name="adminId">The owning admin account.</param>
    /// <param name="now">The revocation time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RevokeRefreshTokensForAdminAsync(Guid adminId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Deletes expired or long-revoked refresh tokens, bounding the table's growth.</summary>
    /// <param name="now">The current time; tokens that expired or were revoked before this are removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> PruneRefreshTokensAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    // ----- Runtime settings ------------------------------------------------------------------

    /// <summary>
    /// Returns the persisted runtime-settings document (an opaque JSON string), or null if none has
    /// been saved yet. Stored as a single row so runtime-tunable settings survive restarts and reach
    /// every instance in a shared-control-plane (HA) deployment.
    /// </summary>
    Task<string?> GetSettingsJsonAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the runtime-settings document (an opaque JSON string), replacing any prior value.</summary>
    /// <param name="json">The serialized settings document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveSettingsJsonAsync(string json, CancellationToken cancellationToken = default);
}

/// <summary>Full API-key record used for authentication. Includes the stored hash - never leaves the server.</summary>
public sealed record ApiKeyRecord
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Human-friendly label.</summary>
    public required string Name { get; init; }

    /// <summary>Identifying non-secret prefix.</summary>
    public required string Prefix { get; init; }

    /// <summary>Stored one-way hash of the secret key material.</summary>
    public required string Hash { get; init; }

    /// <summary>Granted scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Resource grants limiting which procedures the key may call. Empty means unrestricted.</summary>
    public IReadOnlyList<Weir.Contracts.ApiKeyGrant> Grants { get; init; } = [];

    /// <summary>Whether the key may authenticate.</summary>
    public bool Enabled { get; init; }

    /// <summary>Optional expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Optional per-key rate limit, in requests per minute.</summary>
    public int? RateLimitPerMinute { get; init; }
}

/// <summary>
/// Full personal-access-token record used for authentication. Carries the owning admin's identity
/// and current role so the token authenticates as that admin. Never leaves the server.
/// </summary>
public sealed record AdminTokenRecord
{
    /// <summary>Token id.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning admin's id.</summary>
    public Guid AdminId { get; init; }

    /// <summary>Owning admin's username.</summary>
    public required string Username { get; init; }

    /// <summary>Owning admin's current role.</summary>
    public string Role { get; init; } = Weir.Contracts.AdminRoles.Admin;

    /// <summary>Whether the owning admin account may sign in; a disabled admin's tokens do not authenticate.</summary>
    public bool AdminEnabled { get; init; }

    /// <summary>Optional expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>A stored admin refresh token, looked up by hash to mint fresh access tokens.</summary>
public sealed record RefreshTokenRecord
{
    /// <summary>Token id.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning admin's id.</summary>
    public Guid AdminId { get; init; }

    /// <summary>When the refresh token expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Whether the token has been revoked (rotated out or signed out).</summary>
    public bool Revoked { get; init; }
}

/// <summary>Full admin-account record used for sign-in. Includes the password hash - never leaves the server.</summary>
public sealed record AdminUserRecord
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Login name.</summary>
    public required string Username { get; init; }

    /// <summary>Stored password hash.</summary>
    public required string PasswordHash { get; init; }

    /// <summary>Account role (see <see cref="Weir.Contracts.AdminRoles"/>).</summary>
    public string Role { get; init; } = Weir.Contracts.AdminRoles.Admin;

    /// <summary>Whether the account may sign in.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Monotonic token version stamped into issued JWTs and re-checked on every request. Bumping it
    /// (on a password change, role change or disable) immediately invalidates all previously issued
    /// access tokens for the account.
    /// </summary>
    public int TokenVersion { get; init; }
}
