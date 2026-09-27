using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Weir.Host.Http;
using Weir.Host.Options;
using Xunit;

namespace Weir.Tests;

// Weir:Ports puts the endpoint API and the admin surface on listeners of their own. The decision is
// pure - a path belongs to a surface, and a surface belongs on a port - so it is tested directly
// against that decision rather than through a socket. A real Kestrel test would help here and is the
// reason the split was verified by hand against a running host (see STORY-1/TASK-15); it is not
// repeated here because binding fixed ports makes a suite that runs in parallel with itself and with
// anything else on the machine flaky.
public class PortRoutingTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public void Health_Belongs_To_Both_Surfaces(string path) =>
        Assert.Equal(RequestSurface.Shared, PortRouting.Classify(new PathString(path)));

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/orders/42")]
    [InlineData("/ws")]
    [InlineData("/weir.v1.WeirGateway/Invoke")]
    public void The_Endpoint_Api_Belongs_To_The_Data_Plane(string path) =>
        Assert.Equal(RequestSurface.DataPlane, PortRouting.Classify(new PathString(path)));

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/api/auth/login")]
    [InlineData("/hubs/dashboard")]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/_framework/blazor.boot.json")]
    [InlineData("/css/app.css")]
    public void The_Admin_Api_The_Hub_And_The_Pwa_Belong_To_Admin(string path) =>
        Assert.Equal(RequestSurface.Admin, PortRouting.Classify(new PathString(path)));

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/apix")]
    public void A_Longer_Segment_Is_Not_The_Prefix_It_Starts_With(string path) =>
        Assert.Equal(RequestSurface.Admin, PortRouting.Classify(new PathString(path)));

    [Fact]
    public void With_No_Port_Configured_Nothing_Is_Filtered()
    {
        var split = PortRouting.Resolve(new PortRoutingOptions(), 8080);

        Assert.False(split.Enabled);
        Assert.False(split.Filters);
        Assert.True(split.IsAllowed(RequestSurface.Admin, 8080));
        Assert.True(split.IsAllowed(RequestSurface.DataPlane, 8080));
        Assert.True(split.IsAllowed(RequestSurface.Shared, 8080));
    }

    [Fact]
    public void Each_Surface_Answers_Only_On_Its_Own_Port()
    {
        var split = PortRouting.Resolve(
            new PortRoutingOptions { AdminPort = 8081, DataPlanePort = 8080 }, mainPort: 9999);

        Assert.Equal(8080, split.DataPlanePort);
        Assert.Equal(8081, split.AdminPort);
        Assert.True(split.Filters);

        Assert.True(split.IsAllowed(RequestSurface.Admin, 8081));
        Assert.False(split.IsAllowed(RequestSurface.Admin, 8080));
        Assert.True(split.IsAllowed(RequestSurface.DataPlane, 8080));
        Assert.False(split.IsAllowed(RequestSurface.DataPlane, 8081));
        Assert.True(split.IsAllowed(RequestSurface.Shared, 8080));
        Assert.True(split.IsAllowed(RequestSurface.Shared, 8081));
    }

    [Fact]
    public void An_Admin_Port_Alone_Leaves_The_Data_Plane_On_The_Main_Port()
    {
        var split = PortRouting.Resolve(new PortRoutingOptions { AdminPort = 8081 }, mainPort: 5000);

        Assert.Equal(5000, split.DataPlanePort);
        Assert.Equal(8081, split.AdminPort);
        Assert.True(split.IsAllowed(RequestSurface.DataPlane, 5000));
        Assert.False(split.IsAllowed(RequestSurface.DataPlane, 8081));
        Assert.True(split.IsAllowed(RequestSurface.Admin, 8081));
        Assert.False(split.IsAllowed(RequestSurface.Admin, 5000));
    }

    [Fact]
    public void A_Data_Plane_Port_Alone_Leaves_Admin_On_The_Main_Port()
    {
        var split = PortRouting.Resolve(new PortRoutingOptions { DataPlanePort = 8080 }, mainPort: 5000);

        Assert.Equal(8080, split.DataPlanePort);
        Assert.Equal(5000, split.AdminPort);
        Assert.True(split.IsAllowed(RequestSurface.DataPlane, 8080));
        Assert.False(split.IsAllowed(RequestSurface.DataPlane, 5000));
        Assert.True(split.IsAllowed(RequestSurface.Admin, 5000));
        Assert.False(split.IsAllowed(RequestSurface.Admin, 8080));
    }

    [Fact]
    public void A_Split_That_Lands_On_One_Port_Filters_Nothing()
    {
        // The configured admin port is the port the process already uses, so both surfaces sit on it:
        // filtering would refuse the data plane on the only port there is.
        var split = PortRouting.Resolve(new PortRoutingOptions { AdminPort = 8080 }, mainPort: 8080);

        Assert.True(split.Enabled);
        Assert.False(split.Filters);
        Assert.True(split.IsAllowed(RequestSurface.Admin, 8080));
        Assert.True(split.IsAllowed(RequestSurface.DataPlane, 8080));
    }

    [Fact]
    public void An_Extra_Data_Plane_Listener_Stays_Usable_But_Answers_No_Admin()
    {
        // A dedicated gRPC listener (cleartext gRPC cannot share a port with HTTP/1.1) is neither of
        // the two ports Weir manages, and it must keep serving the data plane.
        var split = PortRouting.Resolve(
            new PortRoutingOptions { AdminPort = 8081, DataPlanePort = 8080 }, mainPort: 8080);

        Assert.True(split.IsAllowed(RequestSurface.DataPlane, 5002));
        Assert.False(split.IsAllowed(RequestSurface.Admin, 5002));
    }

    [Theory]
    [InlineData("http://+:8080", 8080)]
    [InlineData("http://127.0.0.1:18083", 18083)]
    [InlineData("https://localhost:52274;http://localhost:52275", 52275)]
    [InlineData("https://localhost:52274", 52274)]
    [InlineData("http://host", 8080)]
    [InlineData("", 8080)]
    [InlineData(null, 8080)]
    public void The_Main_Port_Comes_From_The_Configured_Urls(string? urls, int expected) =>
        Assert.Equal(expected, PortRoutingOptions.ResolveMainPort(urls));

    [Theory]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    public void Only_A_Port_A_Listener_Can_Bind_Is_Accepted(int port, bool valid) =>
        Assert.Equal(valid, PortRouting.IsValidPort(port));

    [Fact]
    public void An_Unset_Port_Is_Not_Validated_Against_A_Range() =>
        Assert.True(PortRouting.IsValidPort(null));
}

// The split is opt-in, and the promise that makes it safe to ship is that not configuring it changes
// nothing: every surface still answers on the one host the application is served from. The unit tests
// above prove the decision; this proves the wiring around it is untouched when the ports are unset.
public class PortRoutingDefaultBehaviourTests : IClassFixture<PortRoutingDefaultBehaviourTests.HostFactory>
{
    private readonly HostFactory _factory;

    /// <summary>Creates the suite over the shared host.</summary>
    /// <param name="factory">The host factory.</param>
    public PortRoutingDefaultBehaviourTests(HostFactory factory) => _factory = factory;

    [Fact]
    public void No_Port_Is_Configured() =>
        Assert.False(_factory.Services.GetRequiredService<IOptions<PortRoutingOptions>>().Value.Enabled);

    [Fact]
    public async Task Every_Surface_Still_Answers_On_The_One_Host()
    {
        var client = _factory.CreateClient();

        // Health belongs to both surfaces and answers either way.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        // 401 rather than 404: the admin API route exists on this host. A 404 would mean a port rule
        // had turned away a surface that was never split off.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/api/auth/me")).StatusCode);

        // Likewise the data plane: an unauthenticated call is refused by the API key check, not by
        // routing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/nothing")).StatusCode);

        // And the admin PWA is still served: its static files and the index fallback.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    /// <summary>Boots the host against a throwaway SQLite control plane with no Weir:Ports configured.</summary>
    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly TempSqliteDatabase _db = new("weir-ports-default");

        /// <summary>Points the host at the throwaway database.</summary>
        /// <param name="builder">The host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", _db.ConnectionString);
            builder.UseSetting("Weir:Admin:Username", "admin");
            builder.UseSetting("Weir:Admin:Password", "admin-password");
            builder.UseSetting("Weir:Jwt:SigningKey", "ports-test-signing-key-0123456789");
        }

        /// <summary>Deletes the throwaway database.</summary>
        /// <param name="disposing">Whether managed resources are being released.</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _db.Dispose();
        }
    }
}

