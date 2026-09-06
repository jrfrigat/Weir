using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Weir.Contracts;
using Weir.Core;
using Weir.Host.Http;
using Weir.Host.Security;

namespace Weir.Host.Grpc;

/// <summary>
/// The gRPC door onto the data plane: the same endpoints, the same envelope and the same authorization
/// as <c>/api</c>, reached over HTTP/2.
/// <para>
/// The payload stays JSON rather than becoming a generated message per endpoint, and that is a decision
/// rather than a shortcut: Weir's endpoints are metadata an operator adds at run time, so there is
/// nothing to generate a message from at build time, and a gateway that needed a redeploy per endpoint
/// would defeat its own purpose. What gRPC contributes here is the transport - one multiplexed
/// connection, real server streaming, deadlines, and a generated client in a dozen languages.
/// </para>
/// </summary>
public sealed class WeirGatewayService : WeirGateway.WeirGatewayBase
{
    private readonly IApiKeyAuthenticator _authenticator;
    private readonly DataPlaneDispatcher _dispatcher;

    /// <summary>Creates the service over the shared data-plane collaborators.</summary>
    /// <param name="authenticator">API-key authenticator, applied to the call's metadata.</param>
    /// <param name="dispatcher">The shared data-plane call path.</param>
    public WeirGatewayService(IApiKeyAuthenticator authenticator, DataPlaneDispatcher dispatcher)
    {
        _authenticator = authenticator;
        _dispatcher = dispatcher;
    }

    /// <summary>Calls an endpoint and returns the whole envelope in one message.</summary>
    /// <param name="request">The call.</param>
    /// <param name="context">The gRPC call context.</param>
    /// <returns>The response envelope.</returns>
    public override async Task<InvokeReply> Invoke(InvokeRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var call = await BuildCallAsync(request, context);
        await using var output = new BufferedTransportStream();
        var result = await _dispatcher.InvokeAsync(call, output, context.CancellationToken);
        if (!result.IsSuccess)
        {
            throw Failure(result);
        }

        return new InvokeReply { Envelope = ByteString.CopyFrom(output.Written.Span) };
    }

    /// <summary>Calls an endpoint and streams the envelope out as it is produced.</summary>
    /// <param name="request">The call.</param>
    /// <param name="responseStream">The chunk stream.</param>
    /// <param name="context">The gRPC call context.</param>
    /// <returns>A task that completes when the last chunk is written.</returns>
    public override async Task InvokeStream(InvokeRequest request, IServerStreamWriter<InvokeChunk> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var call = await BuildCallAsync(request, context);
        await using var output = new GrpcChunkStream(responseStream);
        var result = await _dispatcher.InvokeAsync(call, output, context.CancellationToken);
        if (!result.IsSuccess)
        {
            // Failing after the first chunk is the one thing gRPC handles better than a streamed HTTP
            // response: the stream ends with an error status the client raises at the point it stopped,
            // instead of a truncated body it has to notice for itself.
            throw Failure(result);
        }
    }

    /// <summary>Authenticates the call and turns the request message into a dispatcher call.</summary>
    /// <param name="request">The call.</param>
    /// <param name="context">The gRPC call context.</param>
    /// <returns>The call to dispatch.</returns>
    private async Task<TransportCall> BuildCallAsync(InvokeRequest request, ServerCallContext context)
    {
        // The API key travels in call metadata, which is HTTP/2 headers - so the authenticator that
        // reads an /api request reads a gRPC call unchanged, and there is one place where a key is
        // resolved, throttled and cached.
        var http = context.GetHttpContext();
        var auth = await _authenticator.AuthenticateAsync(http, context.CancellationToken);
        if (auth.Status == ApiKeyAuthStatus.RateLimited)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                "Too many unauthenticated requests. Try again later."));
        }

        if (auth.Status != ApiKeyAuthStatus.Authenticated)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                "A valid API key is required in the \"x-api-key\" or \"authorization\" metadata."));
        }

        if (string.IsNullOrWhiteSpace(request.Route))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A call needs a route."));
        }

        JsonElement body = default;
        var hasBody = false;
        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(request.Body);
            }
            catch (JsonException ex)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"The body is not valid JSON: {ex.Message}"));
            }

            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "The body must be a JSON object whose members are the endpoint's parameters."));
            }

            // Cloned so the element outlives the document: the dispatcher reads it after this method
            // returns, and a using here would have freed the buffer underneath it.
            body = parsed.RootElement.Clone();
            hasBody = true;
            parsed.Dispose();
        }

        return new TransportCall
        {
            Transport = EndpointTransports.Grpc,
            Key = auth.Record!,
            Method = string.IsNullOrWhiteSpace(request.Method) ? HttpMethods.Post : request.Method,
            Route = request.Route.Trim('/'),
            Body = body,
            HasBody = hasBody,
            Query = request.Query.Count == 0 ? EmptyValueSource.Instance : new MapValueSource(request.Query),
            Header = new GrpcMetadataValueSource(context.RequestHeaders),
        };
    }

    /// <summary>Turns a dispatcher failure into the gRPC status a caller should see.</summary>
    /// <param name="result">The failed result.</param>
    /// <returns>The exception to throw.</returns>
    private static RpcException Failure(TransportCallResult result)
    {
        // The mapping is the conventional one, and it is deliberately lossy in one direction only: a
        // caller can always read the exact HTTP-shaped code back out of the message, which is what the
        // logs and the audit record.
        var code = result.Status switch
        {
            StatusCodes.Status400BadRequest => StatusCode.InvalidArgument,
            StatusCodes.Status403Forbidden => StatusCode.PermissionDenied,
            StatusCodes.Status404NotFound => StatusCode.NotFound,
            StatusCodes.Status429TooManyRequests => StatusCode.ResourceExhausted,
            StatusCodes.Status503ServiceUnavailable => StatusCode.Unavailable,
            StatusCodes.Status504GatewayTimeout => StatusCode.DeadlineExceeded,
            499 => StatusCode.Cancelled,
            _ => StatusCode.Internal,
        };

        return new RpcException(new Status(code, $"{result.Status} {result.Title}: {result.Detail}"));
    }
}

/// <summary>An <see cref="IValueSource"/> over a protobuf string map, for query-sourced parameters.</summary>
public sealed class MapValueSource : IValueSource
{
    private readonly MapField<string, string> _values;

    /// <summary>Creates the source over the request's query map.</summary>
    /// <param name="values">The map to read.</param>
    public MapValueSource(MapField<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    /// <inheritdoc />
    public bool TryGet(string key, out string? value)
    {
        if (_values.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }
}

/// <summary>
/// An <see cref="IValueSource"/> over gRPC call metadata, so an endpoint's header-sourced parameters
/// work on this transport too. Metadata keys are lower-case on the wire, which is why the lookup is
/// case-insensitive: an endpoint declaring <c>X-Tenant-Id</c> must not stop binding because the caller's
/// gRPC library normalized the name.
/// </summary>
public sealed class GrpcMetadataValueSource : IValueSource
{
    private readonly Metadata _metadata;

    /// <summary>Creates the source over the call's request metadata.</summary>
    /// <param name="metadata">The metadata to read.</param>
    public GrpcMetadataValueSource(Metadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
    }

    /// <inheritdoc />
    public bool TryGet(string key, out string? value)
    {
        foreach (var entry in _metadata)
        {
            if (!entry.IsBinary && string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
