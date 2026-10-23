using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Host.Options;

namespace Weir.Host.Security;

/// <summary>
/// Reads the request's client certificate once, early, and leaves the result on the request for
/// everything downstream.
/// <para>
/// It has to run <em>before</em> <c>UseForwardedHeaders</c>, and that is the whole reason it exists
/// as its own middleware. A certificate forwarded by a proxy may only be believed when the request
/// actually came from a trusted proxy, and the only place that question can still be answered is
/// before the forwarded-headers middleware overwrites <c>Connection.RemoteIpAddress</c> with the
/// caller's address. Asked later - inside the authenticator, say - the check would compare the
/// client's own address against the proxy list, always fail to match, and so quietly either refuse
/// every forwarded certificate or, worse if written the other way round, accept any.
/// </para>
/// </summary>
public static class ClientCertificateMiddleware
{
    /// <summary>Key the parsed certificate facts are stored under on the request.</summary>
    private const string ItemKey = "Weir.ClientCertificate";

    /// <summary>
    /// The client certificate facts for this request, or null when none was presented, none was
    /// believed, or mTLS is switched off.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The certificate facts, or null.</returns>
    public static ClientCertificateInfo? GetClientCertificate(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) ? value as ClientCertificateInfo : null;
    }

    /// <summary>
    /// Registers the certificate reader. A no-op pipeline step when <c>Weir:Mtls</c> is off, so a
    /// deployment that does not use mTLS pays one comparison per request and nothing else.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IApplicationBuilder UseWeirClientCertificate(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetRequiredService<IOptions<MtlsOptions>>().Value;
        if (!options.Enabled)
        {
            return app;
        }

        var reader = app.ApplicationServices.GetRequiredService<ClientCertificateReader>();
        var network = app.ApplicationServices.GetRequiredService<IOptions<NetworkOptions>>().Value;
        var log = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Weir.Security.Mtls");

        // Parsed once, here, rather than per request: these entries do not change while the host runs.
        network.ParseTrustedProxies(out var trustedProxies, out var parsedNetworks, out _);
        var trustedNetworks = new List<IPNetwork>(parsedNetworks.Count);
        foreach (var (prefix, length) in parsedNetworks)
        {
            trustedNetworks.Add(new IPNetwork(prefix, length));
        }

        var header = options.ForwardedCertificateHeader;

        return app.Use(async (context, next) =>
        {
            var connectionCertificate = context.Connection.ClientCertificate;
            if (connectionCertificate is not null)
            {
                // TLS terminated here. This is the certificate the handshake actually validated, so it
                // always wins over a header - a proxy header on a directly-TLS connection would be a
                // second, weaker claim about the same request.
                context.Items[ItemKey] = reader.Read(connectionCertificate);
            }
            else if (!string.IsNullOrWhiteSpace(header) && context.Request.Headers.TryGetValue(header, out var forwarded))
            {
                var remote = context.Connection.RemoteIpAddress;
                if (IsTrustedProxy(remote, trustedProxies, trustedNetworks))
                {
                    context.Items[ItemKey] = reader.ReadForwarded(forwarded.ToString());
                }
                else
                {
                    // Deliberately not an outright refusal: the request simply has no certificate, and
                    // whatever needs one refuses it on its own terms. But it is logged, because a
                    // forged certificate header is an attempt at exactly the control mTLS provides.
                    Log.MtlsForwardedCertificateFromUntrustedPeer(log, header, remote?.ToString() ?? "unknown");
                }
            }

            await next(context);
        });
    }

    /// <summary>
    /// Whether the request's own peer is one of the configured reverse proxies. With no proxies
    /// configured nothing is trusted, which is the same default <c>Weir:Network</c> applies to
    /// forwarded headers.
    /// </summary>
    /// <param name="remote">The peer's address, as the socket reports it.</param>
    /// <param name="proxies">Trusted single addresses.</param>
    /// <param name="networks">Trusted networks.</param>
    /// <returns>True when the peer may speak for a client.</returns>
    private static bool IsTrustedProxy(IPAddress? remote, List<IPAddress> proxies, List<IPNetwork> networks)
    {
        if (remote is null)
        {
            return false;
        }

        // A dual-stack listener reports an IPv4 peer as ::ffff:a.b.c.d, while an operator writes the
        // proxy's address as a.b.c.d. Compare both forms rather than make the configuration depend on
        // how the socket was bound.
        var mapped = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;

        foreach (var proxy in proxies)
        {
            if (proxy.Equals(remote) || proxy.Equals(mapped))
            {
                return true;
            }
        }

        foreach (var network in networks)
        {
            if (network.Contains(remote) || network.Contains(mapped))
            {
                return true;
            }
        }

        return false;
    }
}
