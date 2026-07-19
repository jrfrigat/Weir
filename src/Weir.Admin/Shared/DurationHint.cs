using Weir.Admin.Resources;

namespace Weir.Admin.Shared;

/// <summary>
/// Formats a whole number of seconds as a short, human-readable duration for a numeric field's helper
/// text. The stored and transmitted value stays in seconds (it maps straight onto HTTP <c>max-age</c>
/// and the runtime settings); this only relabels a large second-count that is awkward to read - 3600
/// as "1 h", 86400 as "1 d" - so the operator does not have to do the arithmetic.
/// </summary>
internal static class DurationHint
{
    /// <summary>Seconds in a day, hour and minute, for the unit breakdown.</summary>
    private const int Day = 86400;
    private const int Hour = 3600;
    private const int Minute = 60;

    /// <summary>
    /// Returns the two largest non-zero units of <paramref name="seconds"/> (e.g. "1 h 30 m"), or an
    /// empty string below a minute - where the raw seconds already read clearly and a hint would be
    /// noise. Null, zero and negative values all yield an empty string.
    /// </summary>
    /// <param name="seconds">The duration in seconds, or null.</param>
    /// <returns>The hint text, or an empty string when no hint is warranted.</returns>
    internal static string Humanize(int? seconds)
    {
        if (seconds is not int total || total < Minute)
        {
            return string.Empty;
        }

        var parts = new List<string>(2);
        Append(parts, total / Day, AdminStrings.Common_UnitDay);
        Append(parts, total % Day / Hour, AdminStrings.Common_UnitHour);
        Append(parts, total % Hour / Minute, AdminStrings.Common_UnitMinute);
        Append(parts, total % Minute, AdminStrings.Common_UnitSecond);
        return string.Join(" ", parts);
    }

    /// <summary>Appends "<paramref name="value"/> <paramref name="unit"/>" when the value is non-zero and fewer than two units are shown.</summary>
    /// <param name="parts">The accumulating parts, capped at the two largest units.</param>
    /// <param name="value">The unit's value.</param>
    /// <param name="unit">The localized unit abbreviation.</param>
    private static void Append(List<string> parts, int value, string unit)
    {
        if (value > 0 && parts.Count < 2)
        {
            parts.Add($"{value} {unit}");
        }
    }
}
