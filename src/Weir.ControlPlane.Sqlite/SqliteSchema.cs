namespace Weir.ControlPlane.Sqlite;

/// <summary>
/// Ordered, idempotent schema migrations for the SQLite control plane. The store applies every
/// migration whose 1-based index exceeds <c>PRAGMA user_version</c>, then advances the version.
/// Append new migrations; never edit an already-shipped one.
/// </summary>
internal static class SqliteSchema
{
    /// <summary>The migration scripts, applied in order.</summary>
    public static readonly string[] Migrations =
    [
        // v1 - initial schema
        """
        CREATE TABLE IF NOT EXISTS Endpoints (
            Id                    TEXT    PRIMARY KEY,
            Route                 TEXT    NOT NULL,
            HttpMethod            TEXT    NOT NULL,
            ConnectionName        TEXT    NOT NULL,
            ObjectType            INTEGER NOT NULL,
            SchemaName            TEXT    NOT NULL,
            ObjectName            TEXT    NOT NULL,
            ResultMode            INTEGER NOT NULL,
            CommandTimeoutSeconds INTEGER NULL,
            Enabled               INTEGER NOT NULL,
            CacheJson             TEXT    NOT NULL,
            ParametersJson        TEXT    NOT NULL,
            RequiredScopesJson    TEXT    NOT NULL,
            Description           TEXT    NULL,
            CreatedAt             TEXT    NOT NULL,
            UpdatedAt             TEXT    NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Endpoints_Method_Route ON Endpoints (HttpMethod, Route);

        CREATE TABLE IF NOT EXISTS ApiKeys (
            Id                 TEXT    PRIMARY KEY,
            Name               TEXT    NOT NULL,
            Prefix             TEXT    NOT NULL,
            Hash               TEXT    NOT NULL,
            ScopesJson         TEXT    NOT NULL,
            Enabled            INTEGER NOT NULL,
            ExpiresAt          TEXT    NULL,
            CreatedAt          TEXT    NOT NULL,
            LastUsedAt         TEXT    NULL,
            RateLimitPerMinute INTEGER NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_ApiKeys_Hash ON ApiKeys (Hash);

        CREATE TABLE IF NOT EXISTS Scopes (
            Name        TEXT PRIMARY KEY,
            Description TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS AdminUsers (
            Id           TEXT    PRIMARY KEY,
            Username     TEXT    NOT NULL,
            PasswordHash TEXT    NOT NULL,
            Enabled      INTEGER NOT NULL,
            CreatedAt    TEXT    NOT NULL,
            LastLoginAt  TEXT    NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AdminUsers_Username ON AdminUsers (Username);

        CREATE TABLE IF NOT EXISTS Audit (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            Timestamp  TEXT    NOT NULL,
            Category   TEXT    NOT NULL,
            Actor      TEXT    NULL,
            Route      TEXT    NULL,
            Outcome    TEXT    NULL,
            StatusCode INTEGER NULL,
            DurationMs REAL    NULL,
            Detail     TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Audit_Timestamp ON Audit (Timestamp);
        """,

        // v2 - admin role-based access control
        """
        ALTER TABLE AdminUsers ADD COLUMN Role TEXT NOT NULL DEFAULT 'Admin';
        """,

        // v3 - per-key resource grants (which procedures a key may call)
        """
        ALTER TABLE ApiKeys ADD COLUMN GrantsJson TEXT NOT NULL DEFAULT '[]';
        """,

        // v4 - personal access tokens for admins (scripted / CI-CD access to the admin API)
        """
        CREATE TABLE IF NOT EXISTS AdminTokens (
            Id         TEXT PRIMARY KEY,
            AdminId    TEXT NOT NULL REFERENCES AdminUsers (Id),
            Name       TEXT NOT NULL,
            Prefix     TEXT NOT NULL,
            Hash       TEXT NOT NULL,
            CreatedAt  TEXT NOT NULL,
            ExpiresAt  TEXT NULL,
            LastUsedAt TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AdminTokens_Hash ON AdminTokens (Hash);
        CREATE INDEX IF NOT EXISTS IX_AdminTokens_AdminId ON AdminTokens (AdminId);
        """,

        // v5 - runtime settings document (single row) editable from the admin panel
        """
        CREATE TABLE IF NOT EXISTS Settings (
            Id        INTEGER PRIMARY KEY CHECK (Id = 1),
            Json      TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,

        // v6 - token version for JWT revocation (bumped on password / role change or disable)
        """
        ALTER TABLE AdminUsers ADD COLUMN TokenVersion INTEGER NOT NULL DEFAULT 0;
        """,

        // v7 - per-endpoint toggle to suppress SQL PRINT / notice messages in the response
        """
        ALTER TABLE Endpoints ADD COLUMN SuppressMessages INTEGER NOT NULL DEFAULT 0;
        """,

        // v8 - persisted admin sign-in throttle (lockout survives restart and is shared across instances)
        """
        CREATE TABLE IF NOT EXISTS LoginThrottle (
            Client       TEXT PRIMARY KEY,
            Failures     INTEGER NOT NULL DEFAULT 0,
            LockedUntil  TEXT NULL,
            LastActivity TEXT NOT NULL
        );
        """,

        // v9 - admin refresh tokens (short access tokens + long, revocable, rotating refresh tokens)
        """
        CREATE TABLE IF NOT EXISTS AdminRefreshTokens (
            Id        TEXT PRIMARY KEY,
            AdminId   TEXT NOT NULL REFERENCES AdminUsers (Id),
            Hash      TEXT NOT NULL,
            ExpiresAt TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            RevokedAt TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AdminRefreshTokens_Hash ON AdminRefreshTokens (Hash);
        CREATE INDEX IF NOT EXISTS IX_AdminRefreshTokens_AdminId ON AdminRefreshTokens (AdminId);
        """,

        // v10 - per-endpoint request-logging policy (what to record: params, result, slow threshold)
        """
        ALTER TABLE Endpoints ADD COLUMN LoggingJson TEXT NOT NULL DEFAULT '{}';
        """,

        // v11 - data-plane request log (per-call history: timing, rows, cache, slow flag, opt-in params/result)
        """
        CREATE TABLE IF NOT EXISTS RequestLog (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            Timestamp      TEXT    NOT NULL,
            EndpointId     TEXT    NULL,
            Route          TEXT    NOT NULL,
            HttpMethod     TEXT    NOT NULL,
            ConnectionName TEXT    NULL,
            ObjectName     TEXT    NULL,
            StatusCode     INTEGER NULL,
            Outcome        TEXT    NULL,
            DurationMs     REAL    NOT NULL,
            DbDurationMs   REAL    NULL,
            RowsReturned   INTEGER NULL,
            CacheHit       INTEGER NOT NULL DEFAULT 0,
            Slow           INTEGER NOT NULL DEFAULT 0,
            AverageMs      REAL    NULL,
            ApiKeyPrefix   TEXT    NULL,
            Parameters     TEXT    NULL,
            Result         TEXT    NULL,
            Error          TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS IX_RequestLog_Id ON RequestLog (Id DESC);
        CREATE INDEX IF NOT EXISTS IX_RequestLog_Endpoint ON RequestLog (EndpointId, Id DESC);
        CREATE INDEX IF NOT EXISTS IX_RequestLog_Timestamp ON RequestLog (Timestamp);
        """,

        // v12 - add ON DELETE CASCADE to AdminTokens and AdminRefreshTokens foreign keys.
        // SQLite does not support ALTER CONSTRAINT, so the tables must be dropped and recreated.
        """
        DROP INDEX IF EXISTS IX_AdminRefreshTokens_AdminId;
        DROP INDEX IF EXISTS UX_AdminRefreshTokens_Hash;
        DROP TABLE IF EXISTS AdminRefreshTokens;

        DROP INDEX IF EXISTS IX_AdminTokens_AdminId;
        DROP INDEX IF EXISTS UX_AdminTokens_Hash;
        DROP TABLE IF EXISTS AdminTokens;

        CREATE TABLE AdminTokens (
            Id         TEXT PRIMARY KEY,
            AdminId    TEXT NOT NULL REFERENCES AdminUsers (Id) ON DELETE CASCADE,
            Name       TEXT NOT NULL,
            Prefix     TEXT NOT NULL,
            Hash       TEXT NOT NULL,
            CreatedAt  TEXT NOT NULL,
            ExpiresAt  TEXT NULL,
            LastUsedAt TEXT NULL
        );
        CREATE UNIQUE INDEX UX_AdminTokens_Hash ON AdminTokens (Hash);
        CREATE INDEX IX_AdminTokens_AdminId ON AdminTokens (AdminId);

        CREATE TABLE AdminRefreshTokens (
            Id        TEXT PRIMARY KEY,
            AdminId   TEXT NOT NULL REFERENCES AdminUsers (Id) ON DELETE CASCADE,
            Hash      TEXT NOT NULL,
            ExpiresAt TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            RevokedAt TEXT NULL
        );
        CREATE UNIQUE INDEX UX_AdminRefreshTokens_Hash ON AdminRefreshTokens (Hash);
        CREATE INDEX IX_AdminRefreshTokens_AdminId ON AdminRefreshTokens (AdminId);
        """,

        // v13 - per-endpoint response delivery policy (stream or buffer, and the flush threshold).
        // Empty object means both fields are null, which is "use the system setting" - so an endpoint
        // that predates this column behaves exactly as it did.
        """
        ALTER TABLE Endpoints ADD COLUMN DeliveryJson TEXT NOT NULL DEFAULT '{}';
        """,

        // v14 - force-purge stamps, so a purge reaches the other instances of a deployment. Keyed by
        // route rather than by endpoint id: the cache is keyed by route too, and a row has to outlive
        // the endpoint long enough for every instance to have read it.
        """
        CREATE TABLE IF NOT EXISTS CachePurges (
            Route    TEXT PRIMARY KEY,
            PurgedAt TEXT NOT NULL
        );
        """,
    ];
}
