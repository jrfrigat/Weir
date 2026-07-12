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

## A note on the seed format

`endpoints.seed.json` stores enumerations as numbers (the wire format Weir uses), for example
`objectType: 0` is a stored procedure and `dbType: 18` is a table-valued parameter. You do not need
to memorize these: create endpoints in the admin UI and use **Export** to produce a file in the
same format.
