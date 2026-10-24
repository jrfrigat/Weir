using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Host.Security;
using Xunit;

namespace Weir.Tests;

// Per-key CORS, end to end. The thing worth testing is the shape the whole feature is built around: a
// CORS preflight carries no API key and cannot be made to, so the two halves of the exchange are
// answered from two different places - the preflight from the origin alone, the real request from the
// key that sent it. A test that only checked the preflight, or only the real request, would miss the
// case the design exists for.
//
// Two further things are checked because they have already been wrong once:
//  - Vary: Origin has to survive an error response. ProblemResults calls Response.Clear() before
//    writing a problem body, which drops anything set earlier in the pipeline.
//  - Access-Control-Allow-Origin must never be "*" together with credentials. The specification
//    forbids the pair and browsers refuse it, so a policy configured that way would look permissive
//    and allow nothing.
public class DataPlaneCorsTests
{
    /// <summary>The origin named by the server-wide default in these tests.</summary>
    private const string ServerOrigin = "https://server.example.com";

    /// <summary>The origin named by one key's own policy.</summary>
    private const string KeyOrigin = "https://keyb.example.com";

    [Fact]
    public async Task With_Nothing_Configured_No_Cors_Headers_Are_Sent()
    {
        using var factory = new HostFactory(ServerOrigins: null);
        var key = await factory.CreateKeyAsync("plain");
        var client = factory.CreateClient();

        var response = await CallAsync(client, key, ServerOrigin);

        Assert.Null(AllowOrigin(response));
        Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_Request_With_No_Origin_Is_Left_Alone()
    {
        using var factory = new HostFactory();
        var key = await factory.CreateKeyAsync("plain");
        var client = factory.CreateClient();

        var response = await CallAsync(client, key, origin: null);

        // Not a CORS request at all: no Vary either, which is the cheap exit the hot path depends on.
        Assert.Null(AllowOrigin(response));
        Assert.DoesNotContain("Origin", Vary(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_Server_Wide_Origin_List_Still_Works()
    {
        using var factory = new HostFactory();
        var key = await factory.CreateKeyAsync("plain");
        var client = factory.CreateClient();

        var response = await CallAsync(client, key, ServerOrigin);

        Assert.Equal(ServerOrigin, AllowOrigin(response));
        Assert.Contains("Origin", Vary(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_Origin_Nobody_Names_Gets_No_Allow_Header()
    {
        using var factory = new HostFactory();
        var key = await factory.CreateKeyAsync("plain");
        var client = factory.CreateClient();

        var response = await CallAsync(client, key, "https://nobody.example.com");

        Assert.Null(AllowOrigin(response));
        // Vary is still there: the answer did depend on the Origin, it just depended on it by refusing.
        Assert.Contains("Origin", Vary(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Preflight_Carrying_No_Key_Is_Answered_From_The_Key_Policy()
    {
        using var factory = new HostFactory();
        await factory.CreateKeyAsync("own-cors", new ApiKeyCorsPolicy
        {
            AllowedOrigins = [KeyOrigin],
            AllowedMethods = ["POST"],
            MaxAgeSeconds = 120,
        });
        var client = factory.CreateClient();

        // No key header at all - a browser does not send one on a preflight, and this is the whole
        // reason the origin index exists.
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/anything");
        request.Headers.TryAddWithoutValidation("Origin", KeyOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "x-api-key, content-type");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(KeyOrigin, AllowOrigin(response));
        Assert.Equal("POST", Header(response, "Access-Control-Allow-Methods"));
        Assert.Equal("120", Header(response, "Access-Control-Max-Age"));
        // Empty in the policy means "echo what was asked for": a key's own clients send X-Api-Key and
        // Content-Type, and making every operator list those before anything works is a trap.
        Assert.Equal("x-api-key, content-type", Header(response, "Access-Control-Allow-Headers"));
    }

    [Fact]
    public async Task A_Preflight_For_An_Origin_Nobody_Names_Gets_No_Allow_Header()
    {
        using var factory = new HostFactory();
        await factory.CreateKeyAsync("own-cors", new ApiKeyCorsPolicy { AllowedOrigins = [KeyOrigin] });
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/anything");
        request.Headers.TryAddWithoutValidation("Origin", "https://nobody.example.com");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        var response = await client.SendAsync(request);

        // 204 with no headers, not a refusal of our own: a status here would break non-browser callers
        // that send OPTIONS, and the browser already reads "no headers" as "not allowed".
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(AllowOrigin(response));
    }

    [Fact]
    public async Task A_Keys_Own_Policy_Replaces_The_Server_Default_Rather_Than_Adding_To_It()
    {
        using var factory = new HostFactory();
        var plain = await factory.CreateKeyAsync("plain");
        var owns = await factory.CreateKeyAsync("own-cors", new ApiKeyCorsPolicy { AllowedOrigins = [KeyOrigin] });
        var client = factory.CreateClient();

        // The four cases together are what "per key" means. Any three of them would also pass if the
        // policies were simply merged, which is exactly the mistake worth catching.
        Assert.Equal(ServerOrigin, AllowOrigin(await CallAsync(client, plain, ServerOrigin)));
        Assert.Null(AllowOrigin(await CallAsync(client, plain, KeyOrigin)));
        Assert.Equal(KeyOrigin, AllowOrigin(await CallAsync(client, owns, KeyOrigin)));
        Assert.Null(AllowOrigin(await CallAsync(client, owns, ServerOrigin)));
    }

    [Fact]
    public async Task A_Policy_That_Names_No_Origin_Closes_Cross_Origin_Access_For_That_Key_Alone()
    {
        using var factory = new HostFactory();
        // Present but empty: a deliberate "this key may not be called cross-origin", which the
        // server-wide default must not open back up. That is the difference from stating no policy.
        var closed = await factory.CreateKeyAsync("closed", new ApiKeyCorsPolicy { MaxAgeSeconds = 10 });
        var plain = await factory.CreateKeyAsync("plain");
        var client = factory.CreateClient();

        Assert.Null(AllowOrigin(await CallAsync(client, closed, ServerOrigin)));
        Assert.Equal(ServerOrigin, AllowOrigin(await CallAsync(client, plain, ServerOrigin)));
    }

    [Fact]
    public async Task Vary_Survives_An_Error_Response()
    {
        using var factory = new HostFactory();
        var client = factory.CreateClient();

        // No key: the 401 is written by ProblemResults, which calls Response.Clear() first. Anything
        // set on the way in is gone by then - which is how Vary went missing the first time.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/anything")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", ServerOrigin);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Origin", Vary(response), StringComparison.OrdinalIgnoreCase);
        // The unauthenticated answer still carries CORS headers on purpose: without them the browser
        // will not let the page read the 401, and the developer sees an opaque CORS error instead of
        // "the key is wrong". The headers grant nothing - the server already refused.
        Assert.Equal(ServerOrigin, AllowOrigin(response));
    }

    [Fact]
    public async Task Credentials_Are_Never_Answered_With_A_Wildcard_Origin()
    {
        using var factory = new HostFactory();
        var wildcardWithCredentials = await factory.CreateKeyAsync("creds", new ApiKeyCorsPolicy
        {
            AllowedOrigins = ["*"],
            AllowCredentials = true,
        });
        var wildcardAlone = await factory.CreateKeyAsync("any", new ApiKeyCorsPolicy { AllowedOrigins = ["*"] });
        var client = factory.CreateClient();

        var withCredentials = await CallAsync(client, wildcardWithCredentials, KeyOrigin);
        // Named, not "*": the specification forbids that pair, so the host decides it here rather than
        // trusting the configuration. The admin API refuses to save the pair; this is what happens if
        // it reaches the request path anyway.
        Assert.Equal(KeyOrigin, AllowOrigin(withCredentials));
        Assert.Equal("true", Header(withCredentials, "Access-Control-Allow-Credentials"));

        var withoutCredentials = await CallAsync(client, wildcardAlone, KeyOrigin);
        Assert.Equal("*", AllowOrigin(withoutCredentials));
    }

    [Fact]
    public async Task Exposed_Headers_Reach_The_Caller()
    {
        using var factory = new HostFactory();
        var key = await factory.CreateKeyAsync("exposes", new ApiKeyCorsPolicy
        {
            AllowedOrigins = [KeyOrigin],
            ExposedHeaders = ["X-Correlation-ID"],
        });
        var client = factory.CreateClient();

        var response = await CallAsync(client, key, KeyOrigin);

        Assert.Equal("X-Correlation-ID", Header(response, "Access-Control-Expose-Headers"));
    }

    /// <summary>Calls a route this host does not serve, with an optional origin.</summary>
    /// <param name="client">The test client.</param>
    /// <param name="key">The key to authenticate with.</param>
    /// <param name="origin">The origin to send, or null to send none.</param>
    /// <returns>The response.</returns>
    private static async Task<HttpResponseMessage> CallAsync(HttpClient client, TestKey key, string? origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/anything")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", key.PlainText);
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return await client.SendAsync(request);
    }

    /// <summary>The <c>Access-Control-Allow-Origin</c> of a response, or null when absent.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The header value, or null.</returns>
    private static string? AllowOrigin(HttpResponseMessage response) =>
        Header(response, "Access-Control-Allow-Origin");

    /// <summary>The joined <c>Vary</c> values of a response.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The values, joined; empty when the header is absent.</returns>
    private static string Vary(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Vary", out var values) ? string.Join(", ", values) : string.Empty;

    /// <summary>One response header, or null when absent.</summary>
    /// <param name="response">The response.</param>
    /// <param name="name">The header name.</param>
    /// <returns>The joined value, or null.</returns>
    private static string? Header(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
    }

    /// <summary>A key created for a test, with the plaintext the caller presents.</summary>
    /// <param name="PlainText">The secret to send in the header.</param>
    /// <param name="Prefix">The identifying prefix.</param>
    private sealed record TestKey(string PlainText, string Prefix);

    /// <summary>Boots the host with an optional server-wide CORS default.</summary>
    /// <param name="ServerOrigins">The server-wide origin, or null to configure none.</param>
    private sealed class HostFactory(string? ServerOrigins = ServerOrigin) : WebApplicationFactory<Program>
    {
        /// <summary>The throwaway control plane.</summary>
        private readonly TempSqliteDatabase _db = new("weir-cors");

        /// <summary>Creates a key with an optional CORS policy.</summary>
        /// <param name="name">The key's label.</param>
        /// <param name="policy">The CORS policy, or null for none.</param>
        /// <returns>The key, with the plaintext to present.</returns>
        internal async Task<TestKey> CreateKeyAsync(string name, ApiKeyCorsPolicy? policy = null)
        {
            var store = Services.GetRequiredService<IControlPlaneStore>();
            var (plainText, prefix, hash) = ApiKeyGenerator.Generate();
            await store.CreateApiKeyAsync(new ApiKeyCreate { Name = name, Cors = policy }, hash, prefix);

            // The index is built from the key list and cached, so a key created after it was last built
            // would not be seen - exactly as in production, where the admin API drops it on every key
            // change. Dropping it here is the test standing in for that.
            Services.GetRequiredService<Weir.Host.Http.CorsPolicyIndex>().Invalidate();
            return new TestKey(plainText, prefix);
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", _db.ConnectionString);
            builder.UseSetting("Weir:Jwt:SigningKey", "cors-test-signing-key-0123456789");
            if (ServerOrigins is not null)
            {
                builder.UseSetting("Weir:Cors:AllowedOrigins:0", ServerOrigins);
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _db.Dispose();
        }
    }
}
