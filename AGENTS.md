# AGENTS.md - Weir project guide

Weir is a thin, high-performance HTTP gateway over a SQL database (SQL Server first, PostgreSQL also
in-box): a client calls an endpoint, Weir invokes a stored procedure or function and streams JSON back.
No business logic in C# - only routing, auth, parameter mapping, caching, telemetry and serialization.
Target framework: .NET 10.

## Golden rules (must follow)

1. **XML docs on all code.** Every type and member - public, internal and private - carries an XML
   doc comment (`/// <summary>`, plus param/returns/typeparam where relevant).
2. **Keyboard-only text.** All authored text (XML docs, code strings, README, other docs, commit
   messages) uses only characters typeable on a standard keyboard. English text is ASCII; Russian
   docs may use Cyrillic. Banned: em-dash and en-dash, arrows, bullets, the ellipsis glyph, the
   empty-set glyph, the copyright glyph, box-drawing characters, IPA, emoji. Use "-" or "--" for a
   dash, "->" for an arrow, "..." for ellipsis, "(c)" for copyright, plain indentation for trees.
   **One exception: a language name is written in its own language**, so the link to the Russian page
   says "Русский" even in an English document (and the Russian page says "English"). That is what a
   reader looking for their language scans for; "Russian / Russkiy" served nobody. The rule exists to
   keep typographic junk out, not to transliterate a proper noun - do not "fix" these back to ASCII.
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
  audit) in a separate store behind IControlPlaneStore. Three providers ship: SQLite (single node),
  PostgreSQL and SQL Server (both shared / HA-capable).

Projects (src/):
- Weir.Contracts - pure DTOs and enums, browser-safe, shared by host and admin.
- Weir.Abstractions - server ports: IDbConnector, IControlPlaneStore, IWeirCallObserver,
  IMetricsAggregator, IResponseCache, IDataConnectionRegistry, and IWeirPlugin (plugin entry point).
- Weir.Core - engine: EndpointCatalog (literal + templated routes), ParameterBinder (incl. TVP),
  WeirResponseWriter (streaming), DataConnectionRegistry, cache, WeirEngine.
- Weir.Diagnostics - telemetry (ActivitySource "Weir", Meter "Weir", in-memory aggregator).
- Weir.ControlPlane.Sqlite / .PostgreSql / .SqlServer - IControlPlaneStore per provider, each with
  idempotent, checksummed migrations.
- connectors/Weir.Connectors.SqlServer - IDbConnector via Microsoft.Data.SqlClient + Dapper.
- connectors/Weir.Connectors.PostgreSql - IDbConnector via Npgsql (pooled NpgsqlDataSource).
- Weir.Host - ASP.NET Core: dynamic routes, API-key middleware, admin API, health, serves the PWA.
  Three data-plane transports, per-endpoint (EndpointDefinition.Transports, flags): HTTP under /api,
  a WebSocket session at /ws, and gRPC (weir.v1.WeirGateway, Protos/weir.proto). The last two share
  DataPlaneDispatcher - resolve, transport check, scopes/grants, rate limit, engine, error mapping -
  so only framing and the key's carrier vary by door. See docs/en/transports.md.
- Weir.Admin - Blazor WASM PWA admin and dashboard, built on Flare (NuGet Flare.Blazor). Localized
  EN/RU via Resources/AdminStrings.resx.

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
  pinning SQLitePCLRaw.bundle_e_sqlite3 / .core to 3.0.5 in Directory.Packages.props (central
  transitive pinning). Verified at runtime by the SQLite-backed tests. Audit codes NU1901-NU1904
  are still excluded from warnings-as-errors so a future advisory cannot break the build silently.
- An XML comment cannot contain a double dash; do not write "--" inside `<!-- -->` in .props/.csproj.
- ParameterDirection is ambiguous between Weir.Contracts and System.Data - alias per file when both
  namespaces are in scope.
- Enum members named after types (String, Guid, ...) intentionally trip CA1720; it is in NoWarn.
- FlareDataGrid columns must live inside a `<Columns>` render fragment. `<FlareColumn>` placed as a
  bare child of `<FlareDataGrid>` is silently ignored: the grid renders rows with zero cells (empty),
  not a compile error. When adding or copying a grid, always wrap the columns in `<Columns>...</Columns>`.
- nuget.org is reachable, but only past the ai.comss.one proxy: restore with
  `NO_PROXY="localhost,127.0.0.1,::1,.local,nuget.org,api.nuget.org"` (and `no_proxy` likewise) or it
  fails with a 403 from the proxy tunnel. This is how Flare 0.26.x was pulled. A version that the flat
  container already lists can still fail to restore with NU1102: the registration blobs restore reads
  lag behind it, and a failed restore caches "not found" locally for 30 minutes - clear it with
  `dotnet nuget locals http-cache --clear` before concluding a package was never published.
- When measuring drawer geometry in a browser, wait for the slide to finish (poll until
  `element.getAnimations()` is empty). A mid-animation read shows the closed transform and looks
  exactly like a containing-block bug; this cost two false diagnoses. The real one was Flare's, fixed
  in 0.26.1 - see the CHANGELOG entry for 1.6.1.
- gRPC over cleartext does NOT share a port with HTTP/1.1. Kestrel serves an http:// endpoint as
  HTTP/1.1 and answers an h2c attempt with `HTTP_1_1_REQUIRED`; there is no negotiation without TLS.
  Over TLS, ALPN does it and one port carries everything. To test gRPC locally, give it a second
  endpoint: `Kestrel__Endpoints__Grpc__Url=http://localhost:5002` plus
  `Kestrel__Endpoints__Grpc__Protocols=Http2` (and then ASPNETCORE_URLS is ignored, so the main port
  needs `Kestrel__Endpoints__Http__Url` too).
- `Weir:Ports` splits the endpoint API and the admin surface onto separate listeners. Setting either
  port makes Weir bind both listeners itself with `ListenAnyIP`, and a listener configured in code makes
  Kestrel ignore the addresses from ASPNETCORE_URLS - so the surface left without a port of its own
  keeps the port read from those URLs (or `Weir:Ports:MainPort`). The split is enforced by
  `PortRouting.UseWeirPortRouting`, which classifies the path (/health shared, /api + /ws + gRPC data
  plane, everything else admin) and answers a bodiless 404 on the wrong port; it matches
  `HttpContext.Connection.LocalPort`, never the `Host` header a client supplies. Ports are read at
  startup only (no runtime setting, nothing in the admin panel), and the two decisions
  (`PortRouting.Classify`, `PortSplit.IsAllowed`) are pure because `TestServer` binds no real port.
- The Command Center theme reuses Flare.Theme.VisualStudio's stylesheets under its own id, and since
  Flare 0.34 those sheets are `@scope (.flare-theme-visualstudio)`. Since Flare 0.42 `CommandCenterTheme.Base`
  names the theme it is built on (an `ITheme`; the old string `StyleFamilyId` is gone), and the root
  carries one `flare-theme-{id}` class per generation of that chain. Drop the base and the tab strip (and
  any later VS rule) silently loses its styling - no build or test failure, only a look at the page shows it.
- A `FlareMenu` activator is a typed slot (Flare 0.39+): `aria-haspopup`, `aria-expanded` and
  `aria-controls` live in `context.Attributes` and have to be spread onto the element that takes focus
  (`<FlareButton ... @attributes="context.Attributes">`). An activator that does not spread them compiles
  and clicks but announces nothing - and a slot nested in another templated component (Weir's account
  menu sits inside `AuthorizeView`) has to name its own context (`Context="menu"`) or the Razor compiler
  reports RZ9999.
- SqlClient's `NextResultAsync` blocks the calling thread for a server pause between two result sets
  and returns an already-completed task, but only when the current result set still has unread rows
  (measured on 7.0.3 and 7.1.0; the probe and the report are in
  `.claude/issues/sqlclient-nextresultasync-sync-wait.md`). `ReadAsync` across the same pause is
  properly async, and so is the boundary after a drained set - there the pause is spent in the await of
  one of the reads. Either way "is this read pending?" cannot detect a wait before a result-set
  boundary, which is why WeirResponseWriter flushes unconditionally there on the streaming path.
- When timing responses from Windows, use `127.0.0.1`, not `localhost`: a host listening on `0.0.0.0`
  is IPv4-only and the client's `::1` attempt costs about two seconds on the first request, which reads
  exactly like a slow time-to-first-byte. nginx does not change streaming by default - see
  docs/en/deployment.md "Streaming through the proxy" for what was measured and what does.- When sampling a Blazor-rendered element from the browser console, re-query it each tick. Blazor can
  replace the element on re-render, so a captured reference goes stale and silently reports "nothing
  ever changed" - which is indistinguishable from the bug you are trying to measure.

## Docs layout

- README.md / README.ru.md at root.
- docs/en/*.md and docs/ru/*.md for prose docs.
- docs/adr/*.md for Architecture Decision Records (English, ASCII).
- If Flare lacks a needed capability, file an issue under C:\Job\Projects\FrigaT\Flare\.Codex\issues
  (Flare moved its issue documents out of docs for the same reason Weir did - see below).

## Issues and backlog

Issue documents and the backlog live in `.Codex/`, never in `docs/`, exactly as they do in Flare:

- `.Codex/issues/*.md` - issue documents, audits and reports (Russian). One document per subject.
- `.Codex/backlog/backlog.json` - the triage data; `.Codex/backlog/weir-backlog.html` - its report.
- `.Codex/skills/backlog-triage/` - the `/backlog-triage` skill that scores the issues and renders
  the report. `render.mjs` owns the ranking formula and the validation, `template.html` owns every
  pixel, and a triage run changes only the data. Both files are kept in step with Flare's copy of the
  skill; the only intended difference is the `weir` score field, which is `flare` there.

`.gitignore` covers `.Codex/` wholesale, so none of this is committed: it is working material on this
machine, not project history. Consequences to keep in mind - a clone of the repository has no backlog,
there is no backup of one, and there is no diff between triage runs, so the previous ranking has to be
carried forward from `backlog.json` itself. What ships with the product stays in `docs/`.

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
      PostgreSQL data-plane connector (Weir.Connectors.PostgreSql), plus a SQL Server control-plane
      provider (Weir.ControlPlane.SqlServer, Provider=SqlServer; HA-capable like Postgres).
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
      (The live audit tail landed later: the hub also carries audit entries as they are stored.)
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

Released since Phase 15 (the roadmap above is the pre-1.0 plan; these shipped after it):

- v1.1.0: route templates actually resolve (`orders/{id}` used to 404 - the catalog did an exact
      dictionary lookup - and ParameterSource.Route had nothing feeding it); admin localized EN/RU
      (AdminStrings.resx + LanguageService + LocalizedPage, picker in the app bar and on login);
      response cache bounded (ResponseCacheMaxBytes, private bounded MemoryCache, LRU eviction);
      concurrent cache fills coalesced and the cache store taken off the response path
      (IResponseCache.SetAsync now takes a built CachedResponse - breaking for third-party caches);
      request-log deep links + FlareMeter timing bar; Flare 0.4.0; hot-path work (lock-free metric
      rings, cached validation regexes, TVP token gated, pre-encoded column names).
- v1.2.0: a cache fill outlives the client that started it (refcounted waiters; the query is detached
      from its starter's request, so a gateway timeout still reaches that client) and CachePolicy
      .CoalesceRequests opts an endpoint out; buffered responses sized from the endpoint's last one.
- v1.6.0: an endpoint can name a TABLE or VIEW instead of something callable. EndpointDefinition
      .Operation (Invoke | Dictionary | Import) picks what to do with it; DictionaryPolicy and
      ImportPolicy describe the read and the write. The statement is composed in Weir.Abstractions
      (TableSql + a per-connector SqlDialect; TableImport runs the batches), so both connectors share
      the composition and a third-party one gets it free. Identifiers come from endpoint metadata and
      are quoted; every caller value is a bound parameter; a sort column must be one the endpoint
      already declares. IDbConnector gained ListTablesAsync / DescribeColumnsAsync with
      return-nothing defaults, so existing connectors still compile. Control-plane migration adds
      Operation + DictionaryJson + ImportJson (SQLite v15, Postgres v14, SQL Server v14).
      New package FrigaT.Weir.Client (src/client) - the typed client callers were hand-rolling.
      Flare 0.26.0.
- v1.7.0: Flare 0.29.0 (no admin edit needed - all three breaking changes miss Weir). Two TVP binding
      defects found in a real application: an empty or omitted table was sent as DBNull, which SqlClient
      rejects, and row keys were matched to column names case-sensitively, so a camelCase client bound
      NULL into every cell. Concurrent 401s no longer sign the admin out (the refresh exchange is
      serialized, and a request whose token was already renewed replays with it). SettingsBounds in
      Weir.Contracts - one range table the admin form constrains its inputs with and the admin API
      validates against. TouchThrottleCache in Weir.Abstractions - the per-key last-used throttle now
      prunes instead of growing forever, shared by all three stores. OpenAPI describes an endpoint's
      output parameters (allOf over ResponseEnvelope). OUTPUT-parameter binding and the envelope's
      output/returnValue are covered by tests at last - the last open Critical from the audit.
- v1.8.0: connection pooling is configurable per data connection (Weir:DataConnections:{name}:Pool -
      Enabled / MinSize / MaxSize / AcquireTimeoutSeconds), stated once in Weir's terms and translated by
      each connector onto its own driver; sizes with pooling off are refused at startup. Note both drivers
      ALREADY pooled - this makes it sayable, and Enabled:false is the genuinely new mode. Watch the
      interaction: MaxConcurrentRequestsPerConnection is a fail-fast bulkhead in front of the pool, so it
      turns "wait for a connection" into 503. Also: Weir:Admin:PasswordIterations (the decoy hash follows
      it, or raising the setting reopens the username-timing channel); the endpoint test drawer lists an
      endpoint's output parameters; TimeRing.WindowHistogram fills a caller span (stack-allocated, five
      per snapshot per route); Logs and Audit report a failed load instead of blanking the page.
- v1.9.0: an endpoint names its transports (EndpointTransports flags, default HTTP; control-plane
      migration SQLite v16 / Postgres v15 / SQL Server v15). WebSocket at /ws and gRPC
      (weir.v1.WeirGateway) join HTTP, sharing DataPlaneDispatcher so only framing and the key's carrier
      vary by door - remember gRPC needs its own cleartext port (see gotchas). Flare 0.32.0, and every
      admin grid now states Height="auto" PageSize="50". Metrics time series are clock-aligned and
      whole-bucket only (ring capacity 315s; percentile window pinned to 300s), the dashboard hub pushes
      on change with a 5s keepalive instead of a 2s beat, the Overview tab is now Dashboard, and the app
      bar is 43px. FlareChart.AnimateUpdates is set but is a no-op - Flare rebuilds the chart's SVG nodes
      instead of patching attributes, so its MutationObserver never sees the change (filed in
      Flare/.Codex/issues/chart-animate-updates-never-runs.md); the sparklines' 5-second buckets are the
      workaround that makes the step small enough to read as movement.

The buffered path's response stream is pooled: `WeirEngine` fills the buffered body and each cache fill
from one `RecyclableMemoryStreamManager` (`Microsoft.IO.RecyclableMemoryStream`), so a response past one
128 KB block no longer allocates a fresh large-object-heap buffer per request. The manager's free pools
are bounded explicitly (16 MB of blocks, 32 MB of large buffers) because the library's default keeps
every returned buffer without limit. The body is still handed to the client in a single write: the
handover reads the pooled stream through `GetBuffer()`. This used to be filed as "response buffers are
not pooled", which pointed at the wrong buffer: the dominant unpooled LOH allocator was Utf8JsonWriter's
own internal one, because the writer only flushed after the last row and so grew to the size of the
whole envelope. The row loop now flushes at 32 KB, which caps that one.

<!-- aiko:begin -->
When Aiko MCP is available, use it as the durable project workflow and task memory. Any work the user
asks for starts with a card: create it in Aiko first (aiko_create_card, or /aiko-create), and stop
there - the card is the answer to the request. An order is still a request: "поправь ...", "исправь ..."
and "нужно ..." earn a card and nothing more, and the work begins only when the user asks for it
(aiko-run <cardId>, or "выполни <cardId>"). A card in its backlog stage has no work in it yet, so never
create or change files for a
card before you have started the stage you are working in with aiko_start_stage - the start moves the
card into that stage and is what records the work. A card may wait for another one: aiko_get_card lists
the cards that block it, and aiko_start_stage refuses a blocked card, naming the blocker. When that
happens, do not work this card - say so to the user and offer the blocking card instead. Do what the
stage's instruction asks for, produce
its required artifacts and complete it with aiko_complete_stage; only then does the card move on, and
aiko_move_card refuses to advance a card whose stage is not finished. Before you complete a stage,
re-estimate the card with aiko_estimate_card: the readiness criterion is what says the work is done,
and it must describe the card as it is after your change - a stage is not completed with a score
nobody refreshed. The card's feed is the notebook between stages: read it with aiko_list_comments before
you work a stage, and post the outcome with aiko_add_comment - signed with your adapter id - before you
complete it, so what one stage learned is not lost on the next. One run is one stage: work the next
stage only when the user asks again, and
/aiko-run <cardId> --all is the explicit exception - it
walks the pipeline and descends into the card's children, creating the ones a container's stage calls
for and working each child before going back to its parent, and even then it stops when a stage asks the
user a question, when an agent fails or hits its limit, when a stage forbids an action, or when a
required artifact cannot be produced. Read the
project context before touching files. Warn before modifying files outside the card scopeFiles and
record the actual changed files. A project can be linked to other projects: aiko_list_links lists them
with what each one is for, where its reference lives and when work belongs there. Before you reach for
something a neighbour owns - a library, a component, a pattern - read the reference its entry names and
search this project's memory first, rather than inferring it from a package installed here: the
reference is recorded so that no agent has to guess where the neighbour's documentation lives. When a
request belongs to a linked project, file it there with
aiko_create_card_in_project and pass originProjectId and originCardId so the receiving card remembers
where it came from - and read that project's context first, because its card types, its stages and its
rules are its own.
<!-- aiko:end -->
