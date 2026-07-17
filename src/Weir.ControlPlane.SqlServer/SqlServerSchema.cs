namespace Weir.ControlPlane.SqlServer;

/// <summary>
/// Ordered, idempotent schema migrations for the SQL Server control plane. The store applies every
/// migration whose 1-based index exceeds the recorded schema version, then advances the version.
/// Append new migrations; never edit an already-shipped one.
/// </summary>
internal static class SqlServerSchema
{
    /// <summary>The migration scripts, applied in order.</summary>
    public static readonly string[] Migrations =
    [
        // v1 - initial schema
        """
        IF OBJECT_ID(N'Endpoints', N'U') IS NULL
        CREATE TABLE Endpoints (
            Id                    nvarchar(64)  PRIMARY KEY,
            Route                 nvarchar(450) NOT NULL,
            HttpMethod            nvarchar(16)  NOT NULL,
            ConnectionName        nvarchar(128) NOT NULL,
            ObjectType            int           NOT NULL,
            SchemaName            nvarchar(128) NOT NULL,
            ObjectName            nvarchar(256) NOT NULL,
            ResultMode            int           NOT NULL,
            CommandTimeoutSeconds int           NULL,
            Enabled               bit           NOT NULL,
            CacheJson             nvarchar(max) NOT NULL,
            ParametersJson        nvarchar(max) NOT NULL,
            RequiredScopesJson    nvarchar(max) NOT NULL,
            Description           nvarchar(max) NULL,
            CreatedAt             nvarchar(40)  NOT NULL,
            UpdatedAt             nvarchar(40)  NOT NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Endpoints_Method_Route' AND object_id = OBJECT_ID(N'Endpoints'))
        CREATE UNIQUE INDEX UX_Endpoints_Method_Route ON Endpoints (HttpMethod, Route);

        IF OBJECT_ID(N'ApiKeys', N'U') IS NULL
        CREATE TABLE ApiKeys (
            Id                 nvarchar(64)  PRIMARY KEY,
            Name               nvarchar(200) NOT NULL,
            Prefix             nvarchar(64)  NOT NULL,
            Hash               nvarchar(200) NOT NULL,
            ScopesJson         nvarchar(max) NOT NULL,
            Enabled            bit           NOT NULL,
            ExpiresAt          nvarchar(40)  NULL,
            CreatedAt          nvarchar(40)  NOT NULL,
            LastUsedAt         nvarchar(40)  NULL,
            RateLimitPerMinute int           NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ApiKeys_Hash' AND object_id = OBJECT_ID(N'ApiKeys'))
        CREATE UNIQUE INDEX UX_ApiKeys_Hash ON ApiKeys (Hash);

        IF OBJECT_ID(N'Scopes', N'U') IS NULL
        CREATE TABLE Scopes (
            Name        nvarchar(200) PRIMARY KEY,
            Description nvarchar(max) NULL
        );

        IF OBJECT_ID(N'AdminUsers', N'U') IS NULL
        CREATE TABLE AdminUsers (
            Id           nvarchar(64)  PRIMARY KEY,
            Username     nvarchar(256) NOT NULL,
            PasswordHash nvarchar(max) NOT NULL,
            Enabled      bit           NOT NULL,
            CreatedAt    nvarchar(40)  NOT NULL,
            LastLoginAt  nvarchar(40)  NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_AdminUsers_Username' AND object_id = OBJECT_ID(N'AdminUsers'))
        CREATE UNIQUE INDEX UX_AdminUsers_Username ON AdminUsers (Username);

        IF OBJECT_ID(N'Audit', N'U') IS NULL
        CREATE TABLE Audit (
            Id         bigint        IDENTITY(1,1) PRIMARY KEY,
            Timestamp  nvarchar(40)  NOT NULL,
            Category   nvarchar(64)  NOT NULL,
            Actor      nvarchar(256) NULL,
            Route      nvarchar(450) NULL,
            Outcome    nvarchar(64)  NULL,
            StatusCode int           NULL,
            DurationMs float         NULL,
            Detail     nvarchar(max) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Audit_Timestamp' AND object_id = OBJECT_ID(N'Audit'))
        CREATE INDEX IX_Audit_Timestamp ON Audit (Timestamp);
        """,

        // v2 - admin role-based access control
        """
        IF COL_LENGTH(N'AdminUsers', N'Role') IS NULL
        ALTER TABLE AdminUsers ADD Role nvarchar(64) NOT NULL DEFAULT 'Admin';
        """,

        // v3 - per-key resource grants (which procedures a key may call)
        """
        IF COL_LENGTH(N'ApiKeys', N'GrantsJson') IS NULL
        ALTER TABLE ApiKeys ADD GrantsJson nvarchar(max) NOT NULL DEFAULT '[]';
        """,

        // v4 - personal access tokens for admins (scripted / CI-CD access to the admin API)
        """
        IF OBJECT_ID(N'AdminTokens', N'U') IS NULL
        CREATE TABLE AdminTokens (
            Id         nvarchar(64)  PRIMARY KEY,
            AdminId    nvarchar(64)  NOT NULL REFERENCES AdminUsers (Id) ON DELETE CASCADE,
            Name       nvarchar(200) NOT NULL,
            Prefix     nvarchar(64)  NOT NULL,
            Hash       nvarchar(200) NOT NULL,
            CreatedAt  nvarchar(40)  NOT NULL,
            ExpiresAt  nvarchar(40)  NULL,
            LastUsedAt nvarchar(40)  NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_AdminTokens_Hash' AND object_id = OBJECT_ID(N'AdminTokens'))
        CREATE UNIQUE INDEX UX_AdminTokens_Hash ON AdminTokens (Hash);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AdminTokens_AdminId' AND object_id = OBJECT_ID(N'AdminTokens'))
        CREATE INDEX IX_AdminTokens_AdminId ON AdminTokens (AdminId);
        """,

        // v5 - runtime settings document (single row) editable from the admin panel
        """
        IF OBJECT_ID(N'Settings', N'U') IS NULL
        CREATE TABLE Settings (
            Id        int           PRIMARY KEY CHECK (Id = 1),
            Json      nvarchar(max) NOT NULL,
            UpdatedAt nvarchar(40)  NOT NULL
        );
        """,

        // v6 - token version for JWT revocation (bumped on password / role change or disable)
        """
        IF COL_LENGTH(N'AdminUsers', N'TokenVersion') IS NULL
        ALTER TABLE AdminUsers ADD TokenVersion int NOT NULL DEFAULT 0;
        """,

        // v7 - per-endpoint toggle to suppress SQL PRINT / notice messages in the response
        """
        IF COL_LENGTH(N'Endpoints', N'SuppressMessages') IS NULL
        ALTER TABLE Endpoints ADD SuppressMessages bit NOT NULL DEFAULT 0;
        """,

        // v8 - persisted admin sign-in throttle (lockout survives restart and is shared across instances)
        """
        IF OBJECT_ID(N'LoginThrottle', N'U') IS NULL
        CREATE TABLE LoginThrottle (
            Client       nvarchar(128) PRIMARY KEY,
            Failures     int           NOT NULL DEFAULT 0,
            LockedUntil  nvarchar(40)  NULL,
            LastActivity nvarchar(40)  NOT NULL
        );
        """,

        // v9 - admin refresh tokens (short access tokens + long, revocable, rotating refresh tokens)
        """
        IF OBJECT_ID(N'AdminRefreshTokens', N'U') IS NULL
        CREATE TABLE AdminRefreshTokens (
            Id        nvarchar(64) PRIMARY KEY,
            AdminId   nvarchar(64) NOT NULL REFERENCES AdminUsers (Id) ON DELETE CASCADE,
            Hash      nvarchar(200) NOT NULL,
            ExpiresAt nvarchar(40) NOT NULL,
            CreatedAt nvarchar(40) NOT NULL,
            RevokedAt nvarchar(40) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_AdminRefreshTokens_Hash' AND object_id = OBJECT_ID(N'AdminRefreshTokens'))
        CREATE UNIQUE INDEX UX_AdminRefreshTokens_Hash ON AdminRefreshTokens (Hash);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AdminRefreshTokens_AdminId' AND object_id = OBJECT_ID(N'AdminRefreshTokens'))
        CREATE INDEX IX_AdminRefreshTokens_AdminId ON AdminRefreshTokens (AdminId);
        """,

        // v10 - per-endpoint request-logging policy (what to record: params, result, slow threshold)
        """
        IF COL_LENGTH(N'Endpoints', N'LoggingJson') IS NULL
        ALTER TABLE Endpoints ADD LoggingJson nvarchar(max) NOT NULL DEFAULT '{}';
        """,

        // v11 - data-plane request log (per-call history: timing, rows, cache, slow flag, opt-in params/result)
        """
        IF OBJECT_ID(N'RequestLog', N'U') IS NULL
        CREATE TABLE RequestLog (
            Id             bigint        IDENTITY(1,1) PRIMARY KEY,
            Timestamp      nvarchar(40)  NOT NULL,
            EndpointId     nvarchar(64)  NULL,
            Route          nvarchar(450) NOT NULL,
            HttpMethod     nvarchar(16)  NOT NULL,
            ConnectionName nvarchar(128) NULL,
            ObjectName     nvarchar(256) NULL,
            StatusCode     int           NULL,
            Outcome        nvarchar(64)  NULL,
            DurationMs     float         NOT NULL,
            DbDurationMs   float         NULL,
            RowsReturned   int           NULL,
            CacheHit       bit           NOT NULL DEFAULT 0,
            Slow           bit           NOT NULL DEFAULT 0,
            AverageMs      float         NULL,
            ApiKeyPrefix   nvarchar(64)  NULL,
            Parameters     nvarchar(max) NULL,
            Result         nvarchar(max) NULL,
            Error          nvarchar(max) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RequestLog_Id' AND object_id = OBJECT_ID(N'RequestLog'))
        CREATE INDEX IX_RequestLog_Id ON RequestLog (Id DESC);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RequestLog_Endpoint' AND object_id = OBJECT_ID(N'RequestLog'))
        CREATE INDEX IX_RequestLog_Endpoint ON RequestLog (EndpointId, Id DESC);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RequestLog_Timestamp' AND object_id = OBJECT_ID(N'RequestLog'))
        CREATE INDEX IX_RequestLog_Timestamp ON RequestLog (Timestamp);
        """,

        // v12 - per-endpoint response delivery policy (stream or buffer, and the flush threshold).
        // Empty object means both fields are null, which is "use the system setting" - so an endpoint
        // that predates this column behaves exactly as it did.
        """
        IF COL_LENGTH(N'Endpoints', N'DeliveryJson') IS NULL
        ALTER TABLE Endpoints ADD DeliveryJson nvarchar(max) NOT NULL DEFAULT '{}';
        """,

        // v13 - force-purge stamps, so a purge reaches the other instances of a deployment. Keyed by
        // route rather than by endpoint id: the cache is keyed by route too, and a row has to outlive
        // the endpoint long enough for every instance to have read it.
        """
        IF OBJECT_ID(N'CachePurges', N'U') IS NULL
        CREATE TABLE CachePurges (
            Route    nvarchar(450) NOT NULL PRIMARY KEY,
            PurgedAt nvarchar(33)  NOT NULL
        );
        """,
    ];
}
