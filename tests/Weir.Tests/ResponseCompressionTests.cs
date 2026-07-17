using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Weir.Contracts;
using Weir.Host.Http;
using Xunit;

namespace Weir.Tests;

// The data plane compresses per endpoint rather than through the generic MIME-type middleware, which
// cannot vary per endpoint (and could not read the data plane's dynamic routes anyway). Two pieces do
// the work: a negotiator that turns the endpoint policy plus Accept-Encoding into a coding, and a
// stream that compresses lazily so a body-less 304 stays body-less. Both are exercised here.
public class ResponseCompressionTests
{
    [Theory]
    [InlineData(ResultMode.MultiRow, null, "br, gzip", "br")]        // Auto compresses the row shape...
    [InlineData(ResultMode.Scalar, null, "br, gzip", null)]          // ...and skips the small ones.
    [InlineData(ResultMode.SingleRow, null, "br", null)]
    [InlineData(ResultMode.NonQuery, null, "gzip", null)]
    [InlineData(ResultMode.Scalar, ResponseCompressionMode.On, "gzip", "gzip")]   // On overrides Auto's skip.
    [InlineData(ResultMode.MultiRow, ResponseCompressionMode.Off, "br, gzip", null)] // Off overrides Auto's compress.
    public void Negotiate_Resolves_The_Policy_Then_The_Coding(
        ResultMode resultMode, ResponseCompressionMode? endpointMode, string acceptEncoding, string? expected)
    {
        var endpoint = Endpoint(resultMode, endpointMode);
        var settings = new WeirSystemSettings { ResponseCompressionMode = ResponseCompressionMode.Auto };

        var coding = ResponseCompression.Negotiate(endpoint, settings, new StringValues(acceptEncoding));

        Assert.Equal(expected, coding);
    }

    [Fact]
    public void Brotli_Is_Preferred_Over_Gzip_When_Both_Are_Accepted()
    {
        var coding = ResponseCompression.Negotiate(
            Endpoint(ResultMode.MultiRow, ResponseCompressionMode.On), Settings(), new StringValues("gzip, br"));

        Assert.Equal("br", coding);
    }

    [Fact]
    public void A_Coding_Refused_With_Q0_Is_Not_Offered()
    {
        // "br;q=0" is an explicit refusal of brotli, so gzip must be chosen even though br is listed.
        var coding = ResponseCompression.Negotiate(
            Endpoint(ResultMode.MultiRow, ResponseCompressionMode.On), Settings(), new StringValues("br;q=0, gzip"));

        Assert.Equal("gzip", coding);
    }

    [Fact]
    public void No_Acceptable_Coding_Means_No_Compression()
    {
        var coding = ResponseCompression.Negotiate(
            Endpoint(ResultMode.MultiRow, ResponseCompressionMode.On), Settings(), new StringValues("deflate"));

        Assert.Null(coding);
    }

    [Fact]
    public void The_Endpoint_Falls_Back_To_The_System_Default_When_It_Sets_No_Mode()
    {
        // Endpoint mode null + system default Off means a MultiRow endpoint that would compress under
        // Auto does not. This is the "Default (from Settings)" choice in the editor.
        var endpoint = Endpoint(ResultMode.MultiRow, null);
        var settings = new WeirSystemSettings { ResponseCompressionMode = ResponseCompressionMode.Off };

        Assert.Null(ResponseCompression.Negotiate(endpoint, settings, new StringValues("br")));
    }

    [Fact]
    public async Task Writing_Through_The_Stream_Produces_A_Body_That_Decompresses_To_The_Input()
    {
        var inner = new MemoryStream();
        var context = new DefaultHttpContext();
        var payload = Encoding.UTF8.GetBytes(new string('x', 10_000));

        await using (var stream = new LazyCompressingStream(inner, context.Response, "br"))
        {
            await stream.WriteAsync(payload);
        }

        Assert.Equal("br", context.Response.Headers.ContentEncoding.ToString());
        Assert.True(inner.Length < payload.Length, "the repetitive payload should have shrunk");
        Assert.Equal(payload, Decompress(inner.ToArray(), "br"));
    }

    [Fact]
    public async Task A_Response_That_Writes_Nothing_Sets_No_Encoding_And_No_Body()
    {
        // The 304 case: the engine answers If-None-Match by writing no body. The lazy stream must then
        // leave Content-Encoding unset and emit nothing - a compression frame here would give a 304 a
        // body it must not have.
        var inner = new MemoryStream();
        var context = new DefaultHttpContext();

        await using (var stream = new LazyCompressingStream(inner, context.Response, "br"))
        {
            // deliberately no write
        }

        Assert.Equal(0, inner.Length);
        Assert.False(context.Response.Headers.ContainsKey("Content-Encoding"));
    }

    /// <summary>Builds settings whose default compression is Auto.</summary>
    /// <returns>The settings.</returns>
    private static WeirSystemSettings Settings() => new() { ResponseCompressionMode = ResponseCompressionMode.Auto };

    /// <summary>Builds one endpoint with a result shape and optional compression override.</summary>
    /// <param name="resultMode">The declared result shape.</param>
    /// <param name="mode">The compression override, or null to follow the settings.</param>
    /// <returns>The definition.</returns>
    private static EndpointDefinition Endpoint(ResultMode resultMode, ResponseCompressionMode? mode) => new()
    {
        Route = "r",
        ConnectionName = "c",
        ObjectName = "o",
        ResultMode = resultMode,
        Delivery = new DeliveryPolicy { Compression = mode },
    };

    /// <summary>Decompresses bytes produced by the given coding, for round-trip assertions.</summary>
    /// <param name="bytes">The compressed bytes.</param>
    /// <param name="encoding">The coding, <c>"br"</c> or <c>"gzip"</c>.</param>
    /// <returns>The original bytes.</returns>
    private static byte[] Decompress(byte[] bytes, string encoding)
    {
        using var source = new MemoryStream(bytes);
        using Stream decompressor = encoding == "br"
            ? new BrotliStream(source, CompressionMode.Decompress)
            : new GZipStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        decompressor.CopyTo(target);
        return target.ToArray();
    }
}
