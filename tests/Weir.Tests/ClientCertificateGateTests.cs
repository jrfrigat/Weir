using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Host.Options;
using Weir.Host.Security;
using Xunit;

namespace Weir.Tests;

// The whole point of a per-key mTLS policy is that a key accepts one set of certificates and no
// others, and the failure mode to be afraid of is the quiet one: a policy that is configured, looks
// restrictive in the admin UI, and lets anything through. The gate is where that is decided for all
// three transports at once, so the table below is checked here rather than per door.
//
// Two levels are deliberately separate and are tested as such: Weir:Mtls:Mode answers "does this
// deployment let anyone in without a certificate", the key's policy answers "which certificate does
// THIS key accept".
public class ClientCertificateGateTests
{
    /// <summary>A certificate as the gate sees it: facts, not an X509 object.</summary>
    private static readonly ClientCertificateInfo Presented = new(
        Thumbprint: "AABBCCDD11223344",
        IssuerThumbprint: "EEFF001122334455",
        Subject: "CN=svc-orders, OU=payments",
        Issuer: "CN=Weir Test CA",
        ChainVerified: true,
        Source: ClientCertificateSource.Connection);

    [Fact]
    public void A_Key_With_No_Policy_Is_Allowed_Without_A_Certificate()
    {
        // Every key that existed before per-key policies looks like this. It must keep working.
        Assert.True(Gate(MtlsMode.None).Allows(Key(null), null));
        Assert.True(Gate(MtlsMode.Allow).Allows(Key(null), null));
    }

    [Fact]
    public void A_Key_With_No_Policy_Is_Refused_Without_A_Certificate_When_The_Deployment_Requires_One()
    {
        // Mode=Require is the blunt, server-wide level: it does not consult the key at all.
        Assert.False(Gate(MtlsMode.Require).Allows(Key(null), null));
        Assert.True(Gate(MtlsMode.Require).Allows(Key(null), Presented));
    }

    [Fact]
    public void A_Policy_That_Only_Requires_A_Certificate_Accepts_Any_Certificate()
    {
        var policy = new ApiKeyMtlsPolicy { RequireCertificate = true };

        Assert.False(Gate(MtlsMode.Allow).Allows(Key(policy), null));
        Assert.True(Gate(MtlsMode.Allow).Allows(Key(policy), Presented));
    }

    [Fact]
    public void A_Policy_Matches_By_Thumbprint_Ignoring_Case_And_Punctuation()
    {
        // An operator pastes a thumbprint out of a certificate viewer, which inserts spaces or colons
        // and may use either case. Refusing over punctuation would be a trap, not a security measure.
        var policy = new ApiKeyMtlsPolicy { Thumbprints = ["aa:bb:cc:dd 11 22 33 44"] };

        Assert.True(Gate(MtlsMode.Allow).Allows(Key(policy), Presented));
    }

    [Fact]
    public void A_Policy_Matches_By_Issuer_Thumbprint()
    {
        // Pinning the issuer is the useful case: one entry covers every certificate that CA will sign.
        var policy = new ApiKeyMtlsPolicy { IssuerThumbprints = ["EEFF001122334455"] };

        Assert.True(Gate(MtlsMode.Allow).Allows(Key(policy), Presented));
    }

    [Fact]
    public void A_Policy_Matches_By_Subject_Pattern()
    {
        Assert.True(Gate(MtlsMode.Allow).Allows(Key(new ApiKeyMtlsPolicy { SubjectPatterns = ["CN=svc-*"] }), Presented));
        Assert.True(Gate(MtlsMode.Allow).Allows(Key(new ApiKeyMtlsPolicy { SubjectPatterns = ["*OU=payments"] }), Presented));
        Assert.False(Gate(MtlsMode.Allow).Allows(Key(new ApiKeyMtlsPolicy { SubjectPatterns = ["CN=svc-billing*"] }), Presented));
    }

    [Fact]
    public void The_Three_Lists_Are_Alternatives_Not_Three_Demands()
    {
        // A certificate that matches by thumbprint must not also have to match a subject pattern: the
        // lists are three ways of naming the same trusted client. Read as a conjunction, "pin my CA
        // and list these thumbprints" would mean "and", which is not what anybody configuring it means.
        var policy = new ApiKeyMtlsPolicy
        {
            Thumbprints = ["AABBCCDD11223344"],
            SubjectPatterns = ["CN=something-else"],
        };

        Assert.True(Gate(MtlsMode.Allow).Allows(Key(policy), Presented));
    }

    [Fact]
    public void A_Policy_Refuses_A_Certificate_It_Does_Not_Name()
    {
        var policy = new ApiKeyMtlsPolicy { Thumbprints = ["0000000000000000"] };

        Assert.False(Gate(MtlsMode.Allow).Allows(Key(policy), Presented));
    }

    [Fact]
    public void A_Policy_That_Names_Certificates_Refuses_A_Request_With_None()
    {
        // RequireCertificate is not set here. Naming the certificates it accepts already implies one is
        // needed; without this a key could be bypassed by presenting nothing at all.
        var policy = new ApiKeyMtlsPolicy { Thumbprints = ["AABBCCDD11223344"] };

        Assert.False(Gate(MtlsMode.Allow).Allows(Key(policy), null));
    }

    [Fact]
    public void An_Unknown_Issuer_Thumbprint_Does_Not_Satisfy_An_Issuer_Pin()
    {
        // The chain did not build far enough to name the issuer. "Could not check the issuer" must not
        // read as "the issuer is right", so a policy that pins one refuses.
        var unknownIssuer = Presented with { IssuerThumbprint = null };
        var policy = new ApiKeyMtlsPolicy { IssuerThumbprints = ["EEFF001122334455"] };

        Assert.False(Gate(MtlsMode.Allow).Allows(Key(policy), unknownIssuer));
    }

    [Fact]
    public void A_Policy_Cannot_Be_Satisfied_While_MTLS_Is_Switched_Off()
    {
        // With Weir:Mtls:Mode=None no certificate ever reaches a request, so a key whose policy asks
        // for one is unusable. The refusal is the right direction for that mistake to fall, and it is
        // why the admin UI warns when a policy is saved while mTLS is off.
        var policy = new ApiKeyMtlsPolicy { RequireCertificate = true };

        Assert.False(Gate(MtlsMode.None).Allows(Key(policy), null));
    }

    [Fact]
    public void An_Empty_Policy_Allows_Exactly_What_No_Policy_Allows()
    {
        var empty = new ApiKeyMtlsPolicy();

        Assert.True(empty.IsEmpty);
        Assert.True(Gate(MtlsMode.Allow).Allows(Key(empty), null));
        Assert.True(Gate(MtlsMode.Allow).Allows(Key(empty), Presented));
    }

    /// <summary>Builds a gate with the given server-wide mode.</summary>
    /// <param name="mode">The mode to configure.</param>
    /// <returns>The gate.</returns>
    private static ClientCertificateGate Gate(MtlsMode mode) =>
        new(Options.Create(new MtlsOptions { Mode = mode }), NullLoggerFactory.Instance);

    /// <summary>Builds a minimal enabled key record carrying the given policy.</summary>
    /// <param name="policy">The key's client-certificate policy, or null for none.</param>
    /// <returns>The key record.</returns>
    private static ApiKeyRecord Key(ApiKeyMtlsPolicy? policy) => new()
    {
        Name = "probe",
        Prefix = "wk_test_aa",
        Hash = "hash",
        Enabled = true,
        Mtls = policy,
    };
}
