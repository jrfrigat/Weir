namespace Weir.Host.Security;

/// <summary>Where a client certificate reached Weir from.</summary>
public enum ClientCertificateSource
{
    /// <summary>Off the TLS connection: Kestrel terminated TLS and did the handshake itself.</summary>
    Connection,

    /// <summary>Out of a header set by a trusted reverse proxy, which terminated TLS instead.</summary>
    ForwardedHeader,
}

/// <summary>
/// What Weir knows about the client certificate on one request, reduced to the facts a key's policy
/// matches on.
/// <para>
/// It holds strings rather than the certificate itself on purpose. <see cref="Weir.Contracts"/> is
/// browser-safe and carries no cryptography, so <c>ApiKeyMtlsPolicy</c> compares a thumbprint, an
/// issuer thumbprint and a subject; this is the server side of that bargain, read once per request
/// and handed to the policy.
/// </para>
/// </summary>
/// <param name="Thumbprint">The presented certificate's own thumbprint.</param>
/// <param name="IssuerThumbprint">
/// The issuing certificate's thumbprint, or null when the chain did not build far enough to name it.
/// Null is not "any issuer": a policy that pins an issuer will not match it, which is the right way
/// round - not being able to check the issuer must not read as the issuer being right.
/// </param>
/// <param name="Subject">The certificate's subject distinguished name.</param>
/// <param name="Issuer">The certificate's issuer distinguished name, for logs and the admin UI.</param>
/// <param name="ChainVerified">
/// Whether the chain verified against the configured authorities. False when it did not, which only
/// reaches a request at all because <c>Weir:Mtls:AllowUnverifiedChain</c> was set.
/// </param>
/// <param name="Source">Where the certificate arrived from.</param>
public sealed record ClientCertificateInfo(
    string Thumbprint,
    string? IssuerThumbprint,
    string Subject,
    string Issuer,
    bool ChainVerified,
    ClientCertificateSource Source);
