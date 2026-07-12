# Weir - Архитектура

[English](../en/architecture.md) | Russkiy

Weir - это шлюз на метаданных, который отображает HTTP-эндпоинты на хранимые процедуры / функции
и возвращает JSON. Он намеренно **тонкий**: путь запроса выполняет маршрутизацию, аутентификацию,
биндинг параметров, опциональное кеширование, телеметрию и потоковую сериализацию - и больше ничего.

## Две плоскости

### Data plane (горячий путь)

```
HTTP-запрос
  1. Матч роута      динамическая таблица эндпоинтов (in-memory снапшот, обновляется при смене метаданных)
  2. AuthN/AuthZ     API-ключ -> поиск по хешу (кеш) -> проверка требуемых скоупов
  3. Биндинг         запрос (body/query/route/header/claim/const) -> WeirParameter[]  (вкл. TVP)
  4. Проверка кеша   если кеш эндпоинта включён -> ключ = роут + vary-by параметры (+ api-ключ)
  5. Выполнение      IDbConnector выполняет SP/функцию через Dapper на именованном подключении
  6. Стриминг JSON   DbDataReader -> Utf8JsonWriter, прямо в тело ответа
  7. Наблюдатели     IWeirCallObserver: OpenTelemetry + in-memory агрегатор
```

Output-параметры, return value, rowsAffected и SQL-сообщения становятся доступны **после** того,
как reader вычитан, поэтому конверт стримится так: пишем "data" (все наборы строк), закрываем
reader, добавляем "output" / "returnValue" / "rowsAffected" / "messages".

### Control plane (метаданные)

Собственное состояние Weir - определения эндпоинтов, API-ключи (только хеши), скоупы, админы,
аудит и однострочный документ runtime-настроек - живёт в **отдельном** хранилище за IControlPlaneStore.
Есть два провайдера: SQLite (по умолчанию, один узел) и PostgreSQL (общее хранилище для развёртываний
с высокой доступностью, где несколько инстансов работают против одной control-базы). У каждого свой
раннер идемпотентных транзакционных миграций; провайдер выбирается через `Weir:ControlPlane:Provider`.

### Runtime-настройки

`IRuntimeSettings` держит тюнингуемые системные настройки (лимиты data-plane, дефолтный rate-limit на
ключ, retention аудита). Он сидится из `appsettings.json`, накладывает сохранённый документ control-plane
на старте и читается вживую движком, биндером, лимитером и gateway-таймаутом - поэтому правка с экрана
**Settings** в админке (`PUT /admin/api/settings`, только Admin, с аудитом) применяется без перезапуска и
доходит до всех инстансов, делящих control-базу.

## Карта модулей и направление зависимостей

```
Weir.Contracts        чистые DTO и enum (browser-safe; общие для Host и Admin)
Weir.Abstractions     серверные порты: IDbConnector, IControlPlaneStore, IWeirCallObserver,
                      IMetricsAggregator, IResponseCache   (ссылается на System.Data.Common)

  зависят от Abstractions:
    Weir.Core                    движок (резолв, биндинг, JSON-writer, оркестрация кеша)
    Weir.Diagnostics             ActivitySource "Weir", Meter "Weir", in-memory агрегатор
    Weir.ControlPlane.Sqlite     реализация IControlPlaneStore + миграции
    Weir.ControlPlane.PostgreSql реализация IControlPlaneStore + миграции (общее хранилище для HA)
    connectors/Weir.Connectors.SqlServer    реализация IDbConnector (SqlClient + Dapper)
    connectors/Weir.Connectors.PostgreSql   реализация IDbConnector (Npgsql)

  корень композиции:
    Weir.Host      DI-обвязка, динамический маппинг эндпоинтов, middleware API-ключей, admin API,
                   health checks, отдача PWA
    Weir.Admin     Blazor WASM PWA (Flare) - дашборд + управление
```

Всё зависит только от Weir.Contracts / Weir.Abstractions. Драйверы и хранилища реализуют порты;
Weir.Host собирает конкретный набор через DI (AddWeirSqlServer(), AddWeirPostgreSql(),
AddWeirControlPlaneSqlite(), AddWeirControlPlanePostgres()). Оба коннектора повторяют только
транзиентные ошибки открытия подключения, поэтому процедура никогда не вызывается более одного раза.

Те же порты - это поверхность для плагинов: сторонний коннектор реализует IDbConnector и либо
компилируется в свой хост, либо кладётся в работающий образ через загрузчик плагинов
(`Weir:Plugins:Paths`). См. [Расширение](extending.md).

## Именованные подключения

Weir:DataConnections:{name} отображает логическое имя на { provider, connectionString }. Каждый
эндпоинт ссылается на ConnectionName, а его объект адресуется как schema.object. Поэтому один
инстанс Weir обслуживает много серверов / БД / схем одновременно.

## Кеширование

Политика кеша на каждый эндпоинт (Enabled, TtlSeconds, VaryByParameters, VaryByApiKey) задаётся в
админке. Ключ кеша - это роут эндпоинта плюс нормализованные значения vary-by параметров (и,
опционально, API-ключ). Отрендеренные байты JSON кешируются через IResponseCache (сейчас
IMemoryCache; абстракция позволяет распределённый бэкенд позже). Кеш управляется сервером; клиент
не может его отключить.

## Телеметрия

- ActivitySource "Weir" - один span на вызов, теги: route, db.system, connection, object, rows,
  cache hit, outcome и (при сбое) классифицированная категория ошибки БД.
- Meter "Weir" - weir.requests, weir.request.duration, weir.db.duration,
  weir.cache.hits / weir.cache.misses, weir.rows, weir.active_requests и weir.db.errors (теги: route и
  категория: timeout / deadlock / constraint / connection / other).
- OTLP-экспортёр (opt-in) -> дашборд .NET Aspire / любой OpenTelemetry-бэкенд. Также подписан meter
  "Npgsql", поэтому метрики пула соединений PostgreSQL (idle / busy соединения, насыщение)
  экспортируются вместе с метриками Weir. Состояние пула SQL Server доступно через event counters
  Microsoft.Data.SqlClient при необходимости.
- Встроенный IMetricsAggregator хранит скользящие агрегаты по эндпоинтам (count, error rate, оконные
  p50/p95/p99, req/s, cache-hit ratio, последние вызовы), которые питают дашборд PWA без внешней
  инфраструктуры.
- IWeirCallObserver - точка расширения: можно добавить свои приёмники (Prometheus, App Insights,
  аудит), не трогая горячий путь. Значения параметров по умолчанию не логируются (PII-safe).
- Хаб SignalR (`/hubs/dashboard`) плюс фоновый broadcaster пушат снапшоты метрик и health соединений
  в дашборд админки, поэтому он рендерится вживую без опроса.

## Логирование

Структурированное логирование через Serilog: rolling-файл (директория, интервал, размер, retention и
формат из `Weir:Logging`) и опциональная консоль. Каждый запрос эмитит одну сводную строку и несёт
correlation id; логируются события безопасности (неудачные входы, блокировки, отказы по scope/grant,
срабатывания rate-limit, изменения настроек). Фоновый сервис чистит историю аудита по `AuditRetentionDays`.

## Безопасность

- **Клиенты**: API-ключи (wk_live_...) со скоупами. Хранятся только хеш и короткий префикс. Каждый
  эндпоинт объявляет RequiredScopes; middleware их проверяет.
- **Админы**: локальные аккаунты (пароль хешируется), первый админ создаётся из config/env. Сессия -
  короткоживущий JWT, отзываемый до истечения через версию токена на аккаунт (смена пароля или отключение
  инвалидирует действующие токены). Персональные access-токены - для скриптов / CI-CD. PWA отдаётся с
  того же origin, что и admin API (без CORS). Каждая мутация админа аудируется.

## Хостинг и деплой

Weir.Host отдаёт и JSON API, и Blazor WASM PWA админку (UseBlazorFrameworkFiles() плюс
MapFallbackToFile("index.html")) как единый деплой. Многоступенчатый Dockerfile собирается на
образе SDK .NET 10 и запускается на aspnet:10.0.
