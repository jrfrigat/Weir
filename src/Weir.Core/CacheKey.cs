using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>Builds a stable cache key for an endpoint response from its vary-by inputs.</summary>
public static class CacheKey
{
    /// <summary>Returns the shared key prefix for every cached response of a route.</summary>
    /// <param name="route">Endpoint route.</param>
    /// <returns>The prefix passed to <see cref="Weir.Abstractions.IResponseCache.RemoveByPrefixAsync"/>.</returns>
    public static string RoutePrefix(string route) => string.Concat("weir:", route, ":");

    /// <summary>
    /// Builds the cache key for one response. Each contributing segment is length-prefixed and every
    /// value is encoded with a type tag, so distinct inputs can never produce the same key (e.g. the
    /// scalar pair <c>a="b", c="d"</c> and the single value <c>a="b|c=d"</c> stay distinct, and two
    /// different binary values do not collapse to the type name).
    /// </summary>
    /// <param name="endpoint">The endpoint whose response is being cached.</param>
    /// <param name="values">Bound input values keyed by logical parameter name (scalars, plus a token
    /// for table-valued parameters).</param>
    /// <param name="apiKeyPrefix">The caller's API-key prefix, when the endpoint varies by key.</param>
    /// <returns>A collision-resistant cache key, or null when the vary-by set cannot be honored (a
    /// <c>VaryByParameters</c> entry names a parameter that produced no keyable value, e.g. an output
    /// parameter or a typo); a null key disables caching for the call rather than risking a collision.</returns>
    internal static string? Build(EndpointDefinition endpoint, IReadOnlyDictionary<string, object?> values, string? apiKeyPrefix)
    {
        var builder = new StringBuilder();
        Append(builder, endpoint.HttpMethod);
        Append(builder, endpoint.Route);

        foreach (var name in endpoint.Cache.VaryByParameters.OrderBy(x => x, StringComparer.Ordinal))
        {
            // A vary-by parameter that never reached the values map (an output/return parameter, a TVP
            // with no token, or a name that matches no parameter) would otherwise encode as NULL for
            // every caller and collapse distinct requests onto one entry - a cross-caller disclosure.
            if (!values.TryGetValue(name, out var value))
            {
                return null;
            }

            Append(builder, name);
            Append(builder, Encode(value));
        }

        if (endpoint.Cache.VaryByApiKey)
        {
            Append(builder, "k");
            Append(builder, apiKeyPrefix ?? string.Empty);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        return string.Concat(RoutePrefix(endpoint.Route), hash);
    }

    /// <summary>Appends a length-prefixed segment so segment boundaries are unambiguous.</summary>
    /// <param name="builder">Target builder.</param>
    /// <param name="segment">Segment text.</param>
    private static void Append(StringBuilder builder, string segment) =>
        builder.Append(segment.Length).Append(':').Append(segment).Append('|');

    /// <summary>Encodes a bound value to a canonical, type-tagged string for keying.</summary>
    /// <param name="value">The value to encode; null is a SQL NULL.</param>
    /// <returns>A representation that round-trips distinct values to distinct strings.</returns>
    internal static string Encode(object? value) => value switch
    {
        null => "\0null",
        byte[] bytes => "b:" + Convert.ToHexString(bytes),
        bool b => b ? "B:1" : "B:0",
        DateTime dt => "d:" + dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => "o:" + dto.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan ts => "t:" + ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => "g:" + g.ToString("N"),
        string s => "s:" + s,
        IFormattable f => "f:" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => "x:" + value.ToString(),
    };
}
