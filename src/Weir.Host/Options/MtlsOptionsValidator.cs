using Microsoft.Extensions.Options;

namespace Weir.Host.Options;

/// <summary>
/// Validates <see cref="MtlsOptions"/> at startup, and reports the problem it actually found.
/// <para>
/// This exists instead of an inline <c>.Validate(predicate, message)</c> for one reason:
/// <see cref="MtlsOptions.Validate"/> works out a precise message - which file is missing, which
/// thumbprint is not hex - and a predicate overload can only answer true or false, so that message
/// would be computed and then thrown away in favour of a fixed one. An operator reading "check that
/// every TrustedCaFiles entry exists" still has to find out which entry does not.
/// </para>
/// </summary>
public sealed class MtlsOptionsValidator : IValidateOptions<MtlsOptions>
{
    /// <summary>Validates the bound section.</summary>
    /// <param name="name">The options name; only the default instance is validated.</param>
    /// <param name="options">The bound options.</param>
    /// <returns>Success, or a failure carrying the precise reason.</returns>
    public ValidateOptionsResult Validate(string? name, MtlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Validate(out var error) ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error!);
    }
}
