using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Host.Options;

namespace Weir.Host.Http;

/// <summary>Which of Weir's surfaces a request path belongs to.</summary>
public enum RequestSurface
{
    /// <summary>
    /// Infrastructure routes that belong to both listeners: the health endpoints. A probe may arrive
    /// on either port, and a probe that answered nothing would drown a split deployment in noise.
    /// </summary>
    Shared,

    /// <summary>
    /// The endpoint API: <c>/api</c>, the WebSocket door at <c>/ws</c> and the gRPC gateway. What a
    /// public caller is allowed to reach.
    /// </summary>
    DataPlane,

    /// <summary>
    /// Everything else: the admin API under <c>/admin/api</c>, the dashboard hub under <c>/hubs</c>,
    /// and the admin PWA (its static assets and the fallback that serves its index page). What is
    /// meant for operators, not for the internet.
    /// </summary>
    Admin,
}

/// <summary>
/// The listener pair a configured split resolved to, and the decision about which surface may answer
/// on which port.
/// </summary>
/// <param name="Enabled">True when at least one surface was given a port of its own.</param>
/// <param name="DataPlanePort">The port the data plane answers on.</param>
/// <param name="AdminPort">The port the admin surface answers on.</param>
public readonly record struct PortSplit(bool Enabled, int DataPlanePort, int AdminPort)
{
    /// <summary>
    /// True when the two surfaces really sit on different ports, which is when a request's port has
    /// to be checked at all.
    /// </summary>
    /// <remarks>
    /// A split that resolved both surfaces onto one port - because only one port was configured and
    /// it is the one already in use - filters nothing. Without that case, the data-plane rule below
    /// would refuse the data plane on its own port, because that port is also the admin port.
    /// </remarks>
    public bool Filters => Enabled && DataPlanePort != AdminPort;

    /// <summary>Decides whether a request of the given surface may be answered on the given port.</summary>
    /// <param name="surface">The surface the request path belongs to.</param>
    /// <param name="localPort">The local port the request arrived on.</param>
    /// <returns>True when the request belongs on that port.</returns>
    public bool IsAllowed(RequestSurface surface, int localPort)
    {
        if (!Filters || surface == RequestSurface.Shared)
        {
            return true;
        }

        if (surface == RequestSurface.Admin)
        {
            // The admin surface exists on the admin listener, and nowhere else once one is named.
            return localPort == AdminPort;
        }

        // The data plane is refused on the admin listener but stays reachable on any other one: a
        // dedicated gRPC listener is a normal deployment here (cleartext gRPC cannot share a port
        // with HTTP/1.1), and refusing everything but one port would silently break it.
        return localPort != AdminPort;
    }
}

/// <summary>
/// Splits Weir's two surfaces by listener. Every route is still mapped once, on the application as a
/// whole: what tells the surfaces apart is the port a request arrives on, checked here before static
/// files, authentication and routing, so the data-plane port answers nothing of the admin surface -
/// not even the admin PWA's index page - and the admin port answers nothing of the data plane.
/// </summary>
public static class PortRouting
{
    /// <summary>The health root, which both listeners answer.</summary>
    private const string HealthPrefix = "/health";

    /// <summary>Classifies a request path by the surface it belongs to.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>The surface that path belongs to.</returns>
    /// <remarks>
    /// Anything the data plane does not own is admin: the admin PWA is served as static files and as
    /// a fallback for every route the application does not know, so treating "not the data plane" as
    /// admin is what keeps those files off the public port.
    /// </remarks>
    public static RequestSurface Classify(PathString path)
    {
        if (path.StartsWithSegments(HealthPrefix))
        {
            return RequestSurface.Shared;
        }

        if (path.StartsWithSegments("/api")
            || path.StartsWithSegments("/ws")
            || path.StartsWithSegments("/weir.v1.WeirGateway"))
        {
            return RequestSurface.DataPlane;
        }

        return RequestSurface.Admin;
    }

    /// <summary>Resolves the listener pair a configured split binds.</summary>
    /// <param name="options">The bound <c>Weir:Ports</c> options.</param>
    /// <param name="mainPort">The port a surface keeps when it was not given one of its own.</param>
    /// <returns>The resolved listener pair.</returns>
    public static PortSplit Resolve(PortRoutingOptions options, int mainPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PortSplit(options.Enabled, options.DataPlanePort ?? mainPort, options.AdminPort ?? mainPort);
    }

    /// <summary>True when a configured port is one a listener can bind.</summary>
    /// <param name="port">The configured port, or null when it was not set.</param>
    /// <returns>True when the value is usable.</returns>
    public static bool IsValidPort(int? port) => port is null or (>= 1 and <= 65535);

    /// <summary>
    /// Refuses a request whose surface does not belong on the port it arrived on, with a 404.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="split">The resolved listener pair.</param>
    /// <returns>The same application builder, for chaining.</returns>
    /// <remarks>
    /// 404 and no body, deliberately. On this port the route does not exist, which is exactly what
    /// the same request would get from an unregistered route; a body would say more than that (the
    /// shape of the admin API, or that an admin surface exists at all), and the shared problem+json
    /// writer calls <c>Response.Clear()</c>, which would drop the correlation id the pipeline has
    /// already put on the response.
    /// </remarks>
    public static IApplicationBuilder UseWeirPortRouting(this IApplicationBuilder app, PortSplit split)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!split.Filters)
        {
            return app;
        }

        return app.Use(next => context =>
        {
            var surface = Classify(context.Request.Path);
            if (split.IsAllowed(surface, context.Connection.LocalPort))
            {
                return next(context);
            }

            if (context.RequestServices?.GetService<ILoggerFactory>() is { } loggerFactory)
            {
                var logger = loggerFactory.CreateLogger("Weir.Ports");
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    Log.PortRequestRefused(
                        logger,
                        context.Request.Path.Value ?? "/",
                        context.Connection.LocalPort);
                }
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }
}
