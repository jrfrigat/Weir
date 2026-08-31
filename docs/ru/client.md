# Weir - клиент для .NET

> [English](../en/client.md) - [Эндпоинты](endpoints.md) - [Быстрый старт](getting-started.md)

`FrigaT.Weir.Client` - типизированный клиент data-plane шлюза Weir. Он знает форму конверта ответа и
форму ошибки, и больше ничего: маршруты, имена параметров и колонки результата живут в метаданных
эндпоинтов на шлюзе, поэтому клиент не меняется, когда меняется процедура.

Работает везде, где работает `HttpClient`, включая Blazor WebAssembly.

```
dotnet add package FrigaT.Weir.Client
```

## Регистрация

```csharp
builder.Services.AddWeirClient(options =>
{
    options.BaseAddress = new Uri("https://weir.internal");
    options.ApiKey = builder.Configuration["Weir:ApiKey"];
});
```

Либо взять всё из конфигурации:

```csharp
builder.Services.AddWeirClient(builder.Configuration.GetSection("Weir"));
```

```json
{ "Weir": { "BaseAddress": "https://weir.internal", "ApiKey": "wk_live_..." } }
```

| Настройка | Значение |
| :-- | :-- |
| BaseAddress | Origin шлюза. Маршруты задаются относительно него, поэтому суффикс `/api` не нужен - он уже есть в маршрутах эндпоинтов. Обязательна. |
| ApiKey | Отправляется с каждым запросом. Оставьте null, если учётные данные приходят иначе (свой handler или шлюз, который аутентифицирует по сети). |
| ApiKeyHeader | Заголовок, в котором едет ключ. По умолчанию `X-Api-Key`. |
| Timeout | Таймаут запроса. По умолчанию щедрый, потому что вызов Weir - это вызов базы, а собственный таймаут шлюза даёт более понятную ошибку, чем отмена на стороне клиента. |
| AcceptCompression | Запрашивать ли Brotli/gzip. По умолчанию включено. |

`AddWeirClient` возвращает `IHttpClientBuilder`, поэтому политика повторов, трассировка или bearer-токен
навешиваются обычным способом:

```csharp
builder.Services.AddWeirClient(o => { /* ... */ })
    .AddHttpMessageHandler<MyTracingHandler>();
```

Настройки проверяются при старте, поэтому отсутствующий `BaseAddress` роняет хост, а не первый вызов,
которому он понадобился.

## Вызов эндпоинта

Внедрите `WeirClient` и вызовите маршрут. Параметры едут в query-строке для GET и DELETE и плоским
JSON-телом для остальных методов; подойдёт и объект, и словарь, а null-члены не отправляются вовсе,
вместо того чтобы уйти как null.

```csharp
public sealed record Order(int OrderId, string Customer, decimal Total);

var orders = await client.GetListAsync<Order>("api/orders", new { since = new DateOnly(2026, 1, 1) });
var one    = await client.GetSingleAsync<Order>("api/orders/42");
var count  = await client.GetScalarAsync<int>("api/orders/count");

var created = await client.PostSingleAsync<Order>("api/orders/create", new { customer = "ACME", total = 99.50m });
```

Модели строк сопоставляются с именами SQL-колонок без учёта регистра: соглашения об именовании в базе
и в C# всё равно не совпадут.

Для процедуры с несколькими наборами возьмите конверт и выберите их по индексу:

```csharp
var result = await client.PostAsync("api/reports/daily", new { day = DateOnly.FromDateTime(DateTime.Today) });
var totals = result.Set<Total>(0);
var lines  = result.Set<Line>(1);
var stamp  = result.OutputValue<DateTime>("generatedAt");
```

`result.Truncated` равен `true`, когда лимит строк шлюза обрезал ответ. Это частичный ответ, а не
конец данных - так к нему и относитесь.

## Эндпоинты-справочники

`GetPageAsync` заполняет зарезервированные ключи (`search`, `page`, `pageSize`, `sort`, `sortDir`) и
отправляет всё из `filters` рядом как собственные фильтры эндпоинта.

```csharp
var page = await client.GetPageAsync<Product>(
    "api/products",
    search: "widget",
    page: 2,
    pageSize: 25,
    sort: "Name",
    filters: new Dictionary<string, object?> { ["category"] = 7 });

page.Items;      // строки
page.TotalCount; // null, если эндпоинт не возвращает общее число
```

`GetAllAsync` обходит страницы для справочника, который целиком помещается в память, и
останавливается, когда страница пришла неполной. У него есть потолок `maxRows`, чтобы справочник,
который на деле оказался таблицей фактов, не превратился тихо в OutOfMemory.

```csharp
var countries = await client.GetAllAsync<Country>("api/dict/countries");
```

## Эндпоинты импорта

```csharp
var result = await client.ImportAsync("api/import/products", rows);
Console.WriteLine($"записано {result.Written} из {result.Rows} строк за {result.Batches} пакетов");
```

Для объёма, который не стоит слать одним запросом, `ImportChunkedAsync` разбивает его на части и
сообщает о прогрессе. Каждая часть - отдельный запрос и, значит, отдельная транзакция: ошибка на
середине оставит уже принятые части на месте. Если нужно "всё или ничего", отправляйте один запрос и
подберите лимиты эндпоинта под него.

```csharp
var progress = new Progress<int>(written => logger.LogInformation("{Written} строк", written));
await client.ImportChunkedAsync("api/import/products", rows, chunkSize: 5_000, progress: progress);
```

## Ошибки

Вызов, который шлюз отклонил, бросает `WeirApiException`, и его сообщение - это `detail` из тела
ошибки, а для сбоя базы - текст SQL-ошибки. Без разбора этого тела клиент показал бы
"400 Bad Request" и потерял единственную строку, объясняющую, что на самом деле пошло не так.

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
    // Занято, разомкнут предохранитель или таймаут базы: тот же вызов может пройти позже.
}
```

`WeirValidationException` - подтип, который бросается, когда тело ошибки называет поля: пропущен
обязательный параметр, значение не того типа, строка, которую импорт не принял. `IsTransient`
отличает "попробуйте ещё раз" (408, 429, 502, 503, 504) от "проблема в самом запросе", где повтор
завершится так же.
