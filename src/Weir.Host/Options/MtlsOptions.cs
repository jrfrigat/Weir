using System.Security.Cryptography.X509Certificates;

namespace Weir.Host.Options;

/// <summary>
/// How hard the server insists on a client certificate, bound from <c>Weir:Mtls:Mode</c>. Named
/// <c>MtlsMode</c> rather than <c>ClientCertificateMode</c> because Kestrel already has a type of
/// that name and both namespaces meet in the host's composition root.
/// </summary>
public enum MtlsMode
{
    /// <summary>
    /// mTLS is off. No certificate is requested, none is read, and a key whose policy asks for one
    /// cannot be satisfied. This is the default, so an existing deployment is untouched.
    /// </summary>
    None,

    /// <summary>
    /// A certificate is requested and read when presented, but a request without one is not refused
    /// by the server. This is the mode per-key policies are meant for: the key decides.
    /// </summary>
    Allow,

    /// <summary>
    /// Every data-plane request must present an acceptable certificate, whatever its key says. The
    /// refusal happens per request rather than during the TLS handshake - see
    /// <see cref="MtlsOptions"/> for why.
    /// </summary>
    Require,
}

/// <summary>
/// Client-certificate (mTLS) settings, bound from <c>Weir:Mtls</c>. Off by default: with
/// <see cref="Mode"/> at <see cref="MtlsMode.None"/> nothing here runs and no request pays for it.
/// <para>
/// Weir has two ways to see a client certificate, because it has two ways to be deployed. With TLS
/// terminated by Kestrel the certificate arrives on the connection. With TLS terminated by a reverse
/// proxy - the common container deployment, and the one <c>docs/en/deployment.md</c> describes - the
/// connection belongs to the proxy, and the certificate can only arrive in a header the proxy sets.
/// That header is caller input, so it is honoured only when the request really came from a proxy
/// listed in <c>Weir:Network:TrustedProxies</c>: honouring it from anywhere else would let anyone
/// forge a client certificate with one header, which is worse than having no mTLS at all, because
/// the key policy would look like protection while being none.
/// </para>
/// <para>
/// Weir never sets Kestrel's <c>RequireCertificate</c>, even at <see cref="MtlsMode.Require"/>. The
/// same listener serves the admin PWA, and a TLS-level requirement would make an operator's browser
/// produce a client certificate just to open the dashboard. The requirement is applied per request
/// instead, which also makes it the same requirement behind a proxy, where Weir terminates no TLS at
/// all. A deployment that genuinely wants the handshake itself to refuse should split the surfaces
/// with <c>Weir:Ports</c> and configure that on the listener or the proxy.
/// </para>
/// </summary>
public sealed class MtlsOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Weir:Mtls";

    /// <summary>Default cap on the size of a forwarded certificate header, in bytes.</summary>
    public const int DefaultMaxForwardedCertificateBytes = 16 * 1024;

    /// <summary>How hard the server insists on a certificate. <see cref="MtlsMode.None"/> switches mTLS off.</summary>
    public MtlsMode Mode { get; set; } = MtlsMode.None;

    /// <summary>
    /// Paths of PEM or DER files holding the certificate authorities whose chains are accepted. When
    /// this names at least one file, the chain is verified against these roots <em>only</em>, not
    /// against the machine's trust store - which is the point for an internal CA: otherwise a
    /// certificate signed by any public CA would also verify.
    /// </summary>
    public IList<string> TrustedCaFiles { get; } = [];

    /// <summary>
    /// Thumbprints that at least one certificate in the built chain must match. A way to pin an
    /// issuer without deploying its file beside Weir. Empty does not pin.
    /// </summary>
    public IList<string> TrustedCaThumbprints { get; } = [];

    /// <summary>
    /// Whether to accept a certificate whose chain does not verify - a self-signed certificate in
    /// development, typically. This removes the server's half of the check and leaves only what the
    /// key's own policy matches on, so it is logged as a warning at startup: it must not reach
    /// production unnoticed.
    /// </summary>
    public bool AllowUnverifiedChain { get; set; }

    /// <summary>
    /// Revocation checking for the chain: <c>NoCheck</c> (the default), <c>Offline</c> or
    /// <c>Online</c>. The default is deliberate - a certificate from an internal CA usually has no
    /// reachable CRL or OCSP responder, and <c>Online</c> then does not check anything, it just
    /// stalls the handshake until the lookup times out.
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>
    /// Name of the header a trusted reverse proxy puts the client certificate in, for example
    /// <c>X-Client-Cert</c> (nginx: <c>proxy_set_header X-Client-Cert $ssl_client_escaped_cert</c>).
    /// Null or empty means no header is ever read, so TLS must terminate at Kestrel.
    /// </summary>
    public string? ForwardedCertificateHeader { get; set; }

    /// <summary>
    /// Largest forwarded certificate header Weir will try to parse. The header is caller input, so
    /// the parser is not handed a megabyte of it; a real certificate is a few kilobytes.
    /// </summary>
    public int MaxForwardedCertificateBytes { get; set; } = DefaultMaxForwardedCertificateBytes;

    /// <summary>Whether mTLS is switched on at all.</summary>
    public bool Enabled => Mode != MtlsMode.None;

    /// <summary>Whether a certificate is read from a proxy-set header.</summary>
    public bool ReadsForwardedHeader => Enabled && !string.IsNullOrWhiteSpace(ForwardedCertificateHeader);

    /// <summary>
    /// Checks the section for the mistakes that would otherwise show up as a silent "no certificate
    /// ever arrives": a CA file that is absent or unreadable, a thumbprint that is not hex, a
    /// nonsensical size cap.
    /// </summary>
    /// <param name="error">Receives the first problem found, in operator-facing wording.</param>
    /// <returns>True when the section is usable.</returns>
    public bool Validate(out string? error)
    {
        error = null;
        if (!Enabled)
        {
            return true;
        }

        if (MaxForwardedCertificateBytes <= 0)
        {
            error = "Weir:Mtls:MaxForwardedCertificateBytes must be greater than zero.";
            return false;
        }

        foreach (var file in TrustedCaFiles)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                error = "Weir:Mtls:TrustedCaFiles contains an empty entry.";
                return false;
            }

            if (!File.Exists(file))
            {
                error = $"Weir:Mtls:TrustedCaFiles names a file that does not exist: '{file}'.";
                return false;
            }
        }

        foreach (var thumbprint in TrustedCaThumbprints)
        {
            if (string.IsNullOrWhiteSpace(thumbprint) || !IsHexOnly(thumbprint))
            {
                error = $"Weir:Mtls:TrustedCaThumbprints contains an entry that is not a hex thumbprint: '{thumbprint}'.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Loads the configured certificate authorities. Called once at startup, after
    /// <see cref="Validate"/>, so a bad file has already been reported as a configuration error.
    /// </summary>
    /// <returns>The loaded authorities, empty when none are configured.</returns>
    public X509Certificate2Collection LoadTrustedAuthorities()
    {
        var collection = new X509Certificate2Collection();
        foreach (var file in TrustedCaFiles)
        {
            // PEM or DER, because an operator points this at whatever already sits beside their proxy
            // configuration. A PEM file may hold a whole bundle, so it is imported rather than loaded
            // as one certificate; anything without PEM armour is read as DER.
            var text = File.ReadAllText(file);
            if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                collection.ImportFromPem(text);
            }
            else
            {
                collection.Add(X509CertificateLoader.LoadCertificateFromFile(file));
            }
        }

        return collection;
    }

    /// <summary>Whether every character is a hex digit or the punctuation a thumbprint is shown with.</summary>
    /// <param name="value">The value to check.</param>
    /// <returns>True when the value holds at least one hex digit and nothing unexpected.</returns>
    private static bool IsHexOnly(string value)
    {
        var digits = 0;
        foreach (var c in value)
        {
            if (c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
            {
                digits++;
            }
            else if (c is not (' ' or ':' or '-'))
            {
                return false;
            }
        }

        return digits > 0;
    }
}
