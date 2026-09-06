# Weir - Transports

> [Русский](../ru/transports.md) - [Endpoints](endpoints.md) - [Security](security.md)

An endpoint says which front doors serve it. There are three, and an endpoint may name any combination
of them:

| Transport | Where | For |
| :-- | :-- | :-- |
| HTTP | `POST /api/{route}` | Anything. The default, and the only one an endpoint gets unless you say otherwise. |
| gRPC | service `weir.v1.WeirGateway` | A service caller that wants HTTP/2 multiplexing, real server streaming and a generated client. |
| WebSocket | `GET /ws` | A client that makes many small calls and does not want to pay for a connection and a key lookup on each. |

A transport decides framing and how the API key travels. **Nothing else varies by door**: the same
parameter binding, the same response envelope, the same required scopes and resource grants, the same
rate limit, the same request log and audit entry. A call that HTTP would refuse is refused identically
over gRPC and over a WebSocket, and the reverse - an endpoint that does not name a transport answers
404 there, whatever its route table says.

Set it per endpoint in the admin UI (Endpoints -> edit -> Transports), or through the admin API:

```json
{ "route": "reports/sales", "httpMethod": "POST", "transports": 5 }
```

`transports` is a flags value: `1` HTTP, `2` gRPC, `4` WebSocket - so `5` is HTTP and WebSocket, `7` is
all three. `0` is refused when the endpoint is saved: an endpoint reachable over nothing is a mistake,
and `Enabled: false` is how you take one out of service.

## HTTP

Unchanged, and documented in [Endpoints and API contract](endpoints.md). An endpoint that does not name
HTTP is left out of the generated OpenAPI document as well - describing it would hand a generated client
a route that answers 404.

## WebSocket

One session, one API key, one request per frame.

```text
GET /ws
X-Api-Key: wk_live_...
```

The key is checked on the handshake, so an unauthenticated caller never gets a socket. The session
inherits that one key: every call on it is authorized, rate-limited and logged as that key.

**Request frame** (text, JSON):

```json
{ "id": 1, "route": "orders/42", "method": "GET", "body": { "note": "hello" }, "query": { "page": "2" } }
```

| Field | Meaning |
| :-- | :-- |
| `id` | Optional. Echoed back verbatim - any JSON value - so replies can be matched to requests. |
| `route` | Required. The route beneath `/api`, with or without a leading slash. |
| `method` | The HTTP method the endpoint is registered under. An endpoint's identity is method plus route, so a route registered under GET needs `"method": "GET"` here. Defaults to `POST`. |
| `body` | The flat JSON object whose members are the procedure's parameters. |
| `query` | Values for query-sourced parameters - what would follow a `?` over HTTP. |

Header-sourced parameters read the **handshake** headers, which are fixed for the life of the session.

**Reply frame** (text, JSON), one per request, in request order:

```json
{ "id": 1, "body": { "data": [[ ... ]], "output": null, "returnValue": 0, "rowsAffected": -1, "truncated": false, "messages": [] }, "status": 200 }
```

`status` is last on purpose: the body is streamed out as the rows are read, so the status is written
when it is actually known rather than promised in advance.

A failure that happens before any rows are sent replaces the body with an error:

```json
{ "id": 1, "status": 404, "error": { "title": "Endpoint not found", "detail": "No endpoint is mapped to GET /nope." } }
```

The status codes are the HTTP ones, and they mean what they mean on `/api`: 400 invalid parameters, 403
not entitled, 404 no such route (or not served over this transport), 429 rate-limited, 503 the
connection is unavailable, 504 the gateway timeout fired.

Two things follow from the protocol rather than from a choice:

- **A session is ordered.** One request is answered before the next is read. A WebSocket message cannot
  be interleaved with another on the same connection, so a "concurrent" reply would have to be buffered
  whole - which is the cost this transport exists to avoid. A caller that wants two calls in flight
  opens two sessions.
- **A failure part-way through a body closes the session** (close status 1011). Rows are already on the
  wire and no ending can make that message valid JSON, so the connection ends the way a streamed HTTP
  response aborts - the caller can tell a truncated answer from a complete one.

A frame larger than `Weir:DataPlane:MaxRequestBodyBytes` closes the session with 1009 (message too big):
one door must not accept what the other refuses.

Browsers cannot set headers on a WebSocket handshake, so the browser is not the audience here - a
browser calls `/api`. This door is for service clients.

## gRPC

The service is `weir.v1.WeirGateway`; the contract is [`src/Weir.Host/Protos/weir.proto`](../../src/Weir.Host/Protos/weir.proto),
which is all a client needs.

```proto
service WeirGateway {
  rpc Invoke(InvokeRequest) returns (InvokeReply);
  rpc InvokeStream(InvokeRequest) returns (stream InvokeChunk);
}
```

`Invoke` returns the whole envelope in one message. `InvokeStream` sends it in chunks as the rows are
read; concatenating the chunks in order gives exactly the bytes `Invoke` would have returned - the split
is a property of the wire, not of the answer.

The payload is the same JSON envelope, not a generated message per endpoint, and that is a decision
rather than a shortcut: Weir's endpoints are metadata an operator adds at run time, so there is nothing
to generate a message from at build time, and a gateway that needed a redeploy per endpoint would defeat
its own purpose. What gRPC contributes here is the transport.

The API key travels in call metadata:

```csharp
var channel = GrpcChannel.ForAddress("https://weir.example.com");
var client = new WeirGateway.WeirGatewayClient(channel);
var metadata = new Metadata { { "x-api-key", "wk_live_..." } };

var reply = await client.InvokeAsync(new InvokeRequest { Route = "orders/42", Method = "GET" }, metadata);
var envelope = Encoding.UTF8.GetString(reply.Envelope.ToByteArray());
```

Failures come back as gRPC statuses, mapped from the ones above: `NOT_FOUND`, `PERMISSION_DENIED`,
`INVALID_ARGUMENT`, `RESOURCE_EXHAUSTED`, `UNAVAILABLE`, `DEADLINE_EXCEEDED`, `UNAUTHENTICATED`,
`INTERNAL`. The detail carries the HTTP-shaped code and message, which is what the logs and the audit
record, so one event reads the same in both places.

Header-sourced parameters read the call metadata (keys are matched case-insensitively, because gRPC
normalizes them to lower case on the wire).

### gRPC needs HTTP/2, and that has one deployment consequence

Over **TLS** there is nothing to do: ALPN negotiates HTTP/1.1 and HTTP/2 on the same port, so gRPC, the
data plane, the WebSocket door and the admin PWA all share one address. This is the production setup.

Over **cleartext** they cannot share a port: without ALPN there is no negotiation, and Kestrel serves
an HTTP endpoint as HTTP/1.1 (a client that tries HTTP/2 there is refused with `HTTP_1_1_REQUIRED`).
Give gRPC its own port with Kestrel's own configuration - no Weir setting is involved:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http":  { "Url": "http://0.0.0.0:5000" },
      "Grpc":  { "Url": "http://0.0.0.0:5001", "Protocols": "Http2" }
    }
  }
}
```

Then `/api`, `/ws` and the admin UI are on 5000 and gRPC is on 5001. The same fields as environment
variables: `Kestrel__Endpoints__Grpc__Url` and `Kestrel__Endpoints__Grpc__Protocols`.

## Which door for which caller

- A browser, a script, curl, anything that reads an OpenAPI document: **HTTP**.
- A service that calls a handful of endpoints thousands of times a minute: **WebSocket**, for the
  session, or **gRPC**, if it also wants multiplexing and a generated client.
- A job that reads a very large result set: **gRPC streaming**, which ends a failed stream with a status
  instead of a truncated body.
- Anything that must not be reachable from a browser at all: leave HTTP off and name only the others.
