# Weir

Thin, high-performance HTTP gateway over MSSQL and PostgreSQL. Call an endpoint, Weir invokes a stored
procedure or function and streams the result back as JSON. Metadata-driven, with a Blazor admin PWA.
Built on .NET 10.

This image is the whole application: the ASP.NET Core host plus the admin PWA (served from the same
origin). It connects to a SQL Server / PostgreSQL that you provide; it does not bundle a database.

## Run

```sh
docker run -p 8080:8080 \
  -e Weir__DataConnections__default__ConnectionString="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True" \
  -e Weir__Admin__Username=admin -e Weir__Admin__Password=a-strong-password \
  -e Weir__Jwt__SigningKey=a-stable-secret \
  jrfrigat/weir:latest
# Open http://localhost:8080
```

Persist the SQLite control-plane metadata (endpoints, keys, scopes, admins, audit, settings) on a volume:

```sh
-v weir-data:/data -e Weir__ControlPlane__ConnectionString="Data Source=/data/weir-control.db"
```

## Tags

- `latest` - the most recent release.
- `X.Y.Z` - a pinned version (recommended for production).

## Configuration

Everything is configured through environment variables (double underscore for nesting). At minimum set a
target connection string, a strong admin password, and a stable JWT signing key. See the full reference in
the repository.

## Links

- Source, full documentation and issues: https://github.com/jrfrigat/weir
- Also published to GHCR: `ghcr.io/jrfrigat/weir`
- License: MIT
