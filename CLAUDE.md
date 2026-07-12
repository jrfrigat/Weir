# CLAUDE.md - Weir project guide

Weir is a thin, high-performance HTTP gateway over MSSQL: a client calls an endpoint, Weir invokes
a stored procedure or function and streams JSON back. No business logic in C# - only routing, auth,
parameter mapping, caching, telemetry and serialization. Target framework: .NET 10.

## Golden rules (must follow)

1. **XML docs on all code.** Every type and member - public, internal and private - carries an XML
   doc comment (`/// <summary>`, plus param/returns/typeparam where relevant).
2. **Keyboard-only text.** All authored text (XML docs, code strings, README, other docs, commit
   messages) uses only characters typeable on a standard keyboard. English text is ASCII; Russian
   docs may use Cyrillic. Banned: em-dash and en-dash, arrows, bullets, the ellipsis glyph, the
   empty-set glyph, the copyright glyph, box-drawing characters, IPA, emoji. Use "-" or "--" for a
   dash, "->" for an arrow, "..." for ellipsis, "(c)" for copyright, plain indentation for trees.
3. **Bilingual README and docs.** Every README/prose doc exists in English and Russian, like Flare:
   README.md + README.ru.md, and docs split into docs/en and docs/ru. Keep both in sync. (This does
   not apply to XML doc comments, which are English only, nor to this file.)
4. **Commit style.** No `Co-Authored-By` trailer. Do not put test-pass counts, coverage or build
   stats in commit messages. Describe what changed and why.

## Build and test

- `dotnet build` - build the whole solution (Weir.slnx, the modern XML solution format).
- `dotnet test` - run tests.
- Central Package Management: every third-party version lives in Directory.Packages.props; project
  files carry `<PackageReference Include="..."/>` with no Version.
- Common defaults are in Directory.Build.props: net10.0, Nullable, ImplicitUsings,
  TreatWarningsAsErrors, latest-recommended analyzers, GenerateDocumentationFile.

## Architecture (two planes)

- **Data plane** (hot path): auth -> bind params -> optional cache -> execute SP via a connector ->
  stream JSON from DbDataReader. Metadata-driven; endpoints resolved from an in-memory snapshot.
- **Control plane**: Weir's own metadata (endpoints, API keys as hashes, scopes, admin users,
  audit) in a separate store behind IControlPlaneStore. SQLite today; provider-abstracted.

Projects (src/):
- Weir.Contracts - pure DTOs and enums, browser-safe, shared by host and admin.
- Weir.Abstractions - server ports: IDbConnector, IControlPlaneStore, IWeirCallObserver,
  IMetricsAggregator, IResponseCache, IDataConnectionRegistry, and IWeirPlugin (plugin entry point).
- Weir.Core - engine: EndpointCatalog, ParameterBinder (incl. TVP), WeirResponseWriter (streaming),
  DataConnectionRegistry, cache, WeirEngine.
- Weir.Diagnostics - telemetry (ActivitySource "Weir", Meter "Weir", in-memory aggregator).
- Weir.ControlPlane.Sqlite - IControlPlaneStore over SQLite + idempotent migrations.
- connectors/Weir.Connectors.SqlServer - IDbConnector via Microsoft.Data.SqlClient + Dapper.
- Weir.Host - ASP.NET Core: dynamic routes, API-key middleware, admin API, health, serves the PWA.
- Weir.Admin - Blazor WASM PWA admin and dashboard, built on Flare (NuGet Flare.Blazor).

Dependency direction: everything depends only on Weir.Contracts / Weir.Abstractions. Concrete
drivers and stores implement the ports; Weir.Host composes them via DI (AddWeirSqlServer(),
AddWeirControlPlaneSqlite(), AddWeirCore()). The ports are also the plugin surface: third-party
connectors implement IDbConnector and are either compiled into a custom host or loaded at runtime
from Weir:Plugins:Paths via IWeirPlugin (see docs/en/extending.md; sample in
samples/connectors/Weir.Connectors.MySql).

## API contract

- Request: flat JSON body equals the SP parameters; TVPs are arrays of objects. Caching is set
  server-side per endpoint (TtlSeconds + VaryByParameters); clients cannot bypass it.
- Response: one consistent envelope - "data" is an array of result sets (array of arrays), plus
  "output", "returnValue", "rowsAffected", "truncated" (row-cap hit), "messages" (SQL PRINT / info).
- Errors: RFC 7807 application/problem+json.

## Coding conventions

- File-scoped namespaces; `using` outside the namespace; private fields `_camelCase`.
- Prefer IReadOnlyList / IReadOnlyDictionary on public surfaces; collection expressions `[]`.
- Data-plane hot path must stay allocation-light: stream reader -> Utf8JsonWriter, no ORM.
- Never log parameter values by default (PII-safe). Telemetry carries metadata only.
- Coerce request values to CLR types in Weir.Core; connectors receive ready values.

## Known gotchas

- NU1903 (vulnerable transitive SQLitePCLRaw 2.1.11 from Microsoft.Data.Sqlite) is resolved by
  pinning SQLitePCLRaw.bundle_e_sqlite3 / .core to 3.0.3 in Directory.Packages.props (central
  transitive pinning). Verified at runtime by the SQLite-backed tests. Audit codes NU1901-NU1904
  are still excluded from warnings-as-errors so a future advisory cannot break the build silently.
- An XML comment cannot contain a double dash; do not write "--" inside `<!-- -->` in .props/.csproj.
- ParameterDirection is ambiguous between Weir.Contracts and System.Data - alias per file when both
  namespaces are in scope.
- Enum members named after types (String, Guid, ...) intentionally trip CA1720; it is in NoWarn.
- FlareDataGrid columns must live inside a `<Columns>` render fragment. `<FlareColumn>` placed as a
  bare child of `<FlareDataGrid>` is silently ignored: the grid renders rows with zero cells (empty),
  not a compile error. When adding or copying a grid, always wrap the columns in `<Columns>...</Columns>`.

## Docs layout

- README.md / README.ru.md at root.
- docs/en/*.md and docs/ru/*.md for prose docs.
- docs/adr/*.md for Architecture Decision Records (English, ASCII).
- If Flare lacks a needed capability, file an issue under C:\Job\Projects\FrigaT\Flare\docs\issues.

## Roadmap (update as phases land)

- [x] Phase 0-1: solution scaffold, build infra, Weir.Contracts, Weir.Abstractions.
- [x] Phase 2: Weir.ControlPlane.Sqlite.
- [x] Phase 3: Weir.Connectors.SqlServer + Weir.Core.
- [x] Phase 4: Weir.Diagnostics (telemetry).
- [x] Phase 5: Weir.Host (data plane, API-key auth, admin API + JWT, health, bootstrap admin).
      Follow-ons folded into later phases: serve the PWA, OTLP export wiring.
- [x] Phase 6: Weir.Admin (Blazor WASM PWA on Flare, Command Center + Visual Studio 2026 themes) -
      login/auth, dashboard with live metrics (Flare data grid plus FlareChart sparklines), and CRUD
      for endpoints, keys, scopes, admins, audit. Fully Flare - no bespoke component CSS or SVG.
- [x] Phase 7: additional providers - PostgreSQL control plane (Weir.ControlPlane.PostgreSql) and a
      PostgreSQL data-plane connector (Weir.Connectors.PostgreSql). A SQL Server control-plane
      provider is still open.
- [x] Phase 8: xUnit tests (tests/Weir.Tests), multi-stage Dockerfile (build/Dockerfile), GitHub
      Actions CI (.github/workflows/ci.yml), and the NU1903 dependency fix. Container runtime-verified.

The full review and the forward plan (Phases 9-15) live in docs/en/roadmap.md and docs/ru/roadmap.md.
Cross-cutting principle from Phase 9.5 on: every new system setting is also surfaced (and, where safe,
editable) in the admin panel, not only in appsettings.json.

- [x] Phase 9: critical fixes - do-not-cache truncated; VaryByParameters TVP/output cache-key disclosure
      (TVP content token + refuse colliding key); introspect + connection-health error redaction /
      AdminOnly gating; safe data-plane defaults and hard limits (MaxRows, RequestTimeoutSeconds, MaxTvpRows,
      MaxRequestBodyBytes); SQLite transactional idempotent migrations + busy_timeout/foreign_keys;
      rate-limiter eviction + default limit; ValidateOnStart. (Post-stream cancellation was already handled
      by the HasStarted abort path - verified, no change.)
- [x] Phase 9.5: runtime settings subsystem - control-plane single-row Settings table (SQLite+Postgres),
      IRuntimeSettings seeded from appsettings and read live by engine/binder/rate-limiter/timeout,
      GET/PUT /admin/api/settings (AdminOnly, audited) + Flare Settings page. Verified end-to-end.
- [x] Phase 10: Serilog file logging (directory, rolling, retention, format, level via Weir:Logging;
      surfaced read-only in admin Settings); request logging + correlation id; security-event logging
      (login fail/lockout, scope/grant denial, rate-limit, settings change); audit retention pruning
      (runtime AuditRetentionDays) + dropped-audit counter. Remaining (deferred to observability
      follow-up): windowed overview percentiles, DB-error classification, connection-pool metrics.
- [x] Phase 11: SignalR real-time admin - secured /hubs/dashboard hub + background broadcaster (metrics
      every 2s, health every 15s, skipped when no clients), Blazor HubConnection client with auto-reconnect
      and polling fallback, JWT access_token query param for the WS handshake. Verified negotiate + auth.
      (Live audit tail deferred to a follow-up.)
- [x] Phase 12: PWA - app icons (192/512/maskable/apple-touch) + populated manifest (installable),
      update-on-reload banner + service-worker skipWaiting/clients.claim, offline banner, ErrorBoundary
      (recovers on nav), dashboard h1, Admins empty state. Remaining polish (deferred): toast provider +
      per-page mutation-error consistency, proactive session-expiry timer, consistent grid pagination.
- [x] Phase 13: data-plane - response compression; ETag/Cache-Control/304 (engine returns response
      metadata, endpoint answers conditional GETs before streaming); per-endpoint suppress-SQL-messages
      toggle (endpoint model + control-plane column + WeirResponseWriter); typed reader getters on the row
      hot path (per-column kind resolved once per result set, boxed GetValue only as a fallback); pooled
      NpgsqlDataSource (cached per connection string) in the PostgreSQL connector; per-connection circuit
      breaker + concurrency bulkhead (runtime settings, HTTP 503 on trip/saturation); keyset (seek)
      pagination for the audit log (AfterId cursor).
- [x] Phase 14: auth/session maturity - full admin audit trail + security-event logging; JWT
      revocation via token_version (bumped on password change; re-checked per request); per-admin token
      cap + optional mandatory token expiry; persisted login throttle (control-plane LoginThrottle table,
      survives restart + shared across instances); refresh tokens + shorter access TTL (30m access +
      revocable, rotating refresh tokens in AdminRefreshTokens; /auth/refresh + /auth/logout; transparent
      client refresh); distributed rate limiter option (Redis, Weir:RateLimit:RedisConnectionString,
      fail-open, shared across instances).
- [x] Phase 15: HA/scale + tests - windowed overview percentiles (decaying histogram in the per-second
      ring); DB-error classification (per-connector timeout/deadlock/constraint/connection -> weir.db.errors
      metric + span tag) + connection-pool telemetry (Npgsql meter subscribed); migration checksums
      (SchemaMigrations table, verify-or-backfill on start, fail fast on drift); control-plane backup/export
      (AdminOnly audited GET /admin/api/export + Settings download; secret-free JSON snapshot); cross-instance
      metrics story (documented: OTLP backend is source of truth, per-instance dashboard is a convenience) +
      Postgres-for-HA enforcement (Weir:HighAvailability refuses SQLite control plane); connector-execution +
      data-plane e2e tests (Testcontainers Postgres, Docker-gated) and a PWA smoke test.
