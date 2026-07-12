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

        await BootstrapAdminAsync(store, services.GetRequiredService<IOptions<AdminBootstrapOptions>>().Value, logger);

        await services.GetRequiredService<IEndpointCatalog>().LoadAsync();
        Log.Initialized(logger);
    }

    /// <summary>Creates the bootstrap admin account when none exists and credentials are configured.</summary>
    /// <param name="store">The control-plane store.</param>
    /// <param name="options">The bootstrap credentials.</param>
    /// <param name="logger">Logger for startup messages.</param>
    /// <returns>A task that completes when the check finishes.</returns>
    private static async Task BootstrapAdminAsync(IControlPlaneStore store, AdminBootstrapOptions options, ILogger logger)
    {
        var username = options.Username;
        var password = options.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var admins = await store.GetAdminsAsync();
        if (admins.Count > 0)
        {
            return;
        }

        // Hold the bootstrap account to the same strength policy as accounts created through the API.
        if (PasswordPolicy.Validate(password) is { } reason)
        {
            Log.BootstrapPasswordRejected(logger, reason);
            return;
        }

        await store.CreateAdminAsync(username, PasswordHasher.Hash(password), Weir.Contracts.AdminRoles.Admin);
        Log.BootstrappedAdmin(logger, username);
    }
}
