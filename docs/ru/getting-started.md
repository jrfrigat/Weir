# Weir - Начало работы

> [English](../en/getting-started.md) - [README](../../README.ru.md) - [Архитектура](architecture.md)

Weir - это тонкий HTTP-шлюз над MSSQL: клиент вызывает эндпоинт, Weir вызывает хранимую процедуру
или функцию и отдаёт результат в виде JSON. Этот гайд поднимает локальный инстанс и обслуживает
первый эндпоинт.

## Требования

- SDK .NET 10 (для сборки и запуска из исходников) или Docker (для запуска контейнера / compose).
- Доступный SQL Server с хранимыми процедурами, которые нужно опубликовать.

## 1. Запуск

Из исходников:

```sh
dotnet run --project src/Weir.Host
```

Или через Docker (Weir в контейнере; compose подключается к вашему SQL Server, а не запускает его):

```sh
docker compose up --build
# Windows: двойной клик по run-docker-compose.bat
```

Хост отдаёт и JSON API, и PWA-админку с одного origin (по умолчанию `http://localhost:8080` в
контейнере и `http://localhost:5000` при `dotnet run`).

## 2. Настройка подключения к данным

Weir маршрутизирует каждый эндпоинт на именованное подключение. Настройте хотя бы одно в
`appsettings.json` (или через переменные окружения - см. [Конфигурацию](configuration.md)):

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
    "Admin": { "Username": "admin", "Password": "задайте-надёжный-пароль" },
    "Jwt": { "SigningKey": "задайте-стабильный-секрет" }
  }
}
```

- `ControlPlane` хранит собственные метаданные Weir (эндпоинты, ключи, скоупы, админов, аудит) в SQLite.
- `DataConnections` - целевые БД. Один инстанс обслуживает много.
- `Admin` создаёт первого админа при старте (только если админа ещё нет).

## 3. Вход в админку

Откройте URL хоста в браузере. Произойдёт редирект на страницу входа; войдите под bootstrap-админом.
Вы попадёте на дашборд с живыми метриками.

## 4. Описание эндпоинта

Перейдите в **Endpoints -> New endpoint** и заполните:

- **Route**: `customers/get`
- **HTTP method**: `POST`
- **Connection**: `default`
- **Object type**: `StoredProcedure`
- **Schema / Object name**: `dbo` / `usp_GetCustomer`
- **Parameters**: добавьте `customerId` с источником `Body`, тип `Int32`

Сохраните. Каталог перезагрузится сразу - без передеплоя.

## 5. Вызов

Создайте API-ключ в разделе **API keys** (скопируйте plaintext один раз) и вызовите эндпоинт:

```http
POST /api/customers/get
X-Api-Key: wk_live_9f3a...
Content-Type: application/json

{ "customerId": 42 }
```

Ответ - стандартный конверт:

```json
{
  "data": [ [ { "id": 42, "name": "Acme" } ] ],
  "output": null,
  "returnValue": 0,
  "rowsAffected": -1,
  "truncated": false,
  "messages": []
}
```

## Дальше

- [Эндпоинты и контракт API](endpoints.md) - параметры, TVP, output-параметры, кэширование
- [Безопасность](security.md) - API-ключи, скоупы, аккаунты админов
- [Конфигурация](configuration.md) - все настройки
- [Деплой](deployment.md) - Docker и compose
- [Примеры](../../samples/README.ru.md) - готовая к импорту схема "widgets" (SQL Server и PostgreSQL),
  более полная демо-база и CLI-клиент / нагрузочный тестер (`weir-sample`), вызывающий эндпоинты по HTTP
