using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weir.Client;

/// <summary>
/// The envelope every Weir data-plane endpoint answers with. <see cref="Data"/> holds RESULT SETS,
/// not rows: a procedure with three <c>SELECT</c> statements is one request and three sets, and the
/// caller picks the one it wants by index.
/// <para>
/// Rows stay as <see cref="JsonElement"/> until something asks for them as a type. A caller that wants
/// one column of one row should not pay to materialize a dictionary per row of every set, and the row
/// shape is the procedure's, which no client-side model can be sure of in advance.
/// </para>
/// </summary>
public sealed class WeirResult
{
    /// <summary>Result sets, one per statement that returned rows. Each set is a list of row objects.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<IReadOnlyList<JsonElement>> Data { get; init; } = [];

    /// <summary>
    /// Output and input-output parameter values, keyed by logical parameter name. A dictionary
    /// endpoint reports its unpaged row count here as <c>totalCount</c>; an import reports its
    /// summary. Null when the endpoint produced none.
    /// </summary>
    [JsonPropertyName("output")]
    public IReadOnlyDictionary<string, JsonElement>? Output { get; init; }

    /// <summary>The procedure's integer RETURN value, when it produced one.</summary>
    [JsonPropertyName("returnValue")]
    public int? ReturnValue { get; init; }

    /// <summary>Rows affected, as reported by the driver.</summary>
    [JsonPropertyName("rowsAffected")]
    public int RowsAffected { get; init; }

    /// <summary>
    /// Whether the gateway's row cap cut the response short. A truncated response is a partial answer:
    /// treat it as one, rather than as the end of the data.
    /// </summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>SQL informational messages captured while the object ran. Usually empty.</summary>
    [JsonPropertyName("messages")]
    public IReadOnlyList<WeirMessage> Messages { get; init; } = [];
}

/// <summary>One SQL informational message (a <c>PRINT</c>, a notice) captured during execution.</summary>
public sealed record WeirMessage
{
    /// <summary>The message text.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>Severity / class of the message; 0 for a plain <c>PRINT</c>.</summary>
    [JsonPropertyName("severity")]
    public int Severity { get; init; }

    /// <summary>Provider error number, when the provider reported one.</summary>
    [JsonPropertyName("number")]
    public int Number { get; init; }

    /// <summary>Originating procedure, when the provider reported one.</summary>
    [JsonPropertyName("procedure")]
    public string? Procedure { get; init; }

    /// <summary>Line within the batch or procedure, when the provider reported one.</summary>
    [JsonPropertyName("line")]
    public int Line { get; init; }
}

/// <summary>One page of a dictionary endpoint's rows, with the unpaged total when it reports one.</summary>
/// <typeparam name="T">The row model.</typeparam>
public sealed class WeirPage<T>
{
    /// <summary>The rows of this page.</summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>
    /// Rows matching the filters before paging, or null when the endpoint does not report a total.
    /// Reporting one is opt-in per endpoint because it costs a second query.
    /// </summary>
    public int? TotalCount { get; init; }

    /// <summary>The 1-based page number that was requested.</summary>
    public int Page { get; init; }

    /// <summary>The page size that was applied, which may be smaller than the one requested.</summary>
    public int PageSize { get; init; }

    /// <summary>Whether the gateway's row cap cut this page short.</summary>
    public bool Truncated { get; init; }
}

/// <summary>What one import request wrote, read back from the response envelope.</summary>
public sealed class WeirImportResult
{
    /// <summary>Rows the gateway reported as written.</summary>
    public int Written { get; init; }

    /// <summary>Rows the request carried.</summary>
    public int Rows { get; init; }

    /// <summary>Statements the rows were split across.</summary>
    public int Batches { get; init; }

    /// <summary>Rows removed first, for an import that replaces the table's contents.</summary>
    public int Deleted { get; init; }

    /// <summary>The mode the endpoint is configured with: <c>Insert</c>, <c>Upsert</c> or <c>Replace</c>.</summary>
    public string? Mode { get; init; }
}

/// <summary>Reads the sets of a <see cref="WeirResult"/> as typed rows.</summary>
public static class WeirResultExtensions
{
    /// <summary>
    /// Case-insensitive matching, because a row's property names are SQL column names and the C# model
    /// naming them will not agree with the database's casing convention.
    /// </summary>
    private static readonly JsonSerializerOptions RowOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads one result set as typed rows; an absent set reads as no rows.</summary>
    /// <typeparam name="T">The row model; its property names must match the SQL column names.</typeparam>
    /// <param name="result">The envelope to read.</param>
    /// <param name="index">Zero-based result-set index.</param>
    /// <returns>The typed rows.</returns>
    public static IReadOnlyList<T> Set<T>(this WeirResult result, int index = 0)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (index < 0 || index >= result.Data.Count)
        {
            return [];
        }

        var set = result.Data[index];
        var rows = new List<T>(set.Count);
        foreach (var row in set)
        {
            if (row.Deserialize<T>(RowOptions) is { } typed)
            {
                rows.Add(typed);
            }
        }

        return rows;
    }

    /// <summary>Reads the first row of one result set; null when the set has none.</summary>
    /// <typeparam name="T">The row model; its property names must match the SQL column names.</typeparam>
    /// <param name="result">The envelope to read.</param>
    /// <param name="index">Zero-based result-set index.</param>
    /// <returns>The first typed row, or null.</returns>
    public static T? First<T>(this WeirResult result, int index = 0)
        where T : class
    {
        var rows = result.Set<T>(index);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Reads the single scalar an endpoint returned: the first column of the first row of the first
    /// set. This is the shape a scalar function or a <c>SELECT COUNT(*)</c> comes back in.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The envelope to read.</param>
    /// <returns>The value, or the default when there is no row.</returns>
    public static T? Scalar<T>(this WeirResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Data.Count == 0 || result.Data[0].Count == 0)
        {
            return default;
        }

        foreach (var property in result.Data[0][0].EnumerateObject())
        {
            return property.Value.Deserialize<T>(RowOptions);
        }

        return default;
    }

    /// <summary>Reads one output value by name; the default when the endpoint reported none.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The envelope to read.</param>
    /// <param name="name">The output name.</param>
    /// <returns>The value, or the default.</returns>
    public static T? OutputValue<T>(this WeirResult result, string name)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Output is not null && result.Output.TryGetValue(name, out var value)
            ? value.Deserialize<T>(RowOptions)
            : default;
    }
}
