# Weir - Extending with plugins and connectors

> [Русский](../ru/extending.md) - [Architecture](architecture.md) - [Configuration](configuration.md)

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

The SQL Server and PostgreSQL connectors open live data-plane readers with
`CommandBehavior.SequentialAccess`. A custom connector should also avoid buffering the result set.
The engine reads column metadata once, then reads values in ordinal order using typed getters.
Do not depend on revisiting an earlier column after consuming a later one. `CompleteAsync` must
finish or close unread results before exposing output parameters, including after response row or
binary value limits are applied. Sequential access limits driver buffering; it does not by itself
remove allocations from materializing a large string or binary value.

The in-box executions opt into `IDbExecution.SupportsSequentialValues`: large or unbounded text
and binary values use pooled chunks rather than a complete `string` or `byte[]`. Text columns
declared at most 4096 characters keep their typed `GetString` fast path. SQL Server text reads use
bounded synchronous `GetChars` calls, with cancellation checks and awaited network flushes between
chunks; ADO.NET has no `GetCharsAsync`. PostgreSQL can pass raw UTF-8 text streams to the JSON writer
when both driver and server encodings are UTF-8, and otherwise uses character chunks. Binary streams
are read asynchronously. JSON escaping and base64 segment boundaries are handled by .NET.

Custom executions default to their existing getter behavior. Opt in only when your reader supports
incremental `GetChars` and `GetStream`; a short `GetChars` read must indicate the end of the value.
Override `CanStreamTextColumn` for provider-specific string-mapped types that cannot use `GetChars`.
`SupportsUtf8TextStreams` additionally promises UTF-8 text streams with a known `Length`; leave it
false for other encodings. Weir keeps the legacy typed fallback for unsupported string types.

A complete reference implementation lives in
[`samples/connectors/Weir.Connectors.MySql`](../../samples/connectors/Weir.Connectors.MySql).

## Response cache providers

Existing `IResponseCache` implementations remain compatible. Their `GetAsync` must return immutable,
independently owned bytes that stay valid after eviction. The engine retains a filled body's owner
until the background store completes. Legacy providers keep their own invalidation semantics; a
pre-store version check alone cannot prevent a concurrent purge from being undone.

Implement `IVersionedResponseCache` to provide the stronger contract. `LookupAsync(key, scopePrefix)`
returns a `CachedResponseLease` and an opaque `CacheWriteFence` for a subsequent miss fill.
`SetIfCurrentAsync` must check that fence and publish the complete bytes atomically with invalidation.
Invalidating an absent entry must fence fills already running for it. Use non-repeating generations
to avoid restoring a stale version when an epoch expires or the provider restarts. A lookup begun
after completed invalidation cannot return the invalidated generation. Calls already running may
finish their old response. The engine includes the generation in its local single-flight key.

Keep a lease alive through every awaited read. The engine does so for body writes, header callbacks,
304, cancellation and errors; `Dispose` releases the provider owner once and clears the response
reference. A failed lookup may return a miss without a fence: the engine executes the database call
without coalescing or storing that result. Do not convert a missing fence into an unconditional store.
When storing borrowed memory, finish copying or retaining it before returning from the awaited store.

The memory provider shares active admission for one physical payload across fill consumers and hit
borrowers, including after eviction. Its global opaque generation conservatively rejects unrelated
fills after any explicit invalidation. Its legacy `PurgeStamp` remains available but the engine does
not depend on concrete providers. Custom hosts should share one `IRuntimeSettings` instance and the
`ResponseBufferBudget` registered by `AddWeirCore` across the engine, cache and whole-body transports.
Cache hits have a small lease allocation; this contract promises bounded ownership, not literal zero
allocation for the entire HTTP request.

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
