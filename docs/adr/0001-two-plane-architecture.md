# ADR 0001 - Two-plane, metadata-driven architecture

- Status: Accepted
- Date: 2026-07-10

## Context

Weir exposes HTTP endpoints that each invoke a SQL Server stored procedure or function and return
JSON. Requirements gathered during design:

1. Endpoints are **dynamic**, managed from an admin UI, with no redeploy to add/change one.
2. **Stored procedures / functions only** - never ad-hoc SQL.
3. **Thin and fast** - minimal overhead per request.
4. Support **multiple servers / databases / schemas** simultaneously.
5. Data-plane drivers must be **separate packages** (start with MSSQL only, keep the abstraction).
6. **API-key** auth with per-endpoint scopes; admin PWA issues keys.
7. Control-plane metadata lives in a **separate store** (SQLite now, provider-abstracted).
8. Table-valued parameters, output parameters and return values must be supported.
9. Per-endpoint **result caching**, configured from the admin UI.
10. **Telemetry** hooks plus an Aspire-style dashboard in the PWA (built on the Flare component lib).

## Decision

- Split the system into a **data plane** (the hot request path) and a **control plane** (Weir's own
  metadata), each with its own storage and lifecycle.
- Drive endpoints from **metadata** resolved into an in-memory snapshot; refresh on change.
- Use **ASP.NET Core Minimal APIs + Dapper**, streaming result sets directly from DbDataReader to
  the JSON response. No ORM on the hot path.
- Define narrow **ports** in Weir.Abstractions (IDbConnector, IControlPlaneStore, IWeirCallObserver,
  IMetricsAggregator, IResponseCache) and ship concrete providers as independent packages.
- Share wire/DTO types via a browser-safe **Weir.Contracts** package so the WASM admin and the host
  use one definition without the admin taking a dependency on server-only ports.
- Response is **one consistent envelope**: data (array of result sets), output, returnValue,
  rowsAffected, messages. Errors use RFC 7807 problem+json.
- Telemetry on **OpenTelemetry** (OTLP-exportable, Aspire-compatible) plus an in-process aggregator
  for a zero-infrastructure dashboard.

## Consequences

- Adding a database engine equals one new Weir.Connectors.* package implementing IDbConnector.
- Adding a control-plane backend equals one new IControlPlaneStore implementation plus migrations.
- The admin PWA and host never drift on DTOs (single Weir.Contracts).
- Streaming plus "always-envelope" means the writer emits data first, then appends output params /
  messages once the reader is drained (ADO.NET populates those only after the reader closes).
- Leaking System.Data.Common.DbDataReader through IDbConnector is accepted: it is the common,
  provider-agnostic streaming primitive and keeps the hot path allocation-light.
