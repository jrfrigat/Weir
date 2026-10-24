# Weir - Configuration

> [Русский](../ru/configuration.md) - [Deployment](deployment.md) - [Security](security.md)

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
| Pool | (driver default) | Connection pooling for this connection. See below. |

Both connectors retry only transient connection-open failures (before any command runs), so a
procedure is never invoked more than once.

#### `Weir:DataConnections:{name}:Pool`

Both shipped drivers pool by default, so **Weir already reuses connections** whether or not this
section is present: a request borrows a connection from the pool, and when the pool is full it waits
for one to come back rather than opening another. This section is how you say that on purpose, in
Weir's terms, instead of hand-writing driver keywords into the connection string.

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Enabled | (driver default: pooled) | `false` turns pooling off: every request opens its own physical connection and closes it afterwards. |
| MinSize | (driver default) | Connections the pool keeps open while idle. |
| MaxSize | (driver default) | The most physical connections this connection may hold. Once the pool is full, further requests wait. |
| AcquireTimeoutSeconds | (driver default) | How long a request waits for a free pooled connection before failing. |

```json
"Weir": {
  "DataConnections": {
    "default": {
      "Provider": "SqlServer",
      "ConnectionString": "Server=db;Database=Sales;...",
      "Pool": { "Enabled": true, "MinSize": 2, "MaxSize": 40, "AcquireTimeoutSeconds": 15 }
    }
  }
}
```

Notes worth knowing before setting any of it:

- **A key left out changes nothing.** The section overlays the connection string the way the runtime
  settings overlay their `appsettings.json` seeds, so a connection string that already carries
  `Max Pool Size` keeps it until `MaxSize` says otherwise.
- **`AcquireTimeoutSeconds` is the connect timeout.** Both drivers use one timeout for "get me a
  connection", covering opening a new one and waiting for a pooled one alike; neither has a separate
  queue timeout. So this is also the timeout for reaching a database that is simply slow to answer.
- **Turning pooling off costs a TCP connect and a login handshake per request.** That is the cost
  pooling exists to avoid, so set `Enabled: false` only when a connection genuinely may not be shared -
  a server holding per-session state, for instance - not to keep things simple.
- **`MinSize` / `MaxSize` with `Enabled: false` is refused at startup**, rather than half applied: it
  says you expect a pool that will not exist.
- **The pool is not the only limit on concurrency.** `MaxConcurrentRequestsPerConnection` (a runtime
  setting) is a bulkhead in front of it, and it is fail-fast: a request over that limit gets HTTP 503
  instead of waiting. If you want requests to queue for a connection, leave that setting at zero and let
  `MaxSize` be the ceiling.
- The configured pool is reported (read-only, no connection string) by `GET /admin/api/connections`.

### `Weir:Ports`

Optional split of Weir's two surfaces onto listeners of their own, so the endpoint API and the admin
surface can sit behind different network policies - the way an admin interface and a published
application each listen on their own port behind a proxy. The `docker-compose.yml` in this repository
turns it on (8080 for the endpoint API, 8081 for the admin console); the keys below are how it is
changed.

| Key | Default | Meaning |
| :-- | :-- | :-- |
| AdminPort | (unset) | Port the admin surface answers on: `/admin/api`, the `/hubs` dashboard hub and the admin PWA (its static files and the index fallback). |
| DataPlanePort | (unset) | Port the endpoint API answers on: `/api`, the `/ws` session and gRPC. |
| MainPort | (unset) | Port a surface keeps when the other one was given its own. Unset reads it from the configured URLs (the first `http` address), then falls back to `8080`. |

- **Both unset (the default) changes nothing.** The host listens wherever `ASPNETCORE_URLS` says and
  serves every surface from that one port.
- **Setting either port turns the split on.** Weir then binds both listeners itself, so
  `ASPNETCORE_URLS` no longer applies and the surface that was not given a port keeps `MainPort` (or
  the port read from those URLs). Starting in this mode logs the two ports and that fact.
- Admin requests are answered only on `AdminPort`, data-plane requests on any other port.
  `/health`, `/health/live` and `/health/ready` answer on both, so a probe works wherever it points.
- The decision comes from the port a request arrived on, never from the `Host` header, which the
  client supplies. A request of the wrong surface gets `404` with no body, exactly as an unknown
  route would - it is not told that the surface exists elsewhere.
- Ports are read at startup, like the file logging settings: changing them needs a restart, and they
  are not shown on the admin **Settings** screen.
- The listeners Weir binds are plain HTTP. Terminate TLS at the proxy, as described in
  [Deployment](deployment.md#splitting-the-two-surfaces-onto-separate-ports).

```json
{
  "Weir": {
    "Ports": { "AdminPort": 8081, "DataPlanePort": 8080 }
  }
}
```

In a container, publish both ports (`-p 8080:8080 -p 8081:8081`), as the `docker-compose.yml` in this
repository does: the data-plane port is the public one, and the admin port is the one your proxy, VPN or
firewall keeps private.

### `Weir:Admin`

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Username | (none) | Bootstrap admin username. |
| Password | (none) | Bootstrap admin password. The admin is created on startup only if no admin exists and both values are set. Must be at least 8 characters (Weir's minimum password policy); a weaker value is logged and skipped. |
| MaxFailedLogins | `5` | Failed sign-ins from one client (source IP) before it is temporarily locked out. Zero disables lockout. |
| LockoutMinutes | `15` | How long a locked-out client stays locked. |
| MaxTokensPerAdmin | `20` | Maximum personal access tokens one admin may hold at once. Zero or less means unlimited. |
| RequireTokenExpiry | `false` | When true, a personal access token must be created with an expiry; a never-expiring token is rejected. |
| PasswordIterations | `100000` | PBKDF2-SHA256 work factor for admin password hashes. Raise it as hardware gets faster; no migration is needed, since the count is stored inside each hash and an existing password moves up when it is next changed. Values below 10000 are refused at startup. |

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
| MaxImportRows | `50000` | Maximum rows one import request may carry. An endpoint can set a lower limit of its own and the smaller of the two applies, so this is the ceiling nothing may raise. Zero means unlimited, which is worth thinking about: the rows are held in memory to be coerced before any of them is written. |
| MaxRequestBodyBytes | `10485760` | Maximum request body size, enforced by the web server (HTTP 413 beyond it). Applied at startup, so a change requires a restart. Zero or less uses the server default. |
| DefaultApiKeyRateLimitPerMinute | `0` | Default per-minute limit for an API key that sets none of its own. Zero or less leaves such keys unthrottled. |
| MaxConcurrentRequestsPerConnection | `0` | Bulkhead: maximum executions allowed to run at once against one data connection. A request over the limit is rejected fast with HTTP 503. Zero means unlimited. |
| CircuitBreakerFailureThreshold | `0` | Consecutive data-connection failures that open the per-connection circuit breaker; while open, requests are short-circuited with HTTP 503 until a probe succeeds. Zero disables the breaker. |
| CircuitBreakerResetSeconds | `30` | How long a tripped breaker stays open before allowing a probe request through. |
| ApiKeyFailureThreshold | `20` | How many unresolved API keys one caller address may present per minute before further requests are refused with HTTP 429 before a lookup. Caps a random-key flood, which would otherwise query the store on every request (an unknown key is never cached). A valid key never counts against it. Zero disables the guard. Per caller address, so behind a proxy it needs `Weir:Network:TrustedProxies`. |
| ResponseCacheMaxBytes | `134217728` | Total bytes cached response payloads may occupy in memory. Once the cache is full, the least recently used entries are evicted to make room; a payload larger than the cap is never cached. Zero or less means unlimited, which lets an endpoint with a high-cardinality `VaryByParameters` grow the cache until the process runs out of memory. Changing it rebuilds the cache, discarding the entries it currently holds. |

#### Runtime settings

`MaxRows`, `RequestTimeoutSeconds`, `MaxTvpRows`, `MaxImportRows`, `DefaultApiKeyRateLimitPerMinute`,
`MaxConcurrentRequestsPerConnection`, `CircuitBreakerFailureThreshold`, `CircuitBreakerResetSeconds`,
`ApiKeyFailureThreshold`, `ResponseDeliveryMode`, `ResponseFlushBytes`, `ResponseCompressionMode` and
`ResponseCacheMaxBytes` are editable at runtime from the admin **Settings** screen (or `GET` /
`PUT /admin/api/settings`, Admin role, audited).
The edited values are stored in the control plane, so they survive restarts and reach every instance
that shares a control database. `MaxRequestBodyBytes` is shown read-only there because it is applied to
the web server at startup and needs a restart to change.

Three further settings exist only at runtime - they have no `appsettings.json` seed and are edited in
the same place, under the **Request log** group:

| Setting | Default | Meaning |
| :-- | :-- | :-- |
| RequestLogEnabled | `true` | Master switch for the data-plane request log (the per-call history shown in the admin panel). When off, no request-log rows are written regardless of per-endpoint settings. Basic call metadata is recorded when on; parameter and result capture remain per-endpoint opt-ins. |
| SlowRequestThresholdPercent | `20` | Default "slow" threshold, as a percentage above an endpoint's rolling average duration: a logged request is flagged slow when its duration exceeds the endpoint's recent average by at least this much (for example `20` means "20% slower than usual"). An endpoint may override it. Zero or less disables slow flagging. |
| RequestLogRetentionDays | `7` | How many days of request-log history to keep; older rows are pruned by a background service. Zero or less keeps history forever (not recommended - the log can grow quickly). |

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

## Client certificates (mTLS)

Off by default. With `Mode` at `None` nothing here runs and no request pays for it.

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Weir:Mtls:Mode | `None` | `None`, `Allow` (read a certificate when one is presented) or `Require` (every data-plane request must present an acceptable one). |
| Weir:Mtls:TrustedCaFiles | `[]` | PEM or DER files holding the accepted certificate authorities. When set, chains are verified against these roots **only**, not the machine trust store. |
| Weir:Mtls:TrustedCaThumbprints | `[]` | Thumbprints that at least one certificate in the built chain must match. Pins an issuer without deploying its file. |
| Weir:Mtls:AllowUnverifiedChain | `false` | Accept a certificate whose chain does not verify (a self-signed one in development). Logged as a warning at startup. |
| Weir:Mtls:RevocationMode | `NoCheck` | `NoCheck`, `Offline` or `Online`. The default is deliberate: an internal CA usually has no reachable CRL or OCSP responder, and `Online` then does not check anything - it stalls the handshake until the lookup times out. |
| Weir:Mtls:ForwardedCertificateHeader | empty | Header a trusted reverse proxy puts the certificate in. Empty means no header is read, so TLS must terminate at Kestrel. |
| Weir:Mtls:MaxForwardedCertificateBytes | `16384` | Largest forwarded header Weir will try to parse. The header is caller input; a real certificate is a few kilobytes. |

Two levels decide whether a call is allowed, and they answer different questions. `Mode` answers
"does this deployment let anyone in without a certificate at all". A key's own policy - set in the
admin panel, see [security](security.md#client-certificates-mtls) - answers "which certificate does
*this* key accept". Either can refuse on its own.

Setting a certificate policy on a key while `Mode` is `None` leaves that key unable to authenticate:
no certificate can reach a request, so the policy can never be satisfied. That is a refusal in the
closing direction rather than a mistake, and configuring keys before enabling mTLS is a normal order
of work - the admin panel warns when you save such a policy.

Weir never asks Kestrel to *require* a certificate at the TLS layer, even at `Mode=Require`. The
same listener serves the admin PWA, and a TLS-level requirement would make an operator's browser
produce a client certificate just to open the dashboard. The requirement is applied per request
instead, which is also the only form of it that works behind a proxy, where Weir terminates no TLS
at all. A deployment that wants the handshake itself to refuse should split the surfaces with
[`Weir:Ports`](#weirports) and configure that on the listener or the proxy.

## CORS

Browser clients that call the data-plane API from another origin need CORS. CORS is enabled only
when the allowed-origins list is non-empty.

| Key | Default | Meaning |
| :-- | :-- | :-- |
| Weir:Cors:AllowedOrigins | `[]` | Array of allowed origins, e.g. `[ "https://app.example.com" ]`. Empty leaves CORS off. |
| Weir:Cors:AllowedMethods | `[]` | Methods the preflight answer permits. Empty answers with the data plane's own methods. |
| Weir:Cors:AllowedHeaders | `[]` | Request headers the preflight permits. Empty echoes what the preflight asked for. |
| Weir:Cors:ExposedHeaders | `[]` | Response headers the browser may read, beyond the CORS-safelisted ones. |
| Weir:Cors:AllowCredentials | `false` | Whether the browser may send credentials. Refused at startup together with the `*` origin, which the specification forbids and browsers reject. |
| Weir:Cors:MaxAgeSeconds | unset | How long the browser may cache a preflight answer. |

This section is the server-wide default, and a key that states no CORS policy of its own is answered
with it. A key that *does* state one is answered with that policy instead - it **replaces** the
default rather than adding to it, so a key with its own origins does not also get the server's. A
policy that is present but names no origin closes cross-origin access for that key alone, and the
server default does not open it back up.

A CORS preflight carries no API key - the browser does not send one, because it is still deciding
whether it will send the caller's headers at all. So a preflight is answered from the origin alone,
matched against the policies of every enabled key plus the server default. Two consequences are
worth knowing:

- where the server default and a key both name the same origin, the preflight carries the server's
  answer, even if the key's is narrower;
- `AllowedMethods` is therefore advice to the browser, not an authorization rule. Nothing on the
  server consults it again. What a key may actually call is decided by its scopes, its resource
  grants and the endpoint's own method.

## Rate limiting

Each API key can carry a `RateLimitPerMinute` (set in the admin UI). Requests beyond the limit
receive HTTP 429 with a `Retry-After` header. The limiter is per-instance (in-memory) by default; set
[`Weir:RateLimit:RedisConnectionString`](#weirratelimit) to count in Redis instead, so one limit
applies across every instance of a multi-instance deployment.

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
