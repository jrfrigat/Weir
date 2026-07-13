# Weir samples

[Russian version](README.ru.md)

A small, self-contained "widgets" example that exercises multi-row results, single-row results,
output parameters, return values and a table-valued parameter (SQL Server).

## Files

- `sqlserver/schema.sql` - tables, stored procedures and a table-valued type for SQL Server.
- `postgres/schema.sql` - the same domain for PostgreSQL (a jsonb bulk import replaces the TVP).
- `endpoints.seed.json` - endpoint definitions for the SQL Server sample, ready to import.
- `sqlserver/demo-database.sql` - a richer, self-contained SQL Server demo: it creates the
  `WeirDemo` database with a `sales` schema and stored procedures / functions / a table type that
  exercise every Weir feature (see below).
- `weir-demo.endpoints.json` - endpoint definitions that map to `demo-database.sql`, ready to import.
- `client/` - a CLI client and load tester (`weir-sample`) that calls the widgets endpoints over
  HTTP, exactly as any external consumer would (see [CLI sample client](#cli-sample-client)).

## Full demo database (SQL Server)

`sqlserver/demo-database.sql` is a complete, re-runnable script that builds a `WeirDemo` database
covering the whole feature surface: multi-row and single-row results, a scalar function, a
table-valued function, output and input-output parameters, a procedure RETURN value, a table-valued
parameter, multiple result sets, a call with no result set, informational (PRINT) messages, and
error handling with THROW.

1. Run the script against your SQL Server:
   `sqlcmd -S <server> -E -i sqlserver/demo-database.sql` (or execute it in SSMS).
2. Add a Weir data connection named `demo` pointing at `WeirDemo` with provider `SqlServer`, e.g.
   `Weir__DataConnections__demo__Provider=SqlServer` and
   `Weir__DataConnections__demo__ConnectionString="Server=<server>;Database=WeirDemo;Trusted_Connection=True;TrustServerCertificate=True"`.
3. In the admin UI open **Endpoints** and **Import** `weir-demo.endpoints.json`.
4. Create an API key, then call the endpoints, for example:
   - `GET /api/products` and `GET /api/products/by-id?id=1`
   - `GET /api/products/search?maxPrice=20`
   - `GET /api/products/price?id=1` (scalar function)
   - `GET /api/customers/orders?id=1` (table-valued function)
   - `POST /api/orders` with body
     `{ "customerId": 1, "items": [ { "ProductId": 1, "Quantity": 2 }, { "ProductId": 4, "Quantity": 10 } ] }`
     - the response `output` has `orderId` and `total`, and `returnValue` is the item count.
   - `GET /api/orders/detail?orderId=1` (two result sets in `data`)
   - `GET /api/customers/stats?customerId=1` (values only in `output`)
   - `POST /api/inventory/adjust` with body `{ "productId": 1, "delta": -5 }`
   - `GET /api/ping` (PRINT messages appear in `messages`)

## Steps

1. Create a scratch database and run the schema script for your engine.
2. Start Weir with a data connection named `default` pointing at that database (see the
   configuration guide). For SQL Server set the connection `Provider` to `SqlServer`; for
   PostgreSQL set it to `PostgreSql`.
3. Sign in to the admin UI, open **Endpoints**, and use **Import** to load
   `endpoints.seed.json`. The four sample endpoints appear immediately.
4. Create an API key (give it any scope, or none), then call the endpoints, for example:
   - `GET /api/widgets`
   - `GET /api/widgets/by-id?id=1`
   - `POST /api/widgets` with body `{ "name": "Bolt", "price": 1.50 }`
   - `POST /api/widgets/import` with body
     `{ "items": [ { "Name": "Nut", "Price": 0.25 }, { "Name": "Washer", "Price": 0.10 } ] }`

## CLI sample client

`client/Weir.Sample.Client` is a small console app (`weir-sample`) built on
[Spectre.Console](https://spectreconsole.net/) that calls the widgets endpoints and can load-test any
endpoint. It talks to a running host over HTTP with an API key (`X-Api-Key`), so it exercises Weir end
to end - useful for a quick smoke test or a throughput check after a change.

### Interactive mode

Pass the host URL and API key as arguments (no command) and it opens an interactive shell that stays
open, so you can send request after request without relaunching. From
`samples/client/Weir.Sample.Client`:

```sh
dotnet run -- --url http://localhost:8080 --api-key weir_...
```

Then type commands at the `weir>` prompt (`help` lists them, `exit` quits):

```text
weir> list                                    # GET /api/widgets, as a table
weir> get 1                                   # GET /api/widgets/by-id?id=1
weir> create Bolt 1.50                        # POST /api/widgets; prints the new id
weir> import --item Nut:0.25 --item Washer:0.10   # bulk insert via a table-valued parameter
weir> call widgets/by-id?id=1                 # call any route, print the raw envelope
weir> load -c 32 -d 15                        # load-test the widgets endpoint
weir> exit
```

The URL and key are also read from `WEIR_URL` / `WEIR_API_KEY`, so `dotnet run` alone (with those set)
opens the shell.

### One-shot mode

Give a command up front to run it once and exit - handy for scripts and CI. The URL and key come from
`--url` / `--api-key` or the environment:

```sh
export WEIR_URL=http://localhost:8080
export WEIR_API_KEY=weir_...              # an API key created in the admin UI
dotnet run -- list
dotnet run -- create Bolt 1.50
dotnet run -- import --item Nut:0.25 --item Washer:0.10
dotnet run -- help                        # full command and option reference
```

### Demo / orders commands

The `list` / `get` / `create` / `import` commands target the widgets sample (`endpoints.seed.json`).
There is a second family of commands for the richer demo database (`sqlserver/demo-database.sql`,
imported from `weir-demo.endpoints.json`) - products, orders and customers under the `sales` schema:

```text
weir> products                                   # GET /api/products (table)
weir> product 3                                  # GET /api/products/by-id?id=3
weir> create-order 1 --item 1:2 --item 4:10      # POST /api/orders (table-valued parameter)
weir> order 1                                    # GET /api/orders/detail (header + line items)
weir> orders 1                                   # GET /api/customers/orders (table-valued function)
weir> customer-stats 1                           # GET /api/customers/stats (output parameters)
```

`create-order` takes the customer id and one or more `--item ProductId:Quantity` pairs; the response
shows the order id and total (output parameters) and the item count (procedure return value). `order`
renders both result sets (the order header and its line items). Any other demo route - `products/search`,
`products/price`, `inventory/adjust`, `ping`, ... - is reachable with the generic `call` command.

### Load testing

The `load` command drives concurrent requests against one endpoint and reports throughput and latency
percentiles (p50 / p90 / p95 / p99), plus a status-code breakdown. It runs for a time window
(`--duration`) or a fixed request count (`--requests`), with an optional warm-up.

```sh
# 32 workers for 15 seconds, 2s warm-up (results discarded during warm-up):
dotnet run -- load --route widgets -c 32 -d 15 -w 2

# A fixed 50,000 requests at concurrency 64:
dotnet run -- load --route widgets -c 64 -n 50000

# Load a POST endpoint with a body:
dotnet run -- load --route widgets -X POST -b '{"name":"Load","price":1.00}' -c 16 -d 10
```

It preflights a single request first, so a bad URL, key or route fails fast instead of flooding the
results. The tool is a single-process convenience check, not a substitute for a distributed
benchmarking rig.

## A note on the seed format

`endpoints.seed.json` stores enumerations as numbers (the wire format Weir uses), for example
`objectType: 0` is a stored procedure and `dbType: 18` is a table-valued parameter. You do not need
to memorize these: create endpoints in the admin UI and use **Export** to produce a file in the
same format.
