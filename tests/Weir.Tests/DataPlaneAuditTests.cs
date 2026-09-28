using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Contracts;
using Weir.Host.Audit;
using Weir.Host.Grpc;
using Weir.Host.Http;
using Weir.Host.Security;
using Xunit;

namespace Weir.Tests;

// The audit is what an operator reads to find out what happened, and two kinds of event used to leave it
// blind: a call the caller hung up on was recorded as a 200 Ok (the entry took its status from the
// response header, which such a call never wrote), and an authentication refusal on the WebSocket or gRPC
// door was not recorded at all (the only entry was written inside the dispatcher, which those refusals
// never reached). Both are covered here.
public class DataPlaneAuditTests
{
    [Fact]
    public void A_Refusal_Before_The_Dispatcher_Is_Recorded_As_An_Error()
    {
        var auditor = new CapturingAuditor();

        DataPlaneDispatcher.EnqueueRefusal(auditor, EndpointTransports.WebSocket, "/ws", (int)HttpStatusCode.Unauthorized);

        var entry = Assert.Single(auditor.Entries);
        Assert.Equal("endpoint.call", entry.Category);
        // No key resolved, so there is no prefix to name - null rather than a placeholder that would read
        // as a key that exists.
        Assert.Null(entry.Actor);
        Assert.Equal("/ws", entry.Route);
        Assert.Equal((int)HttpStatusCode.Unauthorized, entry.StatusCode);
        Assert.Equal(OutcomeCodes.Error, entry.Outcome);
        Assert.Equal("websocket", entry.Detail);
    }

    [Fact]
    public void A_Cancelled_Call_Is_Not_Recorded_As_A_Success()
    {
        // The shape of the A-1 defect: the response never started, so the header still says 200 while the
        // call did not finish. Without the recorded outcome the audit would call that a success.
        var context = new DefaultHttpContext();
        Assert.Equal(StatusCodes.Status200OK, DataPlaneEndpoints.AuditStatus(context));

        DataPlaneEndpoints.RecordCancellation(context, gatewayTimeout: false);

        Assert.Equal(499, DataPlaneEndpoints.AuditStatus(context));
        Assert.True(DataPlaneEndpoints.AuditStatus(context) >= 400, "a cancelled call must not record as success");
    }

    [Fact]
    public void Our_Own_Timeout_Is_Recorded_As_A_Gateway_Timeout()
    {
        var context = new DefaultHttpContext();

        DataPlaneEndpoints.RecordCancellation(context, gatewayTimeout: true);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, DataPlaneEndpoints.AuditStatus(context));
    }

    [Fact]
    public async Task A_WebSocket_Handshake_Without_A_Key_Leaves_An_Audit_Entry()
    {
        using var factory = new HostFactory();
        // A real handshake, not a GET with upgrade headers: only a genuine upgrade reaches the point
        // where the door authenticates, and only a refused handshake proves the recording happens.
        var socketClient = factory.Server.CreateWebSocketClient();

        await Assert.ThrowsAnyAsync<Exception>(
            () => socketClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None));

        var entry = Assert.Single(factory.Auditor.Entries);
        Assert.Equal("websocket", entry.Detail);
        Assert.Equal((int)HttpStatusCode.Unauthorized, entry.StatusCode);
        Assert.Equal(OutcomeCodes.Error, entry.Outcome);
        Assert.Null(entry.Actor);
    }

    [Fact]
    public void A_Grpc_Call_Without_A_Key_Leaves_An_Audit_Entry()
    {
        var auditor = new CapturingAuditor();
        // The dispatcher is never reached on a refusal, which is the whole point of this test.
        var service = new WeirGatewayService(new RejectingAuthenticator(), dispatcher: null!, auditor);

        var refusal = service.RefuseAuthentication(ApiKeyAuthStatus.Unauthenticated, "orders");

        Assert.Equal(StatusCode.Unauthenticated, refusal.StatusCode);
        var entry = Assert.Single(auditor.Entries);
        Assert.Equal("grpc", entry.Detail);
        Assert.Equal((int)HttpStatusCode.Unauthorized, entry.StatusCode);
        Assert.Equal(OutcomeCodes.Error, entry.Outcome);
        Assert.Equal("orders", entry.Route);
    }

    [Fact]
    public void A_Grpc_Flood_Refusal_Is_Recorded_As_Too_Many_Requests()
    {
        var auditor = new CapturingAuditor();
        var service = new WeirGatewayService(new RejectingAuthenticator(), dispatcher: null!, auditor);

        var refusal = service.RefuseAuthentication(ApiKeyAuthStatus.RateLimited, "orders");

        Assert.Equal(StatusCode.ResourceExhausted, refusal.StatusCode);
        var entry = Assert.Single(auditor.Entries);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, entry.StatusCode);
    }

    /// <summary>Keeps every entry enqueued, so a test can read what the audit would have stored.</summary>
    internal sealed class CapturingAuditor : IDataPlaneAuditor
    {
        /// <summary>Entries enqueued so far, in order.</summary>
        public List<AuditEntry> Entries { get; } = [];

        /// <inheritdoc />
        public bool Enabled => true;

        /// <inheritdoc />
        public void Enqueue(AuditEntry entry) => Entries.Add(entry);
    }

    /// <summary>An authenticator that resolves nothing, for a path that never reaches it.</summary>
    private sealed class RejectingAuthenticator : IApiKeyAuthenticator
    {
        /// <inheritdoc />
        public Task<ApiKeyAuthResult> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiKeyAuthResult.Unauthenticated);

        /// <inheritdoc />
        public void Invalidate()
        {
        }
    }

    /// <summary>Boots the host against a throwaway SQLite control plane with a capturing auditor.</summary>
    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        /// <summary>The throwaway database.</summary>
        private readonly TempSqliteDatabase _db = new("weir-audit");

        /// <summary>The auditor the host was given, so a test can read what it recorded.</summary>
        internal CapturingAuditor Auditor { get; } = new();

        /// <summary>Points the host at the throwaway database and installs the capturing auditor.</summary>
        /// <param name="builder">The host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", _db.ConnectionString);
            builder.UseSetting("Weir:Admin:Username", "admin");
            builder.UseSetting("Weir:Admin:Password", "admin-password");
            builder.UseSetting("Weir:Jwt:SigningKey", "audit-test-signing-key-0123456789");
            // After the application's own registrations, so the capturing auditor wins the interface.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDataPlaneAuditor>();
                services.AddSingleton<IDataPlaneAuditor>(Auditor);
            });
        }

        /// <summary>Deletes the throwaway database.</summary>
        /// <param name="disposing">True when called from Dispose.</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _db.Dispose();
        }
    }
}
