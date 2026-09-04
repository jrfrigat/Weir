using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Weir.Host.Security;

/// <summary>
/// Hashes and verifies admin passwords using PBKDF2 (SHA-256). The encoded form is
/// "pbkdf2$iterations$salt$hash" with base64 salt and hash.
/// <para>
/// The work factor travels inside the hash, which is what makes it raisable: every stored hash is
/// verified at the count it was written with, so changing the configured count is safe at any moment
/// and costs nothing to roll out. It applies to hashes written from then on - an existing password
/// keeps its own count until it is next changed.
/// </para>
/// </summary>
public static class PasswordHasher
{
    /// <summary>
    /// Work factor used when no count is given. OWASP's floor rises over time, so this is a starting
    /// point rather than a recommendation with a shelf life - see <c>Weir:Admin:PasswordIterations</c>.
    /// </summary>
    public const int DefaultIterations = 100_000;

    /// <summary>
    /// The lowest count Weir will accept. Below this the hash is weak enough that configuring it is
    /// worse than having no setting at all, so it is refused at startup rather than honoured.
    /// </summary>
    public const int MinimumIterations = 10_000;

    /// <summary>Salt length in bytes.</summary>
    private const int SaltBytes = 16;

    /// <summary>Derived key length in bytes.</summary>
    private const int HashBytes = 32;

    /// <summary>Decoy hashes, one per work factor, built on first use. See <see cref="Decoy"/>.</summary>
    private static readonly ConcurrentDictionary<int, string> Decoys = new();

    /// <summary>Hashes a password at the default work factor.</summary>
    /// <param name="password">The plaintext password.</param>
    /// <returns>The encoded hash string.</returns>
    public static string Hash(string password) => Hash(password, DefaultIterations);

    /// <summary>Hashes a password into the encoded storage form at the given work factor.</summary>
    /// <param name="password">The plaintext password.</param>
    /// <param name="iterations">PBKDF2 iteration count to write into the hash.</param>
    /// <returns>The encoded hash string.</returns>
    public static string Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// A hash to verify against when a sign-in names an account that does not exist, so the failed path
    /// costs what a real verification costs and the response time does not reveal which usernames are
    /// real. It has to be built at the SAME work factor the deployment uses, or raising the count would
    /// make real accounts measurably slower than missing ones and reopen the very gap it closes.
    /// </summary>
    /// <param name="iterations">The deployment's configured work factor.</param>
    /// <returns>An encoded hash of a value no one can sign in with.</returns>
    public static string Decoy(int iterations) =>
        Decoys.GetOrAdd(iterations, static count => Hash("weir-decoy-not-a-real-password", count));

    /// <summary>Verifies a password against an encoded hash in constant time.</summary>
    /// <param name="password">The plaintext password to check.</param>
    /// <param name="encoded">The encoded hash produced by <see cref="Hash(string, int)"/>.</param>
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
