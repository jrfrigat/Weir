using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Weir.Tests;

// The sign-in throttle keys on the caller's address, so it is only per-client if Weir sees the real
// one. Behind a reverse proxy the socket belongs to the proxy, which would put every admin in the world
// in one bucket: five bad passwords from an anonymous caller would lock everyone out, and the throttle
// would stop slowing brute force down because all attackers would share the bucket too.
//
// The fix is Weir:Network:TrustedProxies, and it has two halves that have to hold together - forwarding
// on when the proxy is named, and X-Forwarded-For ignored when it is not. The second half matters just
// as much: honouring the header from an untrusted caller is worse than the bug, since an attacker would
// put a fresh address in every request and never be throttled at all. One test each.
public class LoginThrottleForwardedTests
{
    private static readonly IPAddress Proxy = IPAddress.Parse("127.0.0.1");

    [Fact]
    public async Task Behind_A_Trusted_Proxy_Each_Forwarded_Client_Is_Throttled_On_Its_Own()
    {
        using var factory = new HostFactory(trustProxy: true);

        // Burn this caller's attempts. MaxFailedLogins defaults to 5.
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(factory, "admin", "wrong-password", forwardedFor: "203.0.113.10");
        }

        var attacker = await LoginAsync(factory, "admin", "wrong-password", forwardedFor: "203.0.113.10");
        Assert.True(attacker.Status == StatusCodes.Status429TooManyRequests, $"expected 429, got {attacker.Status}: {attacker.Body}");

        // A different caller behind the same proxy must be untouched by that: this is the whole point,
        // and it is what fails when the throttle keys on the proxy's socket instead.
        var innocent = await LoginAsync(factory, "admin", "admin-password", forwardedFor: "203.0.113.20");
        Assert.True(innocent.Status == StatusCodes.Status200OK, $"expected 200, got {innocent.Status}: {innocent.Body}");
    }

    [Fact]
    public async Task Without_A_Trusted_Proxy_A_Forged_Forwarded_Header_Does_Not_Buy_A_Fresh_Bucket()
    {
        using var factory = new HostFactory(trustProxy: false);

        // One attacker, a new address claimed every time. Unless the header is ignored, each request
        // lands in its own bucket and the throttle never fires.
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(factory, "admin", "wrong-password", forwardedFor: $"203.0.113.{i + 1}");
        }

        var next = await LoginAsync(factory, "admin", "wrong-password", forwardedFor: "203.0.113.99");
        Assert.True(next.Status == StatusCodes.Status429TooManyRequests, $"expected 429, got {next.Status}: {next.Body}");
    }

    /// <summary>Posts a login through the test server, controlling the socket address and the forwarded header.</summary>
    /// <param name="factory">The host under test.</param>
    /// <param name="username">The username to send.</param>
    /// <param name="password">The password to send.</param>
    /// <param name="forwardedFor">The address to claim in <c>X-Forwarded-For</c>.</param>
    /// <returns>The response status, and the body to name in an assertion when it is not what we expect.</returns>
    private static async Task<(int Status, string Body)> LoginAsync(HostFactory factory, string username, string password, string forwardedFor)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { username, password });

        var context = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/admin/api/auth/login";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            // Minimal APIs ask this feature whether a body exists before binding one, and a context built
            // by SendAsync has no such feature - without it the endpoint reports "no body was provided".
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyIsPresent());
            // Every request arrives on the same socket, exactly as it would behind a proxy.
            context.Connection.RemoteIpAddress = Proxy;
        });

        // TestServer hands back its own forward-only response stream, so read it as it is.
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, text);
    }

    /// <summary>Tells the request pipeline the context carries a body, which SendAsync does not declare on its own.</summary>
    private sealed class BodyIsPresent : IHttpRequestBodyDetectionFeature
    {
        /// <summary>Always true: every request this harness builds posts JSON.</summary>
        public bool CanHaveBody => true;
    }

    /// <summary>Boots the host on a throwaway SQLite control plane, optionally trusting the test's socket as a proxy.</summary>
    /// <param name="trustProxy">When true, names the loopback socket in Weir:Network:TrustedProxies.</param>
    private sealed class HostFactory(bool trustProxy) : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"weir-throttle-{Guid.NewGuid():N}.db");

        /// <summary>Points the host at the throwaway database and sets the proxy trust under test.</summary>
        /// <param name="builder">The host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Weir:Admin:Username", "admin");
            builder.UseSetting("Weir:Admin:Password", "admin-password");
            builder.UseSetting("Weir:Jwt:SigningKey", "throttle-test-signing-key-0123456789");
            if (trustProxy)
            {
                builder.UseSetting("Weir:Network:TrustedProxies:0", Proxy.ToString());
            }
        }

        /// <summary>Deletes the throwaway database.</summary>
        /// <param name="disposing">True when called from Dispose.</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try
            {
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp database file.
            }
        }
    }
}
