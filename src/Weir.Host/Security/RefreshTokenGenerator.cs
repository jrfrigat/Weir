using System.Security.Cryptography;

namespace Weir.Host.Security;

/// <summary>
/// Generates opaque admin refresh tokens. A refresh token is a high-entropy random secret returned to
/// the client once and stored only as a SHA-256 hash, so a database read cannot recover it.
/// </summary>
public static class RefreshTokenGenerator
{
    /// <summary>Generates a new refresh token.</summary>
    /// <returns>The plaintext token (returned to the client once) and its stored hash.</returns>
    public static (string PlainText, string Hash) Generate()
    {
        var plainText = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (plainText, ApiKeyHasher.Hash(plainText));
    }
}
