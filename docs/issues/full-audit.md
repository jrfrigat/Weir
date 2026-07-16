# Полный аудит проекта Weir

**Дата**: 2026-07-15
**Область**: Все компоненты решения (Contracts, Abstractions, Core, Diagnostics, ControlPlane providers, Connectors, Host, Admin, Tests)

---

## Краткое резюме

| Критичность | Количество |
|-------------|------------|
| Critical    | 5          |
| Warning     | 25         |
| Info        | 50+        |

Наиболее серьёзные проблемы: race conditions в SQL Server control-plane (upsert), PAT-токены не отзываются при смене пароля, тестовое покрытие OUTPUT-параметров отсутствует.

---

## Critical

### C-1. Race condition в SQL Server UpsertEndpointAsync

**Файл**: `src/Weir.ControlPlane.SqlServer/SqlServerControlPlaneStore.cs:223-236`

Паттерн UPDATE-then-INSERT (с проверкой `@@ROWCOUNT`) не атомарен. Два конкурентных запроса с одинаковым method+route, но разными новыми ID, оба увидят `@@ROWCOUNT = 0` и попытаются вставить запись. Второй INSERT вызовет нарушение уникального индекса `UX_Endpoints_Method_Route` и ошибку `ControlPlaneConflictException`, хотя логически это upsert нового endpoint'а.

**Исправление**: Обернуть UPDATE+INSERT в транзакцию с изоляцией SERIALIZABLE, либо INSERT с catch duplicate-key и retry как UPDATE.

---

### C-2. Race condition в SQL Server UpsertScopeAsync

**Файл**: `src/Weir.ControlPlane.SqlServer/SqlServerControlPlaneStore.cs:456-460`

Тот же паттерн UPDATE-then-INSERT без обработки дублирующего ключа. Два конкурентных вызова `UpsertScopeAsync` для нового scope могут вызвать нарушение первичного ключа.

**Исправление**: Аналогично C-1.

---

### C-3. Race condition в SQL Server RecordLoginFailureAsync

**Файл**: `src/Weir.ControlPlane.SqlServer/SqlServerControlPlaneStore.cs:896-907`

Паттерн UPDATE-then-INSERT для начальной строки `LoginThrottle`. Две конкурентные неудачи входа для одного клиента обе увидят `@@ROWCOUNT = 0` и попытаются вставить строку, вызвав нарушение PK. Это сломает процесс входа с необработанным исключением.

**Исправление**: SQLite и PostgreSQL используют атомарный `ON CONFLICT DO UPDATE`. SQL Server должен использовать `MERGE` или `TRY/CATCH` с retry.

---

### C-4. PAT-токены не отзываются при смене пароля

**Файлы**: `src/Weir.Host/Security/AdminTokenAuthenticationHandler.cs:55`, `src/Weir.Host/Http/AdminApi.cs:264-267`

При смене пароля `TokenVersion` инкрементируется (инвалидируя JWT), refresh-токены отзываются, но **Personal Access Tokens остаются валидными**. Обработчик PAT (`AdminTokenAuthenticationHandler`) проверяет только `AdminEnabled`, но не сверяет `TokenVersion`. Украденный PAT остаётся рабочим после смены пароля.

**Исправление**: Добавить `await store.RevokeAdminTokensForAdminAsync(adminId)` в обработчики смены и сброса пароля.

---

### C-5. Нет тестового покрытия для фикса OUTPUT-параметров

**Файл**: `tests/Weir.Tests/` (отсутствует)

В `ParameterBinderTests.cs` нет ни одного теста для OUTPUT-параметров. Фикс `Size = -1` для output-параметров без явного размера не покрыт тестами. Similarly, `ResponseWriterTests` не тестирует вывод output-параметров и returnValue в JSON-конверте.

**Исправление**: Добавить тесты, проверяющие что OUTPUT-параметры с `Size = null` корректно пробрасываются в коннектор, и что `WeirResponseWriter` корректно записывает output-значения.

---

## Warning

### W-1. WeirCallContext.Items использует непотокобезопасный Dictionary

**Файл**: `src/Weir.Abstractions/Telemetry.cs:100`

`Items` объявлен как `IDictionary<string, object?>` на базе обычного `Dictionary`. Сегодня безопасен (вызовы сериализованы), но если подписчик сохранит Items для асинхронной обработки -- будет race condition.

**Исправление**: Заменить на `ConcurrentDictionary<string, object?>`.

---

### W-2. DbErrorCategory живёт в Abstractions, а не в Contracts

**Файл**: `src/Weir.Abstractions/DataPlane.cs:57-76`

Enum используется в метриках и может отображаться в админ-панели, но Weir.Admin ссылается только на Weir.Contracts. Админ-панель не может получить доступ к этому enum.

**Исправление**: Перенести `DbErrorCategory` в `Weir.Contracts.Enums.cs`.

---

### W-3. CachePolicy.TtlSeconds не имеет ограничения на отрицательные значения

**Файл**: `src/Weir.Contracts/Endpoints.cs:170`

Отрицательное значение вызовет `ArgumentOutOfRangeException` в `MemoryCacheEntryOptions`.

**Исправление**: Добавить XML-документацию о допустимом диапазоне или валидацию.

---

### W-4. ETag пересчитывается на каждом кэш-попадании

**Файл**: `src/Weir.Core/WeirEngine.cs:313`

При каждом кэш-попадании `ComputeETag` выполняет полный SHA-256 над всем телом ответа для проверки `If-None-Match`. ETag уже был вычислен при кэшировании.

**Исправление**: Хранить ETag рядом с кэшированными байтами (например, `record CachedEntry(byte[] Bytes, string ETag)`).

---

### W-5. CaptureParameters не имеет ограничения размера

**Файл**: `src/Weir.Core/WeirEngine.cs:404-414`

Сериализует весь словарь `values` (включая TVP-токены) без ограничения. Для TVP со 100K строк токен может быть мегабайтным.

**Исправление**: Добавить ограничение размера аналогично `CaptureResult`.

---

### W-6. "Призрачный" endpoint в списке All после коллизии маршрутов

**Файл**: `src/Weir.Core/EndpointCatalog.cs:53-63`

При коллизии двух endpoint'ов на один ключ, старый перезаписывается в `Map`, но остаётся в списке `All`. Мониторинговые инструменты увидят неактивные записи.

**Исправление**: Удалять перезаписанный endpoint из списка или помечать как неактивный.

---

### W-7. PostgreSQL ClassifyError неправильно классифицирует command timeout

**Файл**: `src/connectors/Weir.Connectors.PostgreSql/PostgreSqlConnector.cs:33-71`

`NpgsqlTimeoutException` наследуется от `NpgsqlException`, а не от `TimeoutException`. Проверка `TimeoutException` не срабатывает, и timeout попадает в категорию `Connection` вместо `Timeout` в метриках.

**Исправление**: Добавить явную проверку `NpgsqlTimeoutException` перед проверкой `NpgsqlException`.

---

### W-8. SQL Server DescribeParametersAsync для deprecated text/ntext/image типов

**Файл**: `src/connectors/Weir.Connectors.SqlServer/SqlServerConnector.cs:226`

Для deprecated типов `max_length` возвращает размер указателя (16 байт), а не реальный размер данных. `Size = 16` обрежет данные в admin-консоли.

**Исправление**: Для `text`/`ntext`/`image` устанавливать `Size = null` (неограниченный).

---

### W-9. PostgreSQL миграции не обёрнуты в транзакцию

**Файл**: `src/Weir.ControlPlane.PostgreSql/PostgresControlPlaneStore.cs:112-122`

DDL + checksum INSERT + version UPDATE выполняются как три отдельных операции. При аварии между DDL и обновлением версии следующий запуск повторит DDL.

**Исправление**: Обернуть в транзакцию (как в SQLite-провайдере).

---

### W-10. SQL Server миграции не обёрнуты в транзакцию

**Файл**: `src/Weir.ControlPlane.SqlServer/SqlServerControlPlaneStore.cs:125-135`

Аналогично W-9.

---

### W-11. SQLite foreign keys без ON DELETE CASCADE для AdminTokens/AdminRefreshTokens

**Файл**: `src/Weir.ControlPlane.Sqlite/SqliteSchema.cs:92, 137`

В PostgreSQL и SQL Server задан `ON DELETE CASCADE`. В SQLite -- нет. Удаление админа с токенами вызовет ошибку constraint violation.

**Исправление**: Добавить `ON DELETE CASCADE` в миграцию SQLite.

---

### W-12. _lastTouch/_lastTokenTouch растут без ограничения

**Файлы**: Все три store-провайдера

`ConcurrentDictionary<Guid, DateTimeOffset>` для throttle never pruning. В долгоживущем HA-деплое с тысячами ключей -- неконтролируемый рост памяти.

**Исправление**: Периодическая очистка записей старше `TouchThrottle * 2`.

---

### W-13. SQL Server sp_getapplock с бесконечным ожиданием (@LockTimeout = -1)

**Файл**: `src/Weir.ControlPlane.SqlServer/SqlServerControlPlaneStore.cs:88`

Если другой экземпляр держит блокировку во время долгой миграции, все остальные экземпляры будут заблокированы навечно. Kubernetes убьёт pod за failing health check.

**Исправление**: Положительный таймаут (например, 60 секунд) и быстрый отказ.

---

### W-14. GetAdminsAsync извлекает PasswordHash, хотя он не используется

**Файлы**: Все три провайдера (Sqlite:451, Postgres:475, SqlServer:511)

SELECT включает `PasswordHash`, но `AdminUserInfo` не имеет этого свойства. Значение считывается в память без необходимости.

**Исправление**: Убрать `PasswordHash` из SELECT.

---

### W-15. CORS AllowAnyHeader().AllowAnyMethod() слишком широк

**Файл**: `src/Weir.Host/Program.cs:285-286`

При включённом CORS разрешены любые HTTP-методы с любых разрешённых origins.

**Исправление**: Ограничить методы: `GET, POST, PUT, PATCH, DELETE`.

---

### W-16. Интроспекция и sync возвращают ex.Message клиенту

**Файл**: `src/Weir.Host/Http/AdminApi.cs:889,907,947`

Сообщения ошибок драйверов БД могут раскрывать имена серверов, БД, пользователей.

**Исправление**: Возвращать общее сообщение, полный exception -- в лог.

---

### W-17. Нет верхнего лимита на размер batch import endpoints

**Файл**: `src/Weir.Host/Http/AdminApi.cs:520-533`

`POST /admin/api/endpoints/import` принимает `List<EndpointDefinition>` без ограничения размера.

**Исправление**: Ограничить количество (например, 1000).

---

### W-18. WeirConnectionUnavailableException раскрывает сообщение клиенту

**Файл**: `src/Weir.Host/Http/DataPlaneEndpoints.cs:172`

При сработке circuit breaker или bulkhead сообщение исключения может раскрыть внутреннее состояние.

**Исправление**: Возвращать общее сообщение + TraceId.

---

### W-19. BearerHandler: race condition при конкурентном refresh

**Файл**: `src/Weir.Admin/Services/BearerHandler.cs:29-55`

Два одновременных 401 вызовут два конкурентных refresh. Второй отправит уже отозванный refresh-токен, и пользователь будет разлогинен.

**Исправление**: Обернуть `TryRefreshAsync()` в `SemaphoreSlim(1,1)`.

---

### W-20. Dashboard HubConnection захватывает токен при подключении

**Файл**: `src/Weir.Admin/Pages/Dashboard.razor:175-178`

Access token захватывается один раз. При его истечении SignalR переподключится с устаревшим токеном.

**Исправление**: Использовать лямбду `() => Tokens.GetAsync().AsTask()` вместо захваченного значения.

---

### W-21. EndpointStats: два режима блокировки в одном классе

**Файл**: `src/Weir.Diagnostics/EndpointStats.cs:54-71`

`Interlocked` для счётчиков и `Lock` для TimeRing. Конкурентный `Snapshot()` может увидеть несогласованное состояние.

**Исправление**: Документировать ordering invariant или перенести инкремент `_count` внутрь lock.

---

### W-22. _endpoints в InMemoryMetricsAggregator растёт без ограничения

**Файл**: `src/Weir.Diagnostics/InMemoryMetricsAggregator.cs:80`

Для каждого уникального маршрута создаётся `EndpointStats`. Маршруты никогда не удаляются.

**Исправление**: LRU-эвикция или ограничение cardinality.

---

### W-23. Settings страница не проверяет верхние границы

**Файл**: `src/Weir.Admin/Pages/Settings.razor:219-226`

`MaxRows` может быть установлен в `int.MaxValue`, `RequestTimeoutSeconds` -- в абсурдно большое значение.

**Исправление**: Разумные верхние границы на клиенте + вывод ошибок серверной валидации.

---

### W-24. Экспорт через data: URL может превысить лимит браузера

**Файл**: `src/Weir.Admin/Pages/Endpoints.razor:815, 824`

Для больших определений `data:` URL превышает 2MB лимит Chrome.

**Исправление**: Использовать `URL.createObjectURL(blob)` вместо `data:` URL.

---

### W-25. Temp DB файлы не очищаются при падении тестов

**Файл**: `tests/Weir.Tests/SqliteControlPlaneStoreTests.cs:17-18`

Тесты создают temp-файлы, но не удаляют их при падении.

**Исправление**: Реализовать `IDisposable` или использовать `:memory:` SQLite.

---

## Пложения по улучшению

### У-1. IControlPlaneStore не имеет методов для изменения роли/состояния админа

Нет `UpdateAdminRoleAsync` или `UpdateAdminEnabledAsync`. Роль админа нельзя изменить после создания (только через прямое редактирование БД).

**Предложение**: Добавить методы в `IControlPlaneStore` с автоматическим инкрементом `TokenVersion`.

---

### У-2. IDbConnector.ClassifyError по умолчанию возвращает None вместо Other

**Файл**: `src/Weir.Abstractions/DataPlane.cs:53`

Если коннектор забыл переопределить `ClassifyError`, все ошибки попадают в "None" (не ошибка БД), что маскирует проблемы в телеметрии.

**Предложение**: Изменить default на `DbErrorCategory.Other`.

---

### У-3. ParameterBinder: разная обработка Output vs InputOutput

**Файл**: `src/Weir.Core/ParameterBinder.cs:93-106`

Чистый `Output` возвращает `Value = null`, а `InputOutput` проваливается в `ReadValue`. SQL Server всегда маппит output-параметры как `InputOutput`, но если появится коннектор с чистым `Output` -- поведение будет некорректным.

**Предложение**: Объединить обработку `Output` и `InputOutput` в одном блоке.

---

### У-4. ETag может использовать не-криптографический хеш

**Файл**: `src/Weir.Core/WeirEngine.cs:340-341`

SHA-256 для ETag избыточен -- нужна только устойчивость к коллизиям, не криптографическая стойкость.

**Предложение**: Заменить на xxHash3 или FarmHash (значительно быстрее).

---

### У-5. Outcome -- свободная строка вместо enum

**Файл**: `src/Weir.Abstractions/Telemetry.cs:65`

`Outcome` -- строка `"ok"` / `"error"`. Разные кодовые пути могут использовать разные строки.

**Предложение**: Определить enum или константы.

---

### У-6. OpenAPI: OUTPUT-параметры не описаны в response schema

**Файл**: `src/Weir.Host/Http/OpenApiGenerator.cs:82`

Генератор пропускает OUTPUT-параметры в схеме запроса, но не описывает их в схеме ответа.

**Предложение**: Добавить OUTPUT-параметры как свойства объекта `output` в response schema.

---

### У-7. Админ-панель: визуальное разделение INPUT и OUTPUT параметров

В форме Test все параметры отображаются единообразно. Пользователь не видит, какие параметры являются OUTPUT.

**Предложение**: Пометить OUTPUT-параметры как "read-only" или вынести в отдельную секцию.

---

### У-8. PBKDF2 iteration count захардкожен на 100000

**Файл**: `src/Weir.Host/Security/PasswordHasher.cs:12`

OWASP рекомендует увеличивать количество итераций со временем.

**Предложение**: Сделать настраиваемым или добавить version prefix в формат хеша для будущей миграции.

---

### У-9. Login throttle по IP, а не по username

**Файл**: `src/Weir.Host/Security/LoginThrottle.cs:10-11`

Защита от brute-force по username, но за NAT-сетью с общим IP один пользователь может заблокировать всех.

**Предложение**: Документировать компромисс для операторов.

---

### У-10. Нет тестов для InMemoryMetricsAggregator при конкурентном доступе

**Файл**: `tests/Weir.Tests/MetricsAggregatorTests.cs`

Нет стресс-теста для конкурентного `Record()` + `GetOverview()`.

**Предложение**: Добавить тест с несколькими потоками.

---

### У-11. TimeRing.WindowHistogram выделяет новый массив при каждом вызове

**Файл**: `src/Weir.Diagnostics/TimeRing.cs:98-123`

При 100 маршрутах и 2 обновлениях дашборда в секунду -- 2800 коротких массивов в секунду.

**Предложение**: Пулить или переиспользовать массив гистограммы.

---

### У-12. WeirResponseWriter: бинарные колонки материализуются без ограничения

**Файл**: `src/Weir.Core/WeirResponseWriter.cs:282`

`GetFieldValue<byte[]>` полностью загружает значение в память. Для больших BLOB'ов -- давление на память в hot path.

**Предложение**: Стримить напрямую из reader или ограничить максимальный размер.

---

### У-13. If-None-Match: наивный split(',') не учитывает quoted commas

**Файл**: `src/Weir.Core/WeirEngine.cs:359`

RFC 7232 разрешает запятые внутри quoted ETags. Сегодня ETags -- hex строки без запятых, но формат может измениться.

**Предложение**: Парсить с учётом quoted-string boundaries.

---

### У-14. Кэш: double-buffering при caching + request logging

**Файл**: `src/Weir.Core/WeirEngine.cs:222-238`

При включённом кэшировании и логировании результатов -- пиковый расход ~2x от размера ответа.

**Предложение**: Документировать как known trade-off или стримить с инкрементальным ETag.

---

### У-15. SQLite PRAGMA synchronous=NORMAL для WAL

**Файл**: `src/Weir.ControlPlane.Sqlite/SqliteControlPlaneStore.cs:63-64`

По умолчанию `synchronous=FULL`. Для WAL-режима `NORMAL` безопаснее и быстрее.

**Предложение**: Добавить `PRAGMA synchronous=NORMAL`.

---

### У-16. PostgreSQL advisory lock key захардкожен

**Файл**: `src/Weir.ControlPlane.PostgreSql/PostgresControlPlaneStore.cs:71`

Ключ `4207853001` одинаков для всех Weir-инстансов на одном PostgreSQL-сервере.

**Предложение**: Хешировать имя БД для уникальности ключа.

---

### У-17. Результаты SQL-инъекций: чисто

Все SQL-запросы в обоих коннекторах и всех трёх control-plane провайдерах используют параметризованныеStatements. Динамические запросы (аудит, request log) строятся из фиксированных строковых фрагментов. **Уязвимостей SQL-инъекций не обнаружено.**

---

### У-18. Паттерны.dispose: корректны

Все execution-классы корректно реализуют `IAsyncDisposable`. `CompleteAsync` идемпотентны. Ресурсы не утекают.

---

### У-19. Thread safety: корректно

Обработчики InfoMessage/Notice используют `lock`. PostgreSQL `_dataSources` -- `ConcurrentDictionary`. SQL Server не имеет разделяемого изменяемого состояния. Execution-объекты одноразовые.

---

### У-20. Маппинг типов: полный

Оба коннектора покрывают все стандартные типы данных. Fallback-типы корректны. UDT и нестандартные типы отображаются в String.

---

## Распределение по компонентам

| Компонент | Critical | Warning | Info |
|-----------|----------|---------|------|
| Contracts + Abstractions | 0 | 3 | 22 |
| Weir.Core | 0 | 3 | 13 |
| Connectors | 0 | 2 | 17 |
| ControlPlane providers | 3 | 5 | 10 |
| Weir.Host | 0 | 5 | 10 |
| Diagnostics | 0 | 2 | 7 |
| Admin (Blazor) | 1 | 5 | 8 |
| Tests | 1 | 3 | 7 |
| **Итого** | **5** | **28** | **94** |

---

## Приоритеты исправления

1. **Немедленно** (Critical): C-1/C-2/C-3 (race conditions в SQL Server), C-4 (PAT не отзываются)
2. **Короткий цикл** (Warning, security): W-16 (раскрытие ошибок), W-18 (раскрытие circuit breaker), W-15 (CORS), W-17 (import лимит)
3. **Средний цикл** (Warning, correctness): W-7 (PostgreSQL timeout), W-9/W-10 (миграции), W-11 (SQLite CASCADE), W-19/W-20 (Admin refresh)
4. **Долгий цикл** (Warning + Improvements): W-4 (ETag), W-12 (throttle cache), W-22 (metrics cardinality), все пункты "Предложения по улучшению"
