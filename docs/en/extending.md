# Weir - Extending with plugins and connectors

> [Russian / Russkiy](../ru/extending.md) - [Architecture](architecture.md) - [Configuration](configuration.md)

Weir is built around ports and adapters: the engine talks to a database through the `IDbConnector`
port and never knows the concrete driver. SQL Server and PostgreSQL connectors ship in the box; you
can add more (MySQL, Oracle, anything with an ADO.NET driver) as **plugins**, without changing the
core.

## Two ways to add a connector

### A. Custom host (build your own image)

Reference the connector package and register it, then build your own image. Best when you control
the build pipeline.

```csharp
// Program.cs of your custom host
builder.Services.AddWeirCore(/* ... */);
builder.Services.AddWeirSqlServer();
builder.Services.AddWeirMySql();       // your connector
```

### B. Drop-in plugin (extend the stock image, no rebuild)

Mount the plugin next to the official image and list it in configuration. Best when you want to
extend a shipped image at deploy time.

```sh
docker run \
  -v /opt/weir-plugins:/plugins \
  -e Weir__Plugins__Paths__0=/plugins/Acme.Weir.MySql/Acme.Weir.MySql.dll \
  ghcr.io/jrfrigat/weir
```

At startup Weir loads each listed assembly, finds the `IWeirPlugin` entry point, and lets it
register services. Both models use the same connector code - see the entry point below.

## Writing a connector

1. Create a class library that references the **`Weir.Abstractions`** and **`Weir.Contracts`** NuGet
   packages.
2. Implement `IDbConnector`:
   - `ProviderName` - the string a connection's `Provider` uses to select this connector (e.g. `MySql`).
   - `ExecuteAsync` - open a connection, invoke the object, return an `IDbExecution` the engine streams.
   - `ProbeAsync` - a lightweight `SELECT 1` for health checks.
   - `ListObjectsAsync` / `DescribeParametersAsync` - schema introspection for the admin editor.
3. Add a DI helper (`AddWeirMySql`) that registers the connector with `TryAddEnumerable` so several
   connectors can coexist.
4. For the drop-in model, add an `IWeirPlugin` whose `ConfigureServices` calls your DI helper.

```csharp
public sealed class MySqlPlugin : IWeirPlugin
{
    public string Name => "Acme.Weir.MySql";
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.AddWeirMySql();
}
```

The engine selects a connector per connection by matching the connection's `Provider` to the
connector's `ProviderName`, so once registered, a connection with `Provider = "MySql"` just works.

A complete reference implementation lives in
[`samples/connectors/Weir.Connectors.MySql`](../../samples/connectors/Weir.Connectors.MySql).

## Packaging and deploying a plugin

- Gather the plugin's private dependencies beside it: `dotnet publish` the plugin, or set
  `CopyLocalLockFileAssemblies=true`, so the folder contains the driver DLL (e.g. `MySqlConnector.dll`).
- Point `Weir:Plugins:Paths` at each plugin's entry `.dll` (absolute paths are safest; relative
  paths resolve against the host's working directory).
- Weir loads a plugin's private dependencies in isolation but shares `Weir.Abstractions`,
  `Weir.Contracts` and the `Microsoft.Extensions.*` abstractions with the host - so build the plugin
  against versions compatible with the host.

## Security

A plugin runs **in-process**, with the same access to database credentials and the network as Weir
itself. Load only assemblies you trust. Plugin paths are listed explicitly (no folder auto-scan),
so nothing loads unless an operator opts in.

## Stability

The connector contract (`IDbConnector` and the surrounding types) is pre-1.0 and may still change
between minor versions. Pin the `Weir.Abstractions` version your plugin builds against and expect to
recompile across upgrades until the contract is declared stable.
