using Microsoft.AspNetCore.Http;
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
