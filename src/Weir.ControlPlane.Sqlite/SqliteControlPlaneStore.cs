using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.ControlPlane.Sqlite;

/// <summary>
/// <see cref="IControlPlaneStore"/> backed by SQLite. Opens a pooled connection per operation and
/// self-migrates on <see cref="InitializeAsync"/>. Complex fields (parameters, cache policy, scope
/// lists) are stored as JSON columns.
/// </summary>
public sealed class SqliteControlPlaneStore : IControlPlaneStore
{
    /// <summary>JSON options for the serialized columns (parameters, cache policy, scope/grant lists).</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Minimum interval between persisted last-used updates for a single key (hot-path throttle).</summary>
    private static readonly TimeSpan TouchThrottle = TimeSpan.FromSeconds(60);

    /// <summary>The SQLite connection string.</summary>
    private readonly string _connectionString;

    /// <summary>Lock-wait timeout (milliseconds) applied as <c>PRAGMA busy_timeout</c> per connection.</summary>
    private readonly int _busyTimeoutMs;

    /// <summary>Clock used for timestamps and touch throttling.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Last time each API key's last-used timestamp was actually written, for throttling.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastTouch = new();

    /// <summary>Last time each admin token's last-used timestamp was actually written, for throttling.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastTokenTouch = new();

    /// <summary>Creates the store from bound options and a clock.</summary>
    /// <param name="options">The bound SQLite options.</param>
    /// <param name="clock">Clock for timestamps.</param>
    public SqliteControlPlaneStore(IOptions<SqliteControlPlaneOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.ConnectionString;
        _busyTimeoutMs = Math.Max(0, options.Value.BusyTimeoutMs);
        _clock = clock;
    }

    /// <summary>Opens a connection, disposing it if the open fails so no connection object leaks.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open connection with per-connection pragmas applied.</returns>
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            // busy_timeout makes concurrent writers wait for the lock instead of failing immediately;
            // foreign_keys enforces referential integrity (off by default per SQLite connection).
            await conn.ExecuteAsync(
                new CommandDefinition($"PRAGMA busy_timeout={_busyTimeoutMs}; PRAGMA foreign_keys=ON;", cancellationToken: ct));
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        // journal_mode is a persistent database property and cannot be changed inside a transaction.
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;");
        await conn.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS SchemaMigrations (Version INTEGER PRIMARY KEY, Checksum TEXT NOT NULL, AppliedAt TEXT NOT NULL);");
        var version = await conn.ExecuteScalarAsync<long>("PRAGMA user_version;");

        // Verify (or backfill) the checksum of each already-applied migration. A mismatch means a shipped
        // migration was edited or the history was tampered with, so fail fast instead of trusting the schema.
        await VerifyOrBackfillChecksumsAsync(conn, (int)version, cancellationToken);

        for (var i = (int)version; i < SqliteSchema.Migrations.Length; i++)
        {
            // Apply each migration, record its checksum and advance the version pointer atomically. A crash
            // mid-migration rolls the whole step back, so the next start re-runs it cleanly instead of
            // leaving the schema half-applied (which would otherwise break a non-idempotent ADD COLUMN).
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
            await conn.ExecuteAsync(new CommandDefinition(
                SqliteSchema.Migrations[i], transaction: tx, cancellationToken: cancellationToken));
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO SchemaMigrations (Version, Checksum, AppliedAt) VALUES (@Version, @Checksum, @AppliedAt)",
                new { Version = i + 1, Checksum = Checksum(SqliteSchema.Migrations[i]), AppliedAt = Iso(_clock.GetUtcNow()) },
                transaction: tx, cancellationToken: cancellationToken));
            // PRAGMA does not accept parameters; the value is a trusted loop constant. user_version is
            // part of the transaction, so it is committed together with the migration's DDL.
            await conn.ExecuteAsync(new CommandDefinition(
                $"PRAGMA user_version = {i + 1};", transaction: tx, cancellationToken: cancellationToken));
            await tx.CommitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Compares the recorded checksum of each already-applied migration against the shipped script, and
    /// records a checksum for any applied migration that predates the checksum table (a database migrated
    /// before this feature). Throws <see cref="ControlPlaneMigrationException"/> on a mismatch.
    /// </summary>
    /// <param name="conn">The open connection.</param>
    /// <param name="appliedVersion">The current schema version (number of applied migrations).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task VerifyOrBackfillChecksumsAsync(SqliteConnection conn, int appliedVersion, CancellationToken cancellationToken)
    {
        var recorded = (await conn.QueryAsync<(long Version, string Checksum)>(
                new CommandDefinition("SELECT Version, Checksum FROM SchemaMigrations", cancellationToken: cancellationToken)))
            .ToDictionary(r => r.Version, r => r.Checksum);

        var upTo = Math.Min(appliedVersion, SqliteSchema.Migrations.Length);
        for (var applied = 1; applied <= upTo; applied++)
        {
            var checksum = Checksum(SqliteSchema.Migrations[applied - 1]);
            if (recorded.TryGetValue(applied, out var stored))
            {
                if (!string.Equals(stored, checksum, StringComparison.Ordinal))
                {
                    // Cross-platform fix: an older build hashed the script with its on-disk line
                    // endings (CRLF on Windows), so a database migrated there recorded the CRLF hash.
                    // Checksum() now normalizes to LF, so recompute the hash of the CRLF form and, if it
                    // matches the stored value, update the record to the canonical LF checksum instead
                    // of failing. (Comparing against the LF form would be pointless: it equals checksum.)
                    var crlf = SqliteSchema.Migrations[applied - 1].Replace("\r\n", "\n").Replace("\n", "\r\n");
                    var crlfChecksum = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(crlf)));
                    if (string.Equals(stored, crlfChecksum, StringComparison.Ordinal))
                    {
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE SchemaMigrations SET Checksum = @Checksum WHERE Version = @Version",
                            new { Version = (long)applied, Checksum = checksum },
                            cancellationToken: cancellationToken));
                    }
                    else
                    {
                        throw new ControlPlaneMigrationException(
                            $"Control-plane migration {applied} checksum mismatch: the shipped script differs from the one recorded when it was applied. Shipped migrations must never be edited.");
                    }
                }
            }
            else
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT OR IGNORE INTO SchemaMigrations (Version, Checksum, AppliedAt) VALUES (@Version, @Checksum, @AppliedAt)",
                    new { Version = applied, Checksum = checksum, AppliedAt = Iso(_clock.GetUtcNow()) },
                    cancellationToken: cancellationToken));
            }
        }
    }

    /// <summary>Computes the SHA-256 hex checksum of a migration script.</summary>
    /// <param name="script">The migration SQL.</param>
    /// <returns>The uppercase hex digest.</returns>
    private static string Checksum(string script) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n"))));

    // ===== Endpoints =============================================================================

    private const string EndpointColumns =
        "Id, Route, HttpMethod, ConnectionName, ObjectType, SchemaName, ObjectName, ResultMode, " +
        "CommandTimeoutSeconds, Enabled, SuppressMessages, CacheJson, LoggingJson, DeliveryJson, ParametersJson, RequiredScopesJson, Description, CreatedAt, UpdatedAt";

    /// <inheritdoc />
    public async Task<IReadOnlyList<EndpointDefinition>> GetEndpointsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<EndpointRow>(
            new CommandDefinition($"SELECT {EndpointColumns} FROM Endpoints ORDER BY Route", cancellationToken: cancellationToken));
        return rows.Select(MapEndpoint).ToList();
    }

    /// <inheritdoc />
    public async Task<EndpointDefinition?> GetEndpointAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<EndpointRow>(
            new CommandDefinition($"SELECT {EndpointColumns} FROM Endpoints WHERE Id = @Id",
                new { Id = id.ToString() }, cancellationToken: cancellationToken));
        return row is null ? null : MapEndpoint(row);
    }

    /// <inheritdoc />
    public async Task<EndpointDefinition> UpsertEndpointAsync(EndpointDefinition endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var now = _clock.GetUtcNow();
        var id = endpoint.Id == Guid.Empty ? Guid.NewGuid() : endpoint.Id;
        var createdAt = endpoint.CreatedAt == default ? now : endpoint.CreatedAt;

        const string sql = """
            INSERT INTO Endpoints (Id, Route, HttpMethod, ConnectionName, ObjectType, SchemaName, ObjectName, ResultMode,
                                   CommandTimeoutSeconds, Enabled, SuppressMessages, CacheJson, LoggingJson, DeliveryJson, ParametersJson, RequiredScopesJson, Description, CreatedAt, UpdatedAt)
            VALUES (@Id, @Route, @HttpMethod, @ConnectionName, @ObjectType, @SchemaName, @ObjectName, @ResultMode,
                    @CommandTimeoutSeconds, @Enabled, @SuppressMessages, @CacheJson, @LoggingJson, @DeliveryJson, @ParametersJson, @RequiredScopesJson, @Description, @CreatedAt, @UpdatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Route=excluded.Route, HttpMethod=excluded.HttpMethod, ConnectionName=excluded.ConnectionName,
                ObjectType=excluded.ObjectType, SchemaName=excluded.SchemaName, ObjectName=excluded.ObjectName,
                ResultMode=excluded.ResultMode, CommandTimeoutSeconds=excluded.CommandTimeoutSeconds, Enabled=excluded.Enabled,
                SuppressMessages=excluded.SuppressMessages, CacheJson=excluded.CacheJson, LoggingJson=excluded.LoggingJson, DeliveryJson=excluded.DeliveryJson, ParametersJson=excluded.ParametersJson,
                RequiredScopesJson=excluded.RequiredScopesJson, Description=excluded.Description, UpdatedAt=excluded.UpdatedAt;
            """;

        await using var conn = await OpenAsync(cancellationToken);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                Id = id.ToString(),
                endpoint.Route,
                endpoint.HttpMethod,
                endpoint.ConnectionName,
                ObjectType = (int)endpoint.ObjectType,
                SchemaName = endpoint.Schema,
                endpoint.ObjectName,
                ResultMode = (int)endpoint.ResultMode,
                endpoint.CommandTimeoutSeconds,
                Enabled = endpoint.Enabled ? 1 : 0,
                SuppressMessages = endpoint.SuppressMessages ? 1 : 0,
                CacheJson = JsonSerializer.Serialize(endpoint.Cache, Json),
                LoggingJson = JsonSerializer.Serialize(endpoint.Logging, Json),
                DeliveryJson = JsonSerializer.Serialize(endpoint.Delivery, Json),
                ParametersJson = JsonSerializer.Serialize(endpoint.Parameters, Json),
                RequiredScopesJson = JsonSerializer.Serialize(endpoint.RequiredScopes, Json),
                endpoint.Description,
                CreatedAt = Iso(createdAt),
                UpdatedAt = Iso(now),
            }, cancellationToken: cancellationToken));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // SQLITE_CONSTRAINT (19): another endpoint already uses this method + route.
            throw new ControlPlaneConflictException(
                $"An endpoint already exists for '{endpoint.HttpMethod} {endpoint.Route}'.", ex);
        }

        return endpoint with { Id = id, CreatedAt = createdAt, UpdatedAt = now };
    }

    /// <inheritdoc />
    public async Task DeleteEndpointAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM Endpoints WHERE Id = @Id",
            new { Id = id.ToString() }, cancellationToken: cancellationToken));
    }

    /// <summary>Maps a database row to an endpoint definition, deserializing the JSON columns.</summary>
    /// <param name="r">The row.</param>
    /// <returns>The endpoint definition.</returns>
    private static EndpointDefinition MapEndpoint(EndpointRow r) => new()
    {
        Id = Guid.Parse(r.Id),
        Route = r.Route,
        HttpMethod = r.HttpMethod,
        ConnectionName = r.ConnectionName,
        ObjectType = (DbObjectType)r.ObjectType,
        Schema = r.SchemaName,
        ObjectName = r.ObjectName,
        ResultMode = (ResultMode)r.ResultMode,
        CommandTimeoutSeconds = r.CommandTimeoutSeconds,
        Enabled = r.Enabled != 0,
        SuppressMessages = r.SuppressMessages != 0,
        Cache = JsonSerializer.Deserialize<CachePolicy>(r.CacheJson, Json) ?? new CachePolicy(),
        Logging = JsonSerializer.Deserialize<EndpointLogging>(r.LoggingJson ?? "{}", Json) ?? new EndpointLogging(),
        Delivery = JsonSerializer.Deserialize<DeliveryPolicy>(r.DeliveryJson ?? "{}", Json) ?? new DeliveryPolicy(),
        Parameters = JsonSerializer.Deserialize<List<EndpointParameter>>(r.ParametersJson, Json) ?? [],
        RequiredScopes = JsonSerializer.Deserialize<List<string>>(r.RequiredScopesJson, Json) ?? [],
        Description = r.Description,
        CreatedAt = ParseDto(r.CreatedAt),
        UpdatedAt = ParseDto(r.UpdatedAt),
    };

    // ===== API keys ==============================================================================

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKeyInfo>> GetApiKeysAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<ApiKeyRow>(new CommandDefinition(
            "SELECT Id, Name, Prefix, Hash, ScopesJson, GrantsJson, Enabled, ExpiresAt, CreatedAt, LastUsedAt, RateLimitPerMinute FROM ApiKeys ORDER BY CreatedAt DESC",
            cancellationToken: cancellationToken));
        return rows.Select(MapKeyInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<ApiKeyRecord?> FindApiKeyByHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<ApiKeyRow>(new CommandDefinition(
            "SELECT Id, Name, Prefix, Hash, ScopesJson, GrantsJson, Enabled, ExpiresAt, CreatedAt, LastUsedAt, RateLimitPerMinute FROM ApiKeys WHERE Hash = @Hash",
            new { Hash = hash }, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        return new ApiKeyRecord
        {
            Id = Guid.Parse(row.Id),
            Name = row.Name,
            Prefix = row.Prefix,
            Hash = row.Hash,
            Scopes = JsonSerializer.Deserialize<List<string>>(row.ScopesJson, Json) ?? [],
            Grants = JsonSerializer.Deserialize<List<ApiKeyGrant>>(row.GrantsJson, Json) ?? [],
            Enabled = row.Enabled != 0,
            ExpiresAt = row.ExpiresAt is null ? null : ParseDto(row.ExpiresAt),
            RateLimitPerMinute = row.RateLimitPerMinute,
        };
    }

    /// <inheritdoc />
    public async Task<ApiKeyInfo> CreateApiKeyAsync(ApiKeyCreate create, string hash, string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);
        var id = Guid.NewGuid();
        var now = _clock.GetUtcNow();

        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ApiKeys (Id, Name, Prefix, Hash, ScopesJson, GrantsJson, Enabled, ExpiresAt, CreatedAt, LastUsedAt, RateLimitPerMinute)
            VALUES (@Id, @Name, @Prefix, @Hash, @ScopesJson, @GrantsJson, 1, @ExpiresAt, @CreatedAt, NULL, @RateLimitPerMinute);
            """, new
        {
            Id = id.ToString(),
            create.Name,
            Prefix = prefix,
            Hash = hash,
            ScopesJson = JsonSerializer.Serialize(create.Scopes, Json),
            GrantsJson = JsonSerializer.Serialize(create.Grants, Json),
            ExpiresAt = create.ExpiresAt is null ? null : Iso(create.ExpiresAt.Value),
            CreatedAt = Iso(now),
            create.RateLimitPerMinute,
        }, cancellationToken: cancellationToken));

        return new ApiKeyInfo
        {
            Id = id,
            Name = create.Name,
            Prefix = prefix,
            Scopes = create.Scopes,
            Grants = create.Grants,
            Enabled = true,
            ExpiresAt = create.ExpiresAt,
            CreatedAt = now,
            RateLimitPerMinute = create.RateLimitPerMinute,
        };
    }

    /// <inheritdoc />
    public async Task RevokeApiKeyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE ApiKeys SET Enabled = 0 WHERE Id = @Id",
            new { Id = id.ToString() }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task TouchApiKeyAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        // Throttle: writing the last-used timestamp on every authenticated request would serialize the
        // single SQLite writer under load. Persist at most once per TouchThrottle window per key.
        if (_lastTouch.TryGetValue(id, out var last) && usedAt - last < TouchThrottle)
        {
            return;
        }

        _lastTouch[id] = usedAt;
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE ApiKeys SET LastUsedAt = @UsedAt WHERE Id = @Id",
            new { Id = id.ToString(), UsedAt = Iso(usedAt) }, cancellationToken: cancellationToken));
    }

    /// <summary>Maps a database row to a non-secret API key view.</summary>
    /// <param name="r">The row.</param>
    /// <returns>The API key info.</returns>
    private static ApiKeyInfo MapKeyInfo(ApiKeyRow r) => new()
    {
        Id = Guid.Parse(r.Id),
        Name = r.Name,
        Prefix = r.Prefix,
        Scopes = JsonSerializer.Deserialize<List<string>>(r.ScopesJson, Json) ?? [],
        Grants = JsonSerializer.Deserialize<List<ApiKeyGrant>>(r.GrantsJson, Json) ?? [],
        Enabled = r.Enabled != 0,
        ExpiresAt = r.ExpiresAt is null ? null : ParseDto(r.ExpiresAt),
        CreatedAt = ParseDto(r.CreatedAt),
        LastUsedAt = r.LastUsedAt is null ? null : ParseDto(r.LastUsedAt),
        RateLimitPerMinute = r.RateLimitPerMinute,
    };

    // ===== Scopes ================================================================================

    /// <inheritdoc />
    public async Task<IReadOnlyList<Scope>> GetScopesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<Scope>(new CommandDefinition(
            "SELECT Name, Description FROM Scopes ORDER BY Name", cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task UpsertScopeAsync(Scope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Scopes (Name, Description) VALUES (@Name, @Description)
            ON CONFLICT(Name) DO UPDATE SET Description = excluded.Description;
            """, new { scope.Name, scope.Description }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task DeleteScopeAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM Scopes WHERE Name = @Name",
            new { Name = name }, cancellationToken: cancellationToken));
    }

    // ===== Admin users ===========================================================================

    /// <inheritdoc />
    public async Task<AdminUserRecord?> FindAdminByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<AdminRow>(new CommandDefinition(
            "SELECT Id, Username, PasswordHash, Role, Enabled, TokenVersion, CreatedAt, LastLoginAt FROM AdminUsers WHERE Username = @Username",
            new { Username = username }, cancellationToken: cancellationToken));
        return row is null ? null : MapAdminRecord(row);
    }

    /// <inheritdoc />
    public async Task<AdminUserRecord?> FindAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<AdminRow>(new CommandDefinition(
            "SELECT Id, Username, PasswordHash, Role, Enabled, TokenVersion, CreatedAt, LastLoginAt FROM AdminUsers WHERE Id = @Id",
            new { Id = id.ToString() }, cancellationToken: cancellationToken));
        return row is null ? null : MapAdminRecord(row);
    }

    /// <summary>Maps an admin row to the full record.</summary>
    /// <param name="row">The database row.</param>
    /// <returns>The mapped admin record.</returns>
    private static AdminUserRecord MapAdminRecord(AdminRow row) => new()
    {
        Id = Guid.Parse(row.Id),
        Username = row.Username,
        PasswordHash = row.PasswordHash,
        Role = row.Role,
        Enabled = row.Enabled != 0,
        TokenVersion = (int)row.TokenVersion,
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminUserInfo>> GetAdminsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<AdminRow>(new CommandDefinition(
            "SELECT Id, Username, PasswordHash, Role, Enabled, CreatedAt, LastLoginAt FROM AdminUsers ORDER BY Username",
            cancellationToken: cancellationToken));
        return rows.Select(r => new AdminUserInfo
        {
            Id = Guid.Parse(r.Id),
            Username = r.Username,
            Role = r.Role,
            Enabled = r.Enabled != 0,
            CreatedAt = ParseDto(r.CreatedAt),
            LastLoginAt = r.LastLoginAt is null ? null : ParseDto(r.LastLoginAt),
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<AdminUserInfo> CreateAdminAsync(string username, string passwordHash, string role, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO AdminUsers (Id, Username, PasswordHash, Role, Enabled, CreatedAt, LastLoginAt)
            VALUES (@Id, @Username, @PasswordHash, @Role, 1, @CreatedAt, NULL);
            """, new { Id = id.ToString(), Username = username, PasswordHash = passwordHash, Role = role, CreatedAt = Iso(now) },
            cancellationToken: cancellationToken));

        return new AdminUserInfo { Id = id, Username = username, Role = role, Enabled = true, CreatedAt = now };
    }

    /// <inheritdoc />
    public async Task UpdateAdminRoleAsync(Guid id, string role, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE AdminUsers SET Role = @Role, TokenVersion = TokenVersion + 1 WHERE Id = @Id",
            new { Id = id.ToString(), Role = role }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task UpdateAdminEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE AdminUsers SET Enabled = @Enabled, TokenVersion = TokenVersion + 1 WHERE Id = @Id",
            new { Id = id.ToString(), Enabled = enabled ? 1 : 0 }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task UpdateAdminPasswordAsync(Guid id, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        // Bump TokenVersion so any JWTs issued before the password change stop authenticating at once.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE AdminUsers SET PasswordHash = @PasswordHash, TokenVersion = TokenVersion + 1 WHERE Id = @Id",
            new { Id = id.ToString(), PasswordHash = passwordHash }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task TouchAdminLoginAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE AdminUsers SET LastLoginAt = @At WHERE Id = @Id",
            new { Id = id.ToString(), At = Iso(at) }, cancellationToken: cancellationToken));
    }

    // ===== Admin access tokens ===================================================================

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminTokenInfo>> GetAdminTokensAsync(Guid adminId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<AdminTokenRow>(new CommandDefinition(
            "SELECT Id, AdminId, Name, Prefix, CreatedAt, ExpiresAt, LastUsedAt FROM AdminTokens WHERE AdminId = @AdminId ORDER BY CreatedAt DESC",
            new { AdminId = adminId.ToString() }, cancellationToken: cancellationToken));
        return rows.Select(r => new AdminTokenInfo
        {
            Id = Guid.Parse(r.Id),
            Name = r.Name,
            Prefix = r.Prefix,
            CreatedAt = ParseDto(r.CreatedAt),
            ExpiresAt = r.ExpiresAt is null ? null : ParseDto(r.ExpiresAt),
            LastUsedAt = r.LastUsedAt is null ? null : ParseDto(r.LastUsedAt),
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<AdminTokenInfo> CreateAdminTokenAsync(
        Guid adminId, string name, DateTimeOffset? expiresAt, string hash, string prefix, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO AdminTokens (Id, AdminId, Name, Prefix, Hash, CreatedAt, ExpiresAt, LastUsedAt)
            VALUES (@Id, @AdminId, @Name, @Prefix, @Hash, @CreatedAt, @ExpiresAt, NULL);
            """, new
        {
            Id = id.ToString(),
            AdminId = adminId.ToString(),
            Name = name,
            Prefix = prefix,
            Hash = hash,
            CreatedAt = Iso(now),
            ExpiresAt = expiresAt is null ? null : Iso(expiresAt.Value),
        }, cancellationToken: cancellationToken));

        return new AdminTokenInfo { Id = id, Name = name, Prefix = prefix, CreatedAt = now, ExpiresAt = expiresAt };
    }

    /// <inheritdoc />
    public async Task<AdminTokenRecord?> FindAdminTokenByHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<AdminTokenAuthRow>(new CommandDefinition(
            """
            SELECT t.Id, t.AdminId, t.ExpiresAt, a.Username, a.Role, a.Enabled
            FROM AdminTokens t JOIN AdminUsers a ON a.Id = t.AdminId
            WHERE t.Hash = @Hash
            """, new { Hash = hash }, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        return new AdminTokenRecord
        {
            Id = Guid.Parse(row.Id),
            AdminId = Guid.Parse(row.AdminId),
            Username = row.Username,
            Role = row.Role,
            AdminEnabled = row.Enabled != 0,
            ExpiresAt = row.ExpiresAt is null ? null : ParseDto(row.ExpiresAt),
        };
    }

    /// <inheritdoc />
    public async Task RevokeAdminTokensForAdminAsync(Guid adminId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM AdminTokens WHERE AdminId = @AdminId",
            new { AdminId = adminId.ToString() }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task RevokeAdminTokenAsync(Guid id, Guid adminId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM AdminTokens WHERE Id = @Id AND AdminId = @AdminId",
            new { Id = id.ToString(), AdminId = adminId.ToString() }, cancellationToken: cancellationToken));
        _lastTokenTouch.TryRemove(id, out _);
    }

    /// <inheritdoc />
    public async Task TouchAdminTokenAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        // Throttle: the admin-API auth path can run this on every scripted request. Persist at most once
        // per TouchThrottle window per token.
        if (_lastTokenTouch.TryGetValue(id, out var last) && usedAt - last < TouchThrottle)
        {
            return;
        }

        _lastTokenTouch[id] = usedAt;
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE AdminTokens SET LastUsedAt = @UsedAt WHERE Id = @Id",
            new { Id = id.ToString(), UsedAt = Iso(usedAt) }, cancellationToken: cancellationToken));
    }

    // ===== Audit =================================================================================

    /// <inheritdoc />
    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Audit (Timestamp, Category, Actor, Route, Outcome, StatusCode, DurationMs, Detail)
            VALUES (@Timestamp, @Category, @Actor, @Route, @Outcome, @StatusCode, @DurationMs, @Detail);
            """, new
        {
            Timestamp = Iso(entry.Timestamp == default ? _clock.GetUtcNow() : entry.Timestamp),
            entry.Category,
            entry.Actor,
            entry.Route,
            entry.Outcome,
            entry.StatusCode,
            entry.DurationMs,
            entry.Detail,
        }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEntry>> QueryAuditAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sql = new StringBuilder(
            "SELECT Id, Timestamp, Category, Actor, Route, Outcome, StatusCode, DurationMs, Detail FROM Audit WHERE 1 = 1");
        var p = new DynamicParameters();
        if (query.From is { } from)
        {
            sql.Append(" AND Timestamp >= @From");
            p.Add("From", Iso(from));
        }

        if (query.To is { } to)
        {
            sql.Append(" AND Timestamp < @To");
            p.Add("To", Iso(to));
        }

        if (!string.IsNullOrEmpty(query.Category))
        {
            sql.Append(" AND Category = @Category");
            p.Add("Category", query.Category);
        }

        if (!string.IsNullOrEmpty(query.Actor))
        {
            sql.Append(" AND Actor = @Actor");
            p.Add("Actor", query.Actor);
        }

        if (!string.IsNullOrEmpty(query.Route))
        {
            sql.Append(" AND Route = @Route");
            p.Add("Route", query.Route);
        }

        if (query.AfterId is { } afterId)
        {
            // Keyset (seek) paging: rows strictly before the cursor in descending-id order.
            sql.Append(" AND Id < @AfterId");
            p.Add("AfterId", afterId);
        }

        sql.Append(" ORDER BY Id DESC LIMIT @Limit OFFSET @Offset");
        p.Add("Limit", query.Limit <= 0 ? 200 : Math.Min(query.Limit, 1000));
        // A keyset cursor supersedes offset paging (and avoids skipping rows inserted between pages).
        p.Add("Offset", query.AfterId is null ? Math.Max(0, query.Offset) : 0);

        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<AuditRow>(new CommandDefinition(sql.ToString(), p, cancellationToken: cancellationToken));
        return rows.Select(r => new AuditEntry
        {
            Id = r.Id,
            Timestamp = ParseDto(r.Timestamp),
            Category = r.Category,
            Actor = r.Actor,
            Route = r.Route,
            Outcome = r.Outcome,
            StatusCode = r.StatusCode,
            DurationMs = r.DurationMs,
            Detail = r.Detail,
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<int> PruneAuditAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Audit WHERE Timestamp < @Cutoff", new { Cutoff = Iso(olderThan) }, cancellationToken: cancellationToken));
    }

    // ===== Request log ===========================================================================

    /// <inheritdoc />
    public async Task AppendRequestLogAsync(RequestLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RequestLog (Timestamp, EndpointId, Route, HttpMethod, ConnectionName, ObjectName, StatusCode,
                                    Outcome, DurationMs, DbDurationMs, RowsReturned, CacheHit, Slow, AverageMs, ApiKeyPrefix, Parameters, Result, Error)
            VALUES (@Timestamp, @EndpointId, @Route, @HttpMethod, @ConnectionName, @ObjectName, @StatusCode,
                    @Outcome, @DurationMs, @DbDurationMs, @RowsReturned, @CacheHit, @Slow, @AverageMs, @ApiKeyPrefix, @Parameters, @Result, @Error);
            """, new
        {
            Timestamp = Iso(entry.Timestamp == default ? _clock.GetUtcNow() : entry.Timestamp),
            EndpointId = entry.EndpointId?.ToString(),
            entry.Route,
            entry.HttpMethod,
            entry.ConnectionName,
            entry.ObjectName,
            entry.StatusCode,
            entry.Outcome,
            entry.DurationMs,
            entry.DbDurationMs,
            entry.RowsReturned,
            CacheHit = entry.CacheHit ? 1 : 0,
            Slow = entry.Slow ? 1 : 0,
            entry.AverageMs,
            entry.ApiKeyPrefix,
            entry.Parameters,
            entry.Result,
            entry.Error,
        }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RequestLogEntry>> QueryRequestLogAsync(RequestLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sql = new StringBuilder(
            "SELECT Id, Timestamp, EndpointId, Route, HttpMethod, ConnectionName, ObjectName, StatusCode, Outcome, " +
            "DurationMs, DbDurationMs, RowsReturned, CacheHit, Slow, AverageMs, ApiKeyPrefix, Parameters, Result, Error FROM RequestLog WHERE 1 = 1");
        var p = new DynamicParameters();
        if (query.EndpointId is { } endpointId)
        {
            sql.Append(" AND EndpointId = @EndpointId");
            p.Add("EndpointId", endpointId.ToString());
        }

        if (!string.IsNullOrEmpty(query.Route))
        {
            sql.Append(" AND Route = @Route COLLATE NOCASE");
            p.Add("Route", query.Route);
        }

        if (query.SlowOnly)
        {
            sql.Append(" AND Slow = 1");
        }

        if (query.ErrorsOnly)
        {
            sql.Append($" AND (StatusCode >= 400 OR Outcome = '{OutcomeCodes.Error}')");
        }

        if (query.From is { } from)
        {
            sql.Append(" AND Timestamp >= @From");
            p.Add("From", Iso(from));
        }

        if (query.To is { } to)
        {
            sql.Append(" AND Timestamp < @To");
            p.Add("To", Iso(to));
        }

        if (query.AfterId is { } afterId)
        {
            sql.Append(" AND Id < @AfterId");
            p.Add("AfterId", afterId);
        }

        sql.Append(" ORDER BY Id DESC LIMIT @Limit");
        p.Add("Limit", query.Limit <= 0 ? 100 : Math.Min(query.Limit, 1000));

        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<RequestLogRow>(new CommandDefinition(sql.ToString(), p, cancellationToken: cancellationToken));
        return rows.Select(MapRequestLog).ToList();
    }

    /// <inheritdoc />
    public async Task<int> PruneRequestLogAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM RequestLog WHERE Timestamp < @Cutoff", new { Cutoff = Iso(olderThan) }, cancellationToken: cancellationToken));
    }

    /// <summary>Maps a request-log row to its contract shape.</summary>
    /// <param name="r">The row.</param>
    /// <returns>The request-log entry.</returns>
    private static RequestLogEntry MapRequestLog(RequestLogRow r) => new()
    {
        Id = r.Id,
        Timestamp = ParseDto(r.Timestamp),
        EndpointId = string.IsNullOrEmpty(r.EndpointId) ? null : Guid.Parse(r.EndpointId),
        Route = r.Route,
        HttpMethod = r.HttpMethod,
        ConnectionName = r.ConnectionName,
        ObjectName = r.ObjectName,
        StatusCode = r.StatusCode,
        Outcome = r.Outcome,
        DurationMs = r.DurationMs,
        DbDurationMs = r.DbDurationMs,
        RowsReturned = r.RowsReturned,
        CacheHit = r.CacheHit != 0,
        Slow = r.Slow != 0,
        AverageMs = r.AverageMs,
        ApiKeyPrefix = r.ApiKeyPrefix,
        Parameters = r.Parameters,
        Result = r.Result,
        Error = r.Error,
    };

    // ===== Login throttle ========================================================================

    /// <inheritdoc />
    public async Task<bool> IsLoginLockedAsync(string client, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var lockedUntil = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT LockedUntil FROM LoginThrottle WHERE Client = @Client",
            new { Client = client }, cancellationToken: cancellationToken));
        return lockedUntil is not null && ParseDto(lockedUntil) > now;
    }

    /// <inheritdoc />
    public async Task RecordLoginFailureAsync(string client, int maxFailures, TimeSpan lockout, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (maxFailures <= 0)
        {
            return;
        }

        // Two statements in one transaction: increment the failure count (unless already locked), then
        // apply a lockout once the count reaches the threshold. Keeping it atomic makes the count correct
        // even when several instances process failures for the same client concurrently.
        const string sql = """
            INSERT INTO LoginThrottle (Client, Failures, LockedUntil, LastActivity)
            VALUES (@Client, 1, NULL, @Now)
            ON CONFLICT(Client) DO UPDATE SET
                Failures = CASE WHEN LockedUntil IS NOT NULL AND LockedUntil > @Now THEN Failures ELSE Failures + 1 END,
                LastActivity = @Now;

            UPDATE LoginThrottle SET LockedUntil = @LockUntil, Failures = 0
            WHERE Client = @Client AND (LockedUntil IS NULL OR LockedUntil <= @Now) AND Failures >= @MaxFailures;
            """;

        await using var conn = await OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Client = client,
            Now = Iso(now),
            LockUntil = Iso(now + lockout),
            MaxFailures = maxFailures,
        }, transaction: tx, cancellationToken: cancellationToken));
        await tx.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ResetLoginThrottleAsync(string client, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LoginThrottle WHERE Client = @Client", new { Client = client }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<int> PruneLoginThrottleAsync(DateTimeOffset staleBefore, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LoginThrottle WHERE (LockedUntil IS NULL OR LockedUntil <= @Now) AND LastActivity < @StaleBefore",
            new { Now = Iso(now), StaleBefore = Iso(staleBefore) }, cancellationToken: cancellationToken));
    }

    // ===== Refresh tokens ========================================================================

    /// <inheritdoc />
    public async Task CreateRefreshTokenAsync(Guid id, Guid adminId, string hash, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO AdminRefreshTokens (Id, AdminId, Hash, ExpiresAt, CreatedAt, RevokedAt) VALUES (@Id, @AdminId, @Hash, @ExpiresAt, @CreatedAt, NULL)",
            new { Id = id.ToString(), AdminId = adminId.ToString(), Hash = hash, ExpiresAt = Iso(expiresAt), CreatedAt = Iso(now) },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<RefreshTokenRecord?> FindRefreshTokenAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<(string Id, string AdminId, string ExpiresAt, string? RevokedAt)?>(
            new CommandDefinition("SELECT Id, AdminId, ExpiresAt, RevokedAt FROM AdminRefreshTokens WHERE Hash = @Hash",
                new { Hash = hash }, cancellationToken: cancellationToken));
        if (row is not { } r)
        {
            return null;
        }

        return new RefreshTokenRecord
        {
            Id = Guid.Parse(r.Id),
            AdminId = Guid.Parse(r.AdminId),
            ExpiresAt = ParseDto(r.ExpiresAt),
            Revoked = r.RevokedAt is not null,
        };
    }

    /// <inheritdoc />
    public async Task RevokeRefreshTokenAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE AdminRefreshTokens SET RevokedAt = @Now WHERE Id = @Id AND RevokedAt IS NULL",
            new { Id = id.ToString(), Now = Iso(now) }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task RevokeRefreshTokensForAdminAsync(Guid adminId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE AdminRefreshTokens SET RevokedAt = @Now WHERE AdminId = @AdminId AND RevokedAt IS NULL",
            new { AdminId = adminId.ToString(), Now = Iso(now) }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<int> PruneRefreshTokensAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM AdminRefreshTokens WHERE ExpiresAt <= @Now OR RevokedAt IS NOT NULL",
            new { Now = Iso(now) }, cancellationToken: cancellationToken));
    }

    // ===== Runtime settings ======================================================================

    /// <inheritdoc />
    public async Task<string?> GetSettingsJsonAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition("SELECT Json FROM Settings WHERE Id = 1", cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task SaveSettingsJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO Settings (Id, Json, UpdatedAt) VALUES (1, @Json, @UpdatedAt) " +
            "ON CONFLICT(Id) DO UPDATE SET Json = excluded.Json, UpdatedAt = excluded.UpdatedAt",
            new { Json = json, UpdatedAt = Iso(_clock.GetUtcNow()) }, cancellationToken: cancellationToken));
    }

    // ===== Helpers & row DTOs ====================================================================

    /// <summary>Formats a timestamp as a round-trippable UTC ISO-8601 string for stable lexical ordering.</summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The UTC ISO-8601 representation.</returns>
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Parses a stored ISO-8601 timestamp back to a <see cref="DateTimeOffset"/>.</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The parsed timestamp.</returns>
    private static DateTimeOffset ParseDto(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Row shape for the Endpoints table (all times and ids stored as text).</summary>
    private sealed class EndpointRow
    {
        /// <summary>Endpoint id (GUID text).</summary>
        public string Id { get; set; } = "";
        /// <summary>Route.</summary>
        public string Route { get; set; } = "";
        /// <summary>HTTP method.</summary>
        public string HttpMethod { get; set; } = "";
        /// <summary>Target connection name.</summary>
        public string ConnectionName { get; set; } = "";
        /// <summary>Object type (enum ordinal).</summary>
        public int ObjectType { get; set; }
        /// <summary>Object schema.</summary>
        public string SchemaName { get; set; } = "";
        /// <summary>Object name.</summary>
        public string ObjectName { get; set; } = "";
        /// <summary>Result mode (enum ordinal).</summary>
        public int ResultMode { get; set; }
        /// <summary>Optional per-command timeout, seconds.</summary>
        public int? CommandTimeoutSeconds { get; set; }
        /// <summary>Enabled flag (0/1).</summary>
        public long Enabled { get; set; }
        /// <summary>Suppress-SQL-messages flag (0/1).</summary>
        public long SuppressMessages { get; set; }
        /// <summary>Serialized cache policy.</summary>
        public string CacheJson { get; set; } = "";
        /// <summary>Serialized request-logging policy.</summary>
        public string? LoggingJson { get; set; }
        /// <summary>Serialized response-delivery policy.</summary>
        public string? DeliveryJson { get; set; }
        /// <summary>Serialized parameter definitions.</summary>
        public string ParametersJson { get; set; } = "";
        /// <summary>Serialized required-scope list.</summary>
        public string RequiredScopesJson { get; set; } = "";
        /// <summary>Optional description.</summary>
        public string? Description { get; set; }
        /// <summary>Created timestamp (ISO-8601 text).</summary>
        public string CreatedAt { get; set; } = "";
        /// <summary>Updated timestamp (ISO-8601 text).</summary>
        public string UpdatedAt { get; set; } = "";
    }

    /// <summary>Row shape for the ApiKeys table.</summary>
    private sealed class ApiKeyRow
    {
        /// <summary>Key id (GUID text).</summary>
        public string Id { get; set; } = "";
        /// <summary>Display name.</summary>
        public string Name { get; set; } = "";
        /// <summary>Non-secret prefix.</summary>
        public string Prefix { get; set; } = "";
        /// <summary>Stored one-way hash.</summary>
        public string Hash { get; set; } = "";
        /// <summary>Serialized scope list.</summary>
        public string ScopesJson { get; set; } = "";
        /// <summary>Serialized resource-grant list.</summary>
        public string GrantsJson { get; set; } = "[]";
        /// <summary>Enabled flag (0/1).</summary>
        public long Enabled { get; set; }
        /// <summary>Optional expiry (ISO-8601 text).</summary>
        public string? ExpiresAt { get; set; }
        /// <summary>Created timestamp (ISO-8601 text).</summary>
        public string CreatedAt { get; set; } = "";
        /// <summary>Optional last-used timestamp (ISO-8601 text).</summary>
        public string? LastUsedAt { get; set; }
        /// <summary>Optional per-key rate limit, requests/minute.</summary>
        public int? RateLimitPerMinute { get; set; }
    }

    /// <summary>Row shape for the AdminUsers table.</summary>
    private sealed class AdminRow
    {
        /// <summary>Admin id (GUID text).</summary>
        public string Id { get; set; } = "";
        /// <summary>Login name.</summary>
        public string Username { get; set; } = "";
        /// <summary>Stored password hash.</summary>
        public string PasswordHash { get; set; } = "";
        /// <summary>Role name.</summary>
        public string Role { get; set; } = "Admin";
        /// <summary>Enabled flag (0/1).</summary>
        public long Enabled { get; set; }
        /// <summary>Token version, bumped to revoke previously issued JWTs.</summary>
        public long TokenVersion { get; set; }
        /// <summary>Created timestamp (ISO-8601 text).</summary>
        public string CreatedAt { get; set; } = "";
        /// <summary>Optional last-login timestamp (ISO-8601 text).</summary>
        public string? LastLoginAt { get; set; }
    }

    /// <summary>Row shape for a non-secret admin-token listing.</summary>
    private sealed class AdminTokenRow
    {
        /// <summary>Token id (GUID text).</summary>
        public string Id { get; set; } = "";
        /// <summary>Owning admin id (GUID text).</summary>
        public string AdminId { get; set; } = "";
        /// <summary>Display name.</summary>
        public string Name { get; set; } = "";
        /// <summary>Non-secret prefix.</summary>
        public string Prefix { get; set; } = "";
        /// <summary>Created timestamp (ISO-8601 text).</summary>
        public string CreatedAt { get; set; } = "";
        /// <summary>Optional expiry (ISO-8601 text).</summary>
        public string? ExpiresAt { get; set; }
        /// <summary>Optional last-used timestamp (ISO-8601 text).</summary>
        public string? LastUsedAt { get; set; }
    }

    /// <summary>Row shape for admin-token authentication (token joined to its owning admin).</summary>
    private sealed class AdminTokenAuthRow
    {
        /// <summary>Token id (GUID text).</summary>
        public string Id { get; set; } = "";
        /// <summary>Owning admin id (GUID text).</summary>
        public string AdminId { get; set; } = "";
        /// <summary>Optional expiry (ISO-8601 text).</summary>
        public string? ExpiresAt { get; set; }
        /// <summary>Owning admin username.</summary>
        public string Username { get; set; } = "";
        /// <summary>Owning admin role.</summary>
        public string Role { get; set; } = "Admin";
        /// <summary>Owning admin enabled flag (0/1).</summary>
        public long Enabled { get; set; }
    }

    /// <summary>Row shape for the Audit table.</summary>
    private sealed class AuditRow
    {
        /// <summary>Auto-increment id.</summary>
        public long Id { get; set; }
        /// <summary>Event timestamp (ISO-8601 text).</summary>
        public string Timestamp { get; set; } = "";
        /// <summary>Event category.</summary>
        public string Category { get; set; } = "";
        /// <summary>Actor, if known.</summary>
        public string? Actor { get; set; }
        /// <summary>Route, if applicable.</summary>
        public string? Route { get; set; }
        /// <summary>Outcome marker.</summary>
        public string? Outcome { get; set; }
        /// <summary>HTTP status code, if applicable.</summary>
        public int? StatusCode { get; set; }
        /// <summary>Duration in milliseconds, if applicable.</summary>
        public double? DurationMs { get; set; }
        /// <summary>Optional detail text.</summary>
        public string? Detail { get; set; }
    }

    /// <summary>Row shape for the RequestLog table (ids and times stored as text).</summary>
    private sealed class RequestLogRow
    {
        /// <summary>Monotonic id.</summary>
        public long Id { get; set; }
        /// <summary>Call timestamp (ISO-8601 text).</summary>
        public string Timestamp { get; set; } = "";
        /// <summary>Endpoint id (GUID text), if resolved.</summary>
        public string? EndpointId { get; set; }
        /// <summary>Route.</summary>
        public string Route { get; set; } = "";
        /// <summary>HTTP method.</summary>
        public string HttpMethod { get; set; } = "";
        /// <summary>Target connection name.</summary>
        public string? ConnectionName { get; set; }
        /// <summary>Schema-qualified object name.</summary>
        public string? ObjectName { get; set; }
        /// <summary>HTTP status code.</summary>
        public int? StatusCode { get; set; }
        /// <summary>Outcome marker.</summary>
        public string? Outcome { get; set; }
        /// <summary>Wall-clock duration in milliseconds.</summary>
        public double DurationMs { get; set; }
        /// <summary>Database time in milliseconds, if known.</summary>
        public double? DbDurationMs { get; set; }
        /// <summary>Rows returned, if known.</summary>
        public int? RowsReturned { get; set; }
        /// <summary>Cache-hit flag (0/1).</summary>
        public long CacheHit { get; set; }
        /// <summary>Slow flag (0/1).</summary>
        public long Slow { get; set; }
        /// <summary>Rolling average duration at call time, if known.</summary>
        public double? AverageMs { get; set; }
        /// <summary>Calling API-key prefix.</summary>
        public string? ApiKeyPrefix { get; set; }
        /// <summary>Captured parameters JSON, if opted in.</summary>
        public string? Parameters { get; set; }
        /// <summary>Captured result JSON, if opted in.</summary>
        public string? Result { get; set; }
        /// <summary>Error detail, if failed.</summary>
        public string? Error { get; set; }
    }
}
