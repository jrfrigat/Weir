# Примеры Weir

[English version](README.md)

Небольшой самодостаточный пример "widgets", который демонстрирует результаты из нескольких строк,
результат из одной строки, выходные параметры, возвращаемое значение и табличный параметр (SQL Server).

## Файлы

- `sqlserver/schema.sql` - таблицы, хранимые процедуры и табличный тип для SQL Server.
- `postgres/schema.sql` - та же предметная область для PostgreSQL (массовый импорт через jsonb вместо TVP).
- `endpoints.seed.json` - определения endpoint для примера SQL Server, готовые к импорту.
- `sqlserver/demo-database.sql` - более полный самодостаточный демо-скрипт для SQL Server: создаёт
  базу `WeirDemo` со схемой `sales`, процедурами / функциями / табличным типом, покрывающими все
  возможности Weir (см. ниже).
- `weir-demo.endpoints.json` - определения endpoint под `demo-database.sql`, готовые к импорту.

## Полная демо-база (SQL Server)

`sqlserver/demo-database.sql` - готовый, повторно запускаемый скрипт, который строит базу `WeirDemo`,
покрывающую весь функционал: результаты из нескольких и из одной строки, скалярную функцию,
табличную функцию, output и input-output параметры, возвращаемое значение процедуры, табличный
параметр, несколько result set, вызов без result set, информационные (PRINT) сообщения и обработку
ошибок через THROW.

1. Выполните скрипт на вашем SQL Server:
   `sqlcmd -S <server> -E -i sqlserver/demo-database.sql` (или запустите в SSMS).
2. Добавьте в Weir подключение к данным с именем `demo`, указывающее на `WeirDemo`, провайдер
   `SqlServer`, например `Weir__DataConnections__demo__Provider=SqlServer` и
   `Weir__DataConnections__demo__ConnectionString="Server=<server>;Database=WeirDemo;Trusted_Connection=True;TrustServerCertificate=True"`.
3. В панели администратора откройте **Endpoints** и через **Import** загрузите `weir-demo.endpoints.json`.
4. Создайте API-ключ и вызывайте endpoint, например:
   - `GET /api/products` и `GET /api/products/by-id?id=1`
   - `GET /api/products/search?maxPrice=20`
   - `GET /api/products/price?id=1` (скалярная функция)
   - `GET /api/customers/orders?id=1` (табличная функция)
   - `POST /api/orders` с телом
     `{ "customerId": 1, "items": [ { "ProductId": 1, "Quantity": 2 }, { "ProductId": 4, "Quantity": 10 } ] }`
     - в ответе `output` содержит `orderId` и `total`, а `returnValue` - число позиций.
   - `GET /api/orders/detail?orderId=1` (два result set в `data`)
   - `GET /api/customers/stats?customerId=1` (значения только в `output`)
   - `POST /api/inventory/adjust` с телом `{ "productId": 1, "delta": -5 }`
   - `GET /api/ping` (PRINT-сообщения появляются в `messages`)

## Шаги

1. Создайте тестовую базу данных и выполните скрипт схемы для вашего движка.
2. Запустите Weir с подключением к данным с именем `default`, указывающим на эту базу (см. руководство
   по конфигурации). Для SQL Server задайте `Provider` подключения `SqlServer`; для PostgreSQL - `PostgreSql`.
3. Войдите в панель администратора, откройте раздел **Endpoints** и с помощью кнопки **Import** загрузите
   `endpoints.seed.json`. Четыре примера endpoint появятся сразу.
4. Создайте ключ API (с любой областью действия или без неё), затем вызывайте endpoint, например:
   - `GET /api/widgets`
   - `GET /api/widgets/by-id?id=1`
   - `POST /api/widgets` с телом `{ "name": "Bolt", "price": 1.50 }`
   - `POST /api/widgets/import` с телом
     `{ "items": [ { "Name": "Nut", "Price": 0.25 }, { "Name": "Washer", "Price": 0.10 } ] }`

## Замечание о формате seed-файла

`endpoints.seed.json` хранит перечисления в виде чисел (внутренний формат Weir): например,
`objectType: 0` - это хранимая процедура, а `dbType: 18` - табличный параметр. Запоминать эти числа
не нужно: создавайте endpoint в панели администратора и используйте кнопку **Export**, чтобы получить
файл в том же формате.
