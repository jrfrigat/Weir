# Weir - Security

> [Русский](../ru/security.md) - [Configuration](configuration.md) - [Architecture](architecture.md)

Weir has two independent authentication surfaces: API keys for data-plane clients, and admin
accounts for the management UI.

## Data-plane clients: API keys

- A client authenticates with an API key in the `X-Api-Key` header or as a `Bearer` token.
- Keys look like `wk_live_<random>`. Only a SHA-256 hash and a short non-secret prefix are stored;
  the plaintext is shown once at creation and never persisted.
- Each key carries a set of scopes. An endpoint declares `RequiredScopes`; the request is allowed
  only if the key holds all of them. An endpoint with no required scopes accepts any enabled key.
- Keys can be disabled (revoked) and can carry an optional expiry.
- Resolved keys are cached in memory briefly to keep the hot path off the database.

Create and revoke keys under **API keys** in the admin UI, or via the admin API
(`POST /admin/api/keys`, `DELETE /admin/api/keys/{id}`).

## Scopes

Scopes are named permissions (e.g. `orders:read`, `orders:write`). Define them under **Scopes**,
attach them to keys, and require them on endpoints. There is no implicit hierarchy - a scope is
required exactly if the endpoint lists it.

## Procedure access (resource grants)

Beyond scopes, a key can carry **resource grants** that limit which procedures it may call, addressed
by connection (a named connection is one server + database), schema and object. Each level is a
specific value or `*` ("any"), so one grant can cover:

| Grant | Meaning |
| :-- | :-- |
| `* / * / *` | any procedure on any connection |
| `sales / * / *` | every procedure on the `sales` connection (its whole server + database) |
| `sales / dbo / *` | every procedure in the `dbo` schema of `sales` |
| `sales / dbo / GetOrders` | just that one procedure |

A key can hold many grants; a call is allowed if at least one grant matches the endpoint's
connection, schema and object. A key with **no** grants is unrestricted (still subject to scopes),
so existing keys keep working. Both checks apply: the key must satisfy the endpoint's required
scopes **and** be granted the resource. A key that is not granted the procedure receives HTTP 403.

## Admin accounts

- The admin UI is protected by local accounts. Passwords are stored with PBKDF2 (SHA-256) and must be
  at least 8 characters; the same policy applies to the bootstrap account and to accounts created or
  updated through the API.
- The first admin is bootstrapped from `Weir:Admin:Username` / `Weir:Admin:Password` on startup,
  only when no admin exists yet. The bootstrap admin holds the `Admin` role.
- Sign-in issues a short-lived JWT (default 30 minutes) plus a long-lived refresh token held by the
  browser. Set a stable `Weir:Jwt:SigningKey` so sessions survive restarts; if unset, an ephemeral key is
  generated per process and sessions reset on restart. In Production the key must be at least 32 characters.
- The refresh token is stored only as a hash, is revocable, and is rotated on each use: the admin PWA
  exchanges it (`POST /admin/api/auth/refresh`) for a fresh access token when the access token expires, and
  the used refresh token is revoked. Signing out (`POST /admin/api/auth/logout`) and a password change
  revoke refresh tokens; expired and revoked tokens are pruned in the background.
- JWTs are revocable before they expire. Each token carries a version stamped from the account, and
  every request re-checks it: changing an admin's password, or disabling the account, rejects that
  admin's outstanding tokens on the next request instead of leaving them valid until expiry.
- Failed sign-ins are throttled per source IP (not per username), so a bad-password flood cannot lock
  a real admin out; a login is verified against a decoy hash when the account is missing, so response
  times do not reveal which usernames exist. The throttle state is persisted in the control plane, so a
  lockout survives a restart and is shared across every instance in an HA deployment.
- Additional admins and password changes are managed under **Admins**.

### Roles

Admin accounts have one of two roles:

| Role | Access |
| :-- | :-- |
| `Admin` | Full access - read everything and make changes (endpoints, keys, scopes, admins). |
| `Viewer` | Read-only - dashboard, endpoints, keys and audit, but no changes. |

Every mutating admin API route requires the `Admin` role; read routes are open to any signed-in
admin. A `Viewer` who attempts a change receives HTTP 403.

## Personal access tokens (admin API)

The browser session JWT is short-lived and not meant for scripts. For automation - a CI/CD job that
syncs an endpoint's parameters on deploy, for example - an admin creates a **personal access token**
under **Account -> Access tokens** (or via `POST /admin/api/account/tokens`).

- A token looks like `weadm_<random>`. Only a SHA-256 hash and a short prefix are stored; the
  plaintext is shown once at creation and never persisted.
- A token authenticates as its owning admin, with that admin's current role. If the account is
  disabled or its role changes, the token reflects that immediately. A `Viewer`'s token is read-only.
- Tokens can carry an optional expiry and are revoked instantly (no cache) under **Account** or via
  `DELETE /admin/api/account/tokens/{id}`.
- Each admin may hold at most `Weir:Admin:MaxTokensPerAdmin` tokens (default 20). Set
  `Weir:Admin:RequireTokenExpiry` to require every token to carry an expiry.
- Send it as `Authorization: Bearer weadm_...`. Every admin API route accepts either a token or a
  login JWT.

Each admin manages only their own tokens; the token's id comes from the caller's identity, so one
admin cannot list or revoke another's tokens.

Example - update stored-procedure parameters from CI after a database migration, by re-syncing from
the database. `POST /admin/api/endpoints/sync` reconciles the parameter metadata of matching endpoints
with what the database now reports; narrow it with query filters (all `AND`-combined, case-insensitive):

```sh
# WEIR_ADMIN_TOKEN is a secret in the CI environment (weadm_...).
AUTH="Authorization: Bearer $WEIR_ADMIN_TOKEN"

# A specific database on a specific server (a named connection):
curl -fsS -X POST "$WEIR_URL/admin/api/endpoints/sync?connection=sales" -H "$AUTH"

# A specific procedure (by name, no endpoint id needed): connection + schema + object.
curl -fsS -X POST "$WEIR_URL/admin/api/endpoints/sync?connection=sales&schema=dbo&object=GetOrders" -H "$AUTH"

# Everything:
curl -fsS -X POST "$WEIR_URL/admin/api/endpoints/sync" -H "$AUTH"

# Or one endpoint by its id, when you already have it:
curl -fsS -X POST "$WEIR_URL/admin/api/endpoints/$ENDPOINT_ID/sync" -H "$AUTH"
```

The response is a JSON array with a per-endpoint `status` (`updated`, `unchanged`, `objectNotFound`
or `error`). Sync only reconciles endpoints that already exist; it does not create new ones. It
returns HTTP 200 even when nothing matched (an empty array), so a CI job that expects a change should
check the results rather than only the status code.

Example - purge cached responses from the same pipeline after a deploy reseeds data behind a cached
read, so callers see the new data without waiting out the TTL. `POST /admin/api/cache/purge` selects
endpoints with the same `AND`-combined, case-insensitive filters, plus `route` and `provider` (the
connector behind an endpoint's connection):

```sh
# Everything behind one database (a named connection):
curl -fsS -X POST "$WEIR_URL/admin/api/cache/purge?connection=sales" -H "$AUTH"

# One procedure by name, across every connection that exposes it:
curl -fsS -X POST "$WEIR_URL/admin/api/cache/purge?object=GetOrders" -H "$AUTH"

# One route, or one endpoint by its id when you already have it:
curl -fsS -X POST "$WEIR_URL/admin/api/cache/purge?route=orders/get" -H "$AUTH"
curl -fsS -X POST "$WEIR_URL/admin/api/endpoints/$ENDPOINT_ID/cache/purge" -H "$AUTH"

# Every endpoint (no filter):
curl -fsS -X POST "$WEIR_URL/admin/api/cache/purge" -H "$AUTH"
```

The response is `{ "matchedEndpoints": <n>, "purgedRoutes": [ ... ] }`. Purging clears rendered
responses only - it never changes an endpoint definition - and the cache refills on the next call.

## Audit and security logging

Every administrative mutation is audited: sign-ins, key and token create / revoke, endpoint
create / update / delete / import, cache purges, scope upsert / delete, admin create and password
reset, and settings changes - each recording the acting admin and the affected resource. Data-plane call auditing
is opt-in (`Weir:Audit:DataPlane`). The audit log is visible under **Audit** and queryable via
`GET /admin/api/audit`, and is pruned by a background service per the runtime `AuditRetentionDays`
setting (see [Configuration](configuration.md#logging)).

Security events - failed sign-ins, lockouts, scope/grant denials and rate-limit hits - are also written
to the file log (see [Configuration](configuration.md#logging)), each request carrying a correlation id.

## Hardening

- Introspection (`/admin/api/introspect/*`), which enumerates database objects, requires the `Admin`
  role; the connection-health route redacts driver error detail for non-admin (Viewer) callers.
- Data-plane guards ship with safe non-zero defaults: a row cap, a gateway timeout, a table-valued
  parameter row cap and a request body-size limit (see [Configuration](configuration.md)). An API key
  with no rate limit of its own can be given a default via `Weir:DataPlane:DefaultApiKeyRateLimitPerMinute`.
- The data-plane request log records call metadata only. Capturing a call's request **parameters** or
  its response **result** - either of which can hold PII - is off by default and opted into per
  endpoint, in the endpoint editor's **Logging** section. When on, the bound scalar parameter values
  (as JSON) and the response body (as text) are stored in the control-plane request log and shown in
  the **Logs** detail panel. Both are size-capped - roughly 64 KB of parameter JSON and 16 KB of
  response body, cut short and marked when longer - so capture cannot hold whole payloads. How long
  they are kept is governed by the runtime `RequestLogRetentionDays` setting, and `RequestLogEnabled`
  turns the request log off entirely (see
  [Configuration](configuration.md#runtime-settings)).
- Security-relevant options are validated at startup, so a misconfiguration fails fast.
- The shipped default connection string does not disable TLS certificate validation.

## Good practice

- Set strong, unique values for `Weir:Admin:Password` and `Weir:Jwt:SigningKey` in production.
- Keep data-plane connection strings out of source control; supply them via environment or a secret
  store.
- Serve Weir behind TLS. The admin UI and admin API share the host origin (no CORS surface).
- Parameter values are never logged by default, so telemetry and audit stay PII-safe.
