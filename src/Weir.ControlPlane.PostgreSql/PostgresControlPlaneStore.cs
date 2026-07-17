using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.ControlPlane.PostgreSql;

/// <summary>
/// <see cref="IControlPlaneStore"/> backed by PostgreSQL, for high-availability deployments where
/// several Weir instances share one control database. Opens a pooled connection per operation and
/// self-migrates on <see cref="InitializeAsync"/>. Complex fields (parameters, cache policy, scope
/// lists) are stored as JSON text columns; timestamps are stored as ISO 8601 text for portability.
/// </summary>
public sealed class PostgresControlPlaneStore : IControlPlaneStore
{
    /// <summary>JSON options for the serialized columns (parameters, cache policy, scope/grant lists).</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Minimum interval between persisted last-used updates for a single key (hot-path throttle).</summary>
    private static readonly TimeSpan TouchThrottle = TimeSpan.FromSeconds(60);

    /// <summary>The Npgsql connection string.</summary>
    private readonly string _connectionString;

    /// <summary>Clock used for timestamps and touch throttling.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Last time each API key's last-used timestamp was actually written, for throttling.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastTouch = new();

    /// <summary>Last time each admin token's last-used timestamp was actually written, for throttling.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastTokenTouch = new();

    /// <summary>Creates the store from bound options and a clock.</summary>
    /// <param name="options">The control-plane options.</param>
    /// <param name="clock">The time provider.</param>
    public PostgresControlPlaneStore(IOptions<PostgresControlPlaneOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.ConnectionString;
        _clock = clock;
    }

    /// <summary>Opens a connection, disposing it if the open fails so no pooled slot leaks.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open connection.</returns>
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Base key for the session advisory lock that serializes migrations across instances that share
    /// this control database (so a rolling deploy in HA cannot run migrations concurrently). The actual
    /// key is this base XORed with the database name hash to avoid contention across deployments that
    /// happen to share the same PostgreSQL server.
    /// </summary>
    private const long BaseMigrationLockKey = 4207853001L;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);

        // Serialize migrations across instances. The lock is held on this connection until released.
        // Derive the lock key from the database name so different deployments on the same server
        // do not contend on the same lock. Use a stable hash of the name: string.GetHashCode() is
        // randomized per process in .NET, so it would yield a different key on every instance and
        // defeat the cross-instance serialization this lock exists for.
        var dbNameHash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(conn.Database ?? string.Empty));
        var lockKey = BaseMigrationLockKey ^ BitConverter.ToInt64(dbNameHash, 0);
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@Key)", new { Key = lockKey }, cancellationToken: cancellationToken));
        try
        {
            // Ensure the version tracker holds exactly one row. The singleton column plus its unique
            // index prevent duplicate rows (which a manual restore could otherwise introduce); the
            // statements are idempotent and also upgrade a legacy single-column table in place.
            await conn.ExecuteAsync(new CommandDefinition("""
                CREATE TABLE IF NOT EXISTS weir_schema_version (version integer NOT NULL);
                ALTER TABLE weir_schema_version ADD COLUMN IF NOT EXISTS singleton boolean NOT NULL DEFAULT true;
                DELETE FROM weir_schema_version
                    WHERE ctid NOT IN (SELECT ctid FROM weir_schema_version ORDER BY version DESC LIMIT 1);
                CREATE UNIQUE INDEX IF NOT EXISTS UX_weir_schema_version_singleton ON weir_schema_version (singleton);
                """, cancellationToken: cancellationToken));

            await conn.ExecuteAsync(new CommandDefinition(
                "CREATE TABLE IF NOT EXISTS SchemaMigrations (Version integer PRIMARY KEY, Checksum text NOT NULL, AppliedAt text NOT NULL);",
                cancellationToken: cancellationToken));

            var version = await conn.ExecuteScalarAsync<int?>(
                new CommandDefinition("SELECT version FROM weir_schema_version LIMIT 1", cancellationToken: cancellationToken));
            if (version is null)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO weir_schema_version (singleton, version) VALUES (true, 0) ON CONFLICT (singleton) DO NOTHING;",
                    cancellationToken: cancellationToken));
                version = 0;
            }

            // Verify (or backfill) the checksum of each already-applied migration; a mismatch means a
            // shipped migration was edited or the history was tampered with, so fail fast.
            await VerifyOrBackfillChecksumsAsync(conn, version.Value, cancellationToken);

            for (var i = version.Value; i < PostgresSchema.Migrations.Length; i++)
            {
                await using var tx = await conn.BeginTransactionAsync(cancellationToken);
                await conn.ExecuteAsync(new CommandDefinition(PostgresSchema.Migrations[i], transaction: tx, cancellationToken: cancellationToken));
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO SchemaMigrations (Version, Checksum, AppliedAt) VALUES (@Version, @Checksum, @AppliedAt)",
                    new { Version = i + 1, Checksum = Checksum(PostgresSchema.Migrations[i]), AppliedAt = Iso(_clock.GetUtcNow()) },
                    transaction: tx, cancellationToken: cancellationToken));
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE weir_schema_version SET version = @Version WHERE singleton = true",
                    new { Version = i + 1 }, transaction: tx, cancellationToken: cancellationToken));
                await tx.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(@Key)", new { Key = lockKey }, cancellationToken: cancellationToken));
        }
    }

    /// <summary>
    /// Compares the recorded checksum of each already-applied migration against the shipped script, and
    /// records a checksum for any applied migration that predates the checksum table. Throws
    /// <see cref="ControlPlaneMigrationException"/> on a mismatch.
    /// </summary>
    /// <param name="conn">The open connection (already holding the migration lock).</param>
    /// <param name="appliedVersion">The current schema version (number of applied migrations).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task VerifyOrBackfillChecksumsAsync(NpgsqlConnection conn, int appliedVersion, CancellationToken cancellationToken)
    {
        var recorded = (await conn.QueryAsync<(long Version, string Checksum)>(
                new CommandDefinition("SELECT Version, Checksum FROM SchemaMigrations", cancellationToken: cancellationToken)))
            .ToDictionary(r => r.Version, r => r.Checksum);

        var upTo = Math.Min(appliedVersion, PostgresSchema.Migrations.Length);
        for (var applied = 1; applied <= upTo; applied++)
        {
            var checksum = Checksum(PostgresSchema.Migrations[applied - 1]);
            if (recorded.TryGetValue(applied, out var stored))
            {
                if (!string.Equals(stored, checksum, StringComparison.Ordinal))
                {
                    // An older build hashed the script with its on-disk line endings (CRLF on Windows),
                    // so a database migrated there recorded the CRLF hash. Checksum() now normalizes to
                    // LF, so recompute the hash of the CRLF form and, if it matches the stored value,
                    // update the record to the canonical LF checksum instead of failing. (Comparing
                    // against the LF form would be pointless: it equals checksum.)
                    var crlf = PostgresSchema.Migrations[applied - 1].Replace("\r\n", "\n").Replace("\n", "\r\n");
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
                    "INSERT INTO SchemaMigrations (Version, Checksum, AppliedAt) VALUES (@Version, @Checksum, @AppliedAt) ON CONFLICT (Version) DO NOTHING",
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

    /// <summary>The endpoint columns selected for read queries, in a fixed order.</summary>
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
            ON CONFLICT (Id) DO UPDATE SET
                Route=EXCLUDED.Route, HttpMethod=EXCLUDED.HttpMethod, ConnectionName=EXCLUDED.ConnectionName,
                ObjectType=EXCLUDED.ObjectType, SchemaName=EXCLUDED.SchemaName, ObjectName=EXCLUDED.ObjectName,
                ResultMode=EXCLUDED.ResultMode, CommandTimeoutSeconds=EXCLUDED.CommandTimeoutSeconds, Enabled=EXCLUDED.Enabled,
                SuppressMessages=EXCLUDED.SuppressMessages, CacheJson=EXCLUDED.CacheJson, LoggingJson=EXCLUDED.LoggingJson, DeliveryJson=EXCLUDED.DeliveryJson, ParametersJson=EXCLUDED.ParametersJson,
                RequiredScopesJson=EXCLUDED.RequiredScopesJson, Description=EXCLUDED.Description, UpdatedAt=EXCLUDED.UpdatedAt;
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
                Enabled = endpoint.Enabled,
                SuppressMessages = endpoint.SuppressMessages,
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
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Another endpoint already uses this method + route.
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
        Enabled = r.Enabled,
        SuppressMessages = r.SuppressMessages,
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
            Enabled = row.Enabled,
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
            VALUES (@Id, @Name, @Prefix, @Hash, @ScopesJson, @GrantsJson, true, @ExpiresAt, @CreatedAt, NULL, @RateLimitPerMinute);
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
        await conn.ExecuteAsync(new CommandDefinition("UPDATE ApiKeys SET Enabled = false WHERE Id = @Id",
            new { Id = id.ToString() }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task TouchApiKeyAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        // Throttle: persist the last-used timestamp at most once per TouchThrottle window per key so
        // the hot authentication path does not issue a control-database write on every request.
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
        Enabled = r.Enabled,
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
            ON CONFLICT (Name) DO UPDATE SET Description = EXCLUDED.Description;
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
        Enabled = row.Enabled,
        TokenVersion = row.TokenVersion,
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
            Enabled = r.Enabled,
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
            VALUES (@Id, @Username, @PasswordHash, @Role, true, @CreatedAt, NULL);
            """, new { Id = id.ToString(), Username = username, PasswordHash = passwordHash, Role = role, CreatedAt = Iso(now) },
            cancellationToken: cancellationToken));

        return new AdminUserInfo { Id = id, Username = username, Role = role, Enabled = true, CreatedAt = now };
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
    public async Task UpdateAdminRoleAsync(Guid id, string role, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        // Bump TokenVersion so any JWTs issued before the role change stop authenticating at once.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE AdminUsers SET Role = @Role, TokenVersion = TokenVersion + 1 WHERE Id = @Id",
            new { Id = id.ToString(), Role = role }, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task UpdateAdminEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        // Bump TokenVersion so any JWTs issued before the enable/disable change stop authenticating at once.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE AdminUsers SET Enabled = @Enabled, TokenVersion = TokenVersion + 1 WHERE Id = @Id",
            new { Id = id.ToString(), Enabled = enabled }, cancellationToken: cancellationToken));
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
            AdminEnabled = row.Enabled,
            ExpiresAt = row.ExpiresAt is null ? null : ParseDto(row.ExpiresAt),
        };
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
            entry.CacheHit,
            entry.Slow,
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
            sql.Append(" AND lower(Route) = lower(@Route)");
            p.Add("Route", query.Route);
        }

        if (query.SlowOnly)
        {
            sql.Append(" AND Slow = true");
        }

        if (query.ErrorsOnly)
        {
            sql.Append(" AND (StatusCode >= 400 OR Outcome = @OutcomeError)");
            p.Add("OutcomeError", OutcomeCodes.Error);
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
        CacheHit = r.CacheHit,
        Slow = r.Slow,
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

        // Increment the failure count (unless already locked), then apply a lockout once the count reaches
        // the threshold. Both statements run in one transaction so concurrent instances keep it consistent.
        const string sql = """
            INSERT INTO LoginThrottle (Client, Failures, LockedUntil, LastActivity)
            VALUES (@Client, 1, NULL, @Now)
            ON CONFLICT (Client) DO UPDATE SET
                Failures = CASE WHEN LoginThrottle.LockedUntil IS NOT NULL AND LoginThrottle.LockedUntil > @Now THEN LoginThrottle.Failures ELSE LoginThrottle.Failures + 1 END,
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
            "ON CONFLICT (Id) DO UPDATE SET Json = EXCLUDED.Json, UpdatedAt = EXCLUDED.UpdatedAt",
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

    /// <summary>Row shape for the Endpoints table (times and ids stored as text; Enabled is a native boolean).</summary>
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
        /// <summary>Enabled flag.</summary>
        public bool Enabled { get; set; }
        /// <summary>Suppress-SQL-messages flag.</summary>
        public bool SuppressMessages { get; set; }
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
        /// <summary>Enabled flag.</summary>
        public bool Enabled { get; set; }
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
        /// <summary>Enabled flag.</summary>
        public bool Enabled { get; set; }
        /// <summary>Token version, bumped to revoke previously issued JWTs.</summary>
        public int TokenVersion { get; set; }
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
        /// <summary>Owning admin enabled flag.</summary>
        public bool Enabled { get; set; }
    }

    /// <summary>Row shape for the Audit table.</summary>
    private sealed class AuditRow
    {
        /// <summary>Identity id.</summary>
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

    /// <summary>Row shape for the RequestLog table.</summary>
    private sealed class RequestLogRow
    {
        /// <summary>Identity id.</summary>
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
        /// <summary>Cache-hit flag.</summary>
        public bool CacheHit { get; set; }
        /// <summary>Slow flag.</summary>
        public bool Slow { get; set; }
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
