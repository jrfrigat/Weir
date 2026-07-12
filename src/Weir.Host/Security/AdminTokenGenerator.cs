using System.Security.Cryptography;

namespace Weir.Host.Security;

/// <summary>
/// Generates personal access tokens for admin accounts. A token has a recognizable prefix so the
/// authentication pipeline can route it to the token handler, and is stored only as a SHA-256 hash.
/// </summary>
public static class AdminTokenGenerator
{
    /// <summary>The fixed prefix identifying a Weir admin personal access token.</summary>
    public const string Prefix = "weadm_";

    /// <summary>Generates a new admin token.</summary>
    /// <returns>The plaintext token (shown once), a short non-secret prefix, and the stored hash.</returns>
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
