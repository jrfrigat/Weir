using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Weir.Contracts;

namespace Weir.Host.Http;

/// <summary>
/// Per-endpoint response compression for the data plane. The generic <c>UseResponseCompression</c>
/// middleware compresses by MIME type for the whole app, which cannot vary per endpoint - and the data
/// plane's routes are dynamic, resolved from the catalog inside one handler, so there is no per-route
/// metadata for the middleware to read anyway. The data plane is therefore excluded from that
/// middleware (see <c>Program</c>) and compresses here instead, deciding per endpoint and negotiating
/// the encoding from the caller's <c>Accept-Encoding</c>.
/// </summary>
internal static class ResponseCompression
{
    /// <summary>
    /// Picks the content coding to compress this endpoint's response with, or null for none - either
    /// because the endpoint's policy says not to, or because the caller accepts no coding Weir offers.
    /// </summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="settings">The current system settings supplying the default.</param>
    /// <param name="acceptEncoding">The caller's <c>Accept-Encoding</c> header.</param>
    /// <returns><c>"br"</c>, <c>"gzip"</c>, or null.</returns>
    internal static string? Negotiate(EndpointDefinition endpoint, WeirSystemSettings settings, StringValues acceptEncoding) =>
        ShouldCompress(endpoint, settings) ? PickEncoding(acceptEncoding) : null;

    /// <summary>
    /// Whether the endpoint's effective policy calls for compression. <see cref="ResponseCompressionMode.Auto"/>
    /// compresses the row-returning results and skips those declared small, where the coding would cost
    /// more than the bytes it saves.
    /// </summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="settings">The current system settings supplying the default.</param>
    /// <returns>True to compress.</returns>
    private static bool ShouldCompress(EndpointDefinition endpoint, WeirSystemSettings settings)
    {
        var mode = endpoint.Delivery.Compression ?? settings.ResponseCompressionMode;
        return mode switch
        {
            ResponseCompressionMode.On => true,
            ResponseCompressionMode.Off => false,
            _ => endpoint.ResultMode == ResultMode.MultiRow,
        };
    }

    /// <summary>
    /// Reads <c>Accept-Encoding</c> and returns the coding to use, preferring Brotli over gzip (the same
    /// order the generic middleware registers its providers in). A coding the caller explicitly refuses
    /// with <c>q=0</c> is not offered; a full ranked negotiation is deliberately not attempted, since the
    /// near-universal header is a plain unranked list.
    /// </summary>
    /// <param name="acceptEncoding">The caller's <c>Accept-Encoding</c> header.</param>
    /// <returns><c>"br"</c>, <c>"gzip"</c>, or null when neither is acceptable.</returns>
    private static string? PickEncoding(StringValues acceptEncoding)
    {
        var brotli = false;
        var gzip = false;
        foreach (var value in acceptEncoding)
        {
            if (value is null)
            {
                continue;
            }

            foreach (var part in value.Split(','))
            {
                var token = part.Trim();
                var coding = token;
                var quality = 1d;

                var semicolon = token.IndexOf(';', StringComparison.Ordinal);
                if (semicolon >= 0)
                {
                    coding = token[..semicolon].Trim();
                    var parameter = token[(semicolon + 1)..].Trim();
                    if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(parameter[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        quality = parsed;
                    }
                }

                if (quality <= 0)
                {
                    continue;
                }

                if (coding.Equals("br", StringComparison.OrdinalIgnoreCase))
                {
                    brotli = true;
                }
                else if (coding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    gzip = true;
                }
            }
        }

        return brotli ? "br" : gzip ? "gzip" : null;
    }
}

/// <summary>
/// A write-only stream that compresses what is written into an inner stream, deferring both the
/// compressor and the <c>Content-Encoding</c> header until the first byte is written.
/// <para>
/// The deferral is what keeps a body-less response correct. A cacheable GET that answers
/// <c>If-None-Match</c> with <c>304 Not Modified</c> writes nothing, so no compressor is built, no
/// <c>Content-Encoding</c> is set, and no compression frame is emitted - a 304 stays empty. Setting the
/// header eagerly would have announced a coding the empty body does not carry.
/// </para>
/// </summary>
internal sealed class LazyCompressingStream : Stream
{
    /// <summary>The real response body the compressed bytes are written to.</summary>
    private readonly Stream _inner;

    /// <summary>The response whose headers are set on first write.</summary>
    private readonly HttpResponse _response;

    /// <summary>The content coding: <c>"br"</c> or <c>"gzip"</c>.</summary>
    private readonly string _encoding;

    /// <summary>The compressor, created on the first write; null until then.</summary>
    private Stream? _compressor;

    /// <summary>Creates the stream.</summary>
    /// <param name="inner">The real response body.</param>
    /// <param name="response">The response whose headers are set on first write.</param>
    /// <param name="encoding">The content coding, <c>"br"</c> or <c>"gzip"</c>.</param>
    internal LazyCompressingStream(Stream inner, HttpResponse response, string encoding)
    {
        _inner = inner;
        _response = response;
        _encoding = encoding;
    }

    /// <summary>Builds the compressor on the first write, setting the response headers first.</summary>
    /// <returns>The compressor stream.</returns>
    private Stream Compressor()
    {
        if (_compressor is null)
        {
            // Set here, on the first byte, so a response that writes nothing (a 304) never carries a
            // coding for a body it does not send. Content-Length would be the uncompressed size, which
            // is wrong once compressed - clear it and let the server chunk the response.
            _response.Headers.ContentEncoding = _encoding;
            _response.Headers.ContentLength = null;
            _compressor = _encoding == "br"
                ? new BrotliStream(_inner, CompressionLevel.Fastest, leaveOpen: true)
                : new GZipStream(_inner, CompressionLevel.Fastest, leaveOpen: true);
        }

        return _compressor;
    }

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Compressor().Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => Compressor().Write(buffer);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Compressor().WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        Compressor().WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Flush() => _compressor?.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _compressor?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        // Disposing the compressor writes the final compressed block; leave the inner body open (the host
        // owns it). Nothing was written on a 304, so there is nothing to flush.
        if (_compressor is not null)
        {
            try
            {
                await _compressor.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The response was aborted mid-stream, so the trailer has nowhere to land. That abort is
                // the failure the caller already sees; masking it with this one would help no one.
            }
        }

        await base.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _compressor?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // See DisposeAsync: a best-effort trailer flush onto an aborted response.
            }
        }

        base.Dispose(disposing);
    }
}
