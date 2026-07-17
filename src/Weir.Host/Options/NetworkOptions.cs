using System.Net;

namespace Weir.Host.Options;

/// <summary>
/// Reverse-proxy trust, bound from <c>Weir:Network</c>. Weir is normally deployed behind a proxy that
/// terminates TLS, which means the socket Weir sees belongs to the proxy, not the caller: without this
/// section every request appears to come from one address. That matters because the sign-in throttle
/// and the security log both key on the caller's address - shared across all callers, the throttle
/// stops being per-client and one attacker can lock every admin out at once.
/// <para>
/// Forwarding is off unless <see cref="TrustedProxies"/> lists at least one entry, and that is
/// deliberate: <c>X-Forwarded-For</c> is caller-supplied, so honouring it from an untrusted source is
/// worse than ignoring it - an attacker would simply put a fresh address in every request and never be
/// throttled at all. Only list proxies you control.
/// </para>
/// </summary>
public sealed class NetworkOptions
{
    /// <summary>
    /// Addresses or CIDR networks of the reverse proxies in front of Weir, e.g. <c>"10.0.0.7"</c> or
    /// <c>"10.0.0.0/8"</c>. Empty (the default) leaves forwarded headers disabled and keeps the raw
    /// socket address. Entries are validated on start, so a typo fails the build-up rather than
    /// silently leaving the throttle keyed on the proxy.
    /// </summary>
    public IList<string> TrustedProxies { get; } = [];

    /// <summary>
    /// How many proxy hops to walk back through <c>X-Forwarded-For</c>. Defaults to 1, matching a
    /// single proxy in front of Weir. Raise it only if you genuinely run a chain (edge CDN plus an
    /// internal ingress, say), and only when every hop in that chain is trusted - each extra hop is
    /// another entry the caller could have forged if it is not.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>True when at least one trusted proxy is configured, so forwarding should be enabled.</summary>
    public bool Enabled => TrustedProxies.Count > 0;

    /// <summary>
    /// Parses <see cref="TrustedProxies"/> into single addresses and networks.
    /// </summary>
    /// <param name="proxies">Receives the entries that named one address.</param>
    /// <param name="networks">Receives the entries that named a CIDR network.</param>
    /// <param name="invalid">Receives the entries that parsed as neither.</param>
    public void ParseTrustedProxies(
        out List<IPAddress> proxies,
        out List<(IPAddress Prefix, int Length)> networks,
        out List<string> invalid)
    {
        proxies = [];
        networks = [];
        invalid = [];

        foreach (var entry in TrustedProxies)
        {
            var text = entry?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var slash = text.IndexOf('/');
            if (slash < 0)
            {
                if (IPAddress.TryParse(text, out var address))
                {
                    proxies.Add(address);
                }
                else
                {
                    invalid.Add(text);
                }

                continue;
            }

            // CIDR: the prefix must parse and the length must fit the address family it was written in.
            var prefixText = text[..slash];
            var lengthText = text[(slash + 1)..];
            if (!IPAddress.TryParse(prefixText, out var prefix)
                || !int.TryParse(lengthText, out var length)
                || length < 0
                || length > (prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))
            {
                invalid.Add(text);
                continue;
            }

            networks.Add((prefix, length));
        }
    }
}
