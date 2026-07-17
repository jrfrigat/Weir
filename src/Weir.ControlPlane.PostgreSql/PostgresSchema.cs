namespace Weir.ControlPlane.PostgreSql;

/// <summary>
/// Ordered, idempotent schema migrations for the PostgreSQL control plane. The store applies every
/// migration whose 1-based index exceeds the recorded schema version, then advances the version.
/// Append new migrations; never edit an already-shipped one.
/// </summary>
internal static class PostgresSchema
{
    /// <summary>The migration scripts, applied in order.</summary>
    public static readonly string[] Migrations =
    [
        // v1 - initial schema
        """
        CREATE TABLE IF NOT EXISTS Endpoints (
            Id                    text    PRIMARY KEY,
            Route                 text    NOT NULL,
            HttpMethod            text    NOT NULL,
            ConnectionName        text    NOT NULL,
            ObjectType            integer NOT NULL,
            SchemaName            text    NOT NULL,
            ObjectName            text    NOT NULL,
            ResultMode            integer NOT NULL,
            CommandTimeoutSeconds integer NULL,
            Enabled               boolean NOT NULL,
            CacheJson             text    NOT NULL,
            ParametersJson        text    NOT NULL,
            RequiredScopesJson    text    NOT NULL,
            Description           text    NULL,
            CreatedAt             text    NOT NULL,
            UpdatedAt             text    NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Endpoints_Method_Route ON Endpoints (HttpMethod, Route);

        CREATE TABLE IF NOT EXISTS ApiKeys (
            Id                 text    PRIMARY KEY,
            Name               text    NOT NULL,
            Prefix             text    NOT NULL,
            Hash               text    NOT NULL,
            ScopesJson         text    NOT NULL,
            Enabled            boolean NOT NULL,
            ExpiresAt          text    NULL,
            CreatedAt          text    NOT NULL,
            LastUsedAt         text    NULL,
            RateLimitPerMinute integer NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_ApiKeys_Hash ON ApiKeys (Hash);

        CREATE TABLE IF NOT EXISTS Scopes (
            Name        text PRIMARY KEY,
            Description text NULL
        );

        CREATE TABLE IF NOT EXISTS AdminUsers (
            Id           text    PRIMARY KEY,
            Username     text    NOT NULL,
            PasswordHash text    NOT NULL,
            Enabled      boolean NOT NULL,
            CreatedAt    text    NOT NULL,
            LastLoginAt  text    NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AdminUsers_Username ON AdminUsers (Username);

        CREATE TABLE IF NOT EXISTS Audit (
            Id         bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            Timestamp  text    NOT NULL,
            Category   text    NOT NULL,
            Actor      text    NULL,
            Route      text    NULL,
            Outcome    text    NULL,
            StatusCode integer NULL,
            DurationMs double precision NULL,
            Detail     text    NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Audit_Timestamp ON Audit (Timestamp);
        """,

        // v2 - admin role-based access control
        """
        ALTER TABLE AdminUsers ADD COLUMN IF NOT EXISTS Role text NOT NULL DEFAULT 'Admin';
        """,

        // v3 - per-key resource grants (which procedures a key may call)
        """
        ALTER TABLE ApiKeys ADD COLUMN IF NOT EXISTS GrantsJson text NOT NULL DEFAULT '[]';
        """,

        // v4 - personal access tokens for admins (scripted / CI-CD access to the admin API)
        """
        CREATE TABLE IF NOT EXISTS AdminTokens (
            Id         text PRIMARY KEY,
            AdminId    text NOT NULL REFERENCES AdminUsers (Id) ON DELETE CASCADE,
            Name       text NOT NULL,
            Prefix     text NOT NULL,
            Hash       text NOT NULL,
            CreatedAt  text NOT NULL,
            ExpiresAt  text NULL,
            LastUsedAt text NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AdminTokens_Hash ON AdminTokens (Hash);
        CREATE INDEX IF NOT EXISTS IX_AdminTokens_AdminId ON AdminTokens (AdminId);
        """,

        // v5 - runtime settings document (single row) editable from the admin panel
        """
        CREATE TABLE IF NOT EXISTS Settings (
            Id        integer PRIMARY KEY CHECK (Id = 1),
            Json      text NOT NULL,
            UpdatedAt text NOT NULL
        );
        """,

        // v6 - token version for JWT revocation (bumped on password / role change or disable)
        """
        ALTER TABLE AdminUsers ADD COLUMN IF NOT EXISTS TokenVersion integer NOT NULL DEFAULT 0;
        """,

        // v7 - per-endpoint toggle to suppress SQL PRINT / notice messages in the response
        """
        ALTER TABLE Endpoints ADD COLUMN IF NOT EXISTS SuppressMessages boolean NOT NULL DEFAULT false;
        """,

        // v8 - persisted admin sign-in throttle (lockout survives restart and is shared across instances)
        """
        CREATE TABLE IF NOT EXISTS LoginThrottle (
            Client       text PRIMARY KEY,
            Failures     integer NOT NULL DEFAULT 0,
            LockedUntil  text NULL,
            LastActivity text NOT NULL
        );
        """,

        // v9 - admin refresh tokens (short access tokens + long, revocable, rotating refresh tokens)
        """
        CREATE TABLE IF NOT EXISTS AdminRefreshTokens (
            Id        text PRIMARY KEY,
            AdminId   text NOT NULL REFERENCES AdminUsers (Id) ON DELETE CASCADE,
            Hash      text NOT NULL,
            ExpiresAt text NOT NULL,
            CreatedAt text NOT NULL,
            RevokedAt text NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AdminRefreshTokens_Hash ON AdminRefreshTokens (Hash);
        CREATE INDEX IF NOT EXISTS IX_AdminRefreshTokens_AdminId ON AdminRefreshTokens (AdminId);
        """,

        // v10 - per-endpoint request-logging policy (what to record: params, result, slow threshold)
        """
        ALTER TABLE Endpoints ADD COLUMN IF NOT EXISTS LoggingJson text NOT NULL DEFAULT '{}';
        """,

        // v11 - data-plane request log (per-call history: timing, rows, cache, slow flag, opt-in params/result)
        """
        CREATE TABLE IF NOT EXISTS RequestLog (
            Id             bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            Timestamp      text    NOT NULL,
            EndpointId     text    NULL,
            Route          text    NOT NULL,
            HttpMethod     text    NOT NULL,
            ConnectionName text    NULL,
            ObjectName     text    NULL,
            StatusCode     integer NULL,
            Outcome        text    NULL,
            DurationMs     double precision NOT NULL,
            DbDurationMs   double precision NULL,
            RowsReturned   integer NULL,
            CacheHit       boolean NOT NULL DEFAULT false,
            Slow           boolean NOT NULL DEFAULT false,
            AverageMs      double precision NULL,
            ApiKeyPrefix   text    NULL,
            Parameters     text    NULL,
            Result         text    NULL,
            Error          text    NULL
        );
        CREATE INDEX IF NOT EXISTS IX_RequestLog_Id ON RequestLog (Id DESC);
        CREATE INDEX IF NOT EXISTS IX_RequestLog_Endpoint ON RequestLog (EndpointId, Id DESC);
        CREATE INDEX IF NOT EXISTS IX_RequestLog_Timestamp ON RequestLog (Timestamp);
        """,

        // v12 - per-endpoint response delivery policy (stream or buffer, and the flush threshold).
        // Empty object means both fields are null, which is "use the system setting" - so an endpoint
        // that predates this column behaves exactly as it did.
        """
        ALTER TABLE Endpoints ADD COLUMN IF NOT EXISTS DeliveryJson text NOT NULL DEFAULT '{}';
        """,
    ];
}
