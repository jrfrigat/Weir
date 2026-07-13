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
- `client/` - CLI-клиент и нагрузочный тестер (`weir-sample`), который вызывает endpoint widgets по
  HTTP, как это делал бы любой внешний потребитель (см. [CLI-клиент](#cli-клиент)).

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

## CLI-клиент

`client/Weir.Sample.Client` - небольшое консольное приложение (`weir-sample`) на
[Spectre.Console](https://spectreconsole.net/), которое вызывает endpoint widgets и умеет нагружать
любой endpoint. Оно общается с запущенным хостом по HTTP с API-ключом (`X-Api-Key`), то есть проверяет
Weir end-to-end - удобно для быстрого smoke-теста или замера пропускной способности после изменений.

### Интерактивный режим

Передайте URL хоста и API-ключ как аргументы (без команды) - откроется интерактивная оболочка, которая
остаётся открытой, так что можно отправлять запрос за запросом без перезапуска. Из каталога
`samples/client/Weir.Sample.Client`:

```sh
dotnet run -- --url http://localhost:8080 --api-key weir_...
```

Затем вводите команды в приглашении `weir>` (`help` покажет список, `exit` выходит):

```text
weir> list                                    # GET /api/widgets, таблицей
weir> get 1                                   # GET /api/widgets/by-id?id=1
weir> create Bolt 1.50                        # POST /api/widgets; печатает новый id
weir> import --item Nut:0.25 --item Washer:0.10   # массовая вставка через табличный параметр
weir> call widgets/by-id?id=1                 # вызвать любой маршрут, напечатать сырой envelope
weir> load -c 32 -d 15                        # нагрузка на endpoint widgets
weir> exit
```

URL и ключ также читаются из `WEIR_URL` / `WEIR_API_KEY`, поэтому один `dotnet run` (с заданными
переменными) открывает оболочку.

### Одиночный режим (one-shot)

Укажите команду сразу - она выполнится один раз и приложение закроется, что удобно для скриптов и CI.
URL и ключ берутся из `--url` / `--api-key` или из окружения:

```sh
export WEIR_URL=http://localhost:8080
export WEIR_API_KEY=weir_...              # API-ключ, созданный в панели администратора
dotnet run -- list
dotnet run -- create Bolt 1.50
dotnet run -- import --item Nut:0.25 --item Washer:0.10
dotnet run -- help                        # полный справочник команд и опций
```

### Команды demo / orders

Команды `list` / `get` / `create` / `import` работают с примером widgets (`endpoints.seed.json`). Есть
второе семейство команд для более полной демо-базы (`sqlserver/demo-database.sql`, импортируется из
`weir-demo.endpoints.json`) - продукты, заказы и клиенты в схеме `sales`:

```text
weir> products                                   # GET /api/products (таблица)
weir> product 3                                  # GET /api/products/by-id?id=3
weir> create-order 1 --item 1:2 --item 4:10      # POST /api/orders (табличный параметр)
weir> order 1                                    # GET /api/orders/detail (заголовок + позиции)
weir> orders 1                                   # GET /api/customers/orders (табличная функция)
weir> customer-stats 1                           # GET /api/customers/stats (output-параметры)
```

`create-order` принимает id клиента и одну или несколько пар `--item ProductId:Quantity`; в ответе
показываются id и сумма заказа (output-параметры) и число позиций (возвращаемое значение процедуры).
`order` отображает оба result set (заголовок заказа и его позиции). Любой другой демо-маршрут -
`products/search`, `products/price`, `inventory/adjust`, `ping`, ... - доступен через универсальную
команду `call`.

### Нагрузочное тестирование

Команда `load` запускает конкурентные запросы к одному endpoint и выводит пропускную способность и
перцентили задержки (p50 / p90 / p95 / p99), а также разбивку по кодам статуса. Она работает заданное
время (`--duration`) или заданное число запросов (`--requests`), с необязательным прогревом.

```sh
# 32 воркера в течение 15 секунд, прогрев 2с (результаты прогрева отбрасываются):
dotnet run -- load --route widgets -c 32 -d 15 -w 2

# Фиксированные 50 000 запросов при concurrency 64:
dotnet run -- load --route widgets -c 64 -n 50000

# Нагрузка на POST-endpoint с телом:
dotnet run -- load --route widgets -X POST -b '{"name":"Load","price":1.00}' -c 16 -d 10
```

Перед стартом выполняется один preflight-запрос, поэтому неверный URL, ключ или маршрут приводят к
быстрой ошибке, а не к потоку одинаковых сбоев. Инструмент - однопроцессная проверка для удобства, а не
замена распределённому стенду для бенчмарков.

## Замечание о формате seed-файла

`endpoints.seed.json` хранит перечисления в виде чисел (внутренний формат Weir): например,
`objectType: 0` - это хранимая процедура, а `dbType: 18` - табличный параметр. Запоминать эти числа
не нужно: создавайте endpoint в панели администратора и используйте кнопку **Export**, чтобы получить
файл в том же формате.
