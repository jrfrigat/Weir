# Weir - Admin UI

> [Russian / Russkiy](../ru/admin-ui.md) - [Getting Started](getting-started.md) - [Security](security.md)

The admin UI is a Blazor WebAssembly PWA styled as a dark "Command Center" ops console (top-nav tabs,
dense monospace tables, a live dashboard). It is served by the host at the root path and talks to the
admin API under `/admin/api`. Every page except login requires a signed-in admin. It is an installable
PWA (see [Install](#install-pwa)).

## Dashboard

Live overview, streamed over a real-time connection (a SignalR hub) so it reflects service state
without polling; if the hub is unreachable it falls back to periodic HTTP polling:

- **Data connections** (top): one card per target database with a health dot (green / red), its
  provider, probe latency, and - when a connection is down - the error text inline, so a broken or
  misconfigured database is obvious at a glance.
- **Service**: compact tiles for requests per second, total requests, errors, error rate, in-flight
  requests, p50 / p95 / p99 latency and cache hit ratio, plus process uptime.
- **Throughput and latency**: two compact charts over the recent window.
- **Endpoints**: a per-endpoint table (count, error rate, p50 / p95 / p99, cache hit, last call).

With no data-plane traffic yet, the metrics are zero and the charts are flat - that is expected; the
connection cards still show whether each database is reachable.

## Endpoints

The central page: a single filterable data grid that unifies the database's objects with the
endpoints mapped to them. Pick a **connection** (top right); the grid then lists that connection's
stored procedures and functions, and for each one:

- a **status** pill - `endpoint` when a route is mapped to it, `disabled` when the endpoint is
  turned off, or `no endpoint` when the object is not yet exposed;
- the **endpoint** route (method + path) if one exists;
- a **changes** column that fills in after a **Sync** to show what was added / updated / removed;
- **actions** - a created row has **Test**, **Edit**, **Sync**, **Delete**, plus **Purge cache** when
  caching is enabled (force-clears that endpoint's cached responses); an object with no endpoint has
  **Params** (inspect its parameters) and **Create** (open the editor pre-filled from that object with
  its parameters already discovered).

Sort any column, filter per column, or use the quick filter to search across all columns. When a
connection's database cannot be introspected, the grid still lists its endpoints.

**New endpoint** / **Edit** open a full editor covering the route and object, result mode, timeout,
enabled flag, required scopes, and the parameter list (source, direction, type, size, precision /
scale, TVP columns). A **Caching** section turns response caching on or off, sets the TTL, and picks
which inputs form the cache key - toggle any of the endpoint's parameters to **vary by** them, and
optionally **vary by API key**, so callers with different values (or different keys) get separate
cached entries; with none selected, one response is shared per endpoint. A **Logging** section sets
the per-endpoint request-logging policy (see [Logs](#logs)). Saving reloads the server-side catalog
immediately, so the running data plane picks up the new cache and logging config without a restart.

Toolbar and editor actions:

- **Test** - a form-based console. Instead of hand-writing JSON, it renders one typed input per
  parameter (numbers, dates, checkboxes; table-valued parameters get a row editor) and builds the
  request for you; a **Raw JSON** toggle exposes the generated body for power users. It routes each
  value to the parameter's source, so query, header and claim parameters are exercised too, not just
  the body, and the response is shown formatted.
- **Discover from database** - fills the parameter list (including TVP columns) from the target
  object, so you rarely define parameters by hand.
- **Sync** (per row) and **Sync all** (toolbar) - reconcile existing endpoints with the current
  procedure signatures. The merge keeps each parameter's binding config and only updates the
  database-derived shape, adds new parameters and removes ones the database no longer declares; a
  report lists what changed per endpoint, and an endpoint whose object is gone is flagged, not wiped.
- **Filter** (above the list) - narrow the endpoints to those that require a given **scope**, and/or
  those a given **key** could reach (the key must hold every required scope and have a resource grant
  that matches the procedure, exactly as the data plane authorizes a call). The count shows how many
  of the total match, and **Export** and **OpenAPI** both honour the active filter.
- **Export** / **Import** - move endpoint definitions between environments as JSON. Export writes the
  currently filtered set.
- **OpenAPI** - download an OpenAPI 3.0 document generated from the endpoint metadata. With a key or
  scope filter active it emits only that surface (its title notes the audience), so you can hand a
  consumer exactly the endpoints their key can call.

## API keys

List keys, create a key (choosing its scopes and its procedure-access grants), and revoke keys. The
plaintext key is shown once, in a highlighted box, immediately after creation - copy it then, as it
cannot be retrieved later.

Under **Procedure access** you add grants that limit which procedures the key may call, by
connection (server + database), schema and object; use `*` for "any". Leave the list empty to allow
every procedure (still subject to scopes). See [Security](security.md) for the full model.

## Scopes

Define and delete the named permissions attached to keys and required by endpoints.

## Admins

List admin accounts and create new ones. Each account has a role: `Admin` (full access) or `Viewer`
(read-only), shown as a badge with an enabled / disabled status. An `Admin` can reset another
account's password inline. See [Security](security.md) for what each role can do.

## Account

Reached from the **Account** item in the account menu, under your username at the top right. Shows your
username and role, and lets you change your own password (any role can change their own; it verifies
your current password first).

It also manages your **access tokens** - long-lived tokens that call the admin API as you, for
scripts and CI/CD. Give a token a name and an optional expiry; the secret is shown once on creation,
so copy it then. Send it as `Authorization: Bearer weadm_...`. Revoke a token here at any time. See
[Security](security.md) for how tokens map to your role and a CI/CD example.

## Audit

A table of recent audit entries (admin sign-ins, endpoint / key / scope / admin / settings changes,
and more), newest first. Every administrative action is recorded with a detail describing exactly what
changed - a `settings.update` lists the changed fields and their old and new values, an
`endpoint.upsert` names the method, route and target object, an `endpoint.sync` summarizes the added,
updated and removed parameters, and so on.

## Logs

The **Logs** screen is the data-plane request history: one row per call, newest first, with the time,
method, route, target object, status, total duration, database time, rows returned and the calling key.
It is the tool for answering "which request was slow, and why".

- **Slow flagging** - a call is marked slow (a warning marker on its duration) when it exceeds its
  endpoint's rolling average by a threshold percentage. The threshold defaults to a global setting and
  can be overridden per endpoint. The **Slow only** and **Errors only** switches filter to those rows.
- **Drill-in** - clicking an endpoint on the **Dashboard** feed, or the **Logs** action on the
  **Endpoints** list, opens this screen already filtered to that endpoint.
- **Detail** - clicking a row opens a panel with the full timing (including the endpoint average for
  context), and, for endpoints that opt in, the captured request **parameters** and response **result**.
  Parameter and result capture is off by default (it can hold PII) and is enabled per endpoint in the
  endpoint editor's **Logging** section (which also sets the per-endpoint slow threshold). The response
  capture is size-capped.

## Settings

Runtime system settings, editable without a restart (Admin role required; changes are audited):

- **Data-plane limits** - the row cap, request timeout and table-valued-parameter row cap.
- **Response cache** - the total memory cached responses may occupy. Once full, the least recently used
  entries are evicted to make room; zero means unlimited. Changing it clears the cache.
- **Rate limiting** - the default per-key request limit for keys that set none of their own.
- **Audit** - how many days of audit history to keep (a background service prunes older entries;
  zero keeps history forever).
- **Request log** - the master on/off switch for request logging, the global slow threshold (percent
  above an endpoint's average), and how many days of request history to keep.

The screen also shows read-only, restart-required values: the request body-size cap and the file-logging
configuration (directory, rolling, retention, format, level). These are set in configuration
(`Weir:Logging`, `Weir:DataPlane`) and applied at startup. See [Configuration](configuration.md).

## Install (PWA)

The admin is an installable Progressive Web App. In a supporting browser, use the install control in
the address bar (or the browser menu) to add **Weir** as a standalone app with its own icon. When a
new version is deployed, a snackbar toast appears (bottom-right, in the app theme) offering to
**Update** - one click activates the new build and reloads onto it; an offline banner appears when the
browser loses connectivity (the app then shows the last loaded data). For the update notice to fire,
the host serves the PWA control files (`service-worker.js`, `service-worker-assets.js`,
`register-sw.js`, the manifest) with `Cache-Control: no-cache` so the browser always sees a fresh
deployment; put the same rule on any reverse proxy or CDN in front of Weir.

## Language

The console ships English and Russian. Pick the language from the **translate** menu in the top bar;
the same picker sits on the sign-in card, since login happens before there is a session. The choice is
a per-browser preference: it is kept in `localStorage` and applied before the first paint, so it
survives a reload and an offline start. With nothing stored, the console follows the browser language
and falls back to English. Switching is instant - no reload - and never touches the control plane: the
language belongs to the browser, not to the gateway, so two admins on one deployment can each read it
in their own language.

The strings live in `src/Weir.Admin/Resources/AdminStrings.resx` (English, the neutral fallback) and
`AdminStrings.ru.resx` (Russian), reached through the generated `AdminStrings` class; components that
render them derive from `LocalizedPage`, which re-renders them on a switch. To add a language: add
`AdminStrings.<code>.resx`, add the code and its native display name to
`LanguageService.SupportedCultures`, and rebuild - the satellite assembly is picked up automatically.

## Theme

The UI uses a single fixed theme: the Visual Studio 2026 geometry recoloured with the **Command Center**
palette (cyan on near-black, dark only). Flare's theme service applies it before the first paint, so
there is no flash. The top navigation and status bar stay fixed while only the content scrolls.
