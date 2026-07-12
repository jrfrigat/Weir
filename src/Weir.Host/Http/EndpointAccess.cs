using Weir.Contracts;

namespace Weir.Host.Http;

/// <summary>
/// Decides whether a set of API-key scopes and resource grants would be allowed to call an endpoint.
/// This mirrors the data-plane authorization in <see cref="DataPlaneEndpoints"/> (required-scope
/// coverage plus resource-grant matching) and is used off the hot path - for example to produce an
/// OpenAPI document scoped to a single key, and to filter the endpoint list in the admin UI.
/// </summary>
public static class EndpointAccess
{
    /// <summary>
    /// Determines whether a key with the given scopes and grants may call the endpoint: it must hold
    /// every scope the endpoint requires, and (unless it has no grants, which means unrestricted) at
    /// least one grant must match the endpoint's connection, schema and object.
    /// </summary>
    /// <param name="endpoint">The endpoint to test.</param>
    /// <param name="scopes">The scopes held by the key.</param>
    /// <param name="grants">The resource grants on the key (empty means unrestricted).</param>
    /// <returns>True if the key would be authorized to call the endpoint.</returns>
    public static bool IsAccessibleBy(EndpointDefinition endpoint, IReadOnlyList<string> scopes, IReadOnlyList<ApiKeyGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(grants);

        return HasRequiredScopes(endpoint, scopes) && IsGrantedResource(endpoint, grants);
    }

    /// <summary>Checks that the scope set covers every scope the endpoint requires.</summary>
    private static bool HasRequiredScopes(EndpointDefinition endpoint, IReadOnlyList<string> scopes)
    {
        if (endpoint.RequiredScopes.Count == 0)
        {
            return true;
        }

        var granted = new HashSet<string>(scopes, StringComparer.Ordinal);
        foreach (var scope in endpoint.RequiredScopes)
        {
            if (!granted.Contains(scope))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks that the grants (if any) allow the endpoint's procedure.</summary>
    private static bool IsGrantedResource(EndpointDefinition endpoint, IReadOnlyList<ApiKeyGrant> grants)
    {
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
}
