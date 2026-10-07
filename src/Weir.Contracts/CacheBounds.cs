namespace Weir.Contracts;

/// <summary>
/// The accepted range of every numeric <see cref="CachePolicy"/> value, in one place so the admin form
/// and the admin API cannot disagree about it. The form reads the bounds to constrain its inputs; the
/// API validates against the same ones, because a client is not a place to enforce a limit. It mirrors
/// <see cref="SettingsBounds"/>, which does the same job for <see cref="WeirSystemSettings"/>.
/// <para>
/// These bounds only apply while caching is on. An endpoint with the cache off routinely stores an
/// unset TTL - zero, as every seed file does - and that is a supported configuration, not a mistake;
/// nothing ever reads the value in that state.
/// </para>
/// </summary>
public static class CacheBounds
{
    /// <summary>One policy value's accepted range, and how to read that value off a policy.</summary>
    /// <param name="Setting">The property name, used as a stable key when reporting a violation.</param>
    /// <param name="Min">Smallest accepted value, inclusive.</param>
    /// <param name="Max">Largest accepted value, inclusive.</param>
    /// <param name="Read">Reads the value this bound applies to.</param>
    public sealed record Bound(string Setting, long Min, long Max, Func<CachePolicy, long> Read);

    /// <summary>One year in seconds - a TTL past this is a mistyped number rather than a policy.</summary>
    private const long OneYearSeconds = 31_536_000;

    /// <summary>
    /// Range for <see cref="CachePolicy.TtlSeconds"/>. The floor is one second: a zero or negative TTL
    /// has no meaning, and an enabled cache would otherwise hand the negative value to the cache's
    /// options and make it throw on a live request.
    /// </summary>
    public static Bound TtlSeconds { get; } =
        new(nameof(CachePolicy.TtlSeconds), 1, OneYearSeconds, policy => policy.TtlSeconds);

    /// <summary>Every bound, in the order the admin form lays the cache settings out.</summary>
    public static IReadOnlyList<Bound> All { get; } = [TtlSeconds];

    /// <summary>Finds the first value whose range is violated while the policy is in use.</summary>
    /// <param name="policy">The cache policy to check.</param>
    /// <returns>The violated bound, or null when caching is off or every value is in range.</returns>
    public static Bound? FirstViolation(CachePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // A disabled policy's numbers are not read by anything, so none of these bounds applies to it.
        if (!policy.Enabled)
        {
            return null;
        }

        foreach (var bound in All)
        {
            var value = bound.Read(policy);
            if (value < bound.Min || value > bound.Max)
            {
                return bound;
            }
        }

        return null;
    }
}
