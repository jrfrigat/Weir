# Changelog

> [Russian / Russkiy](CHANGELOG.ru.md)

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.2.0] - 2026-07-16

### Added

- **Per-endpoint control over coalescing.** `CachePolicy.CoalesceRequests` decides whether callers that
  arrive while a response for the same cache key is already being produced wait for it, or each run the
  procedure for themselves. On by default, so nothing changes unless it is turned off, and an endpoint
  stored before this release picks the default up on its own. It only means anything while caching is
  on: the cache key is what identifies two calls as asking the same question, so without one there is
  nothing to wait on. Editable in the endpoint editor's **Caching** section.

### Fixed

- **A cache fill is no longer cancelled by the client that happened to start it.** A client hanging up
  used to kill the query it had started, and every caller queued behind it went off and re-ran the same
  thing - the work was thrown away exactly when it was nearly done. A fill now counts who is waiting and
  runs its query on its own lifetime: one caller leaving is just that caller leaving, and the query is
  dropped only once the last of them has gone, so it lives exactly as long as somebody still wants the
  answer.
- **A cache-filling query no longer holds its starter's request open.** The query is now detached from
  the request that starts it. Previously the starter awaited it inline, so its own request could not
  return until the query finished - a client that hit the gateway timeout would have waited out the whole
  query instead of getting its `504`. Every caller, including the starter, now waits on the fill with its
  own token.
- Because a fill is only cancelled once nobody is left waiting, a failure a waiter can still see is one
  the database really produced. It is now shared with everyone waiting rather than making each of them
  retry a query that is going to fail again.

### Changed

- **A buffered response starts at the size the endpoint's last one came to.** A cached or captured body
  was assembled in a `MemoryStream` that started empty and doubled as rows arrived, so a large response
  was copied through every intermediate size and each intermediate over ~85 KB landed on the large object
  heap. An endpoint's responses are usually about the same size, so the previous one is a good guess. It
  stays a hint - the stream still grows if the guess is low, and the buffer is right-sized before it is
  cached - and it does not bother below 4 KB.

## [1.1.0] - 2026-07-16

### Added

- **The admin console speaks English and Russian.** A resx catalogue behind a generated `AdminStrings`
  class, a `LanguageService` that resolves the language and stores the choice, and a `LocalizedPage` base
  that re-renders on a switch. The picker sits in the top bar and on the sign-in card, since signing in
  happens before there is a session. The language is a per-browser preference kept in `localStorage` and
  applied before the first paint, so it survives a reload and an offline start; with nothing stored it
  follows the browser and falls back to English. Switching is instant and never touches the control
  plane, so two admins on one deployment can each read the console in their own language. Number and date
  formatting follows the language too.
- **The logs screen links to the endpoint an entry came from.** A request-log entry that resolved to a
  known endpoint offers **Test endpoint** and **Edit endpoint**, which open the test console or the
  editor directly. Parameters and result gain copy and view controls, with the full JSON in a dialog.
- **The timing breakdown moved into the logs drawer**, where the rest of a call's detail lives. It had
  been on the dashboard overview, where it swallowed the row click; clicking a row now opens the logs
  filtered to that endpoint. The logs grid gains per-column filters.

### Fixed

- **Route templates never worked.** A route with a capture - `orders/{id}` - was registered, listed in
  the admin and described in the generated OpenAPI document, but the catalog resolved routes with an
  exact dictionary lookup, so `GET /api/orders/123` returned `404`. `ParameterSource.Route` had nothing
  feeding it either, so even a match would have bound nothing. Routes are now matched segment by segment:
  a literal route wins over a template that would also match, and between two templates the one with more
  literal segments wins. Two routes no request could tell apart are reported as a collision at startup.
- **Output parameters with no configured `Size` got a zero-byte driver buffer**, which truncated
  `nvarchar(max)` and similar max-capable output values. They now default to the max-size buffer.
- **A control plane migrated on Windows refused to start in a Linux container.** Migration checksums
  hashed the script's raw bytes, so CRLF and LF produced different values for the same script. Checksums
  are now computed over the LF form, and an older CRLF-era record is quietly rewritten to it.
- **Instances sharing a control database did not serialize their migrations.** The PostgreSQL advisory
  lock key was derived from `string.GetHashCode`, which is randomized per process, so every instance
  computed a different key. SQL Server's `sp_getapplock` also treated return code 1 - granted after
  waiting - as failure, aborting a waiting instance during a rolling deploy. SQL Server migrations now
  run inside a transaction, matching PostgreSQL.
- **The per-phase timing breakdown counted streaming twice**: the database phase spanned the whole
  execute-and-stream block while the streaming phase measured part of the same span.
- **Switching the admin's language orphaned its grid state.** Flare keys a column's filter and sort by
  its title unless given an id, and no grid set one - so with localized titles, filtering a grid and then
  switching language left it filtered while the filter box read empty. Every column now carries a stable
  id.
- A batch of audit findings: races in the SQL Server upserts, personal access tokens surviving a password
  change, introspection / health / circuit-breaker errors handing exception text to clients, CORS
  allowing any method, PostgreSQL timeout classification reading the wrong exception, and an uncapped
  import batch.

### Changed

- **Concurrent callers for one cache key now cost one database call, not one each.** They each used to
  miss and each execute the same procedure, so the cache only began absorbing load once the first
  response had been stored - the moment it helps least, because none of them can hit it yet. The first
  caller now runs the procedure and the rest wait for its bytes.
- **BREAKING (third-party cache implementations): the cache store moved off the response path.**
  `IResponseCache.SetAsync` now takes a built `CachedResponse` and returns nothing, where it used to take
  raw bytes and return the entry it had built. The engine computes the entity tag itself, so it can set
  `ETag` and answer `If-None-Match` without waiting for the store, and hands the entry to the cache
  without awaiting it. It is passed `CancellationToken.None`: the entry outlives the request that
  produced it, so one client giving up must not discard a payload others are waiting on.
- **Hot-path work**, from an audit of the data plane: the metrics rings no longer take a process-wide
  lock per request; validation regexes are cached, so the sixteenth configured pattern no longer falls
  off `Regex.Cache` and recompiles on every request; a table-valued parameter's content token is built
  only when the cache key or parameter capture will read it, instead of walking every cell on every
  request; column names are pre-encoded once per result set instead of being re-escaped per row; and the
  route key, claim dictionary, scope check, vary-by sort and per-request service lookups stop allocating.
- **Flare 0.2.0 -> 0.4.0**, and the logs timing bar is now a `FlareMeter` - the component this project
  had filed an issue for. 0.4.0's breaking change (`FlareZone` split into `FlareZone` and
  `FlareMeterSegment`) does not reach Weir, which uses no slider, progress zone, pagination or rating.
- **The admin no longer hand-rolls browser interop.** Clipboard, downloads, token storage, file upload
  and the PWA update check all go through Flare's services; `IJSRuntime` is gone from the admin. Token
  storage now JSON-encodes its values under the same keys, so **upgrading forces a one-time re-login**.
- Runtime, Flare and test tooling bumped (.NET and Microsoft.Extensions 10.0.9 -> 10.0.10,
  Microsoft.NET.Test.Sdk 18.8.1, Spectre.Console 0.57.2, Microsoft.SourceLink.GitHub 10.0.301).
- `NU1900` no longer fails the build. It means the vulnerability-advisory source could not be reached,
  which is not a defect in the code being built - the same reasoning that already excluded NU1901-NU1904.

### Security

- The response cache is now bounded. It always computed a size per entry, but the backing memory cache
  was created without a size limit, which makes that size inert - so entries only ever left on TTL and an
  endpoint with a high-cardinality `VaryByParameters` could grow the cache until the process ran out of
  memory. A new runtime setting, `ResponseCacheMaxBytes` (default 128 MiB; zero means unlimited, now an
  explicit opt-out rather than an accident), caps the total bytes cached payloads may occupy; it is seeded
  from `Weir:DataPlane` and editable on the admin **Settings** screen. Once full, the least recently used
  entries are evicted to make room, and a payload larger than the cap is never cached. The response cache
  owns a private, bounded memory cache: a size limit applies to a whole `MemoryCache` instance and, once
  set, makes every entry declare a size, so bounding the shared instance would break its other consumers
  (for example the API-key authenticator).

## [1.0.5] - 2026-07-13

### Changed

- Flare (the admin UI component library) updated 0.1.9 -> 0.2.0. The release adds `FlareSlider` colored
  zones and keyboard events across the field family, restores the field focus indicator, and improves the
  in-box theme fidelity (Fluent / Material state layers, the Visual Studio switch geometry). Its breaking
  changes are theme-authoring only (new required `InputTokens` / `StateTokens` fields when a theme
  constructs those tokens directly); Weir's Command Center theme derives from the in-box Visual Studio
  theme via `with`, so it inherits them and needed no change.

### Added

- A **sample CLI client and load tester** under `samples/client/Weir.Sample.Client` (`weir-sample`),
  built on Spectre.Console. It calls the sample endpoints over HTTP with an API key, exactly as an
  external consumer would. Two command families: the widgets sample (`samples/sqlserver/schema.sql`) -
  `list`, `get`, `create`, `import` (table-valued parameter) - and the demo / orders sample
  (`samples/sqlserver/demo-database.sql`, `weir-demo.endpoints.json`) - `products`, `product`, `orders`,
  `order` (two result sets), `create-order` (table-valued parameter, output params + return value) and
  `customer-stats` (output params); plus a generic `call` that prints the raw envelope. Launched with just
  a URL and key (no command) it opens an **interactive shell** that stays open for request-after-request
  use; given a command up front it runs one-shot for scripts / CI. A `load` command drives concurrent
  requests against any endpoint (`--concurrency`, `--duration` or `--requests`, `--warmup`) and reports
  throughput and latency percentiles (p50 / p90 / p95 / p99) plus a status-code breakdown, preflighting one
  request so a bad URL / key / route fails fast. Not packed or shipped in the image; documented in
  `samples/README.md`.
- **Forced cache purge** for cached data-plane responses, from the admin UI and over the admin API for
  CI/CD. The Endpoints grid gains a **Purge cache** action (shown when caching is enabled) that clears one
  endpoint's cached responses. `POST /admin/api/endpoints/{id}/cache/purge` does the same by id, and
  `POST /admin/api/cache/purge` invalidates in bulk with `AND`-combined, case-insensitive filters: `route`,
  `connection` (a database on a server), `schema`, `object` (procedure / function name) and `provider`
  (the connector behind an endpoint's connection); with no filter it purges every endpoint. Both return
  `{ matchedEndpoints, purgedRoutes }`, are `AdminOnly`, and are audited under `cache.purge`. Purging clears
  rendered responses only - it never changes an endpoint definition - and the cache refills on the next call.

## [1.0.4] - 2026-07-13

### Changed

- NuGet package ids are now prefixed `FrigaT.Weir.*` (for example `FrigaT.Weir.Core`). The bare `Weir`
  id prefix is reserved on nuget.org by another owner, which silently blocked every publish. Only the
  package ids change: assemblies, namespaces, project references and the container image stay `Weir.*` /
  `weir`, so a consumer runs `dotnet add package FrigaT.Weir.Abstractions` and still writes
  `using Weir.Abstractions`.

## [1.0.3] - 2026-07-13

### Added

- A **SQL Server control-plane provider** (`Weir.ControlPlane.SqlServer`, `Provider=SqlServer`), the third
  `IControlPlaneStore` backend alongside SQLite and PostgreSQL. It mirrors the PostgreSQL provider in
  T-SQL (bounded `nvarchar` keys, `bit`/`IDENTITY`, `UPDATE ...; IF @@ROWCOUNT = 0 INSERT` upserts,
  `OFFSET/FETCH` pagination) and serializes migrations across instances with `sp_getapplock`, so - like
  PostgreSQL - it is valid for high-availability deployments (the host does not reject it under
  `Weir:HighAvailability`). Verified end-to-end against a live SQL Server and covered by a
  Testcontainers.MsSql integration test.

## [1.0.2] - 2026-07-13

### Added

- Published-artifact presentation. Every packable library now embeds a shared brand README and the
  Weir mark, so the nuget.org pages render a description and an icon instead of warning that the
  readme is missing; the release workflow syncs `build/dockerhub-overview.md` as the Docker Hub
  repository overview and short description; and both READMEs carry a Docker Pulls badge. The NuGet
  changes apply from the next published version, since versions already on nuget.org are immutable.

### Fixed

- The Docker Hub image namespace is `frigat/weir`: the Docker Hub account is `frigat`, not the GitHub
  owner `jrfrigat`. Corrected in the Docker Hub overview and the NuGet package README; the release
  workflow already derives the image from the `DOCKERHUB_USERNAME` variable.

### Changed

- The internal design mockups (`design/`, the weir-http and weir-http2 HTML prototypes with their
  screenshots and support scripts) and the two-plane architecture ADR
  (`docs/adr/0001-two-plane-architecture.md`) are removed from the repository.

## [1.0.1] - 2026-07-12

### Added

- The release workflow also pushes the container image to Docker Hub alongside GHCR, in the same
  build. The Docker Hub tags and the Docker Hub login are included only when the `DOCKERHUB_USERNAME`
  repository variable is set, so a release keeps working with GHCR alone when Docker Hub is not
  configured.

### Fixed

- The GHCR image name is lowercased in the release workflow. `github.repository` preserves the
  repository's case (`jrfrigat/Weir`) and GHCR rejects an uppercase repository path, so the image
  push failed with "repository name must be lowercase".

## [1.0.0] - 2026-07-12

### Security

- The local Claude Code config directory (`.claude/`) is now git-ignored. Its `launch.json` carries a
  machine-specific dev connection string (with a password), so it must not be committed; the demo
  `docker-compose.override.yml` was already ignored for the same reason.
- Admin sessions now use short-lived access tokens plus refresh tokens. The access-token default drops
  from 60 to 30 minutes; sign-in also returns a long-lived, revocable refresh token, stored only as a hash
  in a new `AdminRefreshTokens` table (SQLite and PostgreSQL). `POST /admin/api/auth/refresh` exchanges it
  for a fresh access token and rotates the refresh token (the used one is revoked); `POST /admin/api/auth/logout`
  revokes it on sign-out; and a password change revokes all of an admin's refresh tokens (on top of the
  existing token-version bump). The admin PWA refreshes transparently: its HTTP handler renews an expired
  access token and replays the request once. Expired and revoked tokens are pruned in the background.

- The admin sign-in throttle is now persisted in the control plane instead of per-instance memory, so a
  lockout survives a restart and is shared across every instance in an HA deployment (an attacker can no
  longer reset their failed-attempt count by waiting for a redeploy or hitting a different node). Backed by
  a new `LoginThrottle` table (SQLite and PostgreSQL migrations) with an atomic record-failure step, an
  async `ILoginThrottle`, and background pruning of stale rows. Throttling stays keyed by source IP.

- Admin JWTs are now revocable. Each token carries a `ver` claim stamped from the account's token
  version, and every request re-checks it against the store; changing an admin's password (which bumps
  the version) or disabling the account rejects all previously issued tokens immediately, instead of
  leaving them valid for up to the full access-token lifetime. Backed by a new `AdminUsers.TokenVersion`
  column (SQLite and PostgreSQL migrations) and `IControlPlaneStore.FindAdminByIdAsync`.
- Personal access tokens are bounded: a per-admin cap (`Weir:Admin:MaxTokensPerAdmin`, default 20) and an
  optional deployment policy requiring every token to have an expiry (`Weir:Admin:RequireTokenExpiry`).

- Cache-key correctness (data disclosure): a `VaryByParameters` entry that named a table-valued or
  output parameter previously keyed the response cache on NULL for every caller, so distinct requests
  could collide onto one entry and serve one caller another caller's data. The binder now records a
  content token for table-valued parameters (so varying by a TVP keys on its rows), and `CacheKey`
  refuses to build a key when a vary-by name did not produce a keyable value (disabling caching for
  that call rather than risking a collision).
- Introspection routes (`GET /admin/api/introspect/{connection}/objects` and `.../parameters`) now
  require the `AdminOnly` policy - they were reachable by read-only viewers and open real database
  connections. The connection-health route redacts driver exception text (which can disclose server /
  database / login names) for non-admin callers while still showing status.
- The shipped default data connection no longer sets `TrustServerCertificate=True`; the dev-only value
  moved to `appsettings.Development.json` so a promoted `appsettings.json` does not disable TLS
  certificate validation.
- Safe-by-default data-plane limits: `Weir:DataPlane:MaxRows` and `RequestTimeoutSeconds` now default
  to non-zero values, and new `MaxTvpRows` (table-valued-parameter row cap) and `MaxRequestBodyBytes`
  (enforced by Kestrel) guard against unbounded bodies. `Weir:Security:DefaultApiKeyRateLimitPerMinute`
  applies a default limit to keys that set none, and the per-key rate limiter now evicts stale windows.
- Startup options validation (`ValidateOnStart`) for the data-plane limits, audit queue, admin lockout
  and JWT lifetime, so misconfiguration fails fast instead of surfacing as runtime errors.
- Admin sign-in throttling is keyed by source IP rather than username, so a bad-password flood cannot
  lock a real admin out; login verifies against a decoy hash for missing accounts to remove the
  username-enumeration timing oracle; throttle state is bounded to prevent unbounded memory growth.
- Passwords (bootstrap and API-created) must be at least 8 characters; unknown roles are rejected
  rather than silently promoted to `Admin`; revoking or creating an API key immediately evicts the
  authentication cache; in Production the JWT signing key must be at least 32 characters.

### Changed

- Dependencies updated to their latest stable versions ahead of the first release, including major
  bumps: Microsoft.Data.SqlClient 6 -> 7, Npgsql 9 -> 10, Serilog.AspNetCore 8 -> 10, Serilog.Sinks.File
  6 -> 7, StackExchange.Redis 2 -> 3, Microsoft.NET.Test.Sdk 17 -> 18 and xunit.runner.visualstudio
  2 -> 3, plus patch/minor bumps across Dapper, the ASP.NET Core packages and Testcontainers. No code
  changes were required beyond moving the Testcontainers image name into the builder constructor.
- The package and repository URLs now use the canonical lowercase repository path
  (`github.com/jrfrigat/weir`).
- Admin audit entries now carry a description of what changed, not just the action category. A settings
  change lists each changed field with its old and new value; endpoint create / update / delete / import
  and both sync routes name the affected endpoint (method, route and object) or summarize the parameter
  changes; and the endpoint import and "try it" invoke routes, previously unaudited, are now recorded, so
  every administrative action lands in the audit log.
- The endpoint editor now exposes the full caching policy in a **Caching** section: enable/disable,
  TTL, vary-by-parameter selection (toggle the endpoint's parameters that should form the cache key)
  and vary-by-API-key. The policy was always stored and honoured; it is now editable in the UI instead
  of only via import. Endpoint cache and logging config, like the runtime settings, is read from an
  in-memory snapshot on the request hot path (the endpoint catalog and `IRuntimeSettings`), loaded from
  the control plane at startup and refreshed in memory when saved from the admin panel, so a data-plane
  call never reads configuration from the database.
- The endpoint editor's **Database** tab now shows the mapped endpoint's method and route for each
  database object (a clickable chip that opens the endpoint), instead of a bare "linked" marker.
- New app icon. One mark is now used everywhere: a "W" gateway where databases (three hollow rings on
  top) flow down through the W and merge onto a gateway rail (the stacked bars at the bottom), in the
  Command Center palette (cyan `#4fd6c9` to accent `#4ea1ff` on the dark `#131416` surface), replacing
  the previous water-weir art and its unrelated blue palette. The favicon (`favicon.svg`) is the source
  of truth; the installed-app icons (192 / 512 / maskable / apple-touch), the README banner, the brand
  logo (`assets/logo.svg`) and the GitHub social-preview image (`assets/social-preview.svg` / `.png`)
  were all regenerated to match it, and the app bar and login card now render the same mark as an inline
  SVG (`WeirMark`) sized for on-surface display, replacing the borrowed Material `hub` glyph. The
  manifest and `theme-color` stay aligned to the app background.
- Overlay drawers (Edit endpoint, the test console, the request-log detail) now inset their content
  horizontally to match the header, like a FluentUI2 / Visual Studio 2026 panel. Flare's default left
  the drawer content edge-to-edge (`padding: spacing-4 0`), so fields ran into the panel edge.
- The PWA "new version available" notice is now a Flare snackbar toast (bottom-right, in the app
  theme), matching the Flare Gallery pattern, instead of a full-width top banner: a single **Update**
  action activates the waiting service worker and reloads onto the new build. The host now serves the
  PWA control files (`service-worker.js`, `service-worker-assets.js`, `register-sw.js`, the manifest)
  with `Cache-Control: no-cache` so a browser cannot keep serving a stale worker and miss the update.
- Overview and per-endpoint latency percentiles (p50 / p95 / p99) are now windowed, not lifetime. The
  latency histogram moved into the per-second ring, so it decays over a trailing five-minute window and a
  slow spike no longer pins p95 forever; all three percentiles are read from one self-consistent windowed
  histogram per snapshot. Lifetime counters (total calls, errors, cache hits, mean) are unchanged.
- The audit log supports keyset (seek) pagination. `AuditQuery` accepts an `AfterId` cursor and the
  admin `GET /admin/api/audit` an `afterId` query parameter; passing the last id of a page fetches the
  next page with `WHERE Id < @AfterId ORDER BY Id DESC`, which stays fast as the table grows and never
  skips or repeats rows when entries are inserted between fetches (offset paging still works when no
  cursor is given). The admin Audit screen now has a "Load older" control that walks pages by cursor.
- The PostgreSQL connector now draws connections from a pooled `NpgsqlDataSource` cached per connection
  string (the driver's recommended pooling entry point) instead of constructing a fresh `NpgsqlConnection`
  per call, on both the data-plane execute path and the admin introspection reads. The connector disposes
  its data sources on shutdown.
- The streaming JSON writer now uses typed `DbDataReader` getters (`GetInt32` / `GetInt64` / `GetDouble`
  / `GetDecimal` / `GetDateTime` / `GetGuid` / ...) instead of the boxing `GetValue` on the row hot path.
  Each column's value-writer kind and name are resolved once per result set from its field type and reused
  for every row; unknown or provider-specific types fall back to the previous boxed path, so the emitted
  JSON is byte-for-byte identical.
- Response compression (Brotli / gzip, negotiated from `Accept-Encoding`) is enabled for the data-plane
  JSON result sets and problem+json errors.
- Every admin mutation is now audited: endpoint create / update / delete / import, scope upsert / delete,
  admin create and password reset, and settings changes join the already-audited key and token actions
  and sign-ins, each recording the acting admin and the affected resource. The data-plane forbidden and
  rate-limit paths, and admin sign-in failures / lockouts, are also logged as security events.
- Destructive admin actions now confirm before running: deleting an endpoint, revoking an API key and
  deleting a scope each open a `Discard`/`Delete`-style confirm (via `IDialogService`) naming the target,
  so a stray click can no longer drop config or break a live caller.
- The scopes table shows how many keys carry each scope and how many endpoints require it, matching the
  Command Center design and surfacing whether a scope is safe to delete.

### Fixed

- A truncated (row-capped) response is no longer written to the response cache; it was previously
  cached for the full TTL, so later callers silently received partial data as if complete.
- SQLite control-plane migrations now run each migration and its version bump in one transaction, so a
  crash mid-migration rolls back cleanly and re-runs instead of leaving the schema half-applied (which
  could break a non-idempotent `ALTER TABLE ADD COLUMN` on the next start). Every SQLite connection now
  applies `PRAGMA busy_timeout` (configurable) so concurrent writers wait for the lock instead of
  failing with `SQLITE_BUSY`, and `PRAGMA foreign_keys=ON` to enforce referential integrity.
- Broken switch/toggle rendering. Under the Visual Studio / Command Center theme the switch showed a
  circular thumb taller than its rail (the ball bulged out of the track), because the theme's design
  reference sets a compact FluentUI2-size rail (40x20) but kept the Material Design thumb sizes (a 24px
  "on" thumb in a 20px rail). Weir now sets FluentUI2-consistent thumb tokens so the handle fits the
  rail (a proper Fluent toggle). Filed upstream as a Flare theme bug; the workaround is removed once the
  VS theme ships consistent switch tokens.
- Admin data grids that render nothing. Several admin screens (keys, scopes, admins, account tokens,
  the dashboard endpoint feed and the endpoint editor's Database object browser) declared their
  `FlareColumn`s as bare children of the grid instead of inside its `<Columns>` fragment, which Flare
  silently ignores - the grid produced rows with no cells, so the page looked empty even though the data
  loaded. Every grid now wraps its columns correctly, so the Database tab and the key/scope/admin lists
  populate as expected.
- Response cache keys are now collision-resistant: values are type-tagged and every keyed segment is
  length-prefixed, so distinct binary values (previously all "System.Byte[]") and delimiter-bearing
  strings no longer collapse to one key and serve the wrong caller's cached response.
- Editing or deleting an endpoint now evicts its cached responses (new `IResponseCache.RemoveByPrefixAsync`)
  instead of serving stale JSON until the TTL expires.
- Duplicate enabled routes are logged on catalog load instead of one endpoint silently disappearing.
- Connectors drain any unread result sets before completing, so output parameters and the return value
  are captured even when a response is row-capped (`"truncated": true`).
- The dashboard requests-per-second is scaled by actual uptime during the first minute; time-series
  windows are clamped to the ring capacity so a long window no longer double-counts; latency
  percentiles are computed from a self-consistent counter snapshot.
- Control-plane stores no longer leak a connection object when opening fails, translate a duplicate
  method+route into HTTP 409, throttle last-used writes on the hot auth path, and normalize audit
  timestamps to UTC for correct range queries.
- Data-plane requests with a chunked body (no `Content-Length`) are read instead of silently dropped;
  driver and configuration error text is no longer echoed to callers; a mid-stream failure aborts the
  connection instead of emitting a truncated `200 OK`.
- Admin PWA hardening: a malformed stored token can no longer brick the app, `returnUrl` is validated
  against open redirects, the dashboard refresh timer no longer touches a disposed component, and a
  401 for an authenticated request signs the session out.
- Endpoint "Test" drawer now shows the response: it was binding the JSON to a non-existent `Code`
  attribute on `FlareCodeBlock` (the parameter is `Value`), so a successful invoke rendered an empty
  Response panel. The pretty-printed envelope now appears as expected.

### Added

- A "view source" GitHub link in the admin app bar, on the right just before the account menu, opening
  `github.com/jrfrigat/weir` in a new tab. Rendered as authored brand art (`GitHubIcon`), since Material
  Symbols carries no brand glyphs.
- Release-oriented documentation: the README now carries CI / license / .NET badges and an **Install**
  section (pull `ghcr.io/jrfrigat/weir` or add the library packages), the Deployment doc documents the
  published GHCR image, and CONTRIBUTING documents the tag-driven release pipeline and exactly what ships
  to NuGet versus the container image. All bilingual (English and Russian) where applicable.
- The release workflow now publishes NuGet packages via **Trusted Publishing** (OIDC), exchanging a
  GitHub token for a short-lived NuGet key through `NuGet/login` instead of storing a long-lived
  `NUGET_API_KEY` secret (it reads the account name from a `NUGET_USER` repository variable). It also
  creates the GitHub Release from the tag (auto-generated notes plus the packed `.nupkg` files).
- Data-plane request log. Every call is recorded off the hot path (route, method, object, connection,
  timing, database time, rows, cache hit, status and caller), viewable on a new admin **Logs** screen
  with keyset paging and slow-only / errors-only filters. A call is flagged **slow** when it exceeds its
  endpoint's rolling average by a threshold percentage; the threshold is a global setting
  (`SlowRequestThresholdPercent`) that an endpoint can override. Per endpoint, request **parameters** and
  the response **result** can be captured (off by default, since they can hold PII; the result capture is
  size-capped) and are shown in a per-call detail panel that also lists the endpoint's average for
  context. The **Dashboard** endpoint feed and the **Endpoints** list link straight into the log filtered
  to one endpoint. Backed by a new control-plane `RequestLog` table (SQLite and PostgreSQL) with
  background writing and retention pruning (`RequestLogRetentionDays`), a per-endpoint logging policy
  stored with the endpoint, and the global request-log settings surfaced in the admin Settings screen.
- Endpoint filtering by scope and by key, in the admin endpoint list. The list can be narrowed to
  endpoints that require a chosen scope, and/or that a chosen API key could actually reach (the key must
  hold every required scope and carry a resource grant that matches the procedure - the same rule the
  data plane enforces on a live call). Export and the generated OpenAPI document both honour the active
  filter, and the OpenAPI route accepts `?key=` / `?scope=` so a consumer can be handed a document
  describing exactly the surface their key can call (the document title notes the audience).
- A shared page-header component gives every admin screen the same title, description and action-button
  layout, so the heading row no longer shifts position or size from page to page.
- Connector-execution and data-plane end-to-end tests, plus a PWA smoke test. The e2e tests spin up a
  real PostgreSQL database with Testcontainers and exercise the full data path - the connector executing a
  table-valued function and streaming rows, and the engine binding parameters, executing, streaming the
  JSON envelope and serving a cache hit with a matching ETag. They are Docker-gated (opt-in via
  `WEIR_CONTAINER_TESTS=1`) so a local run without Docker still passes. The PWA smoke test validates the
  web manifest, its declared icons, the service worker and the index wiring, guarding against a
  non-installable build.

- High-availability guardrail and cross-instance metrics story. A new `Weir:HighAvailability` flag
  asserts a multi-instance deployment: when set, the host refuses to start on the single-node SQLite
  control plane and requires the shared PostgreSQL control plane, so instances cannot silently diverge on
  per-node metadata. The deployment docs now document the cross-instance metrics model - the built-in
  dashboard is per-instance, and a fleet-wide view comes from exporting the `Weir` (and `Npgsql`) meters
  over OTLP to Prometheus / an OpenTelemetry backend that aggregates across instances.

- Distributed rate limiter (Redis) for HA. Setting `Weir:RateLimit:RedisConnectionString` switches the
  per-API-key limiter from the default in-memory (per-instance) limiter to a Redis-backed one that counts
  requests in a shared per-key, per-minute window, so one limit applies across every instance instead of
  N times over. It fails open (allows the request) with a logged warning if Redis is briefly unreachable.
  The limiter interface is now async; the in-memory limiter is unchanged in behaviour.

- Control-plane backup/export. A new AdminOnly, audited `GET /admin/api/export` returns a portable JSON
  snapshot of the configuration - endpoint definitions, scopes, runtime settings, and the non-secret
  inventory of API keys and admin accounts (no key or password hashes). The admin **Settings** screen has
  a "Generate export" action that downloads it. Secrets are excluded by design, so it is a configuration
  and inventory backup; full disaster recovery still uses a database-level snapshot of the control store.

- Control-plane migration checksums. Each applied migration's SHA-256 is recorded in a new
  `SchemaMigrations` table; on startup the store verifies every already-applied migration against the
  shipped script and fails fast with a clear error if one was edited or the history was tampered with
  (shipped migrations must never change). Databases created before this feature have their checksums
  backfilled on first start. Implemented for both SQLite and PostgreSQL.

- Database-error classification in telemetry. Each connector maps a driver failure to a provider-agnostic
  category (timeout / deadlock / constraint / connection / other) from its error codes; the engine records
  it on the call context and the OpenTelemetry observer emits a `weir.db.errors` counter tagged by route
  and category and adds the category to the failure span. The `Npgsql` meter is now also subscribed by the
  OTLP exporter, surfacing PostgreSQL connection-pool telemetry (idle / busy connections, saturation).

- Per-connection resilience for the data plane: a concurrency bulkhead and a circuit breaker, one per
  data connection. The bulkhead caps how many executions may run at once against a connection and rejects
  excess fast with HTTP 503 (never queueing onto a saturated database); the breaker trips after a run of
  connection failures and short-circuits with HTTP 503 until a reset window elapses and a probe succeeds.
  Client-side outcomes (validation errors, cancellations, bulkhead rejections) never trip the breaker.
  Both are runtime settings on the admin **Settings** screen (`MaxConcurrentRequestsPerConnection`,
  `CircuitBreakerFailureThreshold`, `CircuitBreakerResetSeconds`), seeded from `Weir:DataPlane` and applied
  without a restart.

- Per-endpoint toggle to suppress SQL informational messages. When an endpoint sets `SuppressMessages`,
  the response envelope's `messages` array is always empty, so chatty `PRINT` / notice / info diagnostics
  never reach callers (the property stays present for a stable envelope). Editable in the endpoint editor
  and backed by a new `Endpoints.SuppressMessages` column (SQLite and PostgreSQL migrations).

- Conditional GETs and cache validators for cache-eligible reads. `WeirEngine.ExecuteAsync` now returns
  response metadata (cache-hit, a strong `ETag` derived from the exact response bytes, `max-age` and the
  truncated flag) and exposes the tag before the body is written. The data plane sets `ETag` and
  `Cache-Control: private, max-age=<ttl>` on cache-eligible GET responses and answers a matching
  `If-None-Match` (including `*` and weak `W/` tags) with a body-less `304 Not Modified` before streaming.
  Truncated (row-capped) and non-cache-eligible responses carry no validator, so a partial body is never
  revalidated against a complete one.

- The admin PWA is now installable and update-aware. It ships app icons (192, 512, a maskable variant
  and an apple-touch icon) and a populated web manifest, so browsers offer the install prompt (previously
  the manifest declared no icons and none existed). A new version of the app surfaces a "reload" banner
  instead of leaving the cache-first service worker serving a stale build until every tab closes, and an
  offline banner appears when the browser goes offline. The layout now wraps pages in an error boundary
  that recovers on navigation, the dashboard has a heading (so focus-on-navigate works), and the admins
  list has an empty state.

- Real-time admin dashboard over SignalR. A secured hub (`/hubs/dashboard`, authenticated admins only)
  and a background broadcaster push a metrics snapshot every couple of seconds and a connection-health
  update on a slower cadence, so the dashboard no longer polls four HTTP endpoints every three seconds
  per open tab; broadcasts are skipped when no dashboard is connected. The Blazor client subscribes with
  automatic reconnection and falls back to the previous polling if the hub is unreachable. The JWT
  handler accepts the token from the `access_token` query parameter for the WebSocket handshake.

- File logging via Serilog, configured from `Weir:Logging`: a rolling file sink whose directory,
  rolling interval, size limit, retained-file count / time limit, format (human-readable text or
  compact JSON) and minimum level are all configurable, plus an optional console sink. Each request
  emits one structured summary line and carries a correlation id (`X-Correlation-ID`, generated when
  absent and echoed back) attached to every log event for the request. Security events - failed
  sign-ins, lockouts, scope/grant denials, rate-limit hits and settings changes - are logged. The
  logging configuration is surfaced read-only on the admin Settings screen.

- Audit retention: a background service prunes audit entries older than the runtime
  `AuditRetentionDays` setting (editable on the Settings screen; zero keeps history forever), bounding
  the audit table's growth. `IControlPlaneStore.PruneAuditAsync` implements it for SQLite and
  PostgreSQL. The data-plane audit queue now counts and warns about dropped entries under load instead
  of dropping them silently.

- Runtime system settings, editable from the admin panel and persisted in the control plane. A new
  **Settings** screen (and `GET` / `PUT /admin/api/settings`) tunes the data-plane row cap, request
  timeout, table-valued-parameter row cap and the default per-key rate limit without a restart; the
  values overlay the `appsettings.json` seed, survive restarts and reach every instance sharing a
  control plane. Restart-required values (the request body cap) are shown read-only. Updates require
  the Admin role and are audited. Backed by a single-row `Settings` table with SQLite and PostgreSQL
  migrations, an `IRuntimeSettings` service, and `IControlPlaneStore.Get/SaveSettingsJsonAsync`.

- Personal access tokens for admin accounts - long-lived, revocable tokens for scripted / CI-CD access
  to the admin API (for example, re-syncing an endpoint's parameters on deploy). An admin creates and
  revokes their own tokens under **Account -> Access tokens** or via `POST` / `DELETE
  /admin/api/account/tokens`. A token (`weadm_...`, sent as a `Bearer` header) authenticates as its
  owning admin with that admin's current role; only a SHA-256 hash and a short prefix are stored, and
  the admin API accepts either a token or a login JWT. Backed by a new control-plane table with SQLite
  and PostgreSQL migrations.

- `POST /admin/api/endpoints/sync` now accepts `schema` and `object` query filters in addition to
  `connection`, so a CI/CD job can re-sync a specific database (a named connection), a schema, or a
  single procedure by name without needing the endpoint's id.

- Admin UI redesigned as a dark, dense "Command Center" ops console - top-nav tabs, a live status
  ribbon, a monospace service-metrics readout, FlareChart sparklines and a live feed on the
  dashboard; data grids, right-side editor / test drawers and a create-key dialog on the other screens.
  - Rebuilt entirely on Flare components (`FlareLayout`/`FlareLinkTabs` shell, `FlareDataGrid`,
    `FlareDrawer`, `FlareDialog`, `FlareDescriptionList`, `FlareChart`, `FlareCode`/`FlareText.Mono`,
    fields, chips) with no bespoke design-system CSS or SVG - only the pre-Blazor boot splash and the
    Blazor error bar remain.
  - A single fixed theme: the **Visual Studio 2026** geometry recoloured with a **Command Center**
    palette (cyan on near-black), dark-only. No runtime theme or light/dark switcher - the ops console
    has one look. The signed-in user's name in the app bar is a menu (Account / Sign out).
  - Flare packages updated to 0.1.9; the dashboard throughput / latency sparklines now use `FlareChart`
    (`Sparkline` + `Area`), removing the last hand-authored SVG from the admin. The service-metrics rail
    is content-width beside full-width charts (`FlareStack.StretchLast`).
  - Adopted 0.1.8 overlay features: the create-key dialog carries a close button and guards accidental
    dismissal (Escape / scrim / close) of a partly-filled form with a "Discard key?" confirm via
    `IDialogService` (`FlareDialogProvider`); icon-only controls such as the TVP remove-column button now
    carry a `FlareTooltip`.
  - Adopted 0.1.9 first-class parameters and retired the last two Style-only escape hatches: the dashboard
    sparklines use `FlareChart`'s new fixed-pixel `Height` in `Sparkline` mode (`Height="120"`), dropping
    the `.flare-chart--sparkline` app-CSS height override; the app bar uses `FlareLayoutAppBar.Height`
    plus the `--flare-layout-appbar-bg` token (and the now-default bottom border) instead of inlining
    height / background / border. The admin no longer reaches into any Flare component's internals.

- Plugin system for extending a running image without rebuilding it:
  - `IWeirPlugin` entry point in `Weir.Abstractions` - a plugin registers services (most commonly an
    `IDbConnector`) into the host.
  - Plugin loader that loads assemblies listed in `Weir:Plugins:Paths`, isolating each plugin's
    private dependencies while sharing the Weir contract assemblies with the host.
  - A connector still works the compile-time way (reference the package, call `AddWeirX()`); the same
    code serves both the custom-host and drop-in models.
  - Sample MySQL connector (`samples/connectors/Weir.Connectors.MySql`) as a reference for authors,
    and an "Extending" guide (English and Russian).

- Production hardening:
  - The host refuses to start in Production without a stable `Weir:Jwt:SigningKey`.
  - Security response headers on every response (`Content-Security-Policy`, `X-Content-Type-Options`,
    `X-Frame-Options`, `Referrer-Policy`) plus HSTS outside Development; optional in-process HTTPS
    redirect via `Weir:Security:RequireHttps`. The default CSP is Blazor-admin compatible.
  - Admin sign-in lockout after repeated failures (`Weir:Admin:MaxFailedLogins` / `LockoutMinutes`).
  - Data-plane guards: a response row cap (`Weir:DataPlane:MaxRows`, marks `"truncated": true`) and
    an overall request timeout (`Weir:DataPlane:RequestTimeoutSeconds`, returns HTTP 504).
  - Kubernetes-style health endpoints: `/health/live` (process up) and `/health/ready` (dependencies).
  - Periodic endpoint-catalog reload (`Weir:ControlPlane:ReloadSeconds`) so instances in an HA
    deployment pick up metadata changes made elsewhere.
  - Concurrent PostgreSQL control-plane migrations are serialized with a session advisory lock.

- Per-API-key resource grants - a key can be limited to specific procedures by connection
  (server + database), schema and object, each level exact or `*` ("any"); a key can hold many
  grants and is allowed if at least one matches. A key with no grants stays unrestricted. Grants are
  edited under **Procedure access** when creating a key and are enforced on the data plane (403).
- Per-API-key rate limiting - enforces `RateLimitPerMinute` and returns HTTP 429 with a
  `Retry-After` header when exceeded.
- Per-connection health checks - each data connection is probed (SELECT 1) and surfaced at `/health`
  (as Degraded when unreachable), at `GET /admin/api/connections/health`, and on the dashboard.
- OpenTelemetry OTLP export - enabled by setting `OTEL_EXPORTER_OTLP_ENDPOINT`.
- Configurable CORS for browser data-plane clients via `Weir:Cors:AllowedOrigins`.
- Schema introspection - discover stored procedures / functions and auto-fill an endpoint's
  parameters from the database ("Discover from database" in the endpoint editor).
- Admin "try it" console - invoke an endpoint through the engine from the admin UI.
- Admin UI overhaul (enterprise, Aspire-style):
  - Redesigned dashboard - data-connection health cards up top (green / red status and the error
    text inline when a database is unreachable), compact metric tiles (adds p50 / p99 and uptime),
    smaller side-by-side charts, and a full per-endpoint telemetry table.
  - Endpoints page rebuilt on a single filterable data grid (sort, per-column filters, quick search,
    status pills) that unifies the database's objects with their endpoints: each row shows whether an
    endpoint is created, its route, sync changes inline (a **Changes** column, no separate result
    panel), and per-row actions (Test / Edit / Sync / Delete, or **Create** for an unmapped object
    pre-filled from it). The former database explorer and its separate page are folded in and removed.
  - A weir-branded favicon; the sidebar stays fixed while only page content scrolls; tidier account /
    sign-out footer.
  - Form-based test console - one typed input per parameter (with a row editor for table-valued
    parameters) instead of hand-written JSON, a Raw JSON toggle, and query / header / claim
    parameters are exercised too (admin invoke routes each value to its parameter source).
  - Account page - a signed-in admin can change their own password (verifies the current one) and
    see their role; the Admins page shows role and status badges and lets an Admin reset a password.
  - Every screen composed from Flare components (the shell is `FlareLayout` + `FlareLayoutDrawer` +
    `FlareNavMenu`; forms use `FlareField` / `FlareNumericField` / `FlareSelect` / `FlareCheckbox`;
    lists use `FlareDataGrid`; status uses `FlareChip`) so the admin tracks the Visual Studio light /
    dark theme with no bespoke design-system CSS -- `app.css` now holds only the pre-Blazor boot
    splash and the Blazor error bar. The Material Symbols icon font is self-hosted (no external CDN,
    works within a `font-src 'self'` CSP and offline).
  - Endpoint editor now round-trips precision / scale / default value / validation / header / claim,
    and Discover fills table-valued parameter columns.
- Sync from database - refresh one endpoint (row **Sync**) or every endpoint (**Sync all from
  database**) so their parameters match the current procedure signatures. The merge keeps each
  parameter's binding config and only updates the database-derived shape, adding new parameters and
  removing ones the database no longer declares; table-valued parameters and their columns are
  discovered too.
- Import / export of endpoint definitions (JSON) for promoting between environments.
- OpenAPI 3.0 document generated from endpoint metadata (`GET /admin/api/openapi.json` and an
  "OpenAPI" download in the admin UI) for generating typed data-plane clients.
- PostgreSQL data-plane connector (`Weir.Connectors.PostgreSql`) - invokes functions and stored
  procedures via Npgsql with output / INOUT parameter and notice-message support.
- PostgreSQL control-plane store (`Weir.ControlPlane.PostgreSql`) - a shared metadata store for
  high-availability deployments. Selectable via `Weir:ControlPlane:Provider` (`Sqlite` or `Postgres`).
- Opt-in data-plane auditing - each call is recorded as an audit entry through a non-blocking
  background writer. Enable with `Weir:Audit:DataPlane`.
- Transient connection-open retry in the SQL Server and PostgreSQL connectors - only the open is
  retried (before any command runs), so procedures are never invoked more than once.
- Release workflow (`.github/workflows/release.yml`) - on a `v*` tag, packs and publishes the
  library and connector NuGet packages and builds and pushes the host container image to GHCR.
- Samples (`samples/`) - SQL Server and PostgreSQL "widgets" schemas plus an importable
  `endpoints.seed.json` covering result sets, output parameters, return values and a TVP.
- Role-based access control for the admin UI - accounts are `Admin` (full access) or `Viewer`
  (read-only). Every change requires the `Admin` role; read-only endpoints stay open to viewers.
- PostgreSQL control-plane integration tests (Testcontainers) - opt-in via `WEIR_CONTAINER_TESTS=1`
  and run in CI, exercising migrations and the full store round-trip against a real database.

## [0.1.0] - 2026-07-10

Initial release.

### Added

- **Data plane**: metadata-driven HTTP endpoints that invoke SQL Server stored procedures /
  functions and stream the result as JSON directly from the data reader.
- **Response envelope**: a single consistent shape - `data` (array of result sets), `output`,
  `returnValue`, `rowsAffected`, `messages`. Errors use RFC 7807 problem+json.
- **Parameters**: body / query / route / header / claim / const sources, table-valued parameters
  (TVP), output and input-output parameters, return value, and per-parameter validation.
- **Caching**: per-endpoint result cache configured from the admin UI (TTL plus vary-by parameters).
- **Multi-connection**: one instance routes to many servers / databases / schemas via named
  connections.
- **Control plane**: SQLite store for endpoints, API keys, scopes, admin users and audit, behind
  `IControlPlaneStore` with idempotent migrations.
- **Security**: API keys with scopes for clients; local admin accounts with a bootstrap admin and
  JWT sessions for the admin UI.
- **Telemetry**: OpenTelemetry meter and activity source plus an in-memory aggregator that powers a
  built-in dashboard.
- **Admin PWA**: Blazor WebAssembly app built on Flare (Visual Studio 2026 theme) - login, live
  dashboard with charts, and CRUD for endpoints, keys, scopes, admins and audit.
- **Packaging**: multi-stage Dockerfile, docker-compose stack, and GitHub Actions CI.

### Security

- Pinned SQLitePCLRaw to 3.0.3 to resolve the NU1903 advisory on the transitive 2.1.11 native
  library; verified at runtime by the SQLite-backed tests.

[Unreleased]: https://github.com/jrfrigat/weir/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/jrfrigat/weir/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/jrfrigat/weir/compare/v1.0.5...v1.1.0
[1.0.5]: https://github.com/jrfrigat/weir/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/jrfrigat/weir/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/jrfrigat/weir/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/jrfrigat/weir/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/jrfrigat/weir/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/jrfrigat/weir/compare/v0.1.0...v1.0.0
[0.1.0]: https://github.com/jrfrigat/weir/releases/tag/v0.1.0
