using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Host.Options;

namespace Weir.Host.Security;

/// <summary>
/// Turns a presented certificate into the facts a key's policy matches on, and decides whether its
/// chain is acceptable to the server.
/// <para>
/// Chain building is the expensive part of this path, and on an HTTP/2 connection many requests
/// present the very same certificate, so the derived facts are memoized by the certificate's
/// content. What is cached is facts <em>about a certificate</em> - its thumbprint, its issuer's
/// thumbprint, its subject, whether its chain verified - never an authorization decision: that
/// depends on which key is calling and is taken per request, where it belongs.
/// </para>
/// </summary>
public sealed class ClientCertificateReader
{
    /// <summary>How long a certificate's derived facts stay memoized.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>The mTLS settings.</summary>
    private readonly MtlsOptions _options;

    /// <summary>The configured authorities, loaded once at startup.</summary>
    private readonly X509Certificate2Collection _authorities;

    /// <summary>Cache for derived certificate facts, keyed by certificate content.</summary>
    private readonly IMemoryCache _cache;

    /// <summary>Logger for rejected chains and unusable headers.</summary>
    private readonly ILogger _log;

    /// <summary>Creates the reader and loads the configured authorities.</summary>
    /// <param name="options">The mTLS settings.</param>
    /// <param name="cache">Cache for derived certificate facts.</param>
    /// <param name="loggerFactory">Factory for the security logger.</param>
    public ClientCertificateReader(IOptions<MtlsOptions> options, IMemoryCache cache, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options.Value;
        _cache = cache;
        _log = loggerFactory.CreateLogger("Weir.Security.Mtls");
        _authorities = _options.Enabled ? _options.LoadTrustedAuthorities() : [];
    }

    /// <summary>
    /// Reads the facts off a certificate presented on the connection, or returns null when its chain
    /// is not acceptable to the server.
    /// </summary>
    /// <param name="certificate">The certificate Kestrel accepted on the connection.</param>
    /// <returns>The derived facts, or null when the certificate is refused.</returns>
    public ClientCertificateInfo? Read(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Describe(certificate, ClientCertificateSource.Connection);
    }

    /// <summary>
    /// Parses a certificate out of the value a trusted proxy set, in any of the three shapes proxies
    /// actually produce: URL-escaped PEM (nginx's <c>$ssl_client_escaped_cert</c>), plain PEM, or
    /// bare base64 with no armour.
    /// </summary>
    /// <param name="headerValue">The header value, already known to come from a trusted proxy.</param>
    /// <returns>The derived facts, or null when the value is unusable or the chain is refused.</returns>
    public ClientCertificateInfo? ReadForwarded(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        // The header is caller input even when the hop is trusted: a proxy can be misconfigured to pass
        // through whatever a client sent. Cap it before the parser sees it.
        if (headerValue.Length > _options.MaxForwardedCertificateBytes)
        {
            Log.MtlsForwardedCertificateUnusable(_log, "the value is larger than Weir:Mtls:MaxForwardedCertificateBytes");
            return null;
        }

        X509Certificate2 certificate;
        try
        {
            certificate = ParseForwarded(headerValue);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            // The reason is the proxy's configuration, not a secret, but the value itself is not logged:
            // a certificate is not a secret either, and a header's worth of it in every log line is noise.
            Log.MtlsForwardedCertificateUnusable(_log, ex.Message);
            return null;
        }

        using (certificate)
        {
            return Describe(certificate, ClientCertificateSource.ForwardedHeader);
        }
    }

    /// <summary>Parses the header value into a certificate, unescaping it first when it is escaped.</summary>
    /// <param name="headerValue">The header value.</param>
    /// <returns>The parsed certificate.</returns>
    private static X509Certificate2 ParseForwarded(string headerValue)
    {
        var text = headerValue.Trim();

        // nginx's $ssl_client_escaped_cert is percent-encoded, and the newlines inside PEM armour come
        // through as %0A. Unescape only when there is something escaped, so a plain PEM is untouched.
        if (text.Contains('%', StringComparison.Ordinal))
        {
            text = Uri.UnescapeDataString(text);
        }

        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            return X509Certificate2.CreateFromPem(text);
        }

        // Bare base64: some proxies strip the armour. Whitespace inside it is normal when the value
        // was wrapped, and Convert.FromBase64String tolerates it.
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(text));
    }

    /// <summary>
    /// Derives the facts about one certificate, through the memo. Returns null when the chain is not
    /// acceptable and <see cref="MtlsOptions.AllowUnverifiedChain"/> is not set.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <param name="source">Where it arrived from.</param>
    /// <returns>The derived facts, or null when the certificate is refused.</returns>
    private ClientCertificateInfo? Describe(X509Certificate2 certificate, ClientCertificateSource source)
    {
        // Keyed by the certificate's own bytes, so the key is the certificate's identity: an entry
        // cannot be made to answer for different content. The source is part of the key because it is
        // part of the answer, and the same certificate can legitimately arrive both ways.
        var key = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"weir:clientcert:{(int)source}:{Convert.ToHexString(SHA256.HashData(certificate.RawData))}");

        if (_cache.TryGetValue(key, out ClientCertificateInfo? cached))
        {
            return cached;
        }

        var info = Build(certificate, source);

        // A refusal is cached too, as a null entry: a client that keeps presenting a certificate Weir
        // will not accept should not make it rebuild the chain on every request.
        _cache.Set(key, info, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl, Size = 1 });
        return info;
    }

    /// <summary>Builds the chain, applies the server's own trust rules and reduces the result to facts.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <param name="source">Where it arrived from.</param>
    /// <returns>The derived facts, or null when the certificate is refused.</returns>
    private ClientCertificateInfo? Build(X509Certificate2 certificate, ClientCertificateSource source)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = _options.RevocationMode;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        if (_authorities.Count > 0)
        {
            // Custom root trust, not the machine store: for an internal CA that is the whole point,
            // because otherwise a certificate signed by any public authority verifies just as well.
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(_authorities);
        }

        var verified = chain.Build(certificate);
        if (!verified && !_options.AllowUnverifiedChain)
        {
            Log.MtlsChainRejected(_log, certificate.Thumbprint, ChainReason(chain));
            return null;
        }

        var issuerThumbprint = chain.ChainElements.Count > 1 ? chain.ChainElements[1].Certificate.Thumbprint : null;

        if (_options.TrustedCaThumbprints.Count > 0 && !PinnedAuthorityPresent(chain))
        {
            Log.MtlsChainRejected(_log, certificate.Thumbprint, "no certificate in the chain matches Weir:Mtls:TrustedCaThumbprints");
            return null;
        }

        return new ClientCertificateInfo(
            certificate.Thumbprint,
            issuerThumbprint,
            certificate.Subject,
            certificate.Issuer,
            verified,
            source);
    }

    /// <summary>Whether some certificate in the chain matches one of the pinned authority thumbprints.</summary>
    /// <param name="chain">The built chain.</param>
    /// <returns>True when a pinned authority is present.</returns>
    private bool PinnedAuthorityPresent(X509Chain chain)
    {
        foreach (var element in chain.ChainElements)
        {
            foreach (var pinned in _options.TrustedCaThumbprints)
            {
                if (Weir.Contracts.ApiKeyMtlsPolicy.ThumbprintEquals(pinned, element.Certificate.Thumbprint))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Summarizes why a chain did not build, in the status words the platform reports. Safe to log:
    /// it names the failure, not the caller's data.
    /// </summary>
    /// <param name="chain">The built chain.</param>
    /// <returns>A comma-separated list of status flags, or a placeholder when there are none.</returns>
    private static string ChainReason(X509Chain chain)
    {
        if (chain.ChainStatus.Length == 0)
        {
            return "the chain reported no status";
        }

        var flags = new List<string>(chain.ChainStatus.Length);
        foreach (var status in chain.ChainStatus)
        {
            flags.Add(status.Status.ToString());
        }

        return string.Join(", ", flags);
    }
}
