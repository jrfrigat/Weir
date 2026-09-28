using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Weir.Host.Http;
using Xunit;

namespace Weir.Tests;

// H-2: the correlation header is caller input, and it used to flow into the text log and back to the
// caller unchanged - a line break could forge a log entry and a megabyte value would ride along in every
// event for the request. The pure function is covered directly; the host is booted to prove the middleware
// uses it.
public class CorrelationIdTests : IDisposable
{
    /// <summary>The throwaway control-plane database the host is pointed at.</summary>
    private readonly TempSqliteDatabase _db = new("weir-corrid");

    /// <summary>Removes the throwaway database.</summary>
    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void An_Ordinary_Value_Passes_Through() =>
        Assert.Equal("trace-1", CorrelationId.Normalize("trace-1"));

    [Fact]
    public void Control_Characters_Are_Dropped()
    {
        // The CR/LF pair is what would otherwise start a second line in the text log.
        Assert.Equal("traceinjected", CorrelationId.Normalize("trace\r\ninjected"));
    }

    [Fact]
    public void An_Overlong_Value_Is_Capped()
    {
        var normalized = CorrelationId.Normalize(new string('a', 5000));

        Assert.NotNull(normalized);
        Assert.Equal(CorrelationId.MaxLength, normalized!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void A_Value_That_Normalizes_To_Nothing_Is_Rejected(string? value) =>
        Assert.Null(CorrelationId.Normalize(value));

    [Fact]
    public async Task The_Response_Echoes_The_Normalized_Id()
    {
        using var factory = new HostFactory(_db);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", "  trace-42  ");
        var response = await client.SendAsync(request);

        // Trimmed, and echoed back, so the client sees the same id the logs carry.
        Assert.Equal("trace-42", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task An_Overlong_Id_Is_Capped_In_The_Response()
    {
        using var factory = new HostFactory(_db);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", new string('a', 5000));
        var response = await client.SendAsync(request);

        Assert.Equal(CorrelationId.MaxLength, response.Headers.GetValues("X-Correlation-ID").Single().Length);
    }

    /// <summary>Boots the host against the test's throwaway SQLite control plane.</summary>
    private sealed class HostFactory : WebApplicationFactory<Program>
    {
        /// <summary>The throwaway database the host should use.</summary>
        private readonly TempSqliteDatabase _db;

        /// <summary>Creates the factory over an existing database.</summary>
        /// <param name="db">The database to point the host at.</param>
        public HostFactory(TempSqliteDatabase db) => _db = db;

        /// <summary>Points the host at the throwaway database.</summary>
        /// <param name="builder">The host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Weir:ControlPlane:ConnectionString", _db.ConnectionString);
            builder.UseSetting("Weir:Jwt:SigningKey", "correlation-test-signing-key-012345");
        }
    }
}
