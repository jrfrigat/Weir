using System.Security.Cryptography;

namespace Weir.Host.Security;

/// <summary>Generates new API keys with a recognizable prefix and their stored hash.</summary>
public static class ApiKeyGenerator
{
    /// <summary>The fixed prefix identifying a live Weir API key.</summary>
    private const string Prefix = "wk_live_";

    /// <summary>Generates a new API key.</summary>
    /// <returns>The plaintext key (shown once), a short non-secret prefix, and the stored hash.</returns>
    public static (string PlainText, string DisplayPrefix, string Hash) Generate()
    {
        var body = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var plainText = Prefix + body;
        var displayPrefix = Prefix + body[..6];
        return (plainText, displayPrefix, ApiKeyHasher.Hash(plainText));
    }

    /// <summary>Encodes bytes as URL-safe base64 without padding.</summary>
    /// <param name="bytes">Bytes to encode.</param>
    /// <returns>The URL-safe base64 string.</returns>
    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
