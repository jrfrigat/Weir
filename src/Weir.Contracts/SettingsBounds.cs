namespace Weir.Contracts;

/// <summary>
/// The accepted range of every numeric <see cref="WeirSystemSettings"/> value, in one place so the admin
/// form and the admin API cannot disagree about it. The form reads the bounds to constrain its inputs;
/// the API validates against the same ones, because a client is not a place to enforce a limit.
/// <para>
/// The lower bound is zero throughout: every one of these settings reads zero as "unlimited" or
/// "disabled", which is a supported configuration rather than an accident. The upper bounds are not
/// recommendations - they are the point past which a value can only be a typo or a paste, and where the
/// setting stops meaning anything (a request timeout of a year is not a timeout). The one thing they
/// really prevent is the arithmetic: a multiplied or converted <see cref="int.MaxValue"/> overflows, and
/// a limit that overflows into a negative number stops being a limit at all.
/// </para>
/// </summary>
public static class SettingsBounds
{
    /// <summary>One setting's accepted range, and how to read that setting off a settings record.</summary>
    /// <param name="Setting">The property name, used as a stable key when reporting a violation.</param>
    /// <param name="Min">Smallest accepted value, inclusive.</param>
    /// <param name="Max">Largest accepted value, inclusive.</param>
    /// <param name="Read">Reads the value this bound applies to.</param>
    public sealed record Bound(string Setting, long Min, long Max, Func<WeirSystemSettings, long> Read);

    /// <summary>Seconds in a day, the ceiling for anything measured as a timeout or a reset window.</summary>
    private const long OneDaySeconds = 86_400;

    /// <summary>Roughly a century in days, the ceiling for a retention window.</summary>
    private const long OneCenturyDays = 36_500;

    /// <summary>Range for <see cref="WeirSystemSettings.MaxRows"/>.</summary>
    public static Bound MaxRows { get; } = new(nameof(WeirSystemSettings.MaxRows), 0, 100_000_000, s => s.MaxRows);

    /// <summary>Range for <see cref="WeirSystemSettings.RequestTimeoutSeconds"/>.</summary>
    public static Bound RequestTimeoutSeconds { get; } =
        new(nameof(WeirSystemSettings.RequestTimeoutSeconds), 0, OneDaySeconds, s => s.RequestTimeoutSeconds);

    /// <summary>Range for <see cref="WeirSystemSettings.MaxTvpRows"/>.</summary>
    public static Bound MaxTvpRows { get; } = new(nameof(WeirSystemSettings.MaxTvpRows), 0, 10_000_000, s => s.MaxTvpRows);

    /// <summary>Range for <see cref="WeirSystemSettings.MaxImportRows"/>.</summary>
    public static Bound MaxImportRows { get; } = new(nameof(WeirSystemSettings.MaxImportRows), 0, 10_000_000, s => s.MaxImportRows);

    /// <summary>Range for <see cref="WeirSystemSettings.DefaultApiKeyRateLimitPerMinute"/>.</summary>
    public static Bound DefaultApiKeyRateLimitPerMinute { get; } =
        new(nameof(WeirSystemSettings.DefaultApiKeyRateLimitPerMinute), 0, 10_000_000, s => s.DefaultApiKeyRateLimitPerMinute);

    /// <summary>Range for <see cref="WeirSystemSettings.AuditRetentionDays"/>.</summary>
    public static Bound AuditRetentionDays { get; } =
        new(nameof(WeirSystemSettings.AuditRetentionDays), 0, OneCenturyDays, s => s.AuditRetentionDays);

    /// <summary>Range for <see cref="WeirSystemSettings.MaxConcurrentRequestsPerConnection"/>.</summary>
    public static Bound MaxConcurrentRequestsPerConnection { get; } =
        new(nameof(WeirSystemSettings.MaxConcurrentRequestsPerConnection), 0, 100_000, s => s.MaxConcurrentRequestsPerConnection);

    /// <summary>Range for <see cref="WeirSystemSettings.CircuitBreakerFailureThreshold"/>.</summary>
    public static Bound CircuitBreakerFailureThreshold { get; } =
        new(nameof(WeirSystemSettings.CircuitBreakerFailureThreshold), 0, 1_000_000, s => s.CircuitBreakerFailureThreshold);

    /// <summary>Range for <see cref="WeirSystemSettings.CircuitBreakerResetSeconds"/>.</summary>
    public static Bound CircuitBreakerResetSeconds { get; } =
        new(nameof(WeirSystemSettings.CircuitBreakerResetSeconds), 0, OneDaySeconds, s => s.CircuitBreakerResetSeconds);

    /// <summary>Range for <see cref="WeirSystemSettings.ApiKeyFailureThreshold"/>.</summary>
    public static Bound ApiKeyFailureThreshold { get; } =
        new(nameof(WeirSystemSettings.ApiKeyFailureThreshold), 0, 1_000_000, s => s.ApiKeyFailureThreshold);

    /// <summary>Range for <see cref="WeirSystemSettings.SlowRequestThresholdPercent"/>.</summary>
    public static Bound SlowRequestThresholdPercent { get; } =
        new(nameof(WeirSystemSettings.SlowRequestThresholdPercent), 0, 1_000_000, s => s.SlowRequestThresholdPercent);

    /// <summary>Range for <see cref="WeirSystemSettings.RequestLogRetentionDays"/>.</summary>
    public static Bound RequestLogRetentionDays { get; } =
        new(nameof(WeirSystemSettings.RequestLogRetentionDays), 0, OneCenturyDays, s => s.RequestLogRetentionDays);

    /// <summary>Range for <see cref="WeirSystemSettings.ResponseCacheMaxBytes"/>: up to one tebibyte.</summary>
    public static Bound ResponseCacheMaxBytes { get; } =
        new(nameof(WeirSystemSettings.ResponseCacheMaxBytes), 0, 1_099_511_627_776, s => s.ResponseCacheMaxBytes);

    /// <summary>
    /// Range for <see cref="WeirSystemSettings.ResponseFlushBytes"/>: up to one mebibyte. The value that
    /// actually matters here is the 85 KB large-object threshold, which the admin form warns about; this
    /// is only the point past which the setting has clearly been mistyped.
    /// </summary>
    public static Bound ResponseFlushBytes { get; } =
        new(nameof(WeirSystemSettings.ResponseFlushBytes), 0, 1_048_576, s => s.ResponseFlushBytes);

    /// <summary>Every bound, in the order the admin form lays the settings out.</summary>
    public static IReadOnlyList<Bound> All { get; } =
    [
        MaxRows,
        RequestTimeoutSeconds,
        MaxTvpRows,
        MaxImportRows,
        DefaultApiKeyRateLimitPerMinute,
        AuditRetentionDays,
        MaxConcurrentRequestsPerConnection,
        CircuitBreakerFailureThreshold,
        CircuitBreakerResetSeconds,
        ApiKeyFailureThreshold,
        SlowRequestThresholdPercent,
        RequestLogRetentionDays,
        ResponseCacheMaxBytes,
        ResponseFlushBytes,
    ];

    /// <summary>Finds the first setting whose value falls outside its accepted range.</summary>
    /// <param name="settings">The settings to check.</param>
    /// <returns>The violated bound, or null when every value is in range.</returns>
    public static Bound? FirstViolation(WeirSystemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var bound in All)
        {
            var value = bound.Read(settings);
            if (value < bound.Min || value > bound.Max)
            {
                return bound;
            }
        }

        return null;
    }
}
