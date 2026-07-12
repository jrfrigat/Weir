using System.Security.Cryptography;

namespace Weir.Host.Security;

/// <summary>
/// Hashes and verifies admin passwords using PBKDF2 (SHA-256). The encoded form is
/// "pbkdf2$iterations$salt$hash" with base64 salt and hash.
/// </summary>
public static class PasswordHasher
{
    /// <summary>Work factor: PBKDF2 iteration count.</summary>
    private const int Iterations = 100_000;

    /// <summary>Salt length in bytes.</summary>
    private const int SaltBytes = 16;

    /// <summary>Derived key length in bytes.</summary>
    private const int HashBytes = 32;

    /// <summary>Hashes a password into the encoded storage form.</summary>
    /// <param name="password">The plaintext password.</param>
    /// <returns>The encoded hash string.</returns>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Verifies a password against an encoded hash in constant time.</summary>
    /// <param name="password">The plaintext password to check.</param>
    /// <param name="encoded">The encoded hash produced by <see cref="Hash"/>.</param>
    /// <returns>True if the password matches.</returns>
    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2")
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
