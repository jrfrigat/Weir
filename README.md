<p align="center">
  <img src="assets/banner.svg" alt="Weir - high-performance HTTP gateway; your database business logic, as a service" width="760">
</p>

<p align="center"><b>English</b> - <a href="README.ru.md">Русский</a></p>

<p align="center">
  <a href="https://github.com/jrfrigat/weir/actions/workflows/ci.yml"><img src="https://github.com/jrfrigat/weir/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4.svg" alt=".NET 10">
  <a href="https://hub.docker.com/r/frigat/weir"><img src="https://img.shields.io/docker/pulls/frigat/weir?logo=docker&label=Docker%20pulls" alt="Docker Pulls"></a>
</p>

# Weir

**Weir** is a thin, high-performance HTTP gateway over MSSQL (and, later, other RDBMS):
a client calls an endpoint, Weir invokes a **stored procedure or function** and streams the
result back as JSON. No business logic in C# - only routing, authentication, parameter
mapping, caching, telemetry and serialization.

## Principles

- **Stored procedures / functions only.** Weir never issues ad-hoc SQL.
- **Metadata-driven.** Endpoints are defined as metadata and managed from the admin PWA - no
  redeploy to add or change an endpoint.
- **Thin and fast.** ASP.NET Core Minimal APIs, Dapper, result sets streamed straight from the
  DbDataReader into the JSON response. No ORM, no per-request allocation churn on the hot path.
- **Multi-target.** One instance can route to many servers / databases / schemas at once via
  named connections. Data-plane drivers ship as independent packages - reference only what you use.
- **Observable.** OpenTelemetry metrics and traces, structured Serilog file logging with retention,
  plus a built-in in-memory aggregator that powers a real-time (SignalR) dashboard in the admin PWA.
- **Operable.** Runtime settings (data-plane limits, rate limits, audit retention) are edited from the
  admin panel without a restart; every admin action is audited; admin sessions are revocable; the admin
  is an installable PWA. It ships English and Russian, chosen per browser - see
  [Admin UI](docs/en/admin-ui.md#language).

## Two planes

| Plane | Concern | Storage |
|-------|---------|---------|
| **Data plane** | The hot request path: auth, bind params, execute SP, stream JSON | Target databases (MSSQL) |
| **Control plane** | Weir's own metadata: endpoints, API keys, scopes, admin users, audit, settings | Separate store (SQLite, PostgreSQL, or SQL Server; provider-abstracted) |

## Solution layout

```
src/
  Weir.Contracts/            DTOs and enums shared by host and admin (browser-safe)
  Weir.Abstractions/         Server ports: IDbConnector, IControlPlaneStore, telemetry, cache
  Weir.Core/                 Engine: endpoint resolution, param binding, JSON streaming, cache
  Weir.Diagnostics/          Telemetry: ActivitySource, Meter, in-memory metrics aggregator
  Weir.ControlPlane.Sqlite/  Default control-plane store (plus idempotent migrations)
  Weir.ControlPlane.PostgreSql/  Shared control-plane store for high-availability deployments
  Weir.ControlPlane.SqlServer/   Shared control-plane store on SQL Server (also HA-capable)
  connectors/
    Weir.Connectors.SqlServer/    IDbConnector for SQL Server (Microsoft.Data.SqlClient + Dapper)
    Weir.Connectors.PostgreSql/   IDbConnector for PostgreSQL (Npgsql)
  Weir.Host/                 ASP.NET Core host: dynamic routes, API-key auth, admin API, serves PWA
  Weir.Admin/                Blazor WASM PWA admin and dashboard (built on Flare)
tests/
docs/                        en/ and ru/ documentation
build/                       Dockerfile, CI
samples/                     Example schemas + endpoint seeds, and a CLI client / load tester
```

## API contract (summary)

**Request** - flat JSON body equals the stored-procedure parameters; TVPs are arrays of objects.
Caching is configured server-side per endpoint (TTL plus which input parameters form the key).

```http
POST /api/orders/create
X-Api-Key: wk_live_9f3a...
Content-Type: application/json

{ "customerId": 42, "items": [ { "sku": "A1", "qty": 2 } ] }
```

**Response** - one consistent envelope. "data" is an array of result sets (array of arrays);
"messages" carries SQL PRINT / info messages.

```json
{
  "data": [ [ { "id": 1001, "status": "created" } ] ],
  "output": { "newOrderId": 1001, "totalCount": 57 },
  "returnValue": 0,
  "rowsAffected": 1,
  "truncated": false,
  "messages": [ { "text": "Order created", "severity": 0, "number": 50000, "line": 12 } ]
}
```

Errors use **RFC 7807** application/problem+json.

## Documentation

Full docs live in **[docs/](docs/README.md)** (English and Russian):

| Doc | Description |
| :-- | :-- |
| [Getting Started](docs/en/getting-started.md) | Run Weir, configure a connection, first endpoint |
| [Architecture](docs/en/architecture.md) | Two planes, module map, request lifecycle |
| [Endpoints and API contract](docs/en/endpoints.md) | Parameters, TVP, request/response envelope |
| [Security](docs/en/security.md) | API keys and scopes, admin accounts and JWT |
| [Configuration](docs/en/configuration.md) | Every setting and environment variable |
| [Deployment](docs/en/deployment.md) | Docker image and docker-compose |
| [Admin UI](docs/en/admin-ui.md) | Dashboard and management pages |
| [Roadmap](docs/en/roadmap.md) | The code review that shaped Weir, and the phased plan from it |

## Install

Two things ship from a release (a pushed `v*` tag):

**Container image** - the whole application (host + admin PWA), on the GitHub Container Registry:

```sh
docker pull ghcr.io/jrfrigat/weir:latest      # or a pinned :X.Y.Z tag
docker run -p 8080:8080 \
  -e Weir__DataConnections__default__ConnectionString="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True" \
  -e Weir__Admin__Username=admin -e Weir__Admin__Password=a-strong-password \
  -e Weir__Jwt__SigningKey=a-stable-secret \
  ghcr.io/jrfrigat/weir:latest
# Open http://localhost:8080
```

See [Deployment](docs/en/deployment.md) for volumes, compose and high-availability.

**NuGet libraries** - reference these only if you build a custom host or your own data-plane connector
(most users just run the image). Published to NuGet.org under the `FrigaT.Weir.*` prefix (the assemblies
and namespaces stay `Weir.*`): `FrigaT.Weir.Contracts`, `FrigaT.Weir.Abstractions`, `FrigaT.Weir.Core`,
`FrigaT.Weir.Diagnostics`, `FrigaT.Weir.ControlPlane.Sqlite`, `FrigaT.Weir.ControlPlane.PostgreSql`,
`FrigaT.Weir.ControlPlane.SqlServer`, `FrigaT.Weir.Connectors.SqlServer`, `FrigaT.Weir.Connectors.PostgreSql`.

```sh
dotnet add package FrigaT.Weir.Abstractions   # implement IDbConnector / IControlPlaneStore (namespace: Weir.Abstractions)
```

See [Extending](docs/en/extending.md) for writing a connector or plugin.

## Build and run

```sh
dotnet build
dotnet test
dotnet run --project src/Weir.Host
```

Requires the **.NET 10** SDK. Or run Weir in Docker (it connects to a SQL Server you provide; compose does not start one):

```sh
docker compose up -d --build   # Windows: run-docker-compose.bat
# Open http://localhost:8080
```

## Contributing and security

- [CONTRIBUTING.md](CONTRIBUTING.md) - build, conventions, workflow
- [SECURITY.md](SECURITY.md) - reporting vulnerabilities
- [CHANGELOG.md](CHANGELOG.md) - release notes

## Acknowledgements

The admin PWA is built on [Flare](https://github.com/jrfrigat/Flare)

## License

[MIT](LICENSE) (c) 2026 FrigaT
