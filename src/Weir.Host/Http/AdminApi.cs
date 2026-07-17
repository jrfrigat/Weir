using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Weir.Host.Options;
using Weir.Host.Security;
using ConnectionInfo = Weir.Contracts.ConnectionInfo;

namespace Weir.Host.Http;

/// <summary>Maps the admin API consumed by the PWA: auth, endpoint/key/scope/admin management, audit and metrics.</summary>
public static class AdminApi
{
    /// <summary>
    /// A dummy password hash verified when a sign-in names a missing or disabled account, so the
    /// response time does not reveal whether a username exists (equal-cost login paths).
    /// </summary>
    private static readonly string DecoyPasswordHash = PasswordHasher.Hash("weir-decoy-not-a-real-password");

    /// <summary>JSON options for the control-plane export document (web casing, indented for review).</summary>
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Maps all admin endpoints under <c>/admin/api</c>. All routes require an admin token except login.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapWeirAdminApi(this WebApplication app)
    {
        var admin = app.MapGroup("/admin/api").RequireAuthorization();

        MapAuth(admin);
        MapEndpoints(admin);
        MapKeys(admin);
        MapScopes(admin);
        MapAdmins(admin);
        MapAudit(admin);
        MapRequestLog(admin);
        MapMetrics(admin);
        MapSettings(admin);
        MapExport(admin);
        MapIntrospection(admin);
        MapSync(admin);
        MapCache(admin);
        return app;
    }

    /// <summary>Maps the control-plane backup/export route (AdminOnly, audited).</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapExport(RouteGroupBuilder group)
    {
        // A portable, secret-free snapshot of the control-plane configuration for backup or migration
        // between environments. Excludes API-key and password hashes by design; full disaster recovery
        // uses a database-level backup of the control store.
        group.MapGet("/export", async (
            IControlPlaneStore store, IRuntimeSettings settings, ClaimsPrincipal user, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var now = clock.GetUtcNow();
            var export = new ControlPlaneExport
            {
                ExportedAt = now,
                Endpoints = await store.GetEndpointsAsync(cancellationToken),
                Scopes = await store.GetScopesAsync(cancellationToken),
                ApiKeys = await store.GetApiKeysAsync(cancellationToken),
                Admins = await store.GetAdminsAsync(cancellationToken),
                Settings = settings.Current,
            };

            await AuditActionAsync(store, clock, user, "control-plane.export", $"{export.Endpoints.Count} endpoints");

            // Return as a downloadable attachment (indented for human review), not an inline body.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(export, ExportJson);
            var fileName = $"weir-control-plane-{now:yyyyMMdd-HHmmss}.json";
            return Results.Bytes(bytes, "application/json", fileName);
        }).RequireAuthorization("AdminOnly");
    }

    /// <summary>Maps the runtime-settings routes: read for any admin, update for full admins (audited).</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/settings", (IRuntimeSettings settings, IOptions<WeirDataPlaneOptions> options, IOptions<WeirLoggingOptions> logging) =>
        {
            var log = logging.Value;
            return Results.Ok(new WeirSystemSettingsView
            {
                Settings = settings.Current,
                MaxRequestBodyBytes = options.Value.MaxRequestBodyBytes,
                Logging = new WeirLoggingInfo
                {
                    FileEnabled = log.FileEnabled,
                    Directory = log.Directory,
                    RollingInterval = log.RollingInterval,
                    RetainedFileCountLimit = log.RetainedFileCountLimit,
                    RetainedFileTimeLimitDays = log.RetainedFileTimeLimitDays,
                    Format = log.Format,
                    MinimumLevel = log.MinimumLevel,
                },
            });
        });

        group.MapPut("/settings", async (
            WeirSystemSettings update, IRuntimeSettings settings, IControlPlaneStore store,
            ClaimsPrincipal user, TimeProvider clock, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            if (update.MaxRows < 0 || update.RequestTimeoutSeconds < 0 || update.MaxTvpRows < 0 ||
                update.DefaultApiKeyRateLimitPerMinute < 0 || update.AuditRetentionDays < 0 ||
                update.MaxConcurrentRequestsPerConnection < 0 || update.CircuitBreakerFailureThreshold < 0 ||
                update.CircuitBreakerResetSeconds < 0 || update.ApiKeyFailureThreshold < 0 ||
                update.ResponseCacheMaxBytes < 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid settings",
                    detail: "Settings values must not be negative.");
            }

            var before = settings.Current;
            await settings.UpdateAsync(update, cancellationToken);
            var securityLog = loggerFactory.CreateLogger("Weir.Security");
            Log.SettingsChanged(securityLog, user.Identity?.Name);
            await store.AppendAuditAsync(new AuditEntry
            {
                Timestamp = clock.GetUtcNow(),
                Category = "settings.update",
                Actor = user.Identity?.Name,
                Detail = SettingsChangeSummary(before, settings.Current),
                Outcome = OutcomeCodes.Ok,
            }, cancellationToken);

            return Results.Ok(settings.Current);
        }).RequireAuthorization("AdminOnly");
    }

    /// <summary>
    /// Whether a pending change to one admin would leave the control plane with no enabled Admin. Both
    /// routes that can cause it are AdminOnly, so nobody would be left who could undo it: the only way
    /// back is editing the database by hand, since the bootstrap account is only created when the table
    /// is empty and a disabled admin still fills it.
    /// </summary>
    /// <param name="store">The control-plane store.</param>
    /// <param name="id">The admin being changed.</param>
    /// <param name="newRole">The role being assigned, or null when the role is unchanged.</param>
    /// <param name="newEnabled">The enabled state being assigned, or null when it is unchanged.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when no enabled Admin would remain.</returns>
    private static async Task<bool> WouldStrandControlPlaneAsync(
        IControlPlaneStore store,
        Guid id,
        string? newRole,
        bool? newEnabled,
        CancellationToken cancellationToken = default)
    {
        var admins = await store.GetAdminsAsync(cancellationToken);
        return !admins.Any(admin =>
        {
            // Apply the pending change to the one row it touches, then ask the question of the result.
            var role = admin.Id == id && newRole is not null ? newRole : admin.Role;
            var enabled = admin.Id == id && newEnabled is not null ? newEnabled.Value : admin.Enabled;
            return enabled && string.Equals(role, AdminRoles.Admin, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>The refusal returned when a change would strand the control plane.</summary>
    /// <returns>A 409 problem response.</returns>
    private static IResult LastAdminProblem() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Last admin",
        detail: "This would leave no enabled admin, and the routes that could undo it are admin-only. "
              + "Grant the Admin role to another enabled account first.");

    /// <summary>Maps authentication routes.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapAuth(RouteGroupBuilder group)
    {
        group.MapPost("/auth/login", async (LoginRequest request, HttpContext http, IControlPlaneStore store, JwtTokenService jwt, IOptions<JwtOptions> jwtOptions, ILoginThrottle throttle, TimeProvider clock, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Weir.Security");
            // Throttle by source IP, not username, so bad passwords cannot lock a real admin out.
            var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (await throttle.IsLockedAsync(client, cancellationToken))
            {
                Log.LoginLockedOut(logger, client);
                await store.AppendAuditAsync(new AuditEntry
                {
                    Category = "admin.login",
                    Actor = request.Username,
                    Outcome = "locked",
                    Timestamp = clock.GetUtcNow(),
                }, cancellationToken);
                return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many failed sign-ins",
                    detail: "Too many failed attempts from this client. Try again later.");
            }

            var admin = await store.FindAdminByUsernameAsync(request.Username, cancellationToken);
            bool passwordOk;
            if (admin is not null && admin.Enabled)
            {
                passwordOk = PasswordHasher.Verify(request.Password, admin.PasswordHash);
            }
            else
            {
                // Verify against a decoy so a missing/disabled account costs the same as a real one.
                _ = PasswordHasher.Verify(request.Password, DecoyPasswordHash);
                passwordOk = false;
            }

            if (admin is null || !passwordOk)
            {
                await throttle.RecordFailureAsync(client, cancellationToken);
                Log.LoginFailed(logger, request.Username, client);
                await store.AppendAuditAsync(new AuditEntry
                {
                    Category = "admin.login",
                    Actor = request.Username,
                    Outcome = "fail",
                    Timestamp = clock.GetUtcNow(),
                }, cancellationToken);
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials");
            }

            await throttle.ResetAsync(client, cancellationToken);
            await store.TouchAdminLoginAsync(admin.Id, clock.GetUtcNow(), cancellationToken);
            await store.AppendAuditAsync(new AuditEntry
            {
                Category = "admin.login",
                Actor = admin.Username,
                Outcome = OutcomeCodes.Ok,
                Timestamp = clock.GetUtcNow(),
            }, cancellationToken);

            return Results.Ok(await IssueSessionAsync(store, jwt, jwtOptions.Value, admin, clock.GetUtcNow(), cancellationToken));
        }).AllowAnonymous();

        // Exchange a valid refresh token for a fresh access token, rotating the refresh token (the used
        // one is revoked). Rejects a revoked, expired or unknown token, or one whose admin is now disabled.
        group.MapPost("/auth/refresh", async (RefreshRequest request, IControlPlaneStore store, JwtTokenService jwt, IOptions<JwtOptions> jwtOptions, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var now = clock.GetUtcNow();
            var record = await store.FindRefreshTokenAsync(ApiKeyHasher.Hash(request.RefreshToken), cancellationToken);
            if (record is null || record.Revoked || record.ExpiresAt <= now)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid refresh token");
            }

            var admin = await store.FindAdminByIdAsync(record.AdminId, cancellationToken);
            if (admin is null || !admin.Enabled)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid refresh token");
            }

            await store.RevokeRefreshTokenAsync(record.Id, now, cancellationToken);
            return Results.Ok(await IssueSessionAsync(store, jwt, jwtOptions.Value, admin, now, cancellationToken));
        }).AllowAnonymous();

        // Revoke a refresh token on sign-out. Knowing the token is proof enough to revoke it, so this does
        // not require the (possibly expired) access token; an unknown token is treated as already signed out.
        group.MapPost("/auth/logout", async (LogoutRequest request, IControlPlaneStore store, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrEmpty(request.RefreshToken))
            {
                var record = await store.FindRefreshTokenAsync(ApiKeyHasher.Hash(request.RefreshToken), cancellationToken);
                if (record is not null)
                {
                    await store.RevokeRefreshTokenAsync(record.Id, clock.GetUtcNow(), cancellationToken);
                }
            }

            return Results.Ok();
        }).AllowAnonymous();

        group.MapGet("/auth/me", (ClaimsPrincipal user) =>
            Results.Ok(new CurrentAdmin
            {
                Username = user.Identity?.Name ?? string.Empty,
                Role = user.FindFirst("role")?.Value ?? AdminRoles.Viewer,
            }));

        // Self-service password change for the signed-in admin (any role); verifies the current password.
        group.MapPost("/account/password", async (ChangeOwnPasswordRequest request, IControlPlaneStore store, ClaimsPrincipal user, TimeProvider clock) =>
        {
            var username = user.Identity?.Name;
            if (string.IsNullOrEmpty(username))
            {
                return Results.Unauthorized();
            }

            var admin = await store.FindAdminByUsernameAsync(username);
            if (admin is null || !PasswordHasher.Verify(request.CurrentPassword, admin.PasswordHash))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The current password is incorrect.");
            }

            if (PasswordPolicy.Validate(request.NewPassword) is { } reason)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Weak password", detail: reason);
            }

            await store.UpdateAdminPasswordAsync(admin.Id, PasswordHasher.Hash(request.NewPassword));
            // A password change already bumps the token version (revoking access tokens); also revoke the
            // account's refresh tokens and personal access tokens so a leaked one cannot mint new access tokens.
            await store.RevokeRefreshTokensForAdminAsync(admin.Id, clock.GetUtcNow());
            await store.RevokeAdminTokensForAdminAsync(admin.Id);
            await store.AppendAuditAsync(new AuditEntry
            {
                Category = "admin.password.self",
                Actor = username,
                Outcome = OutcomeCodes.Ok,
                Timestamp = clock.GetUtcNow(),
            });
            return Results.NoContent();
        });

        // Personal access tokens: any signed-in admin manages their own long-lived tokens for scripted /
        // CI-CD access to the admin API. A token authenticates as its owner (with that admin's role).
        group.MapGet("/account/tokens", async (IControlPlaneStore store, ClaimsPrincipal user) =>
            TryGetAdminId(user, out var adminId)
                ? Results.Ok(await store.GetAdminTokensAsync(adminId))
                : Results.Unauthorized());

        group.MapPost("/account/tokens", async (AdminTokenCreate request, IControlPlaneStore store, ClaimsPrincipal user, TimeProvider clock, IOptions<AdminSecurityOptions> securityOptions) =>
        {
            if (!TryGetAdminId(user, out var adminId))
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The token name must not be empty.");
            }

            if (request.ExpiresAt is { } expiry && expiry <= clock.GetUtcNow())
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The expiry must be in the future.");
            }

            var policy = securityOptions.Value;
            if (policy.RequireTokenExpiry && request.ExpiresAt is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "An expiry is required",
                    detail: "This deployment requires personal access tokens to have an expiry.");
            }

            if (policy.MaxTokensPerAdmin > 0)
            {
                var existing = await store.GetAdminTokensAsync(adminId);
                if (existing.Count >= policy.MaxTokensPerAdmin)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Token limit reached",
                        detail: $"You may hold at most {policy.MaxTokensPerAdmin} tokens. Revoke one before creating another.");
                }
            }

            var (plainText, prefix, hash) = AdminTokenGenerator.Generate();
            var info = await store.CreateAdminTokenAsync(adminId, request.Name.Trim(), request.ExpiresAt, hash, prefix);
            await store.AppendAuditAsync(new AuditEntry
            {
                Category = "account.token.created",
                Actor = user.Identity?.Name,
                Detail = info.Prefix,
                Outcome = OutcomeCodes.Ok,
                Timestamp = clock.GetUtcNow(),
            });
            return Results.Ok(new AdminTokenCreated { Info = info, PlainTextToken = plainText });
        });

        group.MapDelete("/account/tokens/{id:guid}", async (Guid id, IControlPlaneStore store, ClaimsPrincipal user, TimeProvider clock) =>
        {
            if (!TryGetAdminId(user, out var adminId))
            {
                return Results.Unauthorized();
            }

            await store.RevokeAdminTokenAsync(id, adminId);
            await store.AppendAuditAsync(new AuditEntry
            {
                Category = "account.token.revoked",
                Actor = user.Identity?.Name,
                Detail = id.ToString(),
                Outcome = OutcomeCodes.Ok,
                Timestamp = clock.GetUtcNow(),
            });
            return Results.NoContent();
        });
    }

    /// <summary>Extracts the signed-in admin's id from the <c>sub</c> claim.</summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="adminId">Receives the admin id when present and well-formed.</param>
    /// <returns>True if the id was resolved.</returns>
    private static bool TryGetAdminId(ClaimsPrincipal user, out Guid adminId)
    {
        adminId = default;
        var sub = user.FindFirst("sub")?.Value;
        return sub is not null && Guid.TryParse(sub, out adminId);
    }

    /// <summary>
    /// Issues a session for an admin: a short-lived access token plus a fresh, stored (hashed) refresh
    /// token. Shared by sign-in and refresh so both produce the same response shape.
    /// </summary>
    /// <param name="store">The control-plane store.</param>
    /// <param name="jwt">The JWT token service.</param>
    /// <param name="jwtOptions">The JWT options (for the refresh-token lifetime).</param>
    /// <param name="admin">The authenticated admin.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The login response carrying both tokens.</returns>
    private static async Task<LoginResponse> IssueSessionAsync(
        IControlPlaneStore store, JwtTokenService jwt, JwtOptions jwtOptions, AdminUserRecord admin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (token, expires) = jwt.Issue(admin);
        var (refreshPlainText, refreshHash) = RefreshTokenGenerator.Generate();
        var refreshExpires = now.AddDays(Math.Max(1, jwtOptions.RefreshTokenDays));
        await store.CreateRefreshTokenAsync(Guid.NewGuid(), admin.Id, refreshHash, refreshExpires, now, cancellationToken);

        return new LoginResponse
        {
            Token = token,
            ExpiresAt = expires,
            Username = admin.Username,
            RefreshToken = refreshPlainText,
            RefreshTokenExpiresAt = refreshExpires,
        };
    }

    /// <summary>Appends an admin-action audit entry recording the actor and the affected resource.</summary>
    /// <param name="store">The control-plane store.</param>
    /// <param name="clock">Clock for the timestamp.</param>
    /// <param name="user">The acting admin.</param>
    /// <param name="category">The action category (for example <c>endpoint.update</c>).</param>
    /// <param name="detail">A non-secret description of the affected resource (route, scope, username).</param>
    /// <returns>A task that completes when the entry is written.</returns>
    private static Task AuditActionAsync(
        IControlPlaneStore store, TimeProvider clock, ClaimsPrincipal user, string category, string? detail) =>
        store.AppendAuditAsync(new AuditEntry
        {
            Timestamp = clock.GetUtcNow(),
            Category = category,
            Actor = user.Identity?.Name,
            Outcome = OutcomeCodes.Ok,
            Detail = detail,
        });

    /// <summary>
    /// Builds a human-readable, secret-free diff of the changed settings fields (for example
    /// <c>MaxRows 100000-&gt;42, AuditRetentionDays 0-&gt;30</c>), so a <c>settings.update</c> audit entry
    /// records exactly what changed. Reflects over the settings record's public properties, so new
    /// settings are covered automatically.
    /// </summary>
    /// <param name="before">The settings before the change.</param>
    /// <param name="after">The settings after the change.</param>
    /// <returns>A comma-separated list of changed fields, or "no changes".</returns>
    private static string SettingsChangeSummary(WeirSystemSettings before, WeirSystemSettings after)
    {
        var changes = new List<string>();
        foreach (var property in typeof(WeirSystemSettings).GetProperties())
        {
            var oldValue = property.GetValue(before);
            var newValue = property.GetValue(after);
            if (!Equals(oldValue, newValue))
            {
                changes.Add($"{property.Name} {oldValue}->{newValue}");
            }
        }

        return changes.Count == 0 ? "no changes" : string.Join(", ", changes);
    }

    /// <summary>Summarizes an endpoint for an audit detail: method, route and target object.</summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <returns>A compact description, e.g. <c>POST /api/orders -&gt; dbo.usp_CreateOrder</c>.</returns>
    private static string DescribeEndpoint(EndpointDefinition endpoint) =>
        $"{endpoint.HttpMethod} /api/{endpoint.Route} -> {endpoint.Schema}.{endpoint.ObjectName}";

    /// <summary>Summarizes a sync result for an audit detail (added / updated / removed parameters).</summary>
    /// <param name="result">The sync result.</param>
    /// <returns>A compact change description.</returns>
    private static string DescribeSync(EndpointSyncResult result)
    {
        if (!string.IsNullOrEmpty(result.Message))
        {
            return $"{result.Route}: {result.Message}";
        }

        var parts = new List<string>();
        if (result.Added.Count > 0) parts.Add("added " + string.Join('/', result.Added));
        if (result.Updated.Count > 0) parts.Add("updated " + string.Join('/', result.Updated));
        if (result.Removed.Count > 0) parts.Add("removed " + string.Join('/', result.Removed));
        return $"{result.Route}: {(parts.Count == 0 ? "no changes" : string.Join("; ", parts))}";
    }

    /// <summary>Maps endpoint CRUD. Endpoint changes reload the in-memory catalog.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/endpoints", async (IControlPlaneStore store) =>
            Results.Ok(await store.GetEndpointsAsync()));

        group.MapGet("/endpoints/{id:guid}", async (Guid id, IControlPlaneStore store) =>
            await store.GetEndpointAsync(id) is { } endpoint ? Results.Ok(endpoint) : Results.NotFound());

        group.MapPost("/endpoints", async (EndpointDefinition endpoint, IControlPlaneStore store, IEndpointCatalog catalog, IResponseCache cache, ClaimsPrincipal user, TimeProvider clock) =>
        {
            EndpointDefinition saved;
            try
            {
                saved = await store.UpsertEndpointAsync(endpoint);
            }
            catch (ControlPlaneConflictException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Endpoint conflict", detail: ex.Message);
            }

            await catalog.LoadAsync();
            await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix(saved.Route));
            await AuditActionAsync(store, clock, user, "endpoint.upsert", DescribeEndpoint(saved));
            return Results.Ok(saved);
        }).RequireAuthorization("AdminOnly");

        group.MapPut("/endpoints/{id:guid}", async (Guid id, EndpointDefinition endpoint, IControlPlaneStore store, IEndpointCatalog catalog, IResponseCache cache, ClaimsPrincipal user, TimeProvider clock) =>
        {
            EndpointDefinition saved;
            try
            {
                saved = await store.UpsertEndpointAsync(endpoint with { Id = id });
            }
            catch (ControlPlaneConflictException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Endpoint conflict", detail: ex.Message);
            }

            await catalog.LoadAsync();
            await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix(saved.Route));
            await AuditActionAsync(store, clock, user, "endpoint.update", DescribeEndpoint(saved));
            return Results.Ok(saved);
        }).RequireAuthorization("AdminOnly");

        group.MapDelete("/endpoints/{id:guid}", async (Guid id, IControlPlaneStore store, IEndpointCatalog catalog, IResponseCache cache, ClaimsPrincipal user, TimeProvider clock) =>
        {
            // Read the route first so its cached responses can be evicted after the delete.
            var existing = await store.GetEndpointAsync(id);
            await store.DeleteEndpointAsync(id);
            await catalog.LoadAsync();
            if (existing is not null)
            {
                await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix(existing.Route));
            }

            await AuditActionAsync(store, clock, user, "endpoint.delete", existing is null ? id.ToString() : DescribeEndpoint(existing));
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        // Import a set of endpoint definitions (upsert by id), for promoting between environments.
        group.MapPost("/endpoints/import", async (List<EndpointDefinition> endpoints, IControlPlaneStore store, IEndpointCatalog catalog, IResponseCache cache, ClaimsPrincipal user, TimeProvider clock) =>
        {
            if (endpoints.Count > 1000)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Import too large",
                    detail: "A single import may contain at most 1000 endpoints.");
            }

            var imported = 0;
            foreach (var endpoint in endpoints)
            {
                var saved = await store.UpsertEndpointAsync(endpoint);
                await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix(saved.Route));
                imported++;
            }

            await catalog.LoadAsync();
            await AuditActionAsync(store, clock, user, "endpoint.import", $"{imported} endpoint(s)");
            return Results.Ok(new { imported });
        }).RequireAuthorization("AdminOnly");

        // Admin "try it": run an endpoint through the engine (admin-authorized, no API key) and
        // return the response envelope.
        group.MapPost("/endpoints/{id:guid}/invoke", async (Guid id, JsonElement payload, IControlPlaneStore store, WeirEngine engine, ClaimsPrincipal user, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var endpoint = await store.GetEndpointAsync(id, cancellationToken);
            if (endpoint is null)
            {
                return Results.NotFound();
            }

            await AuditActionAsync(store, clock, user, "endpoint.invoke", DescribeEndpoint(endpoint));

            // The admin console posts a flat { parameterName: value } object. Route each value to its
            // parameter's source so query / header / claim parameters are exercised too, not just body.
            var bodyProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var otherSources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var parameter in endpoint.Parameters)
                {
                    if (!payload.TryGetProperty(parameter.Name, out var value) || value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    if (parameter.Source == ParameterSource.Body)
                    {
                        bodyProperties[parameter.Name] = value;
                    }
                    else
                    {
                        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                        otherSources[parameter.Name] = text;
                        if (!string.IsNullOrEmpty(parameter.DbParameterName)) otherSources[parameter.DbParameterName] = text;
                        if (!string.IsNullOrEmpty(parameter.HeaderName)) otherSources[parameter.HeaderName] = text;
                        if (!string.IsNullOrEmpty(parameter.ClaimType)) otherSources[parameter.ClaimType] = text;
                    }
                }
            }

            using var bodyDocument = JsonSerializer.SerializeToDocument(bodyProperties);
            var invocation = new WeirInvocation
            {
                Endpoint = endpoint,
                Body = bodyDocument.RootElement,
                HasBody = bodyProperties.Count > 0,
                Query = new DictionaryValueSource(otherSources),
                Header = new DictionaryValueSource(otherSources),
                Claim = new DictionaryValueSource(otherSources),
                ApiKeyPrefix = "admin-test",
            };

            using var buffer = new MemoryStream();
            try
            {
                await engine.ExecuteAsync(invocation, buffer, cancellationToken);
            }
            catch (WeirValidationException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid parameters", detail: ex.Message);
            }
            catch (Exception ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Execution error", detail: ex.Message);
            }

            return Results.Bytes(buffer.ToArray(), "application/json");
        }).RequireAuthorization("AdminOnly");

        // Generate an OpenAPI 3.0 document from the current endpoint metadata for client generation.
        // Optionally narrow the document to the endpoints a single API key may call (key=), or to the
        // endpoints that require a given scope (scope=), so a consumer can be handed exactly its surface.
        group.MapGet("/openapi.json", async (HttpContext context, IEndpointCatalog catalog, IControlPlaneStore store, Guid? key, string? scope) =>
        {
            var serverUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            IEnumerable<EndpointDefinition> endpoints = catalog.All;
            string? audience = null;

            if (key is { } keyId)
            {
                var info = (await store.GetApiKeysAsync()).FirstOrDefault(k => k.Id == keyId);
                if (info is null)
                {
                    return Results.NotFound();
                }

                endpoints = endpoints.Where(e => EndpointAccess.IsAccessibleBy(e, info.Scopes, info.Grants));
                audience = $"key \"{info.Name}\"";
            }

            if (!string.IsNullOrWhiteSpace(scope))
            {
                endpoints = endpoints.Where(e => e.RequiredScopes.Contains(scope, StringComparer.Ordinal));
                audience ??= $"scope \"{scope}\"";
            }

            return Results.Json(OpenApiGenerator.Generate(endpoints.ToList(), serverUrl, audience));
        });
    }

    /// <summary>Maps API-key management. The plaintext key is returned once on creation.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapKeys(RouteGroupBuilder group)
    {
        group.MapGet("/keys", async (IControlPlaneStore store) =>
            Results.Ok(await store.GetApiKeysAsync()));

        group.MapPost("/keys", async (ApiKeyCreate request, IControlPlaneStore store, IApiKeyAuthenticator authenticator, ClaimsPrincipal user, TimeProvider clock) =>
        {
            var (plainText, prefix, hash) = ApiKeyGenerator.Generate();
            var info = await store.CreateApiKeyAsync(request, hash, prefix);
            authenticator.Invalidate();
            await store.AppendAuditAsync(new AuditEntry
            {
                Category = "key.created",
                Actor = user.Identity?.Name,
                Detail = info.Prefix,
                Outcome = OutcomeCodes.Ok,
                Timestamp = clock.GetUtcNow(),
            });
            return Results.Ok(new ApiKeyCreated { Info = info, PlainTextKey = plainText });
        }).RequireAuthorization("AdminOnly");

        group.MapDelete("/keys/{id:guid}", async (Guid id, IControlPlaneStore store, IApiKeyAuthenticator authenticator, ClaimsPrincipal user, TimeProvider clock) =>
        {
            await store.RevokeApiKeyAsync(id);
            // Evict the auth cache so the revoked key stops authenticating immediately, not after the TTL.
            authenticator.Invalidate();
            await store.AppendAuditAsync(new AuditEntry
            {
                Category = "key.revoked",
                Actor = user.Identity?.Name,
                Detail = id.ToString(),
                Outcome = OutcomeCodes.Ok,
                Timestamp = clock.GetUtcNow(),
            });
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");
    }

    /// <summary>Maps scope management.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapScopes(RouteGroupBuilder group)
    {
        group.MapGet("/scopes", async (IControlPlaneStore store) =>
            Results.Ok(await store.GetScopesAsync()));

        group.MapPost("/scopes", async (Scope scope, IControlPlaneStore store, ClaimsPrincipal user, TimeProvider clock) =>
        {
            await store.UpsertScopeAsync(scope);
            await AuditActionAsync(store, clock, user, "scope.upsert", scope.Name);
            return Results.Ok(scope);
        }).RequireAuthorization("AdminOnly");

        group.MapDelete("/scopes/{name}", async (string name, IControlPlaneStore store, ClaimsPrincipal user, TimeProvider clock) =>
        {
            await store.DeleteScopeAsync(name);
            await AuditActionAsync(store, clock, user, "scope.delete", name);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");
    }

    /// <summary>Maps admin-account management.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapAdmins(RouteGroupBuilder group)
    {
        group.MapGet("/admins", async (IControlPlaneStore store) =>
            Results.Ok(await store.GetAdminsAsync()));

        group.MapPost("/admins", async (CreateAdminRequest request, IControlPlaneStore store, ClaimsPrincipal user, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The username must not be empty.");
            }

            if (!AdminRoles.IsValid(request.Role))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown role",
                    detail: $"'{request.Role}' is not a valid role.");
            }

            if (PasswordPolicy.Validate(request.Password) is { } reason)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Weak password", detail: reason);
            }

            var created = await store.CreateAdminAsync(request.Username, PasswordHasher.Hash(request.Password), request.Role);
            await AuditActionAsync(store, clock, user, "admin.create", $"{request.Username} ({request.Role})");
            return Results.Ok(created);
        }).RequireAuthorization("AdminOnly");

        group.MapPost("/admins/{id:guid}/password", async (Guid id, ChangePasswordRequest request, IControlPlaneStore store, ClaimsPrincipal user, TimeProvider clock) =>
        {
            if (PasswordPolicy.Validate(request.Password) is { } reason)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Weak password", detail: reason);
            }

            await store.UpdateAdminPasswordAsync(id, PasswordHasher.Hash(request.Password));
            await store.RevokeRefreshTokensForAdminAsync(id, clock.GetUtcNow());
            await store.RevokeAdminTokensForAdminAsync(id);
            await AuditActionAsync(store, clock, user, "admin.password_reset", id.ToString());
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        group.MapPut("/admins/{id:guid}/role", async (Guid id, AdminRoleRequest request, IControlPlaneStore store, ClaimsPrincipal user, TimeProvider clock) =>
        {
            if (!AdminRoles.IsValid(request.Role))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown role",
                    detail: $"'{request.Role}' is not a valid role.");
            }

            if (await WouldStrandControlPlaneAsync(store, id, newRole: request.Role, newEnabled: null))
            {
                return LastAdminProblem();
            }

            await store.UpdateAdminRoleAsync(id, request.Role);
            await AuditActionAsync(store, clock, user, "admin.role_changed", $"{id} -> {request.Role}");
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        group.MapPut("/admins/{id:guid}/enabled", async (Guid id, AdminEnabledRequest request, IControlPlaneStore store, ClaimsPrincipal user, TimeProvider clock) =>
        {
            if (await WouldStrandControlPlaneAsync(store, id, newRole: null, newEnabled: request.Enabled))
            {
                return LastAdminProblem();
            }

            await store.UpdateAdminEnabledAsync(id, request.Enabled);
            await AuditActionAsync(store, clock, user, request.Enabled ? "admin.enabled" : "admin.disabled", id.ToString());
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");
    }

    /// <summary>Maps the audit-log query.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapAudit(RouteGroupBuilder group)
    {
        group.MapGet("/audit", async (
            IControlPlaneStore store,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? category,
            string? actor,
            string? route,
            int? limit,
            int? offset,
            long? afterId) =>
        {
            var query = new AuditQuery
            {
                From = from,
                To = to,
                Category = category,
                Actor = actor,
                Route = route,
                Limit = limit ?? 200,
                Offset = offset ?? 0,
                // Keyset cursor: the last id of the previous page. Preferred over offset for deep paging.
                AfterId = afterId,
            };
            return Results.Ok(await store.QueryAuditAsync(query));
        });
    }

    /// <summary>Maps the data-plane request-log query used by the admin Logs screen and per-endpoint drill-in.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapRequestLog(RouteGroupBuilder group)
    {
        group.MapGet("/logs", async (
            IControlPlaneStore store,
            Guid? endpoint,
            string? route,
            bool? slowOnly,
            bool? errorsOnly,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            long? afterId) =>
        {
            var query = new RequestLogQuery
            {
                EndpointId = endpoint,
                Route = route,
                SlowOnly = slowOnly ?? false,
                ErrorsOnly = errorsOnly ?? false,
                From = from,
                To = to,
                Limit = limit ?? 100,
                AfterId = afterId,
            };
            return Results.Ok(await store.QueryRequestLogAsync(query));
        });
    }

    /// <summary>Maps the metrics and connection endpoints that power the dashboard.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapMetrics(RouteGroupBuilder group)
    {
        group.MapGet("/metrics/overview", (IMetricsAggregator metrics) =>
            Results.Ok(metrics.GetOverview()));

        group.MapGet("/metrics/endpoints", (IMetricsAggregator metrics) =>
            Results.Ok(metrics.GetEndpoints()));

        group.MapGet("/metrics/timeseries", (IMetricsAggregator metrics, string metric, string? route, int? windowSeconds, int? bucketSeconds) =>
            Results.Ok(metrics.GetTimeSeries(
                metric,
                route,
                TimeSpan.FromSeconds(windowSeconds ?? 300),
                TimeSpan.FromSeconds(bucketSeconds ?? 10))));

        group.MapGet("/connections", (IDataConnectionRegistry registry) =>
            Results.Ok(registry.All.Select(descriptor => new ConnectionInfo
            {
                Name = descriptor.Name,
                Provider = descriptor.Provider,
            }).ToList()));

        group.MapGet("/connections/health", async (ClaimsPrincipal user, IDataConnectionRegistry registry, IEnumerable<IDbConnector> connectors, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            // Driver exception text can disclose server / database / login names. Full admins (who
            // configure connections) see it; read-only viewers get a generic status so the dashboard
            // still shows a connection as up or down without leaking infrastructure detail.
            var showDetail = user.IsInRole(AdminRoles.Admin);
            var byProvider = connectors.ToDictionary(connector => connector.ProviderName, StringComparer.OrdinalIgnoreCase);
            var results = new List<ConnectionHealth>();
            foreach (var descriptor in registry.All)
            {
                var start = Stopwatch.GetTimestamp();
                var healthy = false;
                string? error = null;
                if (byProvider.TryGetValue(descriptor.Provider, out var connector))
                {
                    try
                    {
                        await connector.ProbeAsync(descriptor.Name, cancellationToken);
                        healthy = true;
                    }
                    catch (Exception ex)
                    {
                        error = showDetail ? ex.Message : "unreachable";
                    }
                }
                else
                {
                    error = $"No connector for provider '{descriptor.Provider}'.";
                }

                results.Add(new ConnectionHealth
                {
                    Name = descriptor.Name,
                    Provider = descriptor.Provider,
                    Healthy = healthy,
                    LatencyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                    Error = error,
                    CheckedAt = clock.GetUtcNow(),
                });
            }

            return Results.Ok(results);
        });
    }

    /// <summary>Maps schema-introspection endpoints used by the endpoint editor to auto-fill metadata.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapIntrospection(RouteGroupBuilder group)
    {
        group.MapGet("/introspect/{connection}/objects", async (
            string connection, IDataConnectionRegistry registry, IEnumerable<IDbConnector> connectors,
            ILoggerFactory loggerFactory) =>
        {
            if (!TryResolveConnector(registry, connectors, connection, out var connector))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await connector.ListObjectsAsync(connection));
            }
            catch (Exception ex)
            {
                Log.IntrospectObjectsFailed(loggerFactory.CreateLogger("Weir.Admin"), ex, connection);
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Introspection failed", detail: "Introspection failed");
            }
        }).RequireAuthorization("AdminOnly");

        group.MapGet("/introspect/{connection}/parameters", async (
            string connection, string schema, string obj, IDataConnectionRegistry registry, IEnumerable<IDbConnector> connectors,
            ILoggerFactory loggerFactory) =>
        {
            if (!TryResolveConnector(registry, connectors, connection, out var connector))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await connector.DescribeParametersAsync(connection, schema, obj));
            }
            catch (Exception ex)
            {
                Log.IntrospectParametersFailed(loggerFactory.CreateLogger("Weir.Admin"), ex, schema, obj, connection);
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Introspection failed", detail: "Introspection failed");
            }
        }).RequireAuthorization("AdminOnly");
    }

    /// <summary>Maps the "sync from database" endpoints that reconcile endpoint parameters with the DB.</summary>
    /// <param name="group">The admin route group.</param>
    private static void MapSync(RouteGroupBuilder group)
    {
        // Synchronize a single endpoint's parameters with its target object.
        group.MapPost("/endpoints/{id:guid}/sync", async (
            Guid id, IControlPlaneStore store, IDataConnectionRegistry registry,
            IEnumerable<IDbConnector> connectors, IEndpointCatalog catalog, ClaimsPrincipal user, TimeProvider clock,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var endpoint = await store.GetEndpointAsync(id, cancellationToken);
            if (endpoint is null)
            {
                return Results.NotFound();
            }

            if (!TryResolveConnector(registry, connectors, endpoint.ConnectionName, out var connector))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No connector",
                    detail: $"No connector is configured for connection '{endpoint.ConnectionName}'.");
            }

            try
            {
                var objects = await LoadObjectSetAsync(connector, endpoint.ConnectionName, cancellationToken);
                var result = await SyncOneAsync(connector, store, endpoint, objects, cancellationToken);
                if (result.Status == "updated")
                {
                    await catalog.LoadAsync(cancellationToken);
                }

                await AuditActionAsync(store, clock, user, "endpoint.sync", DescribeSync(result));
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                Log.SyncEndpointFailed(loggerFactory.CreateLogger("Weir.Admin"), ex, id);
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Sync failed", detail: "Sync failed");
            }
        }).RequireAuthorization("AdminOnly");

        // Synchronize endpoints, optionally narrowed by connection (a specific database on a specific
        // server), schema and/or object (procedure). With no filter it syncs every endpoint; with all
        // three it targets one procedure by name, so CI/CD does not need the endpoint's id.
        group.MapPost("/endpoints/sync", async (
            string? connection, string? schema, [FromQuery(Name = "object")] string? objectName,
            IControlPlaneStore store, IDataConnectionRegistry registry,
            IEnumerable<IDbConnector> connectors, IEndpointCatalog catalog, ClaimsPrincipal user, TimeProvider clock,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var endpoints = await store.GetEndpointsAsync(cancellationToken);
            var selected = endpoints
                .Where(e => string.IsNullOrEmpty(connection) || string.Equals(e.ConnectionName, connection, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrEmpty(schema) || string.Equals(e.Schema, schema, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrEmpty(objectName) || string.Equals(e.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var results = new List<EndpointSyncResult>();
            var anyUpdated = false;
            var syncLogger = loggerFactory.CreateLogger("Weir.Admin");

            foreach (var connectionGroup in selected.GroupBy(e => e.ConnectionName, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryResolveConnector(registry, connectors, connectionGroup.Key, out var connector))
                {
                    results.AddRange(connectionGroup.Select(e => Failed(e, $"No connector for connection '{connectionGroup.Key}'.")));
                    continue;
                }

                ISet<string> objects;
                try
                {
                    objects = await LoadObjectSetAsync(connector, connectionGroup.Key, cancellationToken);
                }
                catch (Exception ex)
                {
                    Log.SyncObjectLoadFailed(syncLogger, ex, connectionGroup.Key);
                    results.AddRange(connectionGroup.Select(e => Failed(e, "Sync failed")));
                    continue;
                }

                foreach (var endpoint in connectionGroup)
                {
                    try
                    {
                        var result = await SyncOneAsync(connector, store, endpoint, objects, cancellationToken);
                        anyUpdated |= result.Status == "updated";
                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        Log.BulkSyncEndpointFailed(syncLogger, ex, endpoint.Id, endpoint.Route);
                        results.Add(Failed(endpoint, "Sync failed"));
                    }
                }
            }

            if (anyUpdated)
            {
                await catalog.LoadAsync(cancellationToken);
            }

            var changed = results.Count(r => r.Status == "updated");
            await AuditActionAsync(store, clock, user, "endpoint.sync",
                $"{results.Count} endpoint(s), {changed} changed" + (results.Count > 0 && results.Count <= 5 ? ": " + string.Join("; ", results.Select(DescribeSync)) : string.Empty));
            return Results.Ok(results);
        }).RequireAuthorization("AdminOnly");
    }

    /// <summary>
    /// Maps the cache-purge routes that force-evict cached data-plane responses. Both are AdminOnly and
    /// audited, so a personal access token can drive them from a deployment pipeline. Purging never
    /// touches the endpoint definitions - it only clears already-rendered responses, which are refilled
    /// on the next call.
    /// </summary>
    /// <param name="group">The admin route group.</param>
    private static void MapCache(RouteGroupBuilder group)
    {
        // Purge one endpoint's cached responses by id (the admin UI per-row "Purge cache" action).
        group.MapPost("/endpoints/{id:guid}/cache/purge", async (
            Guid id, IControlPlaneStore store, IResponseCache cache, ClaimsPrincipal user, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var endpoint = await store.GetEndpointAsync(id, cancellationToken);
            if (endpoint is null)
            {
                return Results.NotFound();
            }

            await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix(endpoint.Route), cancellationToken);
            // Empties this instance's cache above; the stamp is what tells the others to empty theirs.
            await store.RecordCachePurgeAsync([endpoint.Route], clock.GetUtcNow(), cancellationToken);
            await AuditActionAsync(store, clock, user, "cache.purge", $"route {endpoint.Route}");
            return Results.Ok(new CachePurgeResult { MatchedEndpoints = 1, PurgedRoutes = [endpoint.Route] });
        }).RequireAuthorization("AdminOnly");

        // Purge cached responses by filter, for scripted / CI-CD invalidation. Selects endpoints by
        // route, connection (a database on a server), schema, object (procedure) and/or provider
        // (connector), then evicts each matched endpoint's cache-key prefix. With no filter every
        // endpoint's cache is purged, so the pipeline can invalidate as broadly or narrowly as needed
        // without knowing endpoint ids.
        group.MapPost("/cache/purge", async (
            string? route, string? connection, string? schema, [FromQuery(Name = "object")] string? objectName, string? provider,
            IControlPlaneStore store, IDataConnectionRegistry registry, IResponseCache cache,
            ClaimsPrincipal user, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var endpoints = await store.GetEndpointsAsync(cancellationToken);
            var selected = endpoints
                .Where(e => string.IsNullOrEmpty(route) || string.Equals(e.Route, route, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrEmpty(connection) || string.Equals(e.ConnectionName, connection, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrEmpty(schema) || string.Equals(e.Schema, schema, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrEmpty(objectName) || string.Equals(e.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrEmpty(provider) || ConnectionHasProvider(registry, e.ConnectionName, provider))
                .ToList();

            // Route is the eviction unit (the cache-key prefix), so dedupe: several endpoints (a GET and
            // a POST) can share a route, and clearing its prefix evicts them all in one pass.
            var routes = selected
                .Select(e => e.Route)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var purged in routes)
            {
                await cache.RemoveByPrefixAsync(CacheKey.RoutePrefix(purged), cancellationToken);
            }

            // Empties this instance's cache above; the stamps are what tell the others to empty theirs.
            await store.RecordCachePurgeAsync(routes, clock.GetUtcNow(), cancellationToken);
            await AuditActionAsync(store, clock, user, "cache.purge",
                DescribePurge(route, connection, schema, objectName, provider, routes.Count));
            return Results.Ok(new CachePurgeResult { MatchedEndpoints = selected.Count, PurgedRoutes = routes });
        }).RequireAuthorization("AdminOnly");
    }

    /// <summary>Whether a named connection resolves to a connector with the given provider (case-insensitive).</summary>
    /// <param name="registry">The connection registry.</param>
    /// <param name="connectionName">The endpoint's connection name.</param>
    /// <param name="provider">The provider (connector) key to match, e.g. <c>SqlServer</c>.</param>
    /// <returns>True when the connection is registered and its provider matches.</returns>
    private static bool ConnectionHasProvider(IDataConnectionRegistry registry, string connectionName, string provider) =>
        registry.TryGet(connectionName, out var descriptor) &&
        string.Equals(descriptor.Provider, provider, StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds a compact, secret-free audit detail for a cache purge: the active filters and the route count.</summary>
    /// <param name="route">The route filter, if any.</param>
    /// <param name="connection">The connection filter, if any.</param>
    /// <param name="schema">The schema filter, if any.</param>
    /// <param name="objectName">The object filter, if any.</param>
    /// <param name="provider">The provider filter, if any.</param>
    /// <param name="routeCount">How many distinct routes were purged.</param>
    /// <returns>A description such as <c>connection=orders, object=usp_Get; 2 route(s) purged</c>.</returns>
    private static string DescribePurge(string? route, string? connection, string? schema, string? objectName, string? provider, int routeCount)
    {
        var filters = new List<string>();
        if (!string.IsNullOrEmpty(route)) filters.Add($"route={route}");
        if (!string.IsNullOrEmpty(connection)) filters.Add($"connection={connection}");
        if (!string.IsNullOrEmpty(schema)) filters.Add($"schema={schema}");
        if (!string.IsNullOrEmpty(objectName)) filters.Add($"object={objectName}");
        if (!string.IsNullOrEmpty(provider)) filters.Add($"provider={provider}");
        var scope = filters.Count == 0 ? "all endpoints" : string.Join(", ", filters);
        return $"{scope}; {routeCount} route(s) purged";
    }

    /// <summary>Loads the set of "schema.object" names available on a connection.</summary>
    private static async Task<ISet<string>> LoadObjectSetAsync(IDbConnector connector, string connection, CancellationToken cancellationToken)
    {
        var objects = await connector.ListObjectsAsync(connection, cancellationToken);
        return objects.Select(o => $"{o.Schema}.{o.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Synchronizes one endpoint's parameters, persisting the result only when it changed.</summary>
    private static async Task<EndpointSyncResult> SyncOneAsync(
        IDbConnector connector, IControlPlaneStore store, EndpointDefinition endpoint,
        ISet<string> existingObjects, CancellationToken cancellationToken)
    {
        if (!existingObjects.Contains($"{endpoint.Schema}.{endpoint.ObjectName}"))
        {
            return new EndpointSyncResult
            {
                EndpointId = endpoint.Id,
                Route = endpoint.Route,
                HttpMethod = endpoint.HttpMethod,
                Status = "objectNotFound",
                Message = $"{endpoint.Schema}.{endpoint.ObjectName} was not found on connection '{endpoint.ConnectionName}'.",
            };
        }

        var dbParameters = await connector.DescribeParametersAsync(
            endpoint.ConnectionName, endpoint.Schema, endpoint.ObjectName, cancellationToken);
        var (merged, result) = EndpointSynchronizer.Merge(endpoint, dbParameters);
        if (result.Status == "updated")
        {
            await store.UpsertEndpointAsync(merged, cancellationToken);
        }

        return result;
    }

    /// <summary>Builds an error sync result for an endpoint.</summary>
    private static EndpointSyncResult Failed(EndpointDefinition endpoint, string message) => new()
    {
        EndpointId = endpoint.Id,
        Route = endpoint.Route,
        HttpMethod = endpoint.HttpMethod,
        Status = "error",
        Message = message,
    };

    /// <summary>Resolves the connector for a named connection by its provider.</summary>
    /// <param name="registry">The connection registry.</param>
    /// <param name="connectors">The registered connectors.</param>
    /// <param name="connection">The connection name.</param>
    /// <param name="connector">Receives the matching connector.</param>
    /// <returns>True if a connection and matching connector were found.</returns>
    private static bool TryResolveConnector(
        IDataConnectionRegistry registry, IEnumerable<IDbConnector> connectors, string connection, out IDbConnector connector)
    {
        connector = null!;
        if (!registry.TryGet(connection, out var descriptor))
        {
            return false;
        }

        connector = connectors.FirstOrDefault(c => string.Equals(c.ProviderName, descriptor.Provider, StringComparison.OrdinalIgnoreCase))!;
        return connector is not null;
    }
}
