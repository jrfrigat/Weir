# Weir - Getting Started

> [Russian / Russkiy](../ru/getting-started.md) - [README](../../README.md) - [Architecture](architecture.md)

Weir is a thin HTTP gateway over MSSQL: a client calls an endpoint, Weir invokes a stored procedure
or function and streams the result back as JSON. This guide gets a local instance running and serves
your first endpoint.

## Requirements

- .NET 10 SDK (to build and run from source), or Docker (to run the container / compose stack).
- A reachable SQL Server instance with the stored procedures you want to expose.

## 1. Run it

From source:

```sh
dotnet run --project src/Weir.Host
```

Or with Docker (Weir in a container; compose connects to a SQL Server you provide, it does not start one):

```sh
docker compose up --build
# Windows: double-click run-docker-compose.bat
```

The host serves both the JSON API and the admin PWA on the same origin (default
`http://localhost:8080` in the container, `http://localhost:5000` for `dotnet run`).

## 2. Configure a data connection

Weir routes each endpoint to a named data connection. Configure at least one in `appsettings.json`
(or via environment variables - see [Configuration](configuration.md)):

```json
{
  "Weir": {
    "ControlPlane": { "ConnectionString": "Data Source=weir-control.db" },
    "DataConnections": {
      "default": {
        "Provider": "SqlServer",
        "ConnectionString": "Server=localhost;Database=Demo;Trusted_Connection=True;TrustServerCertificate=True"
      }
    },
    "Admin": { "Username": "admin", "Password": "set-a-strong-password" },
    "Jwt": { "SigningKey": "set-a-stable-secret" }
  }
}
```

- `ControlPlane` holds Weir's own metadata (endpoints, keys, scopes, admins, audit) in SQLite.
- `DataConnections` are the target databases. One instance can serve many.
- `Admin` bootstraps the first admin account on startup (only if no admin exists yet).

## 3. Sign in to the admin UI

Open the host URL in a browser. You are redirected to the login page; sign in with the bootstrap
admin credentials. You land on the dashboard with live metrics.

## 4. Define an endpoint

Go to **Endpoints -> New endpoint** and fill in:

- **Route**: `customers/get`
- **HTTP method**: `POST`
- **Connection**: `default`
- **Object type**: `StoredProcedure`
- **Schema / Object name**: `dbo` / `usp_GetCustomer`
- **Parameters**: add `customerId` with source `Body`, type `Int32`

Save. The catalog reloads immediately - no redeploy.

## 5. Call it

Create an API key under **API keys** (copy the plaintext once), then call the endpoint:

```http
POST /api/customers/get
X-Api-Key: wk_live_9f3a...
Content-Type: application/json

{ "customerId": 42 }
```

The response is the standard envelope:

```json
{
  "data": [ [ { "id": 42, "name": "Acme" } ] ],
  "output": null,
  "returnValue": 0,
  "rowsAffected": -1,
  "messages": []
}
```

## Next steps

- [Endpoints and API contract](endpoints.md) - parameters, TVP, output parameters, caching
- [Security](security.md) - API keys, scopes, admin accounts
- [Configuration](configuration.md) - every setting
- [Deployment](deployment.md) - Docker and compose
