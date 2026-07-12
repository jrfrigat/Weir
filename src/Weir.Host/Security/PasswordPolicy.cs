namespace Weir.Host.Security;

/// <summary>
/// Minimum server-side password rules applied when an admin password is created or changed. Weir keeps
/// the policy deliberately simple (a length floor and a non-whitespace check); operators layer their
/// own identity provider on top when they need more.
/// </summary>
public static class PasswordPolicy
{
    /// <summary>Minimum acceptable password length.</summary>
    public const int MinLength = 8;

    /// <summary>Validates a candidate password.</summary>
    /// <param name="password">The plaintext password to check.</param>
    /// <returns>Null when the password is acceptable; otherwise a human-readable reason it was rejected.</returns>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "The password must not be empty.";
        }

        if (password.Length < MinLength)
        {
            return $"The password must be at least {MinLength} characters long.";
        }

        return null;
    }
}
