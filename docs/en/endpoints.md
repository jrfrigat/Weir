# Weir - Endpoints and API contract

> [Русский](../ru/endpoints.md) - [Getting Started](getting-started.md) - [Architecture](architecture.md)

An endpoint is metadata that maps an HTTP route to a stored procedure or function. Endpoints are
managed from the admin UI (or the admin API) and take effect immediately - no redeploy.

## Endpoint fields

| Field | Meaning |
| :-- | :-- |
| Route | Path under `/api`, e.g. `orders/create`. May contain captures, e.g. `orders/{id}` - see below. Unique per HTTP method. |
| HttpMethod | GET / POST / PUT / PATCH / DELETE. |
| ConnectionName | The named data connection to run against. |
| ObjectType | StoredProcedure, TableValuedFunction, or ScalarFunction. |
| Schema / ObjectName | e.g. `dbo` / `usp_CreateOrder`. |
| ResultMode | MultiRow, SingleRow, Scalar, NonQuery, or MultiResultSet (informational). |
| CommandTimeoutSeconds | Optional per-command timeout. |
| Enabled | Whether the endpoint is served. |
| SuppressMessages | Omit SQL informational (`PRINT` / notice) messages from the response envelope - `messages` is written as an empty array. Off by default (see below). |
| Cache | Result-cache policy (see below). |
| Delivery | How the response body reaches the caller (see below). |
| Logging | Request-logging policy: whether this endpoint's calls are written to the request log at all, whether to capture the request's scalar parameter values and the response body (both can hold PII, so both are off by default and capped in size), and an optional per-endpoint override of the global slow-request threshold. |
| Parameters | Parameter definitions (see below). |
| RequiredScopes | Scopes an API key must hold. Empty means any authenticated key. |
| Description | Human-readable description shown in the admin UI and generated docs. |

## Route templates

A route may capture a whole segment: `orders/{id}` matches `GET /api/orders/123` and makes `123`
available to any parameter declared with `Source = Route` and `Name = id` (the name is matched
ignoring case). A capture takes exactly one segment, so `orders/{id}` does not match
`orders/123/lines`, and it never matches an empty segment.

A capture is the whole segment or nothing: `{id}` is a capture, while `v{id}` is a literal segment
that happens to contain braces.

Resolution is deterministic. A literal route always wins over a template that would also match, so
`orders/count` and `orders/{id}` can coexist. Between two templates, the one with more literal
segments wins: `orders/{id}/lines` is tried before `orders/{id}/{part}`. Two routes that no request
could tell apart - `orders/{id}` and `orders/{orderId}` - are a collision: the later definition is
served and the gateway logs a warning at startup.

## Parameters

Each parameter declares where its value comes from and how it binds to the database.

| Field | Meaning |
| :-- | :-- |
| Name | Logical name - the JSON body property or query key the client uses. |
| DbParameterName | Database parameter name (defaults to Name). |
| Source | Body, Query, Route, Header, Claim, or Const. |
| Direction | Input, Output, InputOutput, or ReturnValue. |
| DbType | Provider-agnostic type; `Structured` denotes a table-valued parameter. |
| Required | Whether the request must supply it. |
| DefaultValue | Applied when the request omits the value. |
| Size / Precision / Scale | For sized / numeric types. |
| TypeName | For a TVP: the SQL type name, e.g. `dbo.OrderItemType`. |
| TableColumns | For a TVP: the ordered column schema. |
| ValidationRegex | Optional regular expression the value must match. |

## Request

The request body is a flat JSON object whose keys are the logical parameter names. Scalars are
values; a table-valued parameter is an array of objects. Cache and timeouts are configured
server-side, not passed by the client.

```http
POST /api/orders/create
X-Api-Key: wk_live_9f3a...
Content-Type: application/json

{
  "customerId": 42,
  "comment": "rush",
  "items": [
    { "sku": "A1", "qty": 2 },
    { "sku": "B2", "qty": 5 }
  ]
}
```

For GET endpoints, parameters come from the query string:
`GET /api/customers/get?customerId=42`.

## Response

Every successful response is one consistent envelope. `data` is an array of result sets (an array
of arrays); `messages` carries SQL `PRINT` / informational messages.

```json
{
  "data": [
    [ { "id": 1001, "status": "created" } ]
  ],
  "output": { "newOrderId": 1001, "totalCount": 57 },
  "returnValue": 0,
  "rowsAffected": 1,
  "truncated": false,
  "messages": [
    { "text": "Order created", "severity": 0, "number": 50000, "procedure": "usp_CreateOrder", "line": 12 }
  ]
}
```

- A single result set is `"data": [ [ ... ] ]`; an empty result set is `"data": [ [] ]`.
- `output` holds output / input-output parameter values by logical name, or null if there are none.
- `returnValue` is the procedure RETURN value, or null.
- `truncated` is `true` when the result hit the configured `Weir:DataPlane:MaxRows` cap and was cut
  short (otherwise `false`).
- A SQL error (severity >= 11 or `THROW`) becomes an HTTP 4xx/5xx `application/problem+json` (RFC 7807);
  low-severity `PRINT` / info messages appear in `messages` on success.
- `messages` can be suppressed per endpoint. Turn on **Suppress SQL messages** in the endpoint editor
  (the `SuppressMessages` flag) and the array is always empty, so chatty diagnostics from a procedure
  never reach callers. The property stays present so the envelope shape does not change.

## Table-valued parameters (TVP)

Declare a parameter with `DbType = Structured`, set `TypeName` to the SQL TVP type, and define
`TableColumns`. The client sends an array of objects; keys are matched to the column names.

## Output parameters and return value

Declare parameters with direction `Output` or `InputOutput`; their values appear in `output` after
execution. For stored procedures the integer RETURN value is always captured in `returnValue`.

## Response delivery

Weir can write rows out as it reads them, or build the whole envelope and then send it. The bytes are
identical either way; what differs is memory and what a failure looks like.

Streaming keeps memory flat regardless of result size and starts sending before the query finishes.
The cost is that a response, once started, cannot be taken back: if the database fails part-way
through a result set, Weir has already sent an opening `{"data":[[...` and can only abort the
connection. Buffering holds the whole body in memory first, which means a failure at any point still
returns a clean `400 application/problem+json` - the caller gets a complete response or an honest
error, never half of one.

| Mode | Behaviour |
| :-- | :-- |
| Auto | Buffer when `ResultMode` is `SingleRow`, `Scalar` or `NonQuery`; stream otherwise. The default. |
| Stream | Always stream. |
| Full | Always buffer. |

`Auto` is the default because the trade resolves itself for most endpoints: where the endpoint says
the result is small, atomic errors cost an amount of memory not worth counting, and where it says rows
are coming, streaming is the point. `ResultMode` is a declaration rather than a guarantee, so an
endpoint labelled `SingleRow` that returns thousands of rows will buffer them all - that costs memory,
not correctness, and such an endpoint can set `Stream` outright.

Two settings control it, both live-editable in the admin panel:

- `ResponseDeliveryMode` and `ResponseFlushBytes` in **Settings**, applying to every endpoint.
- `Delivery.Mode` and `Delivery.FlushBytes` on an **endpoint**, each overriding its setting. Leave
  them empty (null) to follow the system, which is what almost every endpoint should do.

`FlushBytes` is how many bytes may pile up before a streaming response pushes them out. The default,
32768, is chosen rather than round: comfortably above one typical row, so narrow rows batch instead of
costing a write each, and comfortably below the 85 KB large-object-heap threshold, so the writer's
buffer stays a cheap reusable array. Raising it past that threshold undoes the point of flushing;
lowering it far trades the gain for a write per row. It self-adjusts otherwise - a wide row crosses it
on its own.

**The mode does not apply to every endpoint.** One that caches its responses, or captures its result
for the request log, needs the whole body before it can do either - there is nothing to store or log
until it exists - so it buffers whatever the mode says. This is not the mode being overridden so much
as already satisfied. The admin panel says so next to the field when it applies.

## Caching

Set a per-endpoint cache policy: `Enabled`, `TtlSeconds`, `VaryByParameters` (the input parameters
whose values form the cache key), `VaryByApiKey`, and `CoalesceRequests`. The rendered JSON is cached
in memory; clients cannot bypass it. Caching is best suited to read-only endpoints. Editing or deleting an endpoint
evicts its cached responses immediately, so a change never serves stale data. You can also force a
purge at any time - per endpoint from the admin UI, or by filter over the admin API for CI/CD (see
[Purging the cache](#purging-the-cache) below).

Cache-eligible `GET` responses carry HTTP cache validators: a strong `ETag` derived from the exact
response bytes and `Cache-Control: private, max-age=<TtlSeconds>`. A client that re-requests with
`If-None-Match: <etag>` (or `*`) receives `304 Not Modified` with no body when the cached response is
unchanged, saving the transfer. `304` is answered before any bytes are streamed. A truncated response
(one that hit the `MaxRows` cap) is never cached and carries no `ETag`, so a partial body is never
revalidated against a complete one.

### Concurrent calls for the same key

`CoalesceRequests` (on by default) decides what happens when calls for the same cache key arrive while
a response for it is already being produced - on a cold key, or the moment one expires. With it on, the
first caller runs the procedure and the rest wait for its bytes, so the burst costs one database call
instead of one per caller. That is the moment a cache helps least on its own, because none of them can
hit it yet.

The query belongs to everyone waiting on it, not to whoever arrived first. A client hanging up, or
hitting the gateway timeout, ends that caller's request and nothing else - the query keeps running for
the callers still queued behind it. It is dropped only once the last of them has gone, so it lives
exactly as long as somebody still wants the answer. A caller served this way is counted as a cache hit
(it did not touch the database) and its reported duration includes its wait, which is the latency its
client actually saw.

Turn it off for an endpoint that must run its procedure for every call even while it is cached, or as
an escape hatch. Caching must be on for the setting to mean anything: the cache key is what identifies
two calls as asking the same question, so without one there is nothing to wait on.

## Managing endpoints via the admin API

Endpoints are managed under **Endpoints** in the admin UI, or over the admin API at
`/admin/api/endpoints`. Admin API calls authenticate with a login JWT or a personal access token
(`Authorization: Bearer ...`); see [Security](security.md) for tokens and CI/CD usage. Every mutating
route requires the `Admin` role.

| Action | Route |
| :-- | :-- |
| List all endpoints | `GET /admin/api/endpoints` |
| Get one | `GET /admin/api/endpoints/{id}` |
| Create or update | `POST /admin/api/endpoints` / `PUT /admin/api/endpoints/{id}` |
| Delete | `DELETE /admin/api/endpoints/{id}` |
| Import a set (upsert by id) | `POST /admin/api/endpoints/import` |
| Run one (admin "try it") | `POST /admin/api/endpoints/{id}/invoke` |
| Generate an OpenAPI 3.0 document | `GET /admin/api/openapi.json` |

A create or update reloads the in-memory catalog immediately; one that would duplicate an existing
method + route returns HTTP 409.

### Syncing parameters from the database

After a migration changes a procedure's signature, re-sync so an endpoint's parameter definitions
match what the database now reports. Sync reconciles the `Parameters` metadata only; it does not
change the procedure, and it updates only endpoints that already exist (it does not create new ones).

| Scope | Route |
| :-- | :-- |
| One endpoint by id | `POST /admin/api/endpoints/{id}/sync` |
| A specific database on a server (a named connection) | `POST /admin/api/endpoints/sync?connection=sales` |
| A whole schema | `POST /admin/api/endpoints/sync?connection=sales&schema=dbo` |
| A single procedure by name | `POST /admin/api/endpoints/sync?connection=sales&schema=dbo&object=GetOrders` |
| Every endpoint | `POST /admin/api/endpoints/sync` |

The `connection`, `schema` and `object` filters combine with AND and are case-insensitive. The bulk
route returns HTTP 200 with a JSON array of per-endpoint results, each with a `status`:

| Status | Meaning |
| :-- | :-- |
| `updated` | Parameters changed and were saved. |
| `unchanged` | Already in sync; nothing written. |
| `objectNotFound` | The endpoint's procedure was not found on its connection. |
| `error` | Sync failed for this endpoint; the result message explains why. |

An empty array means nothing matched the filter, so a CI/CD job that expects a change should inspect
the results, not only the status code. For a full CI/CD example using a personal access token, see
[Security](security.md).

### Purging the cache

Caching normally expires on its own TTL, and editing or deleting an endpoint already evicts its cached
responses. When a deployment changes the data behind a cached read - a migration reseeds a table, a
job rewrites reference data - force a purge so callers see the new data at once instead of waiting out
the TTL. Purging clears already-rendered responses only; it never changes an endpoint definition, and
the cache refills on the next call.

From the admin UI, the **Purge cache** action on a cache-enabled endpoint's row clears that endpoint.
Over the admin API (a login JWT or a personal access token, `Admin` role), one route covers both a
single endpoint and pipeline-friendly bulk invalidation:

| Scope | Route |
| :-- | :-- |
| One endpoint by id | `POST /admin/api/endpoints/{id}/cache/purge` |
| One route | `POST /admin/api/cache/purge?route=orders/get` |
| One procedure by name (across connections) | `POST /admin/api/cache/purge?object=GetOrders` |
| A specific database on a server (a named connection) | `POST /admin/api/cache/purge?connection=sales` |
| A whole schema | `POST /admin/api/cache/purge?connection=sales&schema=dbo` |
| Every endpoint on a provider (connector) | `POST /admin/api/cache/purge?provider=SqlServer` |
| Every endpoint | `POST /admin/api/cache/purge` |

The `route`, `connection`, `schema`, `object` and `provider` filters combine with AND and are
case-insensitive; `provider` matches the connector behind an endpoint's connection (for example
`SqlServer` or `PostgreSql`). With no filter, every endpoint's cache is purged. Both routes return
HTTP 200 with `{ "matchedEndpoints": <n>, "purgedRoutes": [ ... ] }`, where `purgedRoutes` lists the
distinct routes that were cleared. Every purge is written to the audit log under `cache.purge`.
