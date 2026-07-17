using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Weir.Contracts;

namespace Weir.Admin.Services;

/// <summary>
/// The part of an RFC 7807 error body this client reads back. Declared here rather than pulled in from
/// ASP.NET: the admin is a WebAssembly app, and one property is not worth a server-side dependency.
/// </summary>
/// <param name="Title">Short summary of the problem type.</param>
/// <param name="Detail">The explanation meant for a human, which is what gets shown.</param>
internal sealed record ProblemDetail(string? Title, string? Detail);

/// <summary>
/// A mutation the admin API refused. Carries the server's own explanation (the RFC 7807 problem
/// <c>detail</c>, or a status fallback), so a page can show why a save or delete failed instead of
/// letting the raw failure reach the generic Blazor error boundary.
/// </summary>
public sealed class WeirApiException : Exception
{
    /// <summary>Creates the exception with a human-readable message drawn from the failed response.</summary>
    /// <param name="message">The message to show.</param>
    public WeirApiException(string message) : base(message)
    {
    }
}

/// <summary>Typed client for the Weir admin API under <c>/admin/api</c>.</summary>
public sealed class WeirApiClient
{
    private static readonly System.Text.Json.JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly HttpClient _http;

    /// <summary>Creates the client over the Bearer-enabled HttpClient.</summary>
    /// <param name="http">The HttpClient targeting the host origin.</param>
    public WeirApiClient(HttpClient http) => _http = http;

    // ----- Auth ------------------------------------------------------------------------------

    /// <summary>Attempts an admin sign-in; returns the token response or null on failure.</summary>
    /// <param name="request">The credentials.</param>
    /// <returns>The login response, or null if authentication failed.</returns>
    public async Task<LoginResponse?> LoginAsync(LoginRequest request)
    {
        var response = await _http.PostAsJsonAsync("admin/api/auth/login", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LoginResponse>()
            : null;
    }

    /// <summary>Gets the signed-in admin's identity (username and role).</summary>
    /// <returns>The current admin, or null if not authenticated.</returns>
    public Task<CurrentAdmin?> GetCurrentAdminAsync() =>
        _http.GetFromJsonAsync<CurrentAdmin>("admin/api/auth/me");

    /// <summary>Revokes a refresh token on sign-out. Always treated as best-effort.</summary>
    /// <param name="refreshToken">The refresh token to revoke, or null.</param>
    /// <returns>A task that completes when the request finishes.</returns>
    public async Task LogoutAsync(string? refreshToken)
    {
        try
        {
            await _http.PostAsJsonAsync("admin/api/auth/logout", new LogoutRequest { RefreshToken = refreshToken });
        }
        catch (HttpRequestException)
        {
            // Sign-out proceeds locally even if the revoke call fails; the token still expires server side.
        }
    }

    /// <summary>Changes the signed-in admin's own password.</summary>
    /// <param name="request">The current and new passwords.</param>
    /// <returns>True on success, false if the current password was wrong.</returns>
    public async Task<bool> ChangeOwnPasswordAsync(ChangeOwnPasswordRequest request)
    {
        var response = await _http.PostAsJsonAsync("admin/api/account/password", request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Lists the signed-in admin's personal access tokens.</summary>
    /// <returns>The token list.</returns>
    public Task<List<AdminTokenInfo>?> GetAccountTokensAsync() =>
        _http.GetFromJsonAsync<List<AdminTokenInfo>>("admin/api/account/tokens");

    /// <summary>Creates a personal access token; the plaintext is returned once.</summary>
    /// <param name="request">The token request (name and optional expiry).</param>
    /// <returns>The created token with its plaintext, or null on failure.</returns>
    public async Task<AdminTokenCreated?> CreateAccountTokenAsync(AdminTokenCreate request)
    {
        var response = await _http.PostAsJsonAsync("admin/api/account/tokens", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AdminTokenCreated>()
            : null;
    }

    /// <summary>Revokes one of the signed-in admin's personal access tokens.</summary>
    /// <param name="id">The token id.</param>
    /// <returns>A task that completes when revoked; throws if the server rejects the revoke.</returns>
    public async Task RevokeAccountTokenAsync(Guid id) =>
        (await _http.DeleteAsync($"admin/api/account/tokens/{id}")).EnsureSuccessStatusCode();

    // ----- Metrics ---------------------------------------------------------------------------

    /// <summary>Gets the dashboard overview.</summary>
    /// <returns>The metrics overview.</returns>
    public Task<MetricsOverview?> GetOverviewAsync() =>
        _http.GetFromJsonAsync<MetricsOverview>("admin/api/metrics/overview");

    /// <summary>Gets per-endpoint metrics.</summary>
    /// <returns>The endpoint metrics list.</returns>
    public Task<List<EndpointMetrics>?> GetEndpointMetricsAsync() =>
        _http.GetFromJsonAsync<List<EndpointMetrics>>("admin/api/metrics/endpoints");

    /// <summary>Gets a metric time series.</summary>
    /// <param name="metric">Metric name (requests, errors, latency, cacheHitRatio).</param>
    /// <param name="route">Optional route to scope to; null for service-wide.</param>
    /// <param name="windowSeconds">Window length in seconds.</param>
    /// <param name="bucketSeconds">Bucket width in seconds.</param>
    /// <returns>The time series.</returns>
    public Task<TimeSeries?> GetTimeSeriesAsync(string metric, string? route, int windowSeconds, int bucketSeconds)
    {
        var query = $"admin/api/metrics/timeseries?metric={Uri.EscapeDataString(metric)}&windowSeconds={windowSeconds}&bucketSeconds={bucketSeconds}";
        if (!string.IsNullOrEmpty(route))
        {
            query += $"&route={Uri.EscapeDataString(route)}";
        }

        return _http.GetFromJsonAsync<TimeSeries>(query);
    }

    /// <summary>Gets the configured data connections (without connection strings).</summary>
    /// <returns>The connection list.</returns>
    public Task<List<ConnectionInfo>?> GetConnectionsAsync() =>
        _http.GetFromJsonAsync<List<ConnectionInfo>>("admin/api/connections");

    /// <summary>Probes each data connection and returns its health.</summary>
    /// <returns>The per-connection health list.</returns>
    public Task<List<ConnectionHealth>?> GetConnectionHealthAsync() =>
        _http.GetFromJsonAsync<List<ConnectionHealth>>("admin/api/connections/health");

    // ----- Endpoints -------------------------------------------------------------------------

    /// <summary>Lists all endpoint definitions.</summary>
    /// <returns>The endpoints.</returns>
    public Task<List<EndpointDefinition>?> GetEndpointsAsync() =>
        _http.GetFromJsonAsync<List<EndpointDefinition>>("admin/api/endpoints");

    /// <summary>Creates or updates an endpoint.</summary>
    /// <param name="endpoint">The endpoint definition.</param>
    /// <returns>The stored endpoint.</returns>
    public async Task<EndpointDefinition?> SaveEndpointAsync(EndpointDefinition endpoint)
    {
        var response = await EnsureSuccessAsync(await _http.PostAsJsonAsync("admin/api/endpoints", endpoint));
        return await response.Content.ReadFromJsonAsync<EndpointDefinition>();
    }

    /// <summary>Deletes an endpoint by id.</summary>
    /// <param name="id">The endpoint id.</param>
    /// <returns>A task that completes when deleted; throws if the server rejects the delete.</returns>
    public async Task DeleteEndpointAsync(Guid id) =>
        await EnsureSuccessAsync(await _http.DeleteAsync($"admin/api/endpoints/{id}"));

    /// <summary>Lists the stored procedures and functions on a connection.</summary>
    /// <param name="connection">The connection name.</param>
    /// <returns>The discovered objects.</returns>
    public Task<List<DbObjectDescriptor>?> GetDbObjectsAsync(string connection) =>
        _http.GetFromJsonAsync<List<DbObjectDescriptor>>($"admin/api/introspect/{Uri.EscapeDataString(connection)}/objects");

    /// <summary>Describes the parameters of a stored procedure or function.</summary>
    /// <param name="connection">The connection name.</param>
    /// <param name="schema">Object schema.</param>
    /// <param name="objectName">Object name.</param>
    /// <returns>The discovered parameters.</returns>
    public Task<List<DbParameterDescriptor>?> GetDbParametersAsync(string connection, string schema, string objectName) =>
        _http.GetFromJsonAsync<List<DbParameterDescriptor>>(
            $"admin/api/introspect/{Uri.EscapeDataString(connection)}/parameters?schema={Uri.EscapeDataString(schema)}&obj={Uri.EscapeDataString(objectName)}");

    /// <summary>Imports a set of endpoint definitions (upsert), then reloads the catalog server-side.</summary>
    /// <param name="endpoints">The endpoints to import.</param>
    /// <returns>The number of imported endpoints.</returns>
    public async Task<int> ImportEndpointsAsync(List<EndpointDefinition> endpoints)
    {
        var response = await _http.PostAsJsonAsync("admin/api/endpoints/import", endpoints);
        response.EnsureSuccessStatusCode();
        return endpoints.Count;
    }

    /// <summary>Fetches the generated OpenAPI document as a formatted JSON string.</summary>
    /// <param name="keyId">Optional API-key id to narrow the document to that key's reachable surface.</param>
    /// <param name="scope">Optional scope to narrow the document to endpoints that require it.</param>
    /// <returns>The OpenAPI 3.0 document text.</returns>
    public async Task<string> GetOpenApiAsync(Guid? keyId = null, string? scope = null)
    {
        var query = new List<string>();
        if (keyId is { } id)
        {
            query.Add("key=" + Uri.EscapeDataString(id.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            query.Add("scope=" + Uri.EscapeDataString(scope));
        }

        var url = query.Count == 0 ? "admin/api/openapi.json" : "admin/api/openapi.json?" + string.Join('&', query);
        using var document = await _http.GetFromJsonAsync<System.Text.Json.JsonDocument>(url)
            ?? throw new InvalidOperationException("The service returned no OpenAPI document.");
        return System.Text.Json.JsonSerializer.Serialize(document, IndentedJson);
    }

    /// <summary>Fetches the control-plane backup/export document as a formatted JSON string.</summary>
    /// <returns>The export document text, or null when the caller is not a full admin.</returns>
    public async Task<string?> GetControlPlaneExportAsync()
    {
        var response = await _http.GetAsync("admin/api/export");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Runs an endpoint through the engine (admin "try it") and returns the raw response body.</summary>
    /// <param name="id">The endpoint id.</param>
    /// <param name="bodyJson">The request body JSON.</param>
    /// <returns>The response body (envelope or problem+json).</returns>
    public async Task<string> InvokeEndpointAsync(Guid id, string bodyJson)
    {
        var content = new StringContent(string.IsNullOrWhiteSpace(bodyJson) ? "{}" : bodyJson, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"admin/api/endpoints/{id}/invoke", content);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Synchronizes one endpoint's parameters with its database object.</summary>
    /// <param name="id">The endpoint id.</param>
    /// <returns>The sync result, or null on failure.</returns>
    public async Task<EndpointSyncResult?> SyncEndpointAsync(Guid id)
    {
        var response = await _http.PostAsync($"admin/api/endpoints/{id}/sync", content: null);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<EndpointSyncResult>()
            : null;
    }

    /// <summary>
    /// Synchronizes endpoints with the database, optionally narrowed by connection (a specific
    /// database on a specific server), schema and/or object (procedure). With no filter it syncs every
    /// endpoint; with all three it targets a single procedure by name.
    /// </summary>
    /// <param name="connection">Optional connection name to limit the sync to.</param>
    /// <param name="schema">Optional schema to limit the sync to.</param>
    /// <param name="objectName">Optional object (procedure / function) name to limit the sync to.</param>
    /// <returns>The per-endpoint sync results, or null on failure.</returns>
    public async Task<List<EndpointSyncResult>?> SyncAllEndpointsAsync(string? connection = null, string? schema = null, string? objectName = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(connection))
        {
            query.Add($"connection={Uri.EscapeDataString(connection)}");
        }

        if (!string.IsNullOrEmpty(schema))
        {
            query.Add($"schema={Uri.EscapeDataString(schema)}");
        }

        if (!string.IsNullOrEmpty(objectName))
        {
            query.Add($"object={Uri.EscapeDataString(objectName)}");
        }

        var url = query.Count == 0 ? "admin/api/endpoints/sync" : "admin/api/endpoints/sync?" + string.Join('&', query);
        var response = await _http.PostAsync(url, content: null);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<List<EndpointSyncResult>>()
            : null;
    }

    // ----- Cache -----------------------------------------------------------------------------

    /// <summary>Force-purges one endpoint's cached responses by id.</summary>
    /// <param name="id">The endpoint id.</param>
    /// <returns>The purge result, or null on failure.</returns>
    public async Task<CachePurgeResult?> PurgeEndpointCacheAsync(Guid id)
    {
        var response = await _http.PostAsync($"admin/api/endpoints/{id}/cache/purge", content: null);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CachePurgeResult>()
            : null;
    }

    /// <summary>
    /// Force-purges cached responses by filter, matching the CI/CD invalidation endpoint. Selects
    /// endpoints by route, connection (a database on a server), schema, object (procedure) and/or
    /// provider (connector). With no filter every endpoint's cache is purged.
    /// </summary>
    /// <param name="route">Optional route to limit the purge to.</param>
    /// <param name="connection">Optional connection name to limit the purge to.</param>
    /// <param name="schema">Optional schema to limit the purge to.</param>
    /// <param name="objectName">Optional object (procedure / function) name to limit the purge to.</param>
    /// <param name="provider">Optional provider (connector) key to limit the purge to.</param>
    /// <returns>The purge result, or null on failure.</returns>
    public async Task<CachePurgeResult?> PurgeCacheAsync(string? route = null, string? connection = null, string? schema = null, string? objectName = null, string? provider = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(route))
        {
            query.Add("route=" + Uri.EscapeDataString(route));
        }

        if (!string.IsNullOrEmpty(connection))
        {
            query.Add("connection=" + Uri.EscapeDataString(connection));
        }

        if (!string.IsNullOrEmpty(schema))
        {
            query.Add("schema=" + Uri.EscapeDataString(schema));
        }

        if (!string.IsNullOrEmpty(objectName))
        {
            query.Add("object=" + Uri.EscapeDataString(objectName));
        }

        if (!string.IsNullOrEmpty(provider))
        {
            query.Add("provider=" + Uri.EscapeDataString(provider));
        }

        var url = query.Count == 0 ? "admin/api/cache/purge" : "admin/api/cache/purge?" + string.Join('&', query);
        var response = await _http.PostAsync(url, content: null);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CachePurgeResult>()
            : null;
    }

    // ----- API keys --------------------------------------------------------------------------

    /// <summary>Lists API keys.</summary>
    /// <returns>The key list.</returns>
    public Task<List<ApiKeyInfo>?> GetKeysAsync() =>
        _http.GetFromJsonAsync<List<ApiKeyInfo>>("admin/api/keys");

    /// <summary>Creates an API key; the plaintext is returned once.</summary>
    /// <param name="request">The key creation request.</param>
    /// <returns>The created key with its plaintext.</returns>
    public async Task<ApiKeyCreated?> CreateKeyAsync(ApiKeyCreate request)
    {
        var response = await EnsureSuccessAsync(await _http.PostAsJsonAsync("admin/api/keys", request));
        return await response.Content.ReadFromJsonAsync<ApiKeyCreated>();
    }

    /// <summary>Revokes an API key by id.</summary>
    /// <param name="id">The key id.</param>
    /// <returns>A task that completes when revoked; throws if the server rejects the revoke.</returns>
    public async Task RevokeKeyAsync(Guid id) =>
        await EnsureSuccessAsync(await _http.DeleteAsync($"admin/api/keys/{id}"));

    // ----- Scopes ----------------------------------------------------------------------------

    /// <summary>Lists scopes.</summary>
    /// <returns>The scope list.</returns>
    public Task<List<Scope>?> GetScopesAsync() =>
        _http.GetFromJsonAsync<List<Scope>>("admin/api/scopes");

    /// <summary>Creates or updates a scope.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>A task that completes when saved; throws if the server rejects the save.</returns>
    public async Task SaveScopeAsync(Scope scope) =>
        await EnsureSuccessAsync(await _http.PostAsJsonAsync("admin/api/scopes", scope));

    /// <summary>Deletes a scope by name.</summary>
    /// <param name="name">The scope name.</param>
    /// <returns>A task that completes when deleted; throws if the server rejects the delete.</returns>
    public async Task DeleteScopeAsync(string name) =>
        await EnsureSuccessAsync(await _http.DeleteAsync($"admin/api/scopes/{Uri.EscapeDataString(name)}"));

    // ----- Admins ----------------------------------------------------------------------------

    /// <summary>Lists admin accounts.</summary>
    /// <returns>The admin list.</returns>
    public Task<List<AdminUserInfo>?> GetAdminsAsync() =>
        _http.GetFromJsonAsync<List<AdminUserInfo>>("admin/api/admins");

    /// <summary>Creates an admin account.</summary>
    /// <param name="request">The new admin details.</param>
    /// <returns>A task that completes when created; throws if the server rejects the request.</returns>
    public async Task CreateAdminAsync(CreateAdminRequest request) =>
        await EnsureSuccessAsync(await _http.PostAsJsonAsync("admin/api/admins", request));

    /// <summary>Resets another admin account's password (requires the Admin role).</summary>
    /// <param name="id">The admin id.</param>
    /// <param name="password">The new password.</param>
    /// <returns>True on success.</returns>
    public async Task<bool> ResetAdminPasswordAsync(Guid id, string password)
    {
        var response = await _http.PostAsJsonAsync($"admin/api/admins/{id}/password", new ChangePasswordRequest { Password = password });
        return response.IsSuccessStatusCode;
    }

    /// <summary>Changes an admin's role (requires the Admin role).</summary>
    /// <param name="id">The admin id.</param>
    /// <param name="role">The role to assign.</param>
    /// <returns>Null on success, otherwise the server's reason for refusing.</returns>
    public Task<string?> SetAdminRoleAsync(Guid id, string role) =>
        SendAdminChangeAsync($"admin/api/admins/{id}/role", new AdminRoleRequest { Role = role });

    /// <summary>Enables or disables an admin account (requires the Admin role).</summary>
    /// <param name="id">The admin id.</param>
    /// <param name="enabled">Whether the account may sign in.</param>
    /// <returns>Null on success, otherwise the server's reason for refusing.</returns>
    public Task<string?> SetAdminEnabledAsync(Guid id, bool enabled) =>
        SendAdminChangeAsync($"admin/api/admins/{id}/enabled", new AdminEnabledRequest { Enabled = enabled });

    /// <summary>
    /// Puts a change to one admin and reports why it was refused, if it was. The server declines a change
    /// that would leave nobody able to administer the control plane, and the reason it gives is the useful
    /// half of that - a bare failure would leave the operator guessing.
    /// </summary>
    /// <param name="url">The route to put to.</param>
    /// <param name="request">The request body.</param>
    /// <returns>Null on success, otherwise the problem detail or a status-derived fallback.</returns>
    private async Task<string?> SendAdminChangeAsync(string url, object request)
    {
        var response = await _http.PutAsJsonAsync(url, request);
        return response.IsSuccessStatusCode ? null : await ReadErrorAsync(response);
    }

    /// <summary>
    /// Throws a <see cref="WeirApiException"/> carrying the server's explanation when a mutation
    /// response is not a success. Used by the create/save/delete methods so a failed mutation surfaces
    /// a message a page can show, rather than a bare <c>EnsureSuccessStatusCode</c> throw or a silent
    /// null the page mistakes for success.
    /// </summary>
    /// <param name="response">The mutation response.</param>
    /// <returns>The response, when it was a success, so callers can read a returned body.</returns>
    private static async Task<HttpResponseMessage> EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new WeirApiException(await ReadErrorAsync(response));
        }

        return response;
    }

    /// <summary>
    /// Reads a failed response's RFC 7807 <c>detail</c> (then <c>title</c>), falling back to the status
    /// line when the body is not problem+json. The one place that turns a failure into a human message.
    /// </summary>
    /// <param name="response">The failed response.</param>
    /// <returns>The message to show.</returns>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetail>();
            if (!string.IsNullOrWhiteSpace(problem?.Detail))
            {
                return problem.Detail;
            }

            if (!string.IsNullOrWhiteSpace(problem?.Title))
            {
                return problem.Title;
            }
        }
        catch (JsonException)
        {
            // Not a problem+json body; fall back to the status line below.
        }
        catch (NotSupportedException)
        {
            // Unexpected content type; same fallback.
        }

        return $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }

    // ----- Audit -----------------------------------------------------------------------------

    /// <summary>Queries the audit log, newest first, optionally seeking past a keyset cursor.</summary>
    /// <param name="limit">Maximum rows.</param>
    /// <param name="afterId">Keyset cursor: return only entries older than this id (the last id of the
    /// previous page). Null fetches the newest page.</param>
    /// <returns>The audit entries.</returns>
    public Task<List<AuditEntry>?> GetAuditAsync(int limit = 200, long? afterId = null) =>
        _http.GetFromJsonAsync<List<AuditEntry>>(
            afterId is { } cursor
                ? $"admin/api/audit?limit={limit}&afterId={cursor}"
                : $"admin/api/audit?limit={limit}");

    // ----- Request log -----------------------------------------------------------------------

    /// <summary>Queries the data-plane request log, newest first, with optional filters and a keyset cursor.</summary>
    /// <param name="endpointId">Restrict to one endpoint by id.</param>
    /// <param name="route">Restrict to one route (case-insensitive), used by the dashboard drill-in.</param>
    /// <param name="slowOnly">Return only calls flagged slow.</param>
    /// <param name="errorsOnly">Return only failed calls.</param>
    /// <param name="limit">Maximum rows.</param>
    /// <param name="afterId">Keyset cursor (the last id of the previous page). Null fetches the newest page.</param>
    /// <returns>The request-log entries.</returns>
    public Task<List<RequestLogEntry>?> GetRequestLogAsync(Guid? endpointId = null, string? route = null, bool slowOnly = false, bool errorsOnly = false, int limit = 100, long? afterId = null)
    {
        var query = new List<string> { "limit=" + limit };
        if (endpointId is { } id)
        {
            query.Add("endpoint=" + id);
        }

        if (!string.IsNullOrEmpty(route))
        {
            query.Add("route=" + Uri.EscapeDataString(route));
        }

        if (slowOnly)
        {
            query.Add("slowOnly=true");
        }

        if (errorsOnly)
        {
            query.Add("errorsOnly=true");
        }

        if (afterId is { } cursor)
        {
            query.Add("afterId=" + cursor);
        }

        return _http.GetFromJsonAsync<List<RequestLogEntry>>("admin/api/logs?" + string.Join('&', query));
    }

    // ----- Settings --------------------------------------------------------------------------

    /// <summary>Gets the runtime system settings plus read-only, restart-required values.</summary>
    /// <returns>The settings view, or null if the request failed.</returns>
    public Task<WeirSystemSettingsView?> GetSettingsAsync() =>
        _http.GetFromJsonAsync<WeirSystemSettingsView>("admin/api/settings");

    /// <summary>Updates the runtime system settings (requires the Admin role).</summary>
    /// <param name="settings">The new settings.</param>
    /// <returns>The applied settings, or null if the update was rejected.</returns>
    public async Task<WeirSystemSettings?> UpdateSettingsAsync(WeirSystemSettings settings)
    {
        var response = await _http.PutAsJsonAsync("admin/api/settings", settings);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<WeirSystemSettings>()
            : null;
    }
}
