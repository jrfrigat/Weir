using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Weir.Abstractions;
using Weir.Core;

namespace Weir.Host.Http;

/// <summary>An <see cref="IValueSource"/> over the request query string.</summary>
public sealed class QueryValueSource : IValueSource
{
    private readonly IQueryCollection _query;

    /// <summary>Creates the source over a query collection.</summary>
    /// <param name="query">The request query.</param>
    public QueryValueSource(IQueryCollection query) => _query = query;

    /// <inheritdoc />
    public bool TryGet(string key, out string? value)
    {
        if (_query.TryGetValue(key, out var found))
        {
            value = found.ToString();
            return true;
        }

        value = null;
        return false;
    }
}

/// <summary>An <see cref="IValueSource"/> over the request headers.</summary>
public sealed class HeaderValueSource : IValueSource
{
    private readonly IHeaderDictionary _headers;

    /// <summary>Creates the source over a header dictionary.</summary>
    /// <param name="headers">The request headers.</param>
    public HeaderValueSource(IHeaderDictionary headers) => _headers = headers;

    /// <inheritdoc />
    public bool TryGet(string key, out string? value)
    {
        if (_headers.TryGetValue(key, out var found))
        {
            value = found.ToString();
            return true;
        }

        value = null;
        return false;
    }
}

/// <summary>
/// An <see cref="IValueSource"/> over a JSON object, for a transport whose request carries its
/// query-style values in the frame rather than in a URL. Values are read as text, exactly as a query
/// string would deliver them, so an endpoint's parameter binding does not vary by door: a number in the
/// frame binds the same way as the same number typed after a <c>?</c>.
/// </summary>
public sealed class JsonObjectValueSource : IValueSource
{
    private readonly JsonElement _object;

    /// <summary>Creates the source over a JSON object element.</summary>
    /// <param name="element">The object whose members are the values.</param>
    public JsonObjectValueSource(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A value source needs a JSON object.", nameof(element));
        }

        _object = element;
    }

    /// <inheritdoc />
    public bool TryGet(string key, out string? value)
    {
        if (!_object.TryGetProperty(key, out var found))
        {
            value = null;
            return false;
        }

        value = found.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => found.GetString(),
            // Anything else - number, bool, and an object or array a caller had no business putting
            // here - is handed over as its JSON text, which is what the coercion layer expects to parse.
            _ => found.GetRawText(),
        };

        return true;
    }
}

/// <summary>An <see cref="IValueSource"/> over a fixed dictionary (used for principal claims).</summary>
public sealed class DictionaryValueSource : IValueSource
{
    private readonly IReadOnlyDictionary<string, string?> _values;

    /// <summary>Creates the source over a dictionary of values.</summary>
    /// <param name="values">The backing values.</param>
    public DictionaryValueSource(IReadOnlyDictionary<string, string?> values) => _values = values;

    /// <inheritdoc />
    public bool TryGet(string key, out string? value) => _values.TryGetValue(key, out value);
}

/// <summary>
/// The claims an API key contributes to a data-plane call. Reads the two values straight off the key
/// record: an endpoint only consults this if it declares a claim-sourced parameter, which is rare, so
/// building a dictionary for every request to answer at most two fixed keys is not worth it.
/// </summary>
public sealed class ApiKeyClaimSource : IValueSource
{
    private readonly ApiKeyRecord _key;

    /// <summary>Creates the source over the authenticated key.</summary>
    /// <param name="key">The API key record backing the claims.</param>
    public ApiKeyClaimSource(ApiKeyRecord key) => _key = key;

    /// <inheritdoc />
    public bool TryGet(string key, out string? value)
    {
        if (string.Equals(key, "sub", StringComparison.OrdinalIgnoreCase))
        {
            value = _key.Prefix;
            return true;
        }

        if (string.Equals(key, "key", StringComparison.OrdinalIgnoreCase))
        {
            value = _key.Name;
            return true;
        }

        value = null;
        return false;
    }
}
