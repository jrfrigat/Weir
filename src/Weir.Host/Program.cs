// Weir host composition root: wires the control plane, engine, connector and telemetry, configures
// admin JWT authentication, rate limiting, CORS and OpenTelemetry export, runs startup
// initialization, and maps the data plane, admin API and health.
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;
using StackExchange.Redis;
using Weir.Abstractions;
using Weir.Connectors.PostgreSql;
using Weir.Connectors.SqlServer;
using Weir.Contracts;
using Weir.ControlPlane.PostgreSql;
using Weir.ControlPlane.SqlServer;
using Weir.ControlPlane.Sqlite;
using Weir.Core;
using Weir.Diagnostics;
using Weir.Host;
using Weir.Host.Health;
using Weir.Host.Http;
using Weir.Host.Audit;
using Weir.Host.Options;
using Weir.Host.Plugins;
using Weir.Host.Realtime;
using Weir.Host.RequestLogging;
using Weir.Host.Security;

var builder = WebApplication.CreateBuilder(args);

// Structured logging via Serilog: a rolling file sink (directory, interval, size, retention and format
// from Weir:Logging) plus an optional console sink. Configured before the container is built so every
// component logs through it.
var loggingOptions = builder.Configuration.GetSection("Weir:Logging").Get<WeirLoggingOptions>() ?? new WeirLoggingOptions();
builder.Services.Configure<WeirLoggingOptions>(builder.Configuration.GetSection("Weir:Logging"));
builder.Host.UseSerilog((context, _, config) =>
    Weir.Host.Logging.SerilogSetup.Apply(config, loggingOptions, context.HostingEnvironment.ContentRootPath));

// Cap the data-plane request body so a large (or chunked, length-less) body cannot exhaust memory.
// Zero or less leaves Kestrel's own default in place.
var dataPlaneLimits = builder.Configuration.GetSection("Weir:DataPlane").Get<WeirDataPlaneOptions>() ?? new WeirDataPlaneOptions();
if (dataPlaneLimits.MaxRequestBodyBytes > 0)
{
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = dataPlaneLimits.MaxRequestBodyBytes);
}

// The control-plane store is selectable: SQLite (default, single-node), or a shared server database
// (PostgreSQL or SQL Server) for high-availability deployments where several instances run against one
// control database.
var controlPlaneSection = builder.Configuration.GetSection("Weir:ControlPlane");
var controlPlaneProvider = controlPlaneSection.GetValue<string>("Provider") ?? "Sqlite";
var highAvailability = builder.Configuration.GetValue<bool>("Weir:HighAvailability");
if (string.Equals(controlPlaneProvider, "Postgres", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(controlPlaneProvider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddWeirControlPlanePostgres(options => controlPlaneSection.Bind(options));
}
else if (string.Equals(controlPlaneProvider, "SqlServer", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(controlPlaneProvider, "MsSql", StringComparison.OrdinalIgnoreCase))
{
    // SQL Server is a shared server database, so - like PostgreSQL - it is valid for HA (several
    // instances against one control database); no single-node guard needed.
    builder.Services.AddWeirControlPlaneSqlServer(options => controlPlaneSection.Bind(options));
}
else
{
    // The SQLite control plane is single-node. An HA deployment (several instances behind a load
    // balancer) must share one control database, so enforce the PostgreSQL control plane when HA is
    // declared rather than silently running instances with divergent, per-node SQLite files.
    if (highAvailability)
    {
        throw new InvalidOperationException(
            "Weir:HighAvailability is enabled but the control plane is SQLite, which is single-node. " +
            "Set Weir:ControlPlane:Provider = Postgres (and a shared connection string) so every instance " +
            "shares one control database, or clear Weir:HighAvailability for a single-node deployment.");
    }

    builder.Services.AddWeirControlPlaneSqlite(options => controlPlaneSection.Bind(options));
}

builder.Services.AddWeirCore(options =>
    builder.Configuration.GetSection("Weir:DataConnections").Bind(options.Connections));

builder.Services.AddWeirSqlServer();
builder.Services.AddWeirPostgreSql();
builder.Services.AddWeirDiagnostics();

// Load any configured plugins (e.g. third-party connectors) so they can register services before
// the container is built. A bootstrap logger is used because the app is not built yet.
using (var pluginBootstrapLoggerFactory = LoggerFactory.Create(logging =>
    logging.AddConfiguration(builder.Configuration.GetSection("Logging")).AddConsole()))
{
    PluginLoader.LoadConfiguredPlugins(
        builder.Services, builder.Configuration, pluginBootstrapLoggerFactory.CreateLogger("Weir.Plugins"));
}

builder.Services.AddMemoryCache();
builder.Services.Configure<AdminBootstrapOptions>(builder.Configuration.GetSection("Weir:Admin"));
builder.Services.AddSingleton<IApiKeyAuthenticator, ApiKeyAuthenticator>();

// Per-API-key rate limiter: in-memory (per instance) by default, or a distributed Redis-backed limiter
// (shared across instances for HA) when Weir:RateLimit:RedisConnectionString is set.
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection("Weir:RateLimit"));
var redisConnectionString = builder.Configuration["Weir:RateLimit:RedisConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
    builder.Services.AddSingleton<IApiKeyRateLimiter, RedisApiKeyRateLimiter>();
}
else
{
    builder.Services.AddSingleton<IApiKeyRateLimiter, ApiKeyRateLimiter>();
}

// Opt-in data-plane auditing: one background writer drains a non-blocking queue to the store.
builder.Services.AddOptions<AuditOptions>()
    .Bind(builder.Configuration.GetSection("Weir:Audit"))
    .Validate(o => o.QueueCapacity > 0, "Weir:Audit:QueueCapacity must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddSingleton<DataPlaneAuditor>();
builder.Services.AddSingleton<IDataPlaneAuditor>(sp => sp.GetRequiredService<DataPlaneAuditor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DataPlaneAuditor>());

// Background pruning of old audit entries, per the runtime AuditRetentionDays setting.
builder.Services.AddHostedService<AuditRetentionService>();

// Data-plane request log: a background writer drains a non-blocking queue to the store, and a call
// observer records every call (with the slow flag) when logging is enabled. Registered as an
// IWeirCallObserver so the engine notifies it around each call, on both the /api path and admin "try it".
builder.Services.AddSingleton<RequestLogSink>();
builder.Services.AddSingleton<IRequestLogSink>(sp => sp.GetRequiredService<RequestLogSink>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RequestLogSink>());
builder.Services.AddSingleton<IWeirCallObserver, RequestLogObserver>();

// Data-plane limits, HTTP security, admin sign-in lockout, and (for HA) periodic catalog reload.
builder.Services.AddOptions<WeirDataPlaneOptions>()
    .Bind(builder.Configuration.GetSection("Weir:DataPlane"))
    .Validate(
        o => o.MaxRows >= 0 && o.RequestTimeoutSeconds >= 0 && o.MaxTvpRows >= 0 && o.MaxRequestBodyBytes >= 0,
        "Weir:DataPlane limits must not be negative.")
    .ValidateOnStart();
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Weir:Security"));
builder.Services.AddOptions<AdminSecurityOptions>()
    .Bind(builder.Configuration.GetSection("Weir:Admin"))
    .Validate(o => o.MaxFailedLogins >= 0 && o.LockoutMinutes >= 0, "Weir:Admin lockout settings must not be negative.")
    .ValidateOnStart();
builder.Services.Configure<CatalogRefreshOptions>(builder.Configuration.GetSection("Weir:ControlPlane"));
builder.Services.AddSingleton<ILoginThrottle, PersistedLoginThrottle>();
builder.Services.AddHostedService<CatalogRefreshService>();

// Admin JWT authentication. A configured signing key is hashed to 32 bytes; if none is set an
// ephemeral key is generated (admin sessions then reset on restart).
var jwtOptions = builder.Configuration.GetSection("Weir:Jwt").Get<JwtOptions>() ?? new JwtOptions();

// Fail fast in Production without a stable signing key: an ephemeral key resets admin sessions on
// every restart and, in a multi-instance deployment, tokens issued by one instance are rejected by
// the others.
// In Production the signing key must be present and high-entropy: it is hashed to the HMAC key, so a
// short/guessable value weakens token signing regardless of the hash. In Development a short key (or
// none, which yields an ephemeral key) is acceptable.
if (builder.Environment.IsProduction())
{
    if (string.IsNullOrEmpty(jwtOptions.SigningKey))
    {
        throw new InvalidOperationException(
            "Weir:Jwt:SigningKey must be set in Production. Configure a stable secret (environment or a secret store).");
    }

    if (jwtOptions.SigningKey.Length < 32)
    {
        throw new InvalidOperationException(
            "Weir:Jwt:SigningKey must be at least 32 characters in Production. Configure a high-entropy secret.");
    }
}

var keyBytes = string.IsNullOrEmpty(jwtOptions.SigningKey)
    ? RandomNumberGenerator.GetBytes(32)
    : SHA256.HashData(Encoding.UTF8.GetBytes(jwtOptions.SigningKey));
var signingKey = new SymmetricSecurityKey(keyBytes);

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Weir:Jwt"))
    .Validate(o => o.AccessTokenMinutes > 0, "Weir:Jwt:AccessTokenMinutes must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddSingleton(signingKey);
builder.Services.AddSingleton<JwtTokenService>();

// Admin requests authenticate with either a login JWT or a personal access token. A policy scheme
// inspects the bearer value and forwards a "weadm_"-prefixed token to the access-token handler; every
// other bearer goes to the JWT handler. Both produce the same claim shape (unique_name / role).
const string adminAuthScheme = "AdminAuth";
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = adminAuthScheme;
        options.DefaultChallengeScheme = adminAuthScheme;
    })
    .AddPolicyScheme(adminAuthScheme, "Admin JWT or personal access token", options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer " + AdminTokenGenerator.Prefix, StringComparison.OrdinalIgnoreCase)
                ? AdminTokenAuthenticationHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        // Do not remap inbound claim types. The default map renames the short "role" claim to the
        // long ClaimTypes.Role URI, which would no longer match RoleClaimType below and break
        // RequireRole. Keeping claims as-issued makes RoleClaimType / NameClaimType line up.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            // The token carries short "role" and "unique_name" claims (see JwtTokenService).
            RoleClaimType = "role",
            NameClaimType = "unique_name",
        };
        options.Events = new JwtBearerEvents
        {
            // WebSockets cannot send an Authorization header, so the SignalR client passes the token as
            // an access_token query parameter on the hub handshake; read it there for hub connections only.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
            // Re-check the token against the account on every request: a disabled admin, or one whose
            // TokenVersion was bumped (password / role change), has its outstanding JWTs rejected at once.
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var sub = principal?.FindFirst("sub")?.Value;
                var ver = principal?.FindFirst("ver")?.Value;
                if (sub is null || ver is null || !Guid.TryParse(sub, out var adminId))
                {
                    context.Fail("The token is missing required claims.");
                    return;
                }

                var store = context.HttpContext.RequestServices.GetRequiredService<Weir.Abstractions.IControlPlaneStore>();
                var admin = await store.FindAdminByIdAsync(adminId, context.HttpContext.RequestAborted);
                if (admin is null || !admin.Enabled ||
                    !string.Equals(ver, admin.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    context.Fail("The token has been revoked.");
                }
            },
        };
    })
    .AddScheme<AuthenticationSchemeOptions, AdminTokenAuthenticationHandler>(
        AdminTokenAuthenticationHandler.SchemeName, null);

// Role-based access: "AdminOnly" gates every change. Read-only endpoints use the default policy,
// so Viewer accounts can see the dashboard, endpoints, keys and audit but cannot modify anything.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", policy => policy.RequireRole(AdminRoles.Admin));

// CORS for browser data-plane clients (opt-in via Weir:Cors:AllowedOrigins).
var corsOrigins = builder.Configuration.GetSection("Weir:Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));
}

// OpenTelemetry: emit the Weir meter and activity source plus ASP.NET Core instrumentation. Export
// over OTLP only when an endpoint is configured (OTEL_EXPORTER_OTLP_ENDPOINT).
var openTelemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Weir"))
    .WithMetrics(metrics => metrics
        .AddMeter(WeirInstruments.Name)
        // Npgsql publishes connection-pool metrics (idle/busy connections, pool saturation) on its own
        // meter; subscribing here surfaces PostgreSQL connection-pool telemetry alongside Weir's metrics.
        .AddMeter("Npgsql")
        .AddAspNetCoreInstrumentation())
    .WithTracing(tracing => tracing
        .AddSource(WeirInstruments.Name)
        .AddAspNetCoreInstrumentation());

if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    openTelemetry.UseOtlpExporter();
}

builder.Services.AddHealthChecks()
    .AddCheck<ControlPlaneHealthCheck>("control-plane", tags: ["ready"])
    .AddCheck<DataConnectionsHealthCheck>("data-connections", tags: ["ready"]);

// Response compression for the (often large) JSON result sets and problem+json errors. Brotli/gzip
// are negotiated from the client's Accept-Encoding; static Blazor assets are already precompressed.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/json", "application/problem+json"]);
});

// Real-time dashboard: a SignalR hub the admin PWA subscribes to, plus a background broadcaster that
// pushes metric and health snapshots so the dashboard no longer polls.
builder.Services.AddSignalR();
builder.Services.AddSingleton<DashboardClientTracker>();
builder.Services.AddHostedService<DashboardBroadcaster>();

var app = builder.Build();

await WeirStartup.InitializeAsync(app);

// Assign or propagate a correlation id, echo it back, and attach it to every log event for the request
// so a single call can be traced across the file logs. Runs first so all downstream logs carry it.
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = context.TraceIdentifier;
    }

    context.Response.Headers["X-Correlation-ID"] = correlationId;
    using (LogContext.PushProperty("CorrelationId", correlationId))
    {
        await next();
    }
});

// One structured summary line per request (method, path, status, elapsed) at Information level.
app.UseSerilogRequestLogging();

app.UseResponseCompression();

// Security: optional HTTPS redirect (off by default; usually TLS is terminated at the proxy), HSTS
// outside Development, and hardening response headers on every response.
var securityOptions = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;
if (securityOptions.RequireHttps)
{
    app.UseHttpsRedirection();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    if (!string.IsNullOrEmpty(securityOptions.ContentSecurityPolicy))
    {
        headers["Content-Security-Policy"] = securityOptions.ContentSecurityPolicy;
    }

    await next();
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = static context =>
    {
        // PWA control files must never be cached by the browser (or a proxy): otherwise it keeps
        // serving a stale service worker / asset manifest and never detects a new deployment, so the
        // "new version available" update notice never fires. Everything else keeps default caching.
        var name = context.File.Name;
        if (name.Equals("service-worker.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("service-worker-assets.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("register-sw.js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
    },
});

if (corsOrigins.Length > 0)
{
    app.UseCors();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapWeirDataPlane();
app.MapWeirAdminApi();
// Real-time dashboard hub; the [Authorize] on the hub gates it to authenticated admins.
app.MapHub<DashboardHub>("/hubs/dashboard");
// Aggregate health, plus Kubernetes-style split: liveness has no checks (process up), readiness runs
// the checks tagged "ready" (control plane and data connections).
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// Serve the Blazor WASM admin PWA for any non-API route.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed so the integration tests can boot the host with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
