using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>An endpoint resolved from a request, together with anything captured from its route template.</summary>
/// <param name="Endpoint">The matched endpoint definition.</param>
/// <param name="RouteValues">Values captured from the route template; empty for a literal route.</param>
public readonly record struct EndpointMatch(EndpointDefinition Endpoint, IValueSource RouteValues);

/// <summary>In-memory, atomically-swappable snapshot of enabled endpoints, keyed by method + route.</summary>
public interface IEndpointCatalog
{
    /// <summary>(Re)loads endpoints from the control-plane store and swaps the snapshot.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an enabled endpoint by HTTP method and request path, matching literal routes first and
    /// then route templates such as <c>orders/{id}</c>.
    /// </summary>
    /// <param name="method">The request's HTTP method.</param>
    /// <param name="route">The request path beneath <c>/api/</c>.</param>
    /// <param name="match">The resolved endpoint and its captured route values.</param>
    /// <returns>True when an enabled endpoint matches.</returns>
    bool TryResolve(string method, string route, out EndpointMatch match);

    /// <summary>All enabled endpoints in the current snapshot.</summary>
    IReadOnlyList<EndpointDefinition> All { get; }
}

/// <summary>Default <see cref="IEndpointCatalog"/> backed by the control-plane store.</summary>
public sealed partial class EndpointCatalog : IEndpointCatalog
{
    private readonly IControlPlaneStore _store;
    private readonly ILogger<EndpointCatalog> _logger;
    private volatile Snapshot _snapshot = new(new Dictionary<string, MethodBucket>(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>Creates the catalog over the control-plane store.</summary>
    /// <param name="store">The control-plane store to load endpoints from.</param>
    /// <param name="logger">Logger used to warn about route collisions; defaults to a no-op logger.</param>
    public EndpointCatalog(IControlPlaneStore store, ILogger<EndpointCatalog>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<EndpointCatalog>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<EndpointDefinition> All => _snapshot.All;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var endpoints = await _store.GetEndpointsAsync(cancellationToken);
        var byMethod = new Dictionary<string, MethodBucket>(StringComparer.OrdinalIgnoreCase);
        var list = new List<EndpointDefinition>(endpoints.Count);
        var templates = new Dictionary<string, List<RouteTemplate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in endpoints)
        {
            if (!endpoint.Enabled)
            {
                continue;
            }

            var bucket = Bucket(byMethod, endpoint.HttpMethod);
            var path = endpoint.Route.Trim('/');
            var template = RouteTemplate.Parse(path, endpoint);

            if (template is null)
            {
                // A literal route: resolvable by an exact lookup, which is the common case.
                if (bucket.Literals.TryGetValue(path, out var existing))
                {
                    // Two enabled endpoints canonicalize to the same method+route (differing only by case
                    // or slashes). The later one wins the dictionary slot; warn so the collision is visible
                    // instead of an endpoint silently disappearing. Remove the old entry from the list
                    // so it does not appear as a ghost in the All collection.
                    LogRouteCollision(endpoint.HttpMethod, endpoint.Route, endpoint.Id, existing.Id);
                    list.Remove(existing);
                }

                bucket.Literals[path] = endpoint;
            }
            else
            {
                var forMethod = Templates(templates, endpoint.HttpMethod);
                var clash = forMethod.Find(t => string.Equals(t.Signature, template.Signature, StringComparison.OrdinalIgnoreCase));
                if (clash is not null)
                {
                    // Same shape with the same literals - e.g. orders/{id} and orders/{orderId} - so no
                    // request could tell them apart.
                    LogRouteCollision(endpoint.HttpMethod, endpoint.Route, endpoint.Id, clash.Endpoint.Id);
                    list.Remove(clash.Endpoint);
                    forMethod.Remove(clash);
                }

                forMethod.Add(template);
            }

            list.Add(endpoint);
        }

        foreach (var (method, forMethod) in templates)
        {
            // Most specific first: a template with more literal segments wins over a looser one, so
            // orders/{id}/lines is tried before orders/{id}/{part}.
            forMethod.Sort(static (a, b) => b.LiteralCount.CompareTo(a.LiteralCount));
            Bucket(byMethod, method).Templates = [.. forMethod];
        }

        _snapshot = new Snapshot(byMethod, list);
    }

    /// <inheritdoc />
    public bool TryResolve(string method, string route, out EndpointMatch match)
    {
        var snapshot = _snapshot;
        if (!snapshot.ByMethod.TryGetValue(method, out var bucket))
        {
            match = default;
            return false;
        }

        // Trim returns the same instance when there is nothing to trim, and both dictionaries compare
        // ignoring case, so resolving a literal route allocates nothing.
        var path = route.Trim('/');
        if (bucket.Literals.TryGetValue(path, out var literal))
        {
            match = new EndpointMatch(literal, EmptyValueSource.Instance);
            return true;
        }

        foreach (var template in bucket.Templates)
        {
            if (template.TryMatch(path, out var values))
            {
                match = new EndpointMatch(
                    template.Endpoint,
                    values is null ? EmptyValueSource.Instance : new RouteValueSource(template.Names, values));
                return true;
            }
        }

        match = default;
        return false;
    }

    /// <summary>Returns (creating if needed) the bucket holding one method's routes.</summary>
    /// <param name="byMethod">The method map being built.</param>
    /// <param name="method">The HTTP method.</param>
    /// <returns>The bucket for that method.</returns>
    private static MethodBucket Bucket(Dictionary<string, MethodBucket> byMethod, string method)
    {
        if (!byMethod.TryGetValue(method, out var bucket))
        {
            bucket = new MethodBucket();
            byMethod[method] = bucket;
        }

        return bucket;
    }

    /// <summary>Returns (creating if needed) the template list being accumulated for one method.</summary>
    /// <param name="templates">The per-method template lists.</param>
    /// <param name="method">The HTTP method.</param>
    /// <returns>The list for that method.</returns>
    private static List<RouteTemplate> Templates(Dictionary<string, List<RouteTemplate>> templates, string method)
    {
        if (!templates.TryGetValue(method, out var forMethod))
        {
            forMethod = [];
            templates[method] = forMethod;
        }

        return forMethod;
    }

    /// <summary>Warns that two enabled endpoints resolve to the same canonical key.</summary>
    /// <param name="method">HTTP method of the colliding endpoint.</param>
    /// <param name="route">Route of the colliding endpoint.</param>
    /// <param name="newId">Id of the endpoint that wins the slot.</param>
    /// <param name="existingId">Id of the endpoint that was overwritten.</param>
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Endpoint route collision: '{Method} {Route}' (id {NewId}) canonicalizes to the same key as id {ExistingId}; the later definition is served.")]
    private partial void LogRouteCollision(string method, string route, Guid newId, Guid existingId);

    /// <summary>One method's routes: exact matches, then templates ordered most-specific first.</summary>
    private sealed class MethodBucket
    {
        /// <summary>Routes with no capture, resolvable by an exact lookup.</summary>
        public Dictionary<string, EndpointDefinition> Literals { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Templated routes, most specific first. An array so iterating it allocates no enumerator.</summary>
        public RouteTemplate[] Templates { get; set; } = [];
    }

    /// <summary>The immutable resolution state, swapped atomically on reload.</summary>
    /// <param name="ByMethod">Routes grouped by HTTP method.</param>
    /// <param name="All">Every enabled endpoint.</param>
    private sealed record Snapshot(Dictionary<string, MethodBucket> ByMethod, IReadOnlyList<EndpointDefinition> All);

    /// <summary>
    /// A route with at least one capture, e.g. <c>orders/{id}</c>. Only a whole segment can be a capture;
    /// a segment is either a literal or a single <c>{name}</c>.
    /// </summary>
    private sealed class RouteTemplate
    {
        /// <summary>Per segment: the literal text, or null where the segment is a capture.</summary>
        private readonly string?[] _literals;

        /// <summary>Per segment: the capture name, or null where the segment is a literal.</summary>
        public string?[] Names { get; }

        /// <summary>How many segments are literal; used to order more specific templates first.</summary>
        public int LiteralCount { get; }

        /// <summary>The template's shape, with capture names erased, so two that no request can tell apart compare equal.</summary>
        public string Signature { get; }

        /// <summary>The endpoint this template resolves to.</summary>
        public EndpointDefinition Endpoint { get; }

        private RouteTemplate(string?[] literals, string?[] names, int literalCount, string signature, EndpointDefinition endpoint)
        {
            _literals = literals;
            Names = names;
            LiteralCount = literalCount;
            Signature = signature;
            Endpoint = endpoint;
        }

        /// <summary>Parses a route into a template, or returns null when it has no captures.</summary>
        /// <param name="path">The endpoint's route, already trimmed of slashes.</param>
        /// <param name="endpoint">The endpoint the template resolves to.</param>
        /// <returns>The template, or null for a literal route.</returns>
        public static RouteTemplate? Parse(string path, EndpointDefinition endpoint)
        {
            var parts = path.Split('/');
            var literals = new string?[parts.Length];
            var names = new string?[parts.Length];
            var literalCount = 0;
            var captures = 0;

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Length > 2 && part[0] == '{' && part[^1] == '}')
                {
                    names[i] = part[1..^1];
                    captures++;
                }
                else
                {
                    literals[i] = part;
                    literalCount++;
                }
            }

            if (captures == 0)
            {
                return null;
            }

            var signature = string.Join('/', parts.Select((p, i) => names[i] is null ? p : "{}"));
            return new RouteTemplate(literals, names, literalCount, signature, endpoint);
        }

        /// <summary>
        /// Matches a request path against this template, capturing the segments that line up with a name.
        /// </summary>
        /// <param name="path">The request path, trimmed of slashes.</param>
        /// <param name="values">The captured values, indexed like <see cref="Names"/>; null when the template has no captures to fill.</param>
        /// <returns>True when the path matches.</returns>
        public bool TryMatch(ReadOnlySpan<char> path, out string[]? values)
        {
            values = null;
            var segments = Names.Length;
            var remaining = path;
            string[]? captured = null;

            for (var i = 0; i < segments; i++)
            {
                var slash = remaining.IndexOf('/');
                var last = i == segments - 1;

                // Too few segments to fill the template, or too many to be consumed by it.
                if ((slash < 0) != last)
                {
                    return false;
                }

                var segment = last ? remaining : remaining[..slash];
                if (!last)
                {
                    remaining = remaining[(slash + 1)..];
                }

                if (Names[i] is null)
                {
                    if (!segment.Equals(_literals[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                else
                {
                    // An empty capture would bind a parameter to "", which is never what a caller meant.
                    if (segment.IsEmpty)
                    {
                        return false;
                    }

                    captured ??= new string[segments];
                    captured[i] = segment.ToString();
                }
            }

            values = captured;
            return true;
        }
    }

    /// <summary>
    /// The values captured from a route template. Backed by the template's parallel name/value arrays and
    /// scanned linearly: a route carries a couple of captures, so this beats hashing and allocates nothing
    /// beyond the captured strings themselves.
    /// </summary>
    private sealed class RouteValueSource : IValueSource
    {
        private readonly string?[] _names;
        private readonly string[] _values;

        /// <summary>Creates the source over a template's names and one request's captured values.</summary>
        /// <param name="names">Capture names, null where the segment was a literal.</param>
        /// <param name="values">Captured values, indexed like <paramref name="names"/>.</param>
        public RouteValueSource(string?[] names, string[] values)
        {
            _names = names;
            _values = values;
        }

        /// <inheritdoc />
        public bool TryGet(string key, out string? value)
        {
            for (var i = 0; i < _names.Length; i++)
            {
                if (_names[i] is { } name && string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = _values[i];
                    return true;
                }
            }

            value = null;
            return false;
        }
    }
}
