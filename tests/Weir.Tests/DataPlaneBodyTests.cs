using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Weir.Host.Http;
using Xunit;

namespace Weir.Tests;

// The data-plane request body is read three ways, and the difference matters to the caller: no body at
// all binds from query/route/headers alone, a JSON body binds its parameters, and a body that declared a
// different media type is answered 415 rather than silently dropped. An empty body goes down the first
// path even when the request used chunked transfer (no Content-Length), which the parser sees as an
// empty stream - the same thing it sees for malformed JSON.
public class DataPlaneBodyTests
{
    [Fact]
    public async Task A_Json_Body_Is_Parsed()
    {
        var context = Context("{\"id\":42}", "application/json", contentLength: 9);

        var result = await DataPlaneEndpoints.ReadBodyAsync(context.Request, CancellationToken.None);

        Assert.True(result.HasBody);
        Assert.NotNull(result.Body);
        Assert.Equal(42, result.Body!.RootElement.GetProperty("id").GetInt32());
        Assert.False(result.UnsupportedContentType);
    }

    [Fact]
    public async Task An_Empty_Json_Body_With_No_Content_Length_Is_No_Body()
    {
        // The A-4 case: a chunked request that sent no bytes arrives as an empty stream, and the parse
        // fails for it exactly as it does for malformed JSON. It is "no body", not a caller mistake.
        var context = Context(string.Empty, "application/json", contentLength: null);

        var result = await DataPlaneEndpoints.ReadBodyAsync(context.Request, CancellationToken.None);

        Assert.False(result.HasBody);
        Assert.Null(result.Body);
        Assert.False(result.UnsupportedContentType);
    }

    [Fact]
    public async Task A_Malformed_Json_Body_Is_Still_A_Parse_Failure()
    {
        var context = Context("{\"id\":", "application/json", contentLength: 6);

        // ThrowsAnyAsync, not ThrowsAsync: the parser can raise a JsonException subtype, and what this
        // asserts is that a non-empty body that does not parse is still reported as a parse failure.
        await Assert.ThrowsAnyAsync<JsonException>(
            () => DataPlaneEndpoints.ReadBodyAsync(context.Request, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task A_Body_That_Declares_Another_Media_Type_Is_Reported_As_Unsupported()
    {
        // The A-3 case: the body is there, but it did not declare JSON. Reporting that beats dropping it
        // and answering that a parameter is missing.
        var context = Context("{\"id\":42}", "text/plain", contentLength: 9);

        var result = await DataPlaneEndpoints.ReadBodyAsync(context.Request, CancellationToken.None);

        Assert.True(result.UnsupportedContentType);
        Assert.False(result.HasBody);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task A_Body_Without_A_Content_Type_Is_Also_Reported_As_Unsupported()
    {
        var context = Context("{\"id\":42}", contentType: null, contentLength: 9);

        var result = await DataPlaneEndpoints.ReadBodyAsync(context.Request, CancellationToken.None);

        Assert.True(result.UnsupportedContentType);
    }

    [Fact]
    public async Task A_Request_With_No_Body_And_No_Content_Type_Is_No_Body()
    {
        // An ordinary GET: no length, no content type, no bytes. It must not become a 415.
        var context = Context(string.Empty, contentType: null, contentLength: null);

        var result = await DataPlaneEndpoints.ReadBodyAsync(context.Request, CancellationToken.None);

        Assert.False(result.UnsupportedContentType);
        Assert.False(result.HasBody);
    }

    /// <summary>Builds a request with the given body, content type and declared length.</summary>
    /// <param name="body">The raw body text.</param>
    /// <param name="contentType">The Content-Type header, or null to send none.</param>
    /// <param name="contentLength">The Content-Length to declare; null leaves it undeclared.</param>
    /// <returns>The HTTP context.</returns>
    private static DefaultHttpContext Context(string body, string? contentType, long? contentLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = contentType;
        context.Request.ContentLength = contentLength;
        return context;
    }
}
