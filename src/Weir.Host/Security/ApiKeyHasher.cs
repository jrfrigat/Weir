using System.Security.Cryptography;
using System.Text;

namespace Weir.Host.Security;

/// <summary>
/// Hashes API keys for storage and lookup. Keys are high-entropy random secrets, so a fast SHA-256
/// digest is sufficient (a slow password hash is not needed here).
/// </summary>
public static class ApiKeyHasher
{
    /// <summary>Computes the stored hash (uppercase hex SHA-256) of a raw API key.</summary>
    /// <param name="key">The raw API key.</param>
    /// <returns>The hex-encoded SHA-256 hash.</returns>
    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
