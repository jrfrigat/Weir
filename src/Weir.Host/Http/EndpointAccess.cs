using Weir.Contracts;

namespace Weir.Host.Http;

/// <summary>
/// Decides whether a set of API-key scopes and resource grants would be allowed to call an endpoint, for
/// the callers that hold the lists rather than a resolved key: an OpenAPI document scoped to a single
/// key, and the endpoint list in the admin UI.
/// <para>
/// The rule is not stated here. It lives in <see cref="DataPlaneAuthorization"/>, which the transports
/// use on the hot path; this type only adapts the two lists to it. Keeping one copy is the point - what
/// the document and the admin list report as reachable cannot then drift from what the data plane
/// actually allows.
/// </para>
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
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(grants);

        return DataPlaneAuthorization.HasRequiredScopes(endpoint, scopes)
            && DataPlaneAuthorization.IsGrantedResource(endpoint, grants);
    }
}
