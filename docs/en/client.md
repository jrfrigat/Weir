# Weir - .NET client

> [Русский](../ru/client.md) - [Endpoints](endpoints.md) - [Getting Started](getting-started.md)

`FrigaT.Weir.Client` is a typed client for a Weir gateway's data plane. It knows the response
envelope and the error shape and nothing else: routes, parameter names and result columns all live in
the endpoint metadata on the gateway, so the client does not change when a procedure does.

It runs anywhere an `HttpClient` does, Blazor WebAssembly included.

```
dotnet add package FrigaT.Weir.Client
```

## Registering

```csharp
builder.Services.AddWeirClient(options =>
{
    options.BaseAddress = new Uri("https://weir.internal");
    options.ApiKey = builder.Configuration["Weir:ApiKey"];
});
```

Or bind the whole thing from configuration:

```csharp
builder.Services.AddWeirClient(builder.Configuration.GetSection("Weir"));
```

```json
{ "Weir": { "BaseAddress": "https://weir.internal", "ApiKey": "wk_live_..." } }
```

| Option | Meaning |
| :-- | :-- |
| BaseAddress | The gateway origin. Routes are relative to it, so it needs no `/api` suffix - the endpoint routes carry theirs. Required. |
| ApiKey | Sent with every request. Leave it null when credentials arrive another way (a handler of your own, or a gateway that authenticates by network position). |
| ApiKeyHeader | The header the key travels in. Defaults to `X-Api-Key`. |
| Timeout | Per-request timeout. Generous by default, because a Weir call is a database call and the gateway's own timeout produces a better failure than a client-side cancellation. |
| AcceptCompression | Whether to ask for Brotli/gzip. On by default. |

`AddWeirClient` returns the `IHttpClientBuilder`, so a retry policy, a tracing handler or a bearer
token goes on top the usual way:

```csharp
builder.Services.AddWeirClient(o => { /* ... */ })
    .AddHttpMessageHandler<MyTracingHandler>();
```

The options are validated at startup, so a missing `BaseAddress` fails the host rather than the first
call that needs it.

## Calling an endpoint

Inject `WeirClient` and call the route. Parameters travel in the query string for GET and DELETE, and
as a flat JSON body for the rest; an object or a dictionary works for either, and null members are
left out rather than sent as null.

```csharp
public sealed record Order(int OrderId, string Customer, decimal Total);

var orders = await client.GetListAsync<Order>("api/orders", new { since = new DateOnly(2026, 1, 1) });
var one    = await client.GetSingleAsync<Order>("api/orders/42");
var count  = await client.GetScalarAsync<int>("api/orders/count");

var created = await client.PostSingleAsync<Order>("api/orders/create", new { customer = "ACME", total = 99.50m });
```

Row models are matched to SQL column names ignoring case, since a database's naming convention and
C#'s will not agree.

For a procedure with several result sets, take the envelope and pick them by index:

```csharp
var result = await client.PostAsync("api/reports/daily", new { day = DateOnly.FromDateTime(DateTime.Today) });
var totals = result.Set<Total>(0);
var lines  = result.Set<Line>(1);
var stamp  = result.OutputValue<DateTime>("generatedAt");
```

`result.Truncated` is `true` when the gateway's row cap cut the response short. It is a partial
answer, not the end of the data - treat it as one.

## Dictionary endpoints

`GetPageAsync` fills in the reserved keys (`search`, `page`, `pageSize`, `sort`, `sortDir`) and sends
anything in `filters` alongside as the endpoint's own filters.

```csharp
var page = await client.GetPageAsync<Product>(
    "api/products",
    search: "widget",
    page: 2,
    pageSize: 25,
    sort: "Name",
    filters: new Dictionary<string, object?> { ["category"] = 7 });

page.Items;      // the rows
page.TotalCount; // null unless the endpoint reports one
```

`GetAllAsync` walks the pages for a lookup small enough to hold, stopping when a page comes back
short. It takes a `maxRows` ceiling so a lookup that turns out to be a fact table does not quietly
become an out-of-memory error.

```csharp
var countries = await client.GetAllAsync<Country>("api/dict/countries");
```

## Import endpoints

```csharp
var result = await client.ImportAsync("api/import/products", rows);
Console.WriteLine($"{result.Written} of {result.Rows} rows written in {result.Batches} batches");
```

For a payload larger than one request should carry, `ImportChunkedAsync` splits it and reports
progress. Each chunk is its own request and so its own transaction: a failure part-way leaves the
chunks already accepted in place. For an all-or-nothing load, send one request and size the
endpoint's limits to fit it.

```csharp
var progress = new Progress<int>(written => logger.LogInformation("{Written} rows", written));
await client.ImportChunkedAsync("api/import/products", rows, chunkSize: 5_000, progress: progress);
```

## Errors

A call the gateway refuses throws `WeirApiException`, whose message is the problem body's `detail` -
for a database failure, the SQL error text. Without unwrapping it a caller would show
"400 Bad Request" and lose the only line that says what actually went wrong.

```csharp
try
{
    await client.PostAsync("api/orders/create", order);
}
catch (WeirValidationException ex)
{
    foreach (var (field, messages) in ex.Errors)
    {
        ModelState.AddModelError(field, string.Join(" ", messages));
    }
}
catch (WeirApiException ex) when (ex.IsTransient)
{
    // Busy, circuit open, or a database timeout: the same call could succeed later.
}
```

`WeirValidationException` is the subtype thrown when the problem body names fields - a missing
required parameter, a value of the wrong type, a row an import could not accept. `IsTransient`
distinguishes "try again" (408, 429, 502, 503, 504) from "the request itself is the problem", where
sending it again fails the same way.
