using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Host.Options;

namespace Weir.Host.Http;

/// <summary>
/// Answers "which CORS policy applies to this origin?" without being told which key is calling.
/// <para>
/// It exists because a CORS preflight carries no API key and cannot be made to. The browser sends
/// <c>OPTIONS</c> with <c>Origin</c> and <c>Access-Control-Request-Method</c> and nothing else of
/// the caller's - deciding, from the answer, whether it is willing to send the real request with its
/// headers at all. So on a preflight the only thing Weir knows about the caller is its origin, and
/// the per-key policies have to be looked at the other way round: from origin to policy.
/// </para>
/// <para>
/// Where several keys name the same origin, their policies are merged rather than one being picked:
/// there is nothing on a preflight to pick by, and picking "the first" would make the answer depend
/// on key creation order. A merged answer can be more permissive than one key's own policy, which is
/// safe because the preflight only describes what the browser may try - the real request carries the
/// key and is answered by that key's policy alone.
/// </para>
/// </summary>
public sealed class CorsPolicyIndex : IDisposable
{
    /// <summary>
    /// How long a built index is reused. The same window the authenticator caches a resolved key for,
    /// and for the same reason. It is a backstop, not the mechanism: <see cref="Invalidate"/> is what
    /// makes a policy change take effect at once, and this only bounds the damage of a missed call.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>Control-plane store the key policies are read from.</summary>
    private readonly IControlPlaneStore _store;

    /// <summary>The server-wide default policy, or null when none is configured.</summary>
    private readonly ApiKeyCorsPolicy? _serverPolicy;

    /// <summary>Clock used to expire a built index.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Logger for a failed rebuild.</summary>
    private readonly ILogger _log;

    /// <summary>Serializes rebuilds so a burst of preflights does not read the key list many times over.</summary>
    private readonly SemaphoreSlim _rebuild = new(1, 1);

    /// <summary>The current snapshot, or null when none has been built or it was invalidated.</summary>
    private volatile Snapshot? _snapshot;

    /// <summary>Creates the index.</summary>
    /// <param name="store">Control-plane store the key policies are read from.</param>
    /// <param name="cors">The server-wide CORS section.</param>
    /// <param name="clock">Clock used to expire a built index.</param>
    /// <param name="loggerFactory">Factory for the logger.</param>
    public CorsPolicyIndex(
        IControlPlaneStore store,
        IOptions<WeirCorsOptions> cors,
        TimeProvider clock,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(cors);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _store = store;
        _serverPolicy = cors.Value.ToPolicy();
        _clock = clock;
        _log = loggerFactory.CreateLogger("Weir.Cors");
    }

    /// <summary>The server-wide policy, which a key stating none falls back to.</summary>
    public ApiKeyCorsPolicy? ServerPolicy => _serverPolicy;

    /// <summary>
    /// Drops the built index so the next preflight reads the keys again. Called wherever the API-key
    /// cache is invalidated - a key created, revoked, or its policies changed - because a policy an
    /// operator has just saved and cannot see working reads as a policy that does not work.
    /// </summary>
    public void Invalidate() => _snapshot = null;

    /// <summary>Releases the rebuild lock.</summary>
    public void Dispose() => _rebuild.Dispose();

    /// <summary>
    /// The policy to answer a preflight from this origin with, or null when no key and no server-wide
    /// setting names it.
    /// </summary>
    /// <param name="origin">The request's <c>Origin</c> header value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The policy to answer with, or null to answer with no CORS headers at all.</returns>
    public async ValueTask<ApiKeyCorsPolicy?> ResolveForPreflightAsync(string origin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return null;
        }

        // The server-wide policy is checked first and without touching the store: a deployment that
        // only uses Weir:Cors should never pay for a key read, which is the behaviour this section had
        // before per-key policies existed.
        if (_serverPolicy is not null && _serverPolicy.AllowsOrigin(origin))
        {
            return _serverPolicy;
        }

        var snapshot = await CurrentAsync(cancellationToken);
        return snapshot.Resolve(origin);
    }

    /// <summary>Returns a fresh-enough snapshot, building one if needed.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot.</returns>
    private async ValueTask<Snapshot> CurrentAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var current = _snapshot;
        if (current is not null && current.BuiltAt + Ttl > now)
        {
            return current;
        }

        await _rebuild.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have rebuilt it while this one waited.
            current = _snapshot;
            if (current is not null && current.BuiltAt + Ttl > _clock.GetUtcNow())
            {
                return current;
            }

            var built = await BuildAsync(now, cancellationToken);
            _snapshot = built;
            return built;
        }
        finally
        {
            _rebuild.Release();
        }
    }

    /// <summary>Reads the keys and indexes the policies of those that may authenticate.</summary>
    /// <param name="now">The time to stamp the snapshot with and to judge expiry against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The built snapshot; an empty one when the keys could not be read.</returns>
    private async Task<Snapshot> BuildAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApiKeyInfo> keys;
        try
        {
            keys = await _store.GetApiKeysAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A preflight is not worth failing a deployment over, and an empty index answers "no CORS
            // headers", which a browser reads as "not allowed" - the closing direction. Logged, because
            // an operator whose CORS silently stopped working needs to find this line.
            Log.CorsIndexRebuildFailed(_log, ex);
            return new Snapshot(now, []);
        }

        var byOrigin = new Dictionary<string, ApiKeyCorsPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (key.Cors is not { } policy || policy.IsEmpty)
            {
                // No policy, or one that names no origin. The second is a deliberate "this key may not
                // be called cross-origin", and it contributes nothing to any origin's answer.
                continue;
            }

            if (!key.Enabled || (key.ExpiresAt is { } expiry && expiry <= now))
            {
                // A key that cannot authenticate must not widen a preflight answer.
                continue;
            }

            foreach (var origin in policy.AllowedOrigins)
            {
                var normalized = origin.TrimEnd('/');
                byOrigin[normalized] = byOrigin.TryGetValue(normalized, out var existing)
                    ? Merge(existing, policy)
                    : policy;
            }
        }

        return new Snapshot(now, byOrigin);
    }

    /// <summary>
    /// Merges two policies that name the same origin. Methods, headers and exposed headers are
    /// unioned; credentials are allowed if either policy allows them; the max-age is the smaller of
    /// the two, because the shorter cache is the one that recovers sooner from a policy change.
    /// </summary>
    /// <param name="left">One policy.</param>
    /// <param name="right">The other policy.</param>
    /// <returns>The merged policy.</returns>
    private static ApiKeyCorsPolicy Merge(ApiKeyCorsPolicy left, ApiKeyCorsPolicy right) => new()
    {
        AllowedOrigins = Union(left.AllowedOrigins, right.AllowedOrigins),
        AllowedMethods = Union(left.AllowedMethods, right.AllowedMethods),
        AllowedHeaders = Union(left.AllowedHeaders, right.AllowedHeaders),
        ExposedHeaders = Union(left.ExposedHeaders, right.ExposedHeaders),
        AllowCredentials = left.AllowCredentials || right.AllowCredentials,
        MaxAgeSeconds = (left.MaxAgeSeconds, right.MaxAgeSeconds) switch
        {
            (null, var r) => r,
            (var l, null) => l,
            var (l, r) => Math.Min(l!.Value, r!.Value),
        },
    };

    /// <summary>Unions two lists, ignoring case and keeping a stable order.</summary>
    /// <param name="left">One list.</param>
    /// <param name="right">The other list.</param>
    /// <returns>The union.</returns>
    private static IReadOnlyList<string> Union(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0)
        {
            return right;
        }

        if (right.Count == 0)
        {
            return left;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>(left.Count + right.Count);
        foreach (var value in left)
        {
            if (seen.Add(value))
            {
                merged.Add(value);
            }
        }

        foreach (var value in right)
        {
            if (seen.Add(value))
            {
                merged.Add(value);
            }
        }

        return merged;
    }

    /// <summary>One built index, with the time it was built so it can be expired.</summary>
    /// <param name="BuiltAt">When this index was built.</param>
    /// <param name="ByOrigin">Policies keyed by the origin that names them, exact matches only.</param>
    private sealed record Snapshot(DateTimeOffset BuiltAt, Dictionary<string, ApiKeyCorsPolicy> ByOrigin)
    {
        /// <summary>The policy for an origin, or null when nothing names it.</summary>
        /// <param name="origin">The origin to look up.</param>
        /// <returns>The policy, or null.</returns>
        public ApiKeyCorsPolicy? Resolve(string origin)
        {
            if (ByOrigin.TryGetValue(origin.TrimEnd('/'), out var exact))
            {
                return exact;
            }

            // A wildcard entry is indexed under "*" rather than under every origin, so it is looked up
            // separately - a policy allowing any origin has to answer an origin nobody enumerated.
            return ByOrigin.TryGetValue("*", out var any) ? any : null;
        }
    }
}
