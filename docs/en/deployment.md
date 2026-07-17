# Weir - Deployment

> [Русский](../ru/deployment.md) - [Configuration](configuration.md) - [Getting Started](getting-started.md)

Weir is a single deployable: one ASP.NET Core host that serves the JSON API and the Blazor WASM
admin PWA from the same origin. It targets .NET 10.

## Docker image

A multi-stage `build/Dockerfile` builds the whole app on the .NET 10 SDK image and runs it on the
`aspnet:10.0` runtime. The publish step also bundles the WASM admin as static assets.

### Published image (GHCR)

Each release (a pushed `v*` tag) publishes the host image to the GitHub Container Registry, tagged with
the version and `latest`. Pull it instead of building:

```sh
docker pull ghcr.io/jrfrigat/weir:latest        # or a pinned ghcr.io/jrfrigat/weir:X.Y.Z
docker run -p 8080:8080 \
  -e Weir__DataConnections__default__ConnectionString="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True" \
  -e Weir__Admin__Username=admin -e Weir__Admin__Password=a-strong-password \
  -e Weir__Jwt__SigningKey=a-stable-secret \
  ghcr.io/jrfrigat/weir:latest
```

Pin a version tag in production; `latest` is a moving target. To build the image yourself instead:

```sh
docker build -f build/Dockerfile -t weir:latest .
docker run -p 8080:8080 \
  -e Weir__DataConnections__default__ConnectionString="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True" \
  -e Weir__Admin__Username=admin -e Weir__Admin__Password=a-strong-password \
  -e Weir__Jwt__SigningKey=a-stable-secret \
  weir:latest
# Open http://localhost:8080
```

The container listens on port 8080. Mount a volume for the SQLite control-plane if you want its
metadata (endpoints, keys, scopes, admins, audit, settings) to persist across container recreation,
and point `Weir__ControlPlane__ConnectionString` at it, e.g. `Data Source=/data/weir-control.db` with
a volume mounted at `/data`.

Weir writes rolling log files to `Weir:Logging:Directory` (default `logs`, relative to the content
root). Mount a volume there, or set the directory to a mounted path, if you want logs to survive
container recreation; otherwise ship them to your platform's log pipeline via the console sink.

## docker-compose

`docker-compose.yml` is a template that runs the Weir host and connects to an **external** SQL Server
that you provide. Machine-specific settings - the target connection string, admin credentials and the
JWT signing key - live in `docker-compose.override.yml`, which Compose merges automatically. That
override file is git-ignored; edit it with your values.

```sh
docker compose up -d --build
# Windows: run-docker-compose.bat
# Open http://localhost:8080
```

The control-plane SQLite is persisted in the `weir-data` volume.

> `Trusted_Connection=True` (Windows Integrated Authentication) does not work from inside a Linux
> container - the container has no Windows identity. To reach SQL Server from the container, use SQL
> authentication (`User Id` / `Password`) in the connection string, or run the host directly on
> Windows with `dotnet run --project src/Weir.Host`, where Trusted_Connection works as-is.

## Configuration

Supply settings via environment variables (see [Configuration](configuration.md)). At minimum set a
target `Weir:DataConnections:{name}:ConnectionString`, a strong `Weir:Admin:Password`, and a stable
`Weir:Jwt:SigningKey`.

## Behind a reverse proxy / TLS

Serve Weir behind a reverse proxy that terminates TLS. The admin UI and admin API share the host
origin, so no CORS configuration is required. Leave `Weir:Security:RequireHttps` off when the proxy
does TLS (its default). Weir sends HSTS and hardening headers itself.

**Name the proxy, or the sign-in throttle protects nothing.** The socket Weir sees belongs to the
proxy, not the caller, and the throttle keys on the caller's address (as does the security log). Left
unset, every admin in the world shares one bucket: five bad passwords from an anonymous caller lock
everyone out for `Weir:Admin:LockoutMinutes`, repeatable indefinitely, and brute force is barely
slowed because all attackers share the bucket too. List the proxies you run:

```json
{
  "Weir": {
    "Network": {
      "TrustedProxies": [ "10.0.0.0/8", "172.18.0.5" ],
      "ForwardLimit": 1
    }
  }
}
```

Entries are single addresses or CIDR networks, and a malformed one fails startup rather than quietly
leaving the throttle keyed on the proxy. `ForwardLimit` is how many hops to walk back through
`X-Forwarded-For`; 1 matches a single proxy. Raise it only for a real chain of proxies you trust end
to end - each extra hop is one more entry a caller could have forged if it is not.

Weir ignores `X-Forwarded-For` until this is set, and that default is deliberate: the header is
caller-supplied, so honouring it from an untrusted source is worse than ignoring it - an attacker
would put a fresh address in every request and never be throttled at all. Only list proxies you
control.

Setting it also forwards the scheme, which is what lets `Weir:Security:RequireHttps` work behind a
TLS-terminating proxy (without it the proxy's plain-HTTP hop to Weir redirects forever) and keeps
`https://` in the generated OpenAPI server URL.

## Health probes

- `/health/live` - liveness (process is up; no dependency checks). Safe for a Kubernetes liveness
  probe - a downstream database blip will not restart the pod.
- `/health/ready` - readiness (control plane reachable, data connections probed).
- `/health` - the aggregate, for humans and simple setups.

## High availability

Run several instances behind a load balancer:

- Point every instance at one shared control plane (`Weir:ControlPlane:Provider=Postgres` or `SqlServer`). The SQLite
  control plane is single-node; do not run several instances against per-node SQLite files.
- Set `Weir:HighAvailability=true` on every instance. This is an assertion that the deployment is
  multi-instance: the host then refuses to start on the SQLite control plane (which would give each node
  a divergent, private copy of the metadata) and requires the shared PostgreSQL control plane instead.
- Set a stable `Weir:Jwt:SigningKey` on every instance (required in Production) so a token issued by
  one instance is accepted by the others. The refresh-token store is in the shared control plane, so a
  session renews and revokes consistently across instances.
- Set `Weir:ControlPlane:ReloadSeconds` (e.g. `30`) so metadata changes made on one instance reach
  the others. This is also what carries cache eviction between them: on each reload an instance drops
  the cached responses of every route whose definition changed, so the interval you set here is the
  longest an edit made elsewhere can still be answered from an old body. Leaving it at zero in a
  multi-instance deployment means edits never reach the other instances at all.
- Set `Weir:RateLimit:RedisConnectionString` so the per-API-key rate limit is shared across instances.
  Without it the in-memory limiter enforces the limit per instance, so the effective limit is N x the
  configured value in an N-instance deployment.
- The response cache is per-instance (in-memory), so a cache miss on one instance is independent of
  the others: that part only costs hit ratio. Eviction is the part that matters, and it is shared. The
  instance that serves an edit or a purge evicts at once; every other instance evicts on its next
  catalog reload, so `ReloadSeconds` bounds the staleness rather than the TTL. That covers an explicit
  purge too (the admin **Purge cache** action and `POST /admin/api/cache/purge`), which is recorded in
  the control plane precisely because it changes no metadata for the reload to notice otherwise - so
  one call through the load balancer purges the fleet.

### Cross-instance metrics

The built-in metrics dashboard (the admin **Dashboard**, backed by the in-memory aggregator) reports the
metrics of the single instance that served the request; it is intentionally infrastructure-free and is
not aggregated across instances. For a fleet-wide view, export OpenTelemetry to a metrics backend:
every instance emits the same `Weir` meter (and, for PostgreSQL, the `Npgsql` connection-pool meter), so
set `OTEL_EXPORTER_OTLP_ENDPOINT` and let Prometheus / the OTLP backend aggregate across instances (sum
counters, merge histograms for true fleet-wide percentiles). In an HA deployment that external backend is
the source of truth for cross-instance metrics; the per-instance dashboard is a convenience for a single
node.

## Notes

- Startup runs the control-plane migrations, bootstraps the admin (if configured and none exists),
  and loads the endpoint catalog. It does not require the target database to be reachable at startup;
  data connections are used lazily on endpoint calls.
