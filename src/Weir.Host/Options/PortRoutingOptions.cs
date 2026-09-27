using System.Globalization;

namespace Weir.Host.Options;

/// <summary>
/// Optional split of Weir's two surfaces onto separate listeners, bound from <c>Weir:Ports</c>.
/// <para>
/// By default both ports are unset and nothing changes: the host listens wherever
/// <c>ASPNETCORE_URLS</c> says and serves every surface - the endpoint API, the admin API, the
/// dashboard hub, the admin PWA and health - from that one port. Set <see cref="AdminPort"/> to move
/// the admin surface (admin API, hub and PWA) onto a listener of its own, set
/// <see cref="DataPlanePort"/> to move the endpoint API (plus <c>/ws</c> and gRPC) onto one, or set
/// both to keep them apart for good. That is what makes different network policies possible in front
/// of the two: an edge proxy can publish only the data-plane port, the way an admin interface and a
/// published application sit on separate ports behind a proxy.
/// </para>
/// <para>
/// The port a surface keeps when only the other one is named is <see cref="MainPort"/>; unset, it is
/// the port the process already listens on, read from the configured URLs.
/// </para>
/// </summary>
public sealed class PortRoutingOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Weir:Ports";

    /// <summary>The port the default listener uses when a surface is not given a port of its own.</summary>
    private const int DefaultMainPort = 8080;

    /// <summary>
    /// The port the admin surface (admin API, dashboard hub and admin PWA) answers on. Unset keeps it
    /// on the same listener as the data plane.
    /// </summary>
    public int? AdminPort { get; set; }

    /// <summary>
    /// The port the data plane (the endpoint API, <c>/ws</c> and gRPC) answers on. Unset keeps it on
    /// the same listener as the admin surface.
    /// </summary>
    public int? DataPlanePort { get; set; }

    /// <summary>
    /// The port a surface keeps when the other one was given its own and this one was not. Unset
    /// reads it from the configured URLs (the first <c>http</c> address, so the plain-HTTP listener
    /// Weir binds is the one it replaces; otherwise the first address), and falls back to 8080.
    /// </summary>
    public int? MainPort { get; set; }

    /// <summary>
    /// True when at least one surface was given a port of its own, which is what turns the split on:
    /// Weir then binds both listeners itself instead of relying on the configured URLs.
    /// </summary>
    public bool Enabled => AdminPort.HasValue || DataPlanePort.HasValue;

    /// <summary>
    /// Reads the port the process listens on now from the configured URLs, so a surface that was not
    /// given a port of its own keeps the one it has.
    /// </summary>
    /// <param name="urls">The configured URL list (<c>ASPNETCORE_URLS</c> / <c>urls</c>), or null.</param>
    /// <param name="fallback">The port to return when nothing usable can be read.</param>
    /// <returns>The port the default listener uses.</returns>
    /// <remarks>
    /// The first plain-HTTP address wins: the listeners Weir adds when the split is on are plain
    /// HTTP, so that is the address the split replaces. TLS is terminated at the proxy in that
    /// deployment, which is how Weir is normally run.
    /// </remarks>
    public static int ResolveMainPort(string? urls, int fallback = DefaultMainPort)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return fallback;
        }

        var first = fallback;
        var found = false;
        foreach (var entry in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryReadPort(entry, out var port))
            {
                continue;
            }

            if (entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return port;
            }

            if (!found)
            {
                first = port;
                found = true;
            }
        }

        return found ? first : fallback;
    }

    /// <summary>Reads the port out of one URL such as <c>http://+:8080</c> or <c>https://host:52274</c>.</summary>
    /// <param name="url">The URL to read.</param>
    /// <param name="port">Receives the port when one was written.</param>
    /// <returns>True when the URL carried a port.</returns>
    /// <remarks>
    /// Parsed by hand rather than with <see cref="Uri"/>: a Kestrel URL may name <c>+</c> or <c>*</c>
    /// as its host, which <see cref="Uri"/> does not have to accept, and all that is wanted here is
    /// the trailing port.
    /// </remarks>
    private static bool TryReadPort(string url, out int port)
    {
        port = 0;

        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var authority = scheme >= 0 ? url[(scheme + 3)..] : url;

        var slash = authority.IndexOf('/');
        if (slash >= 0)
        {
            authority = authority[..slash];
        }

        var credentials = authority.LastIndexOf('@');
        if (credentials >= 0)
        {
            authority = authority[(credentials + 1)..];
        }

        var colon = authority.LastIndexOf(':');
        return colon >= 0
            && int.TryParse(authority[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port > 0;
    }
}
