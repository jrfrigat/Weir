using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Host.Http;

/// <summary>
/// The two authorization questions every data-plane call asks, whatever door it came through: does the
/// key hold the scopes the endpoint requires, and do its resource grants cover the object the endpoint
/// calls. HTTP, WebSocket and gRPC share this one copy - three transports answering the same question
/// three ways is how one of them ends up answering it wrongly.
/// <para>
/// Deliberately separate from <see cref="EndpointAccess"/>, which asks the same question off the hot
/// path (an OpenAPI document, an admin list) from a scope list rather than from a key record. This one
/// allocates nothing.
/// </para>
/// </summary>
public static class DataPlaneAuthorization
{
    /// <summary>Checks whether the key grants every scope the endpoint requires.</summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="key">The authenticated key record.</param>
    /// <returns>True if all required scopes are present.</returns>
    public static bool HasRequiredScopes(EndpointDefinition endpoint, ApiKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key);

        if (endpoint.RequiredScopes.Count == 0)
        {
            return true;
        }

        // Both sides hold a handful of entries in practice, so a nested scan beats building a hash set
        // per request: it allocates nothing and, at these sizes, finishes sooner than hashing would.
        foreach (var required in endpoint.RequiredScopes)
        {
            var granted = false;
            foreach (var scope in key.Scopes)
            {
                if (string.Equals(scope, required, StringComparison.Ordinal))
                {
                    granted = true;
                    break;
                }
            }

            if (!granted)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether the key's resource grants allow this endpoint's procedure. A key with no
    /// grants is unrestricted; otherwise at least one grant must match the endpoint's connection,
    /// schema and object.
    /// </summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="key">The authenticated key record.</param>
    /// <returns>True if the key may call this procedure.</returns>
    public static bool IsGrantedResource(EndpointDefinition endpoint, ApiKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Grants.Count == 0)
        {
            return true;
        }

        foreach (var grant in key.Grants)
        {
            if (grant.Allows(endpoint.ConnectionName, endpoint.Schema, endpoint.ObjectName))
            {
                return true;
            }
        }

        return false;
    }
}
