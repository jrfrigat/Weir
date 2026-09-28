using System.Text;

namespace Weir.Host.Http;

/// <summary>
/// Normalizes the caller-supplied <c>X-Correlation-ID</c> before it goes into the logs and back into the
/// response. The header is caller input: a line break would forge a second line in a text log, and an
/// arbitrarily long value would be copied into every log event for the request, so the value is capped and
/// restricted to printable ASCII. A value that carries nothing printable is dropped, and the caller falls
/// back to the request's own trace identifier.
/// </summary>
public static class CorrelationId
{
    /// <summary>Maximum accepted length of a caller-supplied correlation id.</summary>
    public const int MaxLength = 128;

    /// <summary>Normalizes a raw <c>X-Correlation-ID</c> header value.</summary>
    /// <param name="value">The raw header value, or null when the header is absent.</param>
    /// <returns>The normalized id, or null when the value is absent or carries nothing printable.</returns>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Keep printable ASCII only, capped at MaxLength. Dropping an unwanted character rather than
        // rejecting the whole value keeps a value that merely carries a stray control character usable for
        // correlation; only a value that normalizes to nothing falls back to a generated id.
        var builder = new StringBuilder(Math.Min(value.Length, MaxLength));
        foreach (var ch in value)
        {
            if (ch is >= ' ' and <= '~')
            {
                builder.Append(ch);
                if (builder.Length == MaxLength)
                {
                    break;
                }
            }
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
