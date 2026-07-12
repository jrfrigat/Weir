# Weir

Weir is a thin, high-performance HTTP gateway over MSSQL and PostgreSQL: a client calls an endpoint,
Weir invokes a stored procedure or function and streams the result back as JSON. No business logic in
C# - only routing, authentication, parameter mapping, caching, telemetry and serialization.

This is one of the Weir libraries. Reference the `Weir.*` packages only if you build a custom host or
your own data-plane connector; most users just run the container image (`ghcr.io/jrfrigat/weir` or
`docker.io/frigat/weir`).

Library packages: `Weir.Contracts`, `Weir.Abstractions`, `Weir.Core`, `Weir.Diagnostics`,
`Weir.ControlPlane.Sqlite`, `Weir.ControlPlane.PostgreSql`, `Weir.ControlPlane.SqlServer`,
`Weir.Connectors.SqlServer`, `Weir.Connectors.PostgreSql`.

- Source, documentation and issues: https://github.com/jrfrigat/weir
- License: MIT
