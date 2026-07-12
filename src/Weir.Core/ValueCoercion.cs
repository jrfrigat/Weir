using System.Globalization;
using System.Text.Json;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>Coerces JSON and string request values into CLR values for the target <see cref="WeirDbType"/>.</summary>
internal static class ValueCoercion
{
    /// <summary>Coerces a JSON element (from a request body or TVP cell) into a CLR value for the target type.</summary>
    /// <param name="element">The JSON element to coerce.</param>
    /// <param name="type">The target database type.</param>
    /// <returns>The coerced CLR value, or null for JSON null/undefined.</returns>
    public static object? FromJson(JsonElement element, WeirDbType type)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return type switch
        {
            WeirDbType.String or WeirDbType.AnsiString =>
                element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
            WeirDbType.Json or WeirDbType.Xml =>
                element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
            WeirDbType.Boolean => element.GetBoolean(),
            WeirDbType.Byte => element.GetByte(),
            WeirDbType.Int16 => element.GetInt16(),
            WeirDbType.Int32 => element.GetInt32(),
            WeirDbType.Int64 => element.GetInt64(),
            WeirDbType.Decimal => element.GetDecimal(),
            WeirDbType.Double => element.GetDouble(),
            WeirDbType.Single => element.GetSingle(),
            WeirDbType.DateTime => element.GetDateTime(),
            WeirDbType.DateTimeOffset => element.GetDateTimeOffset(),
            WeirDbType.Date => element.GetDateTime().Date,
            WeirDbType.Time => TimeSpan.Parse(element.GetString() ?? "00:00:00", CultureInfo.InvariantCulture),
            WeirDbType.Guid => element.GetGuid(),
            WeirDbType.Binary => element.GetBytesFromBase64(),
            _ => element.GetString(),
        };
    }

    /// <summary>Coerces a string value (from a query, route, header or claim) into a CLR value for the target type.</summary>
    /// <param name="text">The raw string, or null when the source was absent.</param>
    /// <param name="type">The target database type.</param>
    /// <returns>The coerced CLR value, or null for a null input.</returns>
    public static object? FromString(string? text, WeirDbType type)
    {
        if (text is null)
        {
            return null;
        }

        return type switch
        {
            WeirDbType.String or WeirDbType.AnsiString or WeirDbType.Json or WeirDbType.Xml => text,
            WeirDbType.Boolean => bool.Parse(text),
            WeirDbType.Byte => byte.Parse(text, CultureInfo.InvariantCulture),
            WeirDbType.Int16 => short.Parse(text, CultureInfo.InvariantCulture),
            WeirDbType.Int32 => int.Parse(text, CultureInfo.InvariantCulture),
            WeirDbType.Int64 => long.Parse(text, CultureInfo.InvariantCulture),
            WeirDbType.Decimal => decimal.Parse(text, CultureInfo.InvariantCulture),
            WeirDbType.Double => double.Parse(text, CultureInfo.InvariantCulture),
            WeirDbType.Single => float.Parse(text, CultureInfo.InvariantCulture),
            WeirDbType.DateTime => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            WeirDbType.DateTimeOffset => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            WeirDbType.Date => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Date,
            WeirDbType.Time => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
            WeirDbType.Guid => Guid.Parse(text),
            WeirDbType.Binary => Convert.FromBase64String(text),
            _ => text,
        };
    }
}
