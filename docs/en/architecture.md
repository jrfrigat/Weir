# Weir - Architecture

English | [Russian / Russkiy](../ru/architecture.md)

Weir is a metadata-driven gateway that maps HTTP endpoints to stored procedures / functions and
returns JSON. It is deliberately **thin**: the request path does routing, authentication,
parameter binding, optional caching, telemetry, and streaming serialization - nothing else.

## Two planes

### Data plane (hot path)

```
HTTP request
  1. Route match      dynamic endpoint table (in-memory snapshot, refreshed on metadata change)
  2. AuthN/AuthZ      API key -> hash lookup (cached) -> required-scope check
  3. Bind params      request (body/query/route/header/claim/const) -> WeirParameter[]  (incl. TVP)
  4. Cache lookup     if endpoint cache enabled -> key = route + vary-by params (+ api key)
  5. Execute          IDbConnector runs the SP/function via Dapper on the named connection
  6. Stream JSON      DbDataReader -> Utf8JsonWriter, straight into the response body
  7. Observe          IWeirCallObserver: OpenTelemetry + in-memory aggregator
```

Output parameters, return value, rowsAffected and SQL messages become available **after** the
reader is consumed, so the envelope is streamed as: write "data" (all result sets), close reader,
append "output" / "returnValue" / "rowsAffected" / "messages".

### Control plane (metadata)

Weir's own state - endpoint definitions, API keys (hashes only), scopes, admin users, audit, and a
single-row runtime-settings document - lives in a **separate** store behind IControlPlaneStore. Three
providers ship: SQLite (default, single node), PostgreSQL and SQL Server (each a shared server
database for high-availability deployments where several instances run against one control database).
Each owns an idempotent, transactional migration runner; the provider is selected by
`Weir:ControlPlane:Provider`.

### Runtime settings

`IRuntimeSettings` holds the tunable system settings (data-plane limits, the default per-key rate
limit, audit retention). It seeds from `appsettings.json`, overlays the persisted control-plane
document on startup, and is read live by the engine, binder, rate limiter and gateway timeout - so an
edit from the admin **Settings** screen (`PUT /admin/api/settings`, Admin-only, audited) takes effect
without a restart and reaches every instance sharing a control database.

## Module map and dependency direction

```
Weir.Contracts        pure DTOs and enums (browser-safe; shared by Host and Admin)
Weir.Abstractions     server ports: IDbConnector, IControlPlaneStore, IWeirCallObserver,
                      IMetricsAggregator, IResponseCache   (references System.Data.Common)

  depends on Abstractions:
    Weir.Core                    engine (resolution, binding, JSON writer, cache orchestration)
    Weir.Diagnostics             ActivitySource "Weir", Meter "Weir", in-memory aggregator
    Weir.ControlPlane.Sqlite     IControlPlaneStore impl + migrations
    Weir.ControlPlane.PostgreSql IControlPlaneStore impl + migrations (shared store for HA)
    Weir.ControlPlane.SqlServer  IControlPlaneStore impl + migrations (shared store for HA)
    connectors/Weir.Connectors.SqlServer    IDbConnector impl (SqlClient + Dapper)
    connectors/Weir.Connectors.PostgreSql   IDbConnector impl (Npgsql)

  composition root:
    Weir.Host      DI wiring, dynamic endpoint mapping, API-key middleware, admin API,
                   health checks, serves the PWA
    Weir.Admin     Blazor WASM PWA (Flare) - dashboard + management
```

Everything depends only on Weir.Contracts / Weir.Abstractions. Drivers and stores implement the
ports; Weir.Host composes the concrete set via DI (AddWeirSqlServer(), AddWeirPostgreSql(),
AddWeirControlPlaneSqlite(), AddWeirControlPlanePostgres(), AddWeirControlPlaneSqlServer()). Both
connectors retry only transient connection-open failures, so a procedure is never invoked more than
once.

The same ports are the plugin surface: a third-party connector implements IDbConnector and is either
compiled into a custom host or dropped into a running image via the plugin loader
(`Weir:Plugins:Paths`). See [Extending](extending.md).

## Named connections

Weir:DataConnections:{name} maps a logical name to { provider, connectionString }. Each endpoint
references a ConnectionName, and its object is addressed as schema.object. One Weir instance
therefore serves many servers / databases / schemas simultaneously.

## Caching

Per-endpoint CachePolicy (Enabled, TtlSeconds, VaryByParameters, VaryByApiKey, CoalesceRequests) is
set in the admin UI. The cache key is the endpoint route plus the normalized values of the vary-by
parameters (and, optionally, the API key); with CoalesceRequests on (the default), callers that arrive
while a response for the same key is already being produced wait for its bytes instead of each running
the object. Rendered JSON bytes are cached via IResponseCache (in memory now, bounded by
`ResponseCacheMaxBytes` - the least recently used entries are evicted once it is full; the abstraction
allows a distributed backend later). Cache is server-controlled; clients do not opt out.

## Telemetry

- ActivitySource "Weir" - one span per call, tagged with route, db.system, connection, object,
  rows, cache hit, outcome, and (on a failure) the classified db error category.
- Meter "Weir" - weir.requests, weir.request.duration, weir.db.duration,
  weir.cache.hits / weir.cache.misses, weir.rows, weir.active_requests, and weir.db.errors (tagged by
  route and category: timeout / deadlock / constraint / connection / other).
- OTLP exporter (opt-in) -> .NET Aspire dashboard / any OpenTelemetry backend. The "Npgsql" meter is
  also subscribed, so PostgreSQL connection-pool metrics (idle / busy connections, saturation) are
  exported alongside Weir's. SQL Server pool state is available through the
  Microsoft.Data.SqlClient event counters when needed.
- Built-in IMetricsAggregator keeps rolling per-endpoint aggregates (count, error rate,
  windowed p50/p95/p99, req/s, cache-hit ratio, recent calls) that power the PWA dashboard with no
  external infrastructure.
- IWeirCallObserver is the extension point: add custom sinks (Prometheus, App Insights, audit)
  without touching the hot path. Parameter values are never logged by default (PII-safe).
- A SignalR hub (`/hubs/dashboard`) plus a background broadcaster push metric and connection-health
  snapshots to the admin dashboard, so it renders live without polling.

## Logging

Structured logging is provided by Serilog: a rolling file sink (directory, interval, size, retention
and format from `Weir:Logging`) and an optional console sink. Each request emits one summary line and
carries a correlation id; security events (failed sign-ins, lockouts, scope/grant denials, rate-limit
hits, settings changes) are logged. A background service prunes audit history per `AuditRetentionDays`.

## Security

- **Clients**: API keys (wk_live_...) with scopes. Only a hash plus a short prefix are stored. Each
  endpoint declares RequiredScopes; the middleware enforces them.
- **Admins**: local accounts (password hashed), first admin bootstrapped from config/env. Session
  is a short-lived JWT, revocable before expiry via a per-account token version (a password change or
  disable invalidates outstanding tokens). Personal access tokens serve scripts / CI-CD. The PWA is
  served from the same origin as the admin API (no CORS). Every admin mutation is audited.

## Hosting and deployment

Weir.Host serves both the JSON API and the Blazor WASM admin PWA (UseBlazorFrameworkFiles() plus
MapFallbackToFile("index.html")) as a single deployable. A multi-stage Dockerfile builds on the
.NET 10 SDK image and runs on aspnet:10.0.
