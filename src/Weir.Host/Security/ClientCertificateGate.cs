using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Host.Options;

namespace Weir.Host.Security;

/// <summary>
/// The one question mTLS asks of a data-plane call: may this key be used over this connection?
/// <para>
/// It is a class of its own rather than a few lines inside
/// <see cref="ApiKeyAuthenticator"/> because the rule has to be testable on its own. The
/// authenticator needs an <c>HttpContext</c>, a cache, a clock and a flood guard before it will
/// answer anything; this takes a key and a certificate, which is what the rule is actually about.
/// </para>
/// <para>
/// Two levels answer two different questions and are kept apart deliberately.
/// <c>Weir:Mtls:Mode</c> answers "does this deployment let anyone in without a certificate at all",
/// and the key's own <c>ApiKeyMtlsPolicy</c> answers "which certificate does <em>this</em> key
/// accept". Collapsing them into one setting would mean an operator could not require a certificate
/// for one key without requiring it for every caller, which is the whole point of the per-key
/// policy.
/// </para>
/// </summary>
public sealed class ClientCertificateGate
{
    /// <summary>The server-wide mTLS mode.</summary>
    private readonly MtlsMode _mode;

    /// <summary>Logger for refusals, which are security events.</summary>
    private readonly ILogger _log;

    /// <summary>Creates the gate.</summary>
    /// <param name="options">The mTLS settings; only the mode is read.</param>
    /// <param name="loggerFactory">Factory for the security logger.</param>
    public ClientCertificateGate(IOptions<MtlsOptions> options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _mode = options.Value.Mode;
        _log = loggerFactory.CreateLogger("Weir.Security.Mtls");
    }

    /// <summary>
    /// Whether the key may be used with the certificate presented on this request. Refusals are
    /// logged as security events, the way a scope or grant refusal is.
    /// </summary>
    /// <param name="key">The resolved, enabled, unexpired key.</param>
    /// <param name="certificate">
    /// The certificate on this request, or null when none was presented, none was believed, or mTLS
    /// is switched off. The caller does not distinguish those: to this rule they are all "no
    /// certificate", and treating them differently would mean a failed check could read as a pass.
    /// </param>
    /// <returns>True when the call may proceed.</returns>
    public bool Allows(ApiKeyRecord key, ClientCertificateInfo? certificate)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_mode == MtlsMode.Require && certificate is null)
        {
            // The deployment requires one of every caller, whatever the key says.
            Log.MtlsCallRefused(_log, key.Prefix, "the deployment requires a client certificate (Weir:Mtls:Mode=Require) and none was presented");
            return false;
        }

        var policy = key.Mtls;
        if (policy is null)
        {
            // The key states no policy, so nothing beyond the server-wide mode applies. This is every
            // key that existed before per-key policies, and the path that must stay free.
            return true;
        }

        if (policy.Accepts(certificate?.Thumbprint, certificate?.IssuerThumbprint, certificate?.Subject))
        {
            return true;
        }

        Log.MtlsCallRefused(_log, key.Prefix, Reason(policy, certificate));
        return false;
    }

    /// <summary>
    /// Says why the policy refused, in words an operator can act on. The certificate's thumbprint and
    /// subject are named because they are what the policy is matched against and what has to be
    /// corrected; nothing else out of the certificate is logged.
    /// </summary>
    /// <param name="policy">The key's policy.</param>
    /// <param name="certificate">The presented certificate, or null.</param>
    /// <returns>The reason, safe to log.</returns>
    private static string Reason(Weir.Contracts.ApiKeyMtlsPolicy policy, ClientCertificateInfo? certificate)
    {
        if (certificate is null)
        {
            return policy.RequireCertificate
                ? "the key's policy requires a client certificate and none was presented"
                : "the key's policy names the certificates it accepts and none was presented";
        }

        return $"certificate {certificate.Thumbprint} (subject '{certificate.Subject}', issuer thumbprint "
            + $"{certificate.IssuerThumbprint ?? "unknown"}) matches nothing in the key's policy";
    }
}
