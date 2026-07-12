using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Weir.Admin.Services;

/// <summary>Parses claims from a JWT payload. Client-side only; the server validates the signature.</summary>
public static class JwtParser
{
    /// <summary>Extracts claims and the expiry from a JWT.</summary>
    /// <param name="token">The encoded JWT.</param>
    /// <param name="expires">Receives the token expiry, if present.</param>
    /// <returns>The parsed claims.</returns>
    public static IReadOnlyList<Claim> ParseClaims(string token, out DateTimeOffset? expires)
    {
        expires = null;
        if (string.IsNullOrEmpty(token))
        {
            return [];
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return [];
        }

        // A tampered or truncated token must never throw here; the caller treats an empty result as a
        // signed-out (anonymous) session rather than surfacing an exception that would brick the app.
        try
        {
            var json = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (payload is null)
            {
                return [];
            }

            var claims = new List<Claim>(payload.Count);
            foreach (var (key, value) in payload)
            {
                if (key == "exp" && value.TryGetInt64(out var seconds))
                {
                    expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }

                claims.Add(new Claim(key, value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText()));
            }

            return claims;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or DecoderFallbackException)
        {
            expires = null;
            return [];
        }
    }

    /// <summary>Decodes a base64url segment (adding padding as needed).</summary>
    /// <param name="segment">The base64url text.</param>
    /// <returns>The decoded bytes.</returns>
    private static byte[] DecodeBase64Url(string segment)
    {
        var value = segment.Replace('-', '+').Replace('_', '/');
        value += (value.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        return Convert.FromBase64String(value);
    }
}
