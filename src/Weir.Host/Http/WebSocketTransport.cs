using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Weir.Host.Security;

namespace Weir.Host.Http;

/// <summary>
/// The WebSocket door onto the data plane: one session at <c>/ws</c>, authenticated once on the
/// handshake, carrying a request frame per call.
/// <para>
/// What it buys is what a session buys anywhere - the connection, the TLS handshake and the API-key
/// lookup happen once instead of per call - and what it costs is that a session is ordered: one request
/// is answered before the next is read. That is not a simplification, it is the protocol: a WebSocket
/// message cannot be interleaved with another on the same connection, so a "concurrent" reply would have
/// to be buffered whole before it could be sent, which is the one thing this transport exists to avoid.
/// A caller that wants two calls in flight opens two sessions.
/// </para>
/// </summary>
public static class WebSocketTransport
{
    /// <summary>UTF-8 for frame text. Throws on invalid input rather than substituting replacement characters.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Read buffer for one WebSocket fragment.</summary>
    private const int ReceiveBufferBytes = 8 * 1024;

    /// <summary>Maps the data-plane WebSocket endpoint at <c>/ws</c>.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapWeirWebSocket(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/ws", HandleAsync);
        return endpoints;
    }

    /// <summary>
    /// Accepts one session and runs it to completion. The collaborators are handler parameters for the
    /// same reason the HTTP handler takes them that way: they are singletons, and the compiled delegate
    /// resolves them once at build time rather than per connection.
    /// </summary>
    /// <param name="context">The HTTP context of the handshake.</param>
    /// <param name="authenticator">API-key authenticator, applied to the handshake request.</param>
    /// <param name="dispatcher">The shared data-plane call path.</param>
    /// <param name="limits">Data-plane limits (the request body cap).</param>
    /// <param name="loggerFactory">Factory for the security logger.</param>
    /// <returns>A task that completes when the session ends.</returns>
    private static async Task HandleAsync(
        HttpContext context,
        IApiKeyAuthenticator authenticator,
        DataPlaneDispatcher dispatcher,
        IOptions<WeirDataPlaneOptions> limits,
        ILoggerFactory loggerFactory)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await ProblemResults.WriteAsync(context, StatusCodes.Status400BadRequest, "Not a WebSocket request",
                "This endpoint accepts WebSocket connections only.");
            return;
        }

        // Authenticate the handshake, which is an ordinary HTTP request and carries the API key in the
        // ordinary header. Doing it here rather than in a first "auth frame" means an unauthenticated
        // caller never gets a socket at all, and the whole session inherits one key: there is no moment
        // where a connection exists with nobody behind it.
        var auth = await authenticator.AuthenticateAsync(context, context.RequestAborted);
        if (auth.Status == ApiKeyAuthStatus.RateLimited)
        {
            Log.ApiKeyFlood(loggerFactory.CreateLogger("Weir.Security"), context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            await ProblemResults.WriteAsync(context, StatusCodes.Status429TooManyRequests, "Too many requests",
                "Too many unauthenticated requests. Try again later.", retryAfterSeconds: 60);
            return;
        }

        if (auth.Status != ApiKeyAuthStatus.Authenticated)
        {
            context.Response.Headers.WWWAuthenticate = "ApiKey";
            await ProblemResults.WriteAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized",
                "A valid API key is required.");
            return;
        }

        var key = auth.Record!;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var header = new HeaderValueSource(context.Request.Headers);
        await RunSessionAsync(socket, key, header, dispatcher, limits.Value.MaxRequestBodyBytes, context.RequestAborted);
    }

    /// <summary>Reads frames and answers them until the caller closes the socket or the host shuts down.</summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="key">The key that authenticated the handshake, and therefore every call on it.</param>
    /// <param name="header">Header values from the handshake, for header-sourced parameters.</param>
    /// <param name="dispatcher">The shared data-plane call path.</param>
    /// <param name="maxBytes">Cap on one request frame, in bytes; zero means no cap.</param>
    /// <param name="cancellationToken">Cancelled when the connection drops or the host stops.</param>
    /// <returns>A task that completes when the session ends.</returns>
    private static async Task RunSessionAsync(
        WebSocket socket,
        ApiKeyRecord key,
        IValueSource header,
        DataPlaneDispatcher dispatcher,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var receive = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                // A frame is bounded by the same setting as an HTTP body (Weir:DataPlane:MaxRequestBodyBytes,
                // which Kestrel also enforces on /api): one door must not accept what the other refuses.
                using var frame = await ReceiveFrameAsync(socket, receive, maxBytes, cancellationToken);
                if (frame is null)
                {
                    return;
                }

                if (frame.TooLarge)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig,
                        $"A request frame may not exceed {maxBytes} bytes.", cancellationToken);
                    return;
                }

                if (!await AnswerAsync(socket, frame, key, header, dispatcher, cancellationToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection dropped or the host is stopping. Nothing to report to a caller that is gone.
        }
        catch (WebSocketException)
        {
            // An abrupt close from the other end. Same: there is nobody left to tell.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receive);
            await FinishAsync(socket);
        }
    }

    /// <summary>Answers one request frame.</summary>
    /// <param name="socket">The session socket.</param>
    /// <param name="frame">The received frame.</param>
    /// <param name="key">The session's API key.</param>
    /// <param name="header">Handshake header values.</param>
    /// <param name="dispatcher">The shared data-plane call path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True to keep the session open, false when it can no longer carry a reply.</returns>
    private static async Task<bool> AnswerAsync(
        WebSocket socket,
        ReceivedFrame frame,
        ApiKeyRecord key,
        IValueSource header,
        DataPlaneDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        JsonDocument request;
        try
        {
            request = JsonDocument.Parse(frame.Payload);
        }
        catch (JsonException ex)
        {
            await SendErrorAsync(socket, "null", StatusCodes.Status400BadRequest, "Invalid JSON frame", ex.Message, cancellationToken);
            return true;
        }

        using (request)
        {
            var root = request.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await SendErrorAsync(socket, "null", StatusCodes.Status400BadRequest, "Invalid frame",
                    "A request frame must be a JSON object.", cancellationToken);
                return true;
            }

            // Echoed back verbatim, whatever it is: the caller correlates replies by it, and re-typing
            // someone else's identifier is a good way to hand back one they cannot match.
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetRawText() : "null";

            if (!root.TryGetProperty("route", out var routeElement) || routeElement.ValueKind != JsonValueKind.String)
            {
                await SendErrorAsync(socket, id, StatusCodes.Status400BadRequest, "Invalid frame",
                    "A request frame needs a \"route\" string.", cancellationToken);
                return true;
            }

            var route = routeElement.GetString()!.Trim('/');
            var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()!
                : HttpMethods.Post;

            var hasBody = root.TryGetProperty("body", out var body) && body.ValueKind is JsonValueKind.Object;
            var query = root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Object
                ? new JsonObjectValueSource(q)
                : (IValueSource)EmptyValueSource.Instance;

            var call = new TransportCall
            {
                Transport = EndpointTransports.WebSocket,
                Key = key,
                Method = method,
                Route = route,
                Body = hasBody ? body : default,
                HasBody = hasBody,
                Query = query,
                Header = header,
            };

            await using var output = new WebSocketWriteStream(socket);
            await output.BeginAsync(Utf8.GetBytes($"{{\"id\":{id},\"body\":"), cancellationToken);
            var result = await dispatcher.InvokeAsync(call, output, cancellationToken);

            if (result.IsSuccess)
            {
                await output.EndAsync(Utf8.GetBytes($",\"status\":{result.Status}}}"), cancellationToken);
                return true;
            }

            if (result.BodyStarted)
            {
                // Rows were already on the wire when it went wrong, so there is no way to replace them
                // with an error: the message is half an envelope and no ending can make it valid JSON.
                // Closing is what the HTTP path does with a response it has already started, and for the
                // same reason - the caller must be able to tell a truncated answer from a complete one.
                await CloseAsync(socket, WebSocketCloseStatus.InternalServerError,
                    result.Title ?? "The response failed part-way through.", cancellationToken);
                return false;
            }

            // Nothing was written yet, so the message that was opened is abandoned unsent: the buffer is
            // dropped with the stream, and the caller gets a clean error frame instead.
            await SendErrorAsync(socket, id, result.Status, result.Title, result.Detail, cancellationToken);
            return true;
        }
    }

    /// <summary>Sends one error frame in place of a response.</summary>
    /// <param name="socket">The session socket.</param>
    /// <param name="rawId">The caller's correlation id, already JSON-encoded.</param>
    /// <param name="status">HTTP-shaped status code.</param>
    /// <param name="title">Short reason.</param>
    /// <param name="detail">Longer reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the frame is sent.</returns>
    private static async ValueTask SendErrorAsync(
        WebSocket socket,
        string rawId,
        int status,
        string? title,
        string? detail,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            writer.WriteRawValue(rawId);
            writer.WriteNumber("status", status);
            writer.WriteStartObject("error");
            writer.WriteString("title", title ?? "Error");
            writer.WriteString("detail", detail ?? string.Empty);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <summary>
    /// Ends the session the way the state calls for. The distinction matters to a well-behaved client:
    /// after the caller's close frame the socket is in <see cref="WebSocketState.CloseReceived"/>, and
    /// only the reply half of the handshake is left to send - calling the full close there would wait
    /// for a frame that has already arrived, and simply dropping the socket (which is what an "is it
    /// still open?" check does) leaves the caller's own CloseAsync to fail with an unexpected EOF.
    /// </summary>
    /// <param name="socket">The session socket.</param>
    /// <returns>A task that completes when the socket is closed.</returns>
    private static async ValueTask FinishAsync(WebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended.", CancellationToken.None);
            }
            else if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended.", CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // The peer is already gone; there is nothing to close politely.
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    /// <summary>Closes the socket, ignoring a peer that has already gone.</summary>
    /// <param name="socket">The session socket.</param>
    /// <param name="status">Close status.</param>
    /// <param name="description">Close reason, shown to the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the close frame is sent or found to be pointless.</returns>
    private static async ValueTask CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        try
        {
            await socket.CloseAsync(status, description, cancellationToken);
        }
        catch (WebSocketException)
        {
            // The peer is already gone; there is nothing to close politely.
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    /// <summary>Reads one whole message, however many fragments it arrives in.</summary>
    /// <param name="socket">The session socket.</param>
    /// <param name="buffer">Scratch buffer for one fragment.</param>
    /// <param name="maxBytes">Cap on the assembled message; zero means no cap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame, or null when the caller closed the socket.</returns>
    private static async Task<ReceivedFrame?> ReceiveFrameAsync(
        WebSocket socket,
        byte[] buffer,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var payload = new ArrayBufferWriter<byte>(ReceiveBufferBytes);
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (maxBytes > 0 && payload.WrittenCount + received.Count > maxBytes)
            {
                return new ReceivedFrame(payload, true);
            }

            payload.Write(buffer.AsSpan(0, received.Count));
            if (received.EndOfMessage)
            {
                return new ReceivedFrame(payload, false);
            }
        }
    }

    /// <summary>One assembled request message.</summary>
    /// <param name="Buffer">The assembled bytes.</param>
    /// <param name="TooLarge">Whether the message went past the configured cap and was abandoned.</param>
    private sealed record ReceivedFrame(ArrayBufferWriter<byte> Buffer, bool TooLarge) : IDisposable
    {
        /// <summary>The message body.</summary>
        public ReadOnlyMemory<byte> Payload => Buffer.WrittenMemory;

        /// <inheritdoc />
        public void Dispose() => Buffer.Clear();
    }
}
