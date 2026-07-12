# Weir

Weir is a thin, high-performance HTTP gateway over MSSQL and PostgreSQL: a client calls an endpoint,
Weir invokes a stored procedure or function and streams the result back as JSON. No business logic in
C# - only routing, authentication, parameter mapping, caching, telemetry and serialization.

This is one of the Weir libraries. Reference the `FrigaT.Weir.*` packages only if you build a custom
host or your own data-plane connector; most users just run the container image (`ghcr.io/jrfrigat/weir`
or `docker.io/frigat/weir`). The NuGet IDs are prefixed `FrigaT.Weir.*`; the assemblies and namespaces
stay `Weir.*` (for example `dotnet add package FrigaT.Weir.Abstractions` gives you `using Weir.Abstractions`).

Library packages: `FrigaT.Weir.Contracts`, `FrigaT.Weir.Abstractions`, `FrigaT.Weir.Core`,
`FrigaT.Weir.Diagnostics`, `FrigaT.Weir.ControlPlane.Sqlite`, `FrigaT.Weir.ControlPlane.PostgreSql`,
`FrigaT.Weir.ControlPlane.SqlServer`, `FrigaT.Weir.Connectors.SqlServer`, `FrigaT.Weir.Connectors.PostgreSql`.

- Source, documentation and issues: https://github.com/jrfrigat/weir
- License: MIT
