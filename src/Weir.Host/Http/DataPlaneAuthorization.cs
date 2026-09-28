using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Host.Http;

/// <summary>
/// The two authorization questions every data-plane call asks, whatever door it came through: does the
/// caller hold the scopes the endpoint requires, and do its resource grants cover the object the
/// endpoint calls. HTTP, WebSocket and gRPC share this one copy - three transports answering the same
/// question three ways is how one of them ends up answering it wrongly.
/// <para>
/// The rule is stated once here, over the scope list and the grant list, and every caller adapts to it:
/// the transports pass a resolved <see cref="ApiKeyRecord"/>, and <see cref="EndpointAccess"/> passes
/// the lists it holds off the hot path (an OpenAPI document, an admin list). Stating it twice is how the
/// two answers drift, and a drift shows the caller less than it can actually reach - or more.
/// </para>
/// </summary>
public static class DataPlaneAuthorization
{
    /// <summary>Checks whether the scopes grant every scope the endpoint requires.</summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="scopes">The scopes held by the caller.</param>
    /// <returns>True if all required scopes are present.</returns>
    public static bool HasRequiredScopes(EndpointDefinition endpoint, IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(scopes);

        if (endpoint.RequiredScopes.Count == 0)
        {
            return true;
        }

        // Both sides hold a handful of entries in practice, so a nested scan beats building a hash set
        // per request: it allocates nothing and, at these sizes, finishes sooner than hashing would.
        foreach (var required in endpoint.RequiredScopes)
        {
            var granted = false;
            foreach (var scope in scopes)
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

    /// <summary>Checks whether the scopes on a resolved key grant every scope the endpoint requires.</summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="key">The authenticated key record.</param>
    /// <returns>True if all required scopes are present.</returns>
    public static bool HasRequiredScopes(EndpointDefinition endpoint, ApiKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return HasRequiredScopes(endpoint, key.Scopes);
    }

    /// <summary>
    /// Checks whether the grants allow this endpoint's procedure. A caller with no grants is
    /// unrestricted; otherwise at least one grant must match the endpoint's connection, schema and
    /// object.
    /// </summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="grants">The resource grants held by the caller (empty means unrestricted).</param>
    /// <returns>True if the caller may call this procedure.</returns>
    public static bool IsGrantedResource(EndpointDefinition endpoint, IReadOnlyList<ApiKeyGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(grants);

        if (grants.Count == 0)
        {
            return true;
        }

        foreach (var grant in grants)
        {
            if (grant.Allows(endpoint.ConnectionName, endpoint.Schema, endpoint.ObjectName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Checks whether the grants on a resolved key allow this endpoint's procedure.</summary>
    /// <param name="endpoint">The resolved endpoint.</param>
    /// <param name="key">The authenticated key record.</param>
    /// <returns>True if the key may call this procedure.</returns>
    public static bool IsGrantedResource(EndpointDefinition endpoint, ApiKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return IsGrantedResource(endpoint, key.Grants);
    }
}
