using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Core;
using Weir.Host.Options;
using Weir.Host.Security;

namespace Weir.Host;

/// <summary>One-time startup work: migrate the control plane, bootstrap the first admin, load endpoints.</summary>
public static class WeirStartup
{
    /// <summary>Runs startup initialization before the host begins serving.</summary>
    /// <param name="app">The built web application.</param>
    /// <returns>A task that completes when initialization finishes.</returns>
    public static async Task InitializeAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Weir.Startup");

        var store = services.GetRequiredService<IControlPlaneStore>();
        await store.InitializeAsync();

        // Load persisted runtime settings (overlaying the appsettings seed) before serving traffic.
        await services.GetRequiredService<IRuntimeSettings>().InitializeAsync();

        await BootstrapAdminAsync(
            store,
            services.GetRequiredService<IOptions<AdminBootstrapOptions>>().Value,
            services.GetRequiredService<IOptions<AdminSecurityOptions>>().Value.PasswordIterations,
            logger);

        await services.GetRequiredService<IEndpointCatalog>().LoadAsync();
        Log.Initialized(logger);
    }

    /// <summary>
    /// Creates the bootstrap admin account when none exists and credentials are configured. Every exit
    /// leaves a log entry, so an operator can tell a deliberate skip from a configuration mistake - the
    /// silent skip used to leave a host with no admin and no explanation.
    /// </summary>
    /// <param name="store">The control-plane store.</param>
    /// <param name="options">The bootstrap credentials.</param>
    /// <param name="passwordIterations">The configured PBKDF2 work factor.</param>
    /// <param name="logger">Logger for startup messages.</param>
    /// <returns>A task that completes when the check finishes.</returns>
    internal static async Task BootstrapAdminAsync(
        IControlPlaneStore store, AdminBootstrapOptions options, int passwordIterations, ILogger logger)
    {
        var username = options.Username;
        var password = options.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            // Nothing configured: the first admin is created some other way. Say so, so a typo in the
            // setting name is distinguishable from a deliberate choice.
            Log.BootstrapNotConfigured(logger);
            return;
        }

        var admins = await store.GetAdminsAsync();
        if (admins.Count > 0)
        {
            // Say why bootstrap did not run: otherwise an operator who changed Weir:Admin cannot tell
            // from the logs that the account was never created and looks for the fault elsewhere.
            Log.BootstrapSkipped(logger, admins.Count);
            return;
        }

        // Hold the bootstrap account to the same strength policy as accounts created through the API.
        if (PasswordPolicy.Validate(password) is { } reason)
        {
            // Reaching here means the control plane holds no admin, so a rejected password leaves the
            // host with nobody able to sign in. That is an error, not a warning.
            Log.BootstrapPasswordRejectedNoAdmins(logger, reason);
            return;
        }

        await store.CreateAdminAsync(
            username, PasswordHasher.Hash(password, passwordIterations), Weir.Contracts.AdminRoles.Admin);
        Log.BootstrappedAdmin(logger, username);
    }
}
