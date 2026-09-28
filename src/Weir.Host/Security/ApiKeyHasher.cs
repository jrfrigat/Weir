using System.Security.Cryptography;
using System.Text;

namespace Weir.Host.Security;

/// <summary>
/// Hashes API keys for storage and lookup. A plain SHA-256 digest, with no salt and no server-side
/// pepper, is the right tool here only because of one invariant: a key is always generated, never
/// chosen. <see cref="ApiKeyGenerator"/> draws 32 bytes from the CSPRNG, so the input space is 2^256
/// and an offline search over a leaked digest cannot succeed.
/// <para>
/// That invariant is load-bearing. If a key ever becomes settable - a "bring your own key" field, an
/// import of operator-supplied values, a shorter generator - a low-entropy key would make the stored
/// digest equivalent to the plaintext, and a control-plane leak would hand the key over. Any such
/// change has to move this hasher to a keyed digest (HMAC-SHA256 with a server secret) in the same
/// step, and accept the migration that implies.
/// </para>
/// </summary>
public static class ApiKeyHasher
{
    /// <summary>Computes the stored hash (uppercase hex SHA-256) of a raw API key.</summary>
    /// <param name="key">The raw API key.</param>
    /// <returns>The hex-encoded SHA-256 hash.</returns>
    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
