using System.Security.Cryptography;

namespace Weir.Core;

/// <summary>
/// Computes the entity tag for a rendered response body. This lives with the engine rather than in a
/// cache implementation so the tag is available before the entry is stored: the engine needs it to set
/// the <c>ETag</c> header and answer <c>If-None-Match</c>, and it must be able to do both without
/// waiting for a store that may be doing network I/O.
/// </summary>
public static class ResponseETag
{
    /// <summary>
    /// Computes a quoted strong entity tag over the response bytes.
    /// </summary>
    /// <param name="payload">The complete response body.</param>
    /// <returns>A quoted hex SHA-256 tag, e.g. <c>"1A2B..."</c>.</returns>
    /// <remarks>
    /// SHA-256 is deliberate. An entity tag is only a cache validator, so a fast non-cryptographic hash
    /// looks tempting here, but response content is derived from client-supplied parameters: a caller
    /// able to craft two bodies that collide could make the gateway answer 304 for content the client
    /// does not actually hold. SHA-256 is hardware-accelerated and costs microseconds on a response of
    /// realistic size, which is not where this hot path spends its time.
    /// </remarks>
    public static string Compute(ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        return string.Concat("\"", Convert.ToHexString(hash), "\"");
    }
}
