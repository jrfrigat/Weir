# Weir - Configuration

> [Russian / Russkiy](../ru/configuration.md) - [Deployment](deployment.md) - [Security](security.md)

Weir reads standard ASP.NET Core configuration: `appsettings.json`, `appsettings.{Environment}.json`,
environment variables, and command-line arguments (in increasing precedence). Nested keys map to
environment variables with a double underscore, e.g. `Weir:DataConnections:default:ConnectionString`
becomes `Weir__DataConnections__default__ConnectionString`.

## Settings

### `Weir:ControlPlane`

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Provider | `Sqlite` | Control-plane store. `Sqlite` (single node), or `Postgres` / `SqlServer` (a shared server database, for high availability). |
| ConnectionString | `Data Source=weir-control.db` | Connection string for Weir's own metadata store. For `Postgres`, an Npgsql connection string is required. |
| ReloadSeconds | `0` | How often each instance reloads the endpoint catalog from the store. Set to e.g. `30` in a multi-instance (HA) deployment so metadata changes made on one instance reach the others; `0` disables it (single node). |

For a high-availability deployment run several Weir instances against one PostgreSQL control database:

```sh
Weir__ControlPlane__Provider=Postgres
Weir__ControlPlane__ConnectionString="Host=pg;Database=weir_control;Username=weir;Password=..."
Weir__HighAvailability=true
```

`Weir:HighAvailability` (default `false`) asserts that the deployment is multi-instance. When `true`, the
host refuses to start on the single-node SQLite control plane and requires the shared PostgreSQL control
plane, so several instances cannot silently run against divergent per-node metadata files. See
[Deployment - High availability](deployment.md#high-availability) for the full HA checklist (shared JWT
key, Redis rate limiter, cross-instance metrics via OTLP).

### `Weir:DataConnections:{name}`

One entry per named target connection. Endpoints reference a connection by its `{name}`.

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Provider | `SqlServer` | Data-plane provider: `SqlServer` or `PostgreSql`. |
| ConnectionString | (required) | ADO.NET connection string for the target database. |
| DefaultCommandTimeoutSeconds | (none) | Default command timeout when an endpoint sets none. |

Both connectors retry only transient connection-open failures (before any command runs), so a
procedure is never invoked more than once.

### `Weir:Admin`

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Username | (none) | Bootstrap admin username. |
| Password | (none) | Bootstrap admin password. The admin is created on startup only if no admin exists and both values are set. Must be at least 8 characters (Weir's minimum password policy); a weaker value is logged and skipped. |
| MaxFailedLogins | `5` | Failed sign-ins from one client (source IP) before it is temporarily locked out. Zero disables lockout. |
| LockoutMinutes | `15` | How long a locked-out client stays locked. |
| MaxTokensPerAdmin | `20` | Maximum personal access tokens one admin may hold at once. Zero or less means unlimited. |
| RequireTokenExpiry | `false` | When true, a personal access token must be created with an expiry; a never-expiring token is rejected. |

### `Weir:Jwt`

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Issuer | `weir` | JWT issuer. |
| Audience | `weir-admin` | JWT audience. |
| SigningKey | (required in Production) | Symmetric signing secret. In Production the host refuses to start when it is empty or shorter than 32 characters; in Development an ephemeral key is generated (sessions reset on restart, and are not valid across instances). |
| AccessTokenMinutes | `30` | Admin access-token lifetime. Kept short because a refresh token silently renews the session; a shorter access token narrows the window a leaked or not-yet-revoked token is usable. Tokens are also revocable before expiry: changing an admin's password, or disabling the account, rejects that admin's outstanding tokens on the next request. |
| RefreshTokenDays | `14` | Admin refresh-token lifetime. A refresh token is long-lived, revocable and rotated on each use; it is exchanged (`POST /admin/api/auth/refresh`) for a fresh access token when the access token expires. A password change or sign-out revokes it. |

### `Weir:DataPlane`

Guards that protect the service and the database from a single expensive call. These values are the
seed / default; most are also editable at runtime from the admin **Settings** screen (see [Runtime
settings](#runtime-settings) below), which overlays the stored value on the seed without a restart.

| Key | Default | Meaning |
| :-- | :-- | :-- |
| MaxRows | `100000` | Maximum rows (across all result sets) streamed in one response. When exceeded, the response is closed early, marked `"truncated": true`, and is not cached. Zero means unlimited. |
| RequestTimeoutSeconds | `30` | Overall request timeout; on expiry the client receives HTTP 504. Zero means no gateway timeout (the database command timeout still applies). |
| MaxTvpRows | `100000` | Maximum rows accepted for one table-valued parameter; a larger array is rejected with HTTP 400. Zero means unlimited. |
| MaxRequestBodyBytes | `10485760` | Maximum request body size, enforced by the web server (HTTP 413 beyond it). Applied at startup, so a change requires a restart. Zero or less uses the server default. |
| DefaultApiKeyRateLimitPerMinute | `0` | Default per-minute limit for an API key that sets none of its own. Zero or less leaves such keys unthrottled. |
| MaxConcurrentRequestsPerConnection | `0` | Bulkhead: maximum executions allowed to run at once against one data connection. A request over the limit is rejected fast with HTTP 503. Zero means unlimited. |
| CircuitBreakerFailureThreshold | `0` | Consecutive data-connection failures that open the per-connection circuit breaker; while open, requests are short-circuited with HTTP 503 until a probe succeeds. Zero disables the breaker. |
| CircuitBreakerResetSeconds | `30` | How long a tripped breaker stays open before allowing a probe request through. |

#### Runtime settings

`MaxRows`, `RequestTimeoutSeconds`, `MaxTvpRows`, `DefaultApiKeyRateLimitPerMinute`,
`MaxConcurrentRequestsPerConnection`, `CircuitBreakerFailureThreshold` and `CircuitBreakerResetSeconds`
are editable at runtime from the admin **Settings** screen (or `GET` / `PUT /admin/api/settings`, Admin
role, audited).
The edited values are stored in the control plane, so they survive restarts and reach every instance
that shares a control database. `MaxRequestBodyBytes` is shown read-only there because it is applied to
the web server at startup and needs a restart to change.

### `Weir:Security`

| Key | Default | Meaning |
| :-- | :-- | :-- |
| ContentSecurityPolicy | (Blazor-compatible policy) | `Content-Security-Policy` header value. Set to empty to omit it. |
| RequireHttps | `false` | Redirect plain HTTP to HTTPS in-process. Leave off when a reverse proxy / ingress terminates TLS. |

### `Weir:RateLimit`

| Key | Default | Meaning |
| :-- | :-- | :-- |
| RedisConnectionString | (empty) | When set (for example `localhost:6379`), the per-API-key rate limiter counts requests in Redis so one limit applies across every instance, instead of the default in-memory limiter that enforces the limit per instance (N x the limit in an N-instance HA deployment). If Redis is briefly unreachable the limiter fails open (allows the request) and logs a warning. |
| RedisKeyPrefix | `weir:ratelimit:` | Key prefix for the limiter's Redis keys, so it can share a Redis instance with other applications. |

### `Weir:Plugins`

Load plugins (for example third-party connectors) into a running image without rebuilding it. See
[Extending](extending.md). Plugins run in-process - load only assemblies you trust.

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Paths | (empty) | Paths to plugin assemblies to load at startup, e.g. `Weir__Plugins__Paths__0=/plugins/Acme.Weir.MySql.dll`. |

Weir also always sends `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` and
`Referrer-Policy: no-referrer`, and `Strict-Transport-Security` (HSTS) outside Development.

## Standard host settings

| Key / variable | Meaning |
| :-- | :-- |
| ASPNETCORE_URLS | Bind address(es), e.g. `http://+:8080`. |
| ASPNETCORE_ENVIRONMENT | `Development` / `Production`. Development loads `appsettings.Development.json`. |
| Logging:LogLevel:* | Standard logging configuration. |

## Example (environment variables)

```sh
ASPNETCORE_URLS=http://+:8080
Weir__ControlPlane__ConnectionString="Data Source=/data/weir-control.db"
Weir__DataConnections__default__Provider=SqlServer
Weir__DataConnections__default__ConnectionString="Server=mssql;Database=Demo;User Id=sa;Password=...;TrustServerCertificate=True"
Weir__Admin__Username=admin
Weir__Admin__Password=a-strong-password
Weir__Jwt__SigningKey=a-stable-secret
```

## CORS

Browser clients that call the data-plane API from another origin need CORS. CORS is enabled only
when the allowed-origins list is non-empty.

| Key | Meaning |
| :-- | :-- |
| Weir:Cors:AllowedOrigins | Array of allowed origins, e.g. `[ "https://app.example.com" ]`. |

## Rate limiting

Each API key can carry a `RateLimitPerMinute` (set in the admin UI). Requests beyond the limit
receive HTTP 429 with a `Retry-After` header. The limiter is per-instance (in-memory); a
multi-instance deployment would need a distributed limiter.

## Auditing

Administrative actions (logins, key changes) are always audited. Data-plane call auditing is opt-in
because it writes one control-plane row per request; entries are queued and written by a background
writer, so they never block the request hot path.

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Weir:Audit:DataPlane | `false` | Record one audit entry per data-plane call. |
| Weir:Audit:QueueCapacity | `10000` | Bound of the in-memory audit queue; entries are dropped when full so requests never block. |

Audit history is pruned by a background service according to the runtime `AuditRetentionDays` setting
(edited on the admin **Settings** screen; zero keeps history forever). When the audit queue drops
entries under load, a warning is logged with the cumulative dropped count.

## Logging

Weir logs through Serilog. It writes a rolling file (and, optionally, the console); every request emits
one structured summary line carrying a correlation id (the `X-Correlation-ID` request header, or a
generated value, echoed back on the response). Security events - failed sign-ins, lockouts, scope/grant
denials, rate-limit hits and settings changes - are logged. The file configuration is also shown
read-only on the admin **Settings** screen.

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Weir:Logging:FileEnabled | `true` | Write logs to a rolling file. |
| Weir:Logging:ConsoleEnabled | `true` | Also write logs to the console. |
| Weir:Logging:Directory | `logs` | Directory for the log files (created if missing; relative to the content root). |
| Weir:Logging:FileName | `weir-.log` | Base file name; the roll date and, on a size roll, a sequence number are inserted before the extension. |
| Weir:Logging:RollingInterval | `Day` | `Infinite`, `Year`, `Month`, `Day`, `Hour` or `Minute`. |
| Weir:Logging:FileSizeLimitBytes | `52428800` | Roll to a new file once the current one reaches this size. Null disables the size roll. |
| Weir:Logging:RetainedFileCountLimit | `31` | Maximum number of retained rolling files. Null keeps them all. |
| Weir:Logging:RetainedFileTimeLimitDays | (none) | Maximum age of retained files in days. Null disables the time limit. |
| Weir:Logging:Format | `Text` | `Text` (human-readable) or `Json` (compact JSON, one object per line; includes the correlation id). |
| Weir:Logging:MinimumLevel | `Information` | `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. |

Logging settings are applied at startup, so a change requires a restart.

## Telemetry

Weir emits metrics and traces on the `Weir` meter and activity source (plus ASP.NET Core
instrumentation) and keeps an in-memory aggregator that powers the built-in dashboard with no
external backend. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export metrics and traces over OTLP to any
OpenTelemetry collector or the .NET Aspire dashboard.
