using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Host.Audit;
using Weir.Host.Security;
using Xunit;

namespace Weir.Tests;

// Per-key mTLS, end to end through the real host pipeline. ClientCertificateGateTests already covers
// the rule itself (and is mutation-checked); what is checked here is the wiring around it, which is
// where the quiet failures live:
//
//  - the refusal must be 403, not 401. A caller holding a perfectly good key that is told "present
//    valid credentials" will go and reissue the key instead of fixing the certificate.
//  - a certificate forwarded in a header must be believed only from a trusted proxy. Believed from
//    anywhere, mTLS is forged with one header and the key policy is decoration.
//  - the refusal must reach the audit naming the key, or a run of them cannot be traced to the key
//    whose certificate is wrong.
//
// The certificate arrives in the forwarded header rather than on the connection because TestServer
// terminates no TLS. That is not a gap in what is covered: both paths meet in
// ClientCertificateReader.Describe, and nothing downstream - authenticator, gate, transports - knows
// which one it came from. What is NOT covered here is the socket read itself
// (Connection.ClientCertificate) and a real Kestrel handshake; those were exercised by hand against a
// running host with a real CA while TASK-70 was built.
public class ClientCertificatePolicyTests
{
    /// <summary>The address the tests present as the caller's, and the one configured as the proxy.</summary>
    private const string TrustedProxy = "10.9.8.7";

    /// <summary>An address that is deliberately not the configured proxy.</summary>
    private const string UntrustedPeer = "10.0.0.99";

    [Fact]
    public async Task A_Key_Without_A_Policy_Still_Works_Without_A_Certificate()
    {
        using var factory = new HostFactory(MtlsMode: "Allow");
        var key = await factory.CreateKeyAsync("plain");
        var client = factory.CreateClient();

        var response = await CallAsync(client, key);

        // Not merely "not 403": the call has to reach route resolution, and the only honest proof of
        // that is the 404 for a route this host does not have. "Not 403" alone would also pass if the
        // request had failed for some entirely different reason.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Require_Mode_Refuses_A_Request_With_No_Certificate_As_Forbidden()
    {
        using var factory = new HostFactory(MtlsMode: "Require");
        var key = await factory.CreateKeyAsync("needs-cert");
        var client = factory.CreateClient();

        var response = await CallAsync(client, key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Require_Mode_Lets_A_Forwarded_Certificate_Through()
    {
        using var certificate = NewCertificate("CN=svc-orders, OU=payments");
        using var factory = new HostFactory(MtlsMode: "Require");
        var key = await factory.CreateKeyAsync("needs-cert");
        var client = factory.CreateClient();

        var response = await CallAsync(client, key, certificate);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_Forwarded_Certificate_From_An_Untrusted_Peer_Is_Ignored()
    {
        using var certificate = NewCertificate("CN=svc-orders");
        // Same certificate, same header, same everything - only the peer differs from the test above.
        // If the trust check were skipped or asked after the forwarded-headers middleware rewrote the
        // address, this would pass as 404 and mTLS would be forgeable with one header.
        using var factory = new HostFactory(MtlsMode: "Require", PeerAddress: UntrustedPeer);
        var key = await factory.CreateKeyAsync("needs-cert");
        var client = factory.CreateClient();

        var response = await CallAsync(client, key, certificate);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_Key_Pinned_To_A_Thumbprint_Accepts_That_Certificate_And_No_Other()
    {
        using var mine = NewCertificate("CN=svc-orders");
        using var someone_elses = NewCertificate("CN=svc-orders");
        using var factory = new HostFactory(MtlsMode: "Allow");
        var key = await factory.CreateKeyAsync(
            "pinned",
            new ApiKeyMtlsPolicy { Thumbprints = [mine.Thumbprint] });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(client, key, mine)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(client, key, someone_elses)).StatusCode);

        // The two certificates share a subject on purpose: what separates them is the thumbprint, so a
        // policy matching on anything coarser would accept both and this test would catch it.
        Assert.Equal(someone_elses.Subject, mine.Subject);
    }

    [Fact]
    public async Task A_Key_Pinned_To_A_Subject_Pattern_Accepts_A_Matching_Subject()
    {
        using var matching = NewCertificate("CN=svc-orders, OU=payments");
        using var other = NewCertificate("CN=reporting");
        using var factory = new HostFactory(MtlsMode: "Allow");
        var key = await factory.CreateKeyAsync(
            "by-subject",
            new ApiKeyMtlsPolicy { SubjectPatterns = ["CN=svc-*"] });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(client, key, matching)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(client, key, other)).StatusCode);
    }

    [Fact]
    public async Task The_Refusal_Reaches_The_Audit_Naming_The_Key()
    {
        using var factory = new HostFactory(MtlsMode: "Require");
        var key = await factory.CreateKeyAsync("audited");
        var client = factory.CreateClient();

        await CallAsync(client, key);

        var entry = Assert.Single(factory.Auditor.Entries, e => e.StatusCode == (int)HttpStatusCode.Forbidden);
        Assert.Equal("endpoint.call", entry.Category);
        Assert.Equal(OutcomeCodes.Error, entry.Outcome);
        // The key is known on this refusal - it authenticated and only its certificate was wrong - so
        // the entry has to say which key. Null here is what the WebSocket and gRPC doors used to record.
        Assert.Equal(key.Prefix, entry.Actor);
    }

    [Fact]
    public async Task A_WebSocket_Handshake_Is_Refused_And_Audited_With_The_Key()
    {
        using var factory = new HostFactory(MtlsMode: "Require");
        var key = await factory.CreateKeyAsync("ws-cert");
        var socketClient = factory.Server.CreateWebSocketClient();
        socketClient.ConfigureRequest = request =>
        {
            request.Headers["X-Api-Key"] = key.PlainText;
            request.Headers["X-Forwarded-For"] = TrustedProxy;
        };

        // A real handshake: only that reaches the point where the door authenticates.
        await Assert.ThrowsAnyAsync<Exception>(
            () => socketClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None));

        var entry = Assert.Single(factory.Auditor.Entries);
        Assert.Equal("websocket", entry.Detail);
        Assert.Equal((int)HttpStatusCode.Forbidden, entry.StatusCode);
        Assert.Equal(key.Prefix, entry.Actor);
    }

    /// <summary>Calls a route this host does not serve, so the answer reports how far the call got.</summary>
    /// <param name="client">The test client.</param>
    /// <param name="key">The key to authenticate with.</param>
    /// <param name="certificate">The certificate to forward, or null to send none.</param>
    /// <returns>The response.</returns>
    private static async Task<HttpResponseMessage> CallAsync(
        HttpClient client, TestKey key, X509Certificate2? certificate = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/anything")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", key.PlainText);
        if (certificate is not null)
        {
            // Percent-encoded PEM, which is what nginx's $ssl_client_escaped_cert produces.
            request.Headers.TryAddWithoutValidation("X-Client-Cert", Uri.EscapeDataString(ToPem(certificate)));
        }

        return await client.SendAsync(request);
    }

    /// <summary>Builds a throwaway self-signed certificate with the given subject.</summary>
    /// <param name="subject">The subject distinguished name.</param>
    /// <returns>The certificate.</returns>
    private static X509Certificate2 NewCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <summary>Renders a certificate as PEM, the way a proxy forwards one.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The PEM text.</returns>
    private static string ToPem(X509Certificate2 certificate) =>
        new string(PemEncoding.Write("CERTIFICATE", certificate.RawData));

    /// <summary>A key created for a test, with the plaintext the caller presents.</summary>
    /// <param name="PlainText">The secret to send in the header.</param>
    /// <param name="Prefix">The identifying prefix, as the audit records it.</param>
    private sealed record TestKey(string PlainText, string Prefix);

    /// <summary>
    /// Boots the host with mTLS on, a capturing auditor, and a known peer address.
    /// <para>
    /// The peer address matters more than it looks. TestServer leaves
    /// <c>Connection.RemoteIpAddress</c> unset, and the proxy-trust check compares exactly that - so
    /// without setting it, every forwarded certificate would be ignored and the "trusted proxy" test
    /// would pass while proving nothing. It is set by a startup filter, which runs ahead of the whole
    /// pipeline and therefore ahead of the certificate middleware, matching the real order.
    /// </para>
    /// </summary>
    /// <param name="MtlsMode">The server-wide mode to configure.</param>
    /// <param name="PeerAddress">The address the request appears to come from.</param>
    private sealed class HostFactory(string MtlsMode, string PeerAddress = TrustedProxy)
        : WebApplicationFactory<Program>
    {
        /// <summary>The throwaway control plane.</summary>
        private readonly TempSqliteDatabase _db = new("weir-mtls");

        /// <summary>What the host recorded in the audit.</summary>
        internal DataPlaneAuditTests.CapturingAuditor Auditor { get; } = new();

        /// <summary>Creates a key with an optional certificate policy.</summary>
        /// <param name="name">The key's label.</param>
        /// <param name="policy">The certificate policy, or null for none.</param>
        /// <returns>The key, with the plaintext to present.</returns>
        internal async Task<TestKey> CreateKeyAsync(string name, ApiKeyMtlsPolicy? policy = null)
        {
            var store = Services.GetRequiredService<IControlPlaneStore>();
            var (plainText, prefix, hash) = ApiKeyGenerator.Generate();
            await store.CreateApiKeyAsync(new ApiKeyCreate { Name = name, Mtls = policy }, hash, prefix);
            return new TestKey(plainText, prefix);
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", _db.ConnectionString);
            builder.UseSetting("Weir:Jwt:SigningKey", "mtls-test-signing-key-0123456789");
            builder.UseSetting("Weir:Mtls:Mode", MtlsMode);
            builder.UseSetting("Weir:Mtls:ForwardedCertificateHeader", "X-Client-Cert");
            // Self-signed test certificates chain to nothing, so the server's own chain check has to be
            // off for them to be read at all. That also exercises the flag.
            builder.UseSetting("Weir:Mtls:AllowUnverifiedChain", "true");
            builder.UseSetting("Weir:Network:TrustedProxies:0", TrustedProxy);
            builder.UseSetting("Weir:Audit:DataPlane", "true");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDataPlaneAuditor>();
                services.AddSingleton<IDataPlaneAuditor>(Auditor);
                services.AddSingleton<IStartupFilter>(new PeerAddressFilter(PeerAddress));
            });
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _db.Dispose();
        }
    }

    /// <summary>
    /// Puts a fixed address on every request's connection, ahead of everything else in the pipeline -
    /// which is where the real socket address is already present and where the certificate middleware
    /// reads it.
    /// </summary>
    /// <param name="address">The address to present as the peer's.</param>
    private sealed class PeerAddressFilter(string address) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);
            var peer = IPAddress.Parse(address);
            return app =>
            {
                app.Use(async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = peer;
                    await continuation(context);
                });
                next(app);
            };
        }
    }
}
