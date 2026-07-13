using System.Text.Json;

namespace Weir.Sample.Client;

/// <summary>
/// A parsed Weir data-plane response: the HTTP status plus the JSON envelope (or problem+json body).
/// The Weir envelope is <c>{ "data": [[...]], "output": {...}, "returnValue": n, "rowsAffected": n,
/// "truncated": bool, "messages": [...] }</c>; the helpers here read the parts the sample renders.
/// </summary>
internal sealed class WeirResponse : IDisposable
{
    /// <summary>The parsed body; owned and disposed by this instance.</summary>
    private readonly JsonDocument _document;

    /// <summary>Creates a response wrapper.</summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="rawJson">The raw response body.</param>
    /// <param name="document">The parsed body (may be an empty object on a non-JSON body).</param>
    public WeirResponse(int statusCode, string rawJson, JsonDocument document)
    {
        StatusCode = statusCode;
        RawJson = rawJson;
        _document = document;
    }

    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Whether the status is a 2xx success.</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>The raw response body text.</summary>
    public string RawJson { get; }

    /// <summary>The root JSON element of the parsed body.</summary>
    public JsonElement Root => _document.RootElement;

    /// <summary>The <c>output</c> object element, or a default (undefined) element when absent.</summary>
    public JsonElement Output =>
        Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty("output", out var output) ? output : default;

    /// <summary>The procedure <c>returnValue</c> as text, or null when it is absent or JSON null.</summary>
    public string? ReturnValue =>
        Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty("returnValue", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : null;

    /// <summary>The rows of the first result set in <c>data</c>, or an empty list.</summary>
    /// <returns>The first result set's row objects.</returns>
    public IReadOnlyList<JsonElement> FirstResultSet()
    {
        var sets = ResultSets();
        return sets.Count > 0 ? sets[0] : [];
    }

    /// <summary>Every result set in <c>data</c> (each a list of row objects); empty when there are none.</summary>
    /// <returns>The result sets, in order.</returns>
    public IReadOnlyList<IReadOnlyList<JsonElement>> ResultSets()
    {
        if (Root.ValueKind != JsonValueKind.Object ||
            !Root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sets = new List<IReadOnlyList<JsonElement>>(data.GetArrayLength());
        foreach (var set in data.EnumerateArray())
        {
            sets.Add(set.ValueKind == JsonValueKind.Array ? [.. set.EnumerateArray()] : []);
        }

        return sets;
    }

    /// <inheritdoc />
    public void Dispose() => _document.Dispose();
}
