# Weir - Development Roadmap

> [Русский](../ru/roadmap.md) - [Architecture](architecture.md) - [Configuration](configuration.md) - [Security](security.md)

This document captures a full code review of Weir (all planes) and the phased plan that follows
from it. Phases 0-8 are complete (see the CLAUDE.md checklist). Phases 9-15 below are the forward
plan. A cross-cutting principle applies to every phase: any new system setting is also exposed and,
where safe, editable in the admin panel, not only in `appsettings.json`.

## Status

Delivered: Phase 9 (critical correctness and security fixes), Phase 9.5 (runtime settings subsystem +
admin Settings screen), Phase 10 (Serilog file logging with retention + request/security logging +
audit pruning), Phase 11 (SignalR real-time dashboard), Phase 12 (installable, update-aware PWA),
Phase 13 (data-plane capabilities and performance), Phase 14 (auth and session maturity), and Phase 15
(HA, scale and tests). Phases 9-15 are complete; the former backlog below is retained as a record of
what each phase delivered.

Backlog (all delivered - retained as a record of the work per plane):

- Data plane: complete. Delivered ETag / `Cache-Control` / 304 (the engine returns response metadata and
  the endpoint answers conditional GETs with a body-less 304 before streaming), a per-endpoint toggle to
  suppress SQL `PRINT` / notice messages, typed reader getters on the streaming hot path (removing the
  boxing `GetValue` for known column types), a pooled `NpgsqlDataSource` in the PostgreSQL connector, a
  per-connection circuit breaker plus a concurrency bulkhead (both runtime settings), and keyset (seek)
  pagination for the audit log.
- Auth / session: complete. Delivered a persisted login throttle (the sign-in lockout lives in the
  control plane, so it survives a restart and is shared across instances), refresh tokens with a shorter
  access-token lifetime (short access tokens plus long-lived, revocable, rotating refresh tokens, with
  transparent renewal in the admin PWA), and a distributed rate-limiter option (Redis, shared across
  instances, fail-open when the backend is briefly unavailable).
- HA / observability / tests: complete. Delivered windowed overview percentiles (the latency histogram
  decays over a trailing window so p95 no longer pins to a lifetime spike), DB-error classification (each
  connector maps its driver errors to timeout / deadlock / constraint / connection, emitted as the
  `weir.db.errors` metric and a span tag, with the Npgsql pool meter also exported), migration checksums
  (each applied migration's SHA-256 is recorded and re-verified on start, so an edited shipped migration
  fails fast), a control-plane backup / export endpoint (a secret-free JSON snapshot of the configuration),
  a documented cross-instance metrics story (the OTLP backend is the source of truth, the per-instance
  dashboard is a convenience), PostgreSQL-for-HA enforcement (`Weir:HighAvailability` refuses to start on
  the single-node SQLite control plane), and connector-execution + data-plane end-to-end tests
  (Testcontainers PostgreSQL, Docker-gated) plus a PWA smoke test.

## Review summary

Weir is a mature, clean-building (.NET 10, zero warnings) metadata-driven HTTP-to-database gateway.
Strengths confirmed in review: a genuinely streaming JSON hot path (DbDataReader -> Utf8JsonWriter,
no ORM, no whole-result buffering on the uncached path); disciplined SQL parameterization with quoted
identifiers everywhere; strong crypto (PBKDF2-HMAC-SHA256 100k iterations, 32-byte API keys from a CSPRNG,
timing-safe comparisons); complete JWT validation; login enumeration and timing defenses; a careful,
collision-resistant cache key; and observer failures isolated from the request path.

The review found a set of correctness and security defects, missing production limits, observability
gaps, and admin/PWA gaps. They are grouped by severity below and mapped to phases.

### A. Critical correctness and security defects

- A1. Truncated responses (row cap hit) are cached for the full TTL; the `truncated` flag never leaves
  `WeirResponseWriter`, so later callers silently receive partial data. (`WeirEngine.cs`)
- A2. `VaryByParameters` naming a TVP or output parameter keys the cache on `null` (only scalar inputs
  reach the values map), so different inputs collide on one cache entry -- a cross-caller data-disclosure
  shape. (`ParameterBinder.cs`, `CacheKey.cs`)
- A3. A gateway timeout that fires after streaming has started is not caught; the OperationCanceledException
  propagates, the socket resets mid-JSON, and the telemetry status (499) disagrees with the wire. (`DataPlaneEndpoints.cs`)
- A4. SQLite migrations are not transactional and `ALTER TABLE ADD COLUMN` lacks an existence guard; a
  crash between the ALTER and the `user_version` bump leaves the store unable to start. (`SqliteControlPlaneStore.cs`, `SqliteSchema.cs`)
- A5. AdminApi introspection, connection-health and sync routes return raw `ex.Message`, leaking server,
  database and login names -- unlike the sanitized data plane. (`AdminApi.cs`)
- A6. `connections/health` and `introspect/*` are reachable by the read-only Viewer role and open real
  database connections (reconnaissance plus a DoS lever). (`AdminApi.cs`)
- A7. The shipped default connection string sets `TrustServerCertificate=True`; there is no options
  validation on startup beyond the JWT key. (`appsettings.json`, `Program.cs`)

### B. Missing production limits and safe defaults

- `MaxRows = 0` (unlimited) and `RequestTimeoutSeconds = 0` (no gateway timeout) by default.
- No request body-size cap, no TVP row cap, no per-connection concurrency limit.
- Rate limiting applies only when a key sets a limit; the limiter dictionary is never evicted and is
  per-instance (in HA the effective limit is N x limit).

### C. Observability and audit gaps

- Only the stock console logger: no file sink, rotation, retention, configurable directory, or JSON output.
- The audit table is never pruned (unbounded growth); data-plane audit entries are dropped silently under
  load with no counter.
- Auth and rate-limit rejections (401/403/429) are invisible to the aggregator and to OpenTelemetry.
- No DB error classification (timeout/deadlock/constraint) and no connection-pool telemetry.
- Admin mutations (endpoints/scopes/admins/invoke) are not audited.
- Overview percentiles are lifetime, not windowed, so p95 never decays.

### D. Real-time admin

- The dashboard polls four HTTP endpoints every three seconds; connection health is never refreshed after
  first load. `IWeirCallObserver` is the natural push hook.

### E. PWA

- Not installable: the web manifest declares no icons and none exist. No update-on-reload UX. Silent
  mutation failures, no toast/ErrorBoundary, no offline indicator, token in localStorage without a real CSP.

### F. Data-plane capabilities

- No ETag/Cache-Control/304, no response compression, no pagination, hot-path boxing via `GetValue`,
  double-buffering on the cache-fill path, per-call `NpgsqlConnection` instead of a pooled data source,
  no circuit breaker or bulkhead.

## Phased plan

### Phase 9 - Critical fixes (A + B)

Close A1-A7 and set safe defaults and hard limits: non-zero default `MaxRows` and `RequestTimeoutSeconds`,
an explicit request body-size cap and TVP row cap, SQLite `busy_timeout` and `foreign_keys` on every
connection with AdminToken FK parity across providers, rate-limiter eviction and a default per-key limit,
`ValidateOnStart` for the security-relevant options, and removal of the insecure connection-string default.

### Phase 9.5 - Runtime settings subsystem (cross-cutting)

A control-plane-backed settings store (SQLite + Postgres) holding a `WeirRuntimeSettings` document, read
through an options snapshot with `appsettings.json` as the seed/default. An admin API
(`GET`/`PUT /admin/api/settings`, AdminOnly, audited) and a Flare Settings page. Runtime-tunable values
(data-plane limits, audit retention, log level, rate-limit defaults) apply without a restart; values that
require a restart (for example the log directory) are surfaced read-only with a clear "restart required"
marker. This is the foundation for "every setting is also in the admin panel."

### Phase 10 - Logging and observability (C)

Serilog with a file sink whose directory, rolling interval, size limit, retention
(`RetainedFileCountLimit` / time limit), format (compact JSON or plain text) and minimum level are all
configurable and surfaced in admin Settings. Request logging with a correlation id. Security-event logging
(failed auth, lockout, forbidden scope, admin config change). Audit retention pruning plus a dropped-audit
counter. New metrics for auth and rate-limit rejections, DB error classification, and connection-pool
saturation. Windowed overview percentiles.

### Phase 11 - Real-time admin (D)

An ASP.NET Core SignalR hub secured with admin auth. An `IWeirCallObserver` implementation broadcasts
metrics and health snapshots to connected admins. A Blazor WASM `HubConnection` client with auto-reconnect
and a graceful fallback to the existing polling. The dashboard subscribes instead of polling; a live audit
tail and pushed connection-health transitions follow.

### Phase 12 - PWA (E)

App icons (192, 512, maskable, apple-touch) and a populated manifest. An update-on-reload toast wired into
the service-worker registration. An offline/connection indicator. A toast provider and an `ErrorBoundary`
plus consistent mutation error handling that distinguishes load-failed from still-loading. A proactive
session-expiry timer and a graceful expiry experience. A dashboard heading for `FocusOnNavigate`, empty
states, and consistent pagination and filtering.

### Phase 13 - Data-plane capabilities and performance (F)

ETag / Cache-Control / 304 for cache-eligible reads, response compression, keyset pagination, typed reader
getters to remove hot-path boxing, elimination of the cache-fill double copy, a pooled `NpgsqlDataSource`,
a circuit breaker and bulkhead per connection, a per-connection concurrency limit, and a per-endpoint
toggle to suppress SQL messages.

### Phase 14 - Auth and session maturity

JWT revocation via a `token_version` claim checked against the admin record so disable/demote/password-change
take effect immediately; refresh tokens or a shorter access-token lifetime; mandatory token expiry and a
per-owner token cap; a complete admin audit trail across endpoint, scope, admin, invoke, import and sync
routes; a persisted login throttle; and a distributed rate-limiter option with a default limit for keys
that specify none.

### Phase 15 - HA, scale and tests

A decided cross-instance metrics story (aggregate across instances, or document an external time-series
backend as the source of truth) with windowed percentiles; a control-plane backup/export path; migration
checksums; connector execution tests and data-plane end-to-end tests; a PWA smoke test; and enforcement or
clear documentation that HA deployments use the PostgreSQL control plane.
