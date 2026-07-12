# Weir - Расширение плагинами и коннекторами

> [English](../en/extending.md) - [Архитектура](architecture.md) - [Конфигурация](configuration.md)

Weir построен на портах и адаптерах: движок общается с базой через порт `IDbConnector` и не знает
конкретный драйвер. Коннекторы для SQL Server и PostgreSQL входят в поставку; остальные (MySQL,
Oracle, что угодно с ADO.NET-драйвером) добавляются как **плагины**, без изменения ядра.

## Два способа добавить коннектор

### A. Свой хост (собрать свой образ)

Подключите пакет коннектора и зарегистрируйте его, затем соберите свой образ. Подходит, когда вы
управляете сборкой.

```csharp
// Program.cs вашего кастомного хоста
builder.Services.AddWeirCore(/* ... */);
builder.Services.AddWeirSqlServer();
builder.Services.AddWeirMySql();       // ваш коннектор
```

### B. Drop-in плагин (расширить готовый образ без пересборки)

Смонтируйте плагин рядом с официальным образом и укажите его в конфигурации. Подходит, когда нужно
расширить поставляемый образ на этапе деплоя.

```sh
docker run \
  -v /opt/weir-plugins:/plugins \
  -e Weir__Plugins__Paths__0=/plugins/Acme.Weir.MySql/Acme.Weir.MySql.dll \
  ghcr.io/jrfrigat/weir
```

При старте Weir загружает каждую указанную сборку, находит точку входа `IWeirPlugin` и даёт ей
зарегистрировать сервисы. Оба способа используют один и тот же код коннектора - см. точку входа ниже.

## Как написать коннектор

1. Создайте class library, ссылающуюся на NuGet-пакеты **`Weir.Abstractions`** и **`Weir.Contracts`**.
2. Реализуйте `IDbConnector`:
   - `ProviderName` - строка, по которой `Provider` подключения выбирает этот коннектор (например `MySql`).
   - `ExecuteAsync` - открыть соединение, вызвать объект, вернуть `IDbExecution`, который движок стримит.
   - `ProbeAsync` - лёгкий `SELECT 1` для health-check.
   - `ListObjectsAsync` / `DescribeParametersAsync` - интроспекция схемы для редактора в админке.
3. Добавьте DI-хелпер (`AddWeirMySql`), регистрирующий коннектор через `TryAddEnumerable`, чтобы
   несколько коннекторов сосуществовали.
4. Для drop-in-модели добавьте `IWeirPlugin`, чей `ConfigureServices` вызывает ваш DI-хелпер.

```csharp
public sealed class MySqlPlugin : IWeirPlugin
{
    public string Name => "Acme.Weir.MySql";
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.AddWeirMySql();
}
```

Движок выбирает коннектор для подключения, сопоставляя `Provider` подключения с `ProviderName`
коннектора, поэтому после регистрации подключение с `Provider = "MySql"` просто работает.

Полная эталонная реализация лежит в
[`samples/connectors/Weir.Connectors.MySql`](../../samples/connectors/Weir.Connectors.MySql).

## Упаковка и деплой плагина

- Соберите приватные зависимости плагина рядом с ним: `dotnet publish` плагина или
  `CopyLocalLockFileAssemblies=true`, чтобы в папке лежал DLL драйвера (например `MySqlConnector.dll`).
- Укажите в `Weir:Plugins:Paths` путь к входной `.dll` каждого плагина (надёжнее абсолютные пути;
  относительные разрешаются относительно рабочей директории хоста).
- Weir грузит приватные зависимости плагина изолированно, но `Weir.Abstractions`, `Weir.Contracts` и
  абстракции `Microsoft.Extensions.*` разделяет с хостом - поэтому собирайте плагин под совместимые с
  хостом версии.

## Безопасность

Плагин выполняется **в процессе**, с тем же доступом к credentials БД и сети, что и сам Weir.
Загружайте только доверенные сборки. Пути плагинов задаются явно (без авто-скана папки), так что
ничего не загрузится, пока оператор это не включит.

## Стабильность

Контракт коннектора (`IDbConnector` и окружающие типы) - pre-1.0 и может меняться между minor-версиями.
Пиньте версию `Weir.Abstractions`, под которую собран плагин, и рассчитывайте на пересборку при
обновлениях, пока контракт не объявлен стабильным.
