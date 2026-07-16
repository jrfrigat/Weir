// Weir Admin (Blazor WebAssembly PWA) entry point: registers Flare theming and the IDE component
// pack, sets up JWT auth state and a Bearer-enabled HttpClient, and starts the app.
using Flare.Abstractions.Tokens;
using Flare.Components.IDE;
using Flare.Extensions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Weir.Admin;
using Weir.Admin.Services;
using Weir.Admin.Theming;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddFlare(options =>
{
    // A single theme: the Visual Studio 2026 design (its geometry and base stylesheets) recoloured
    // with the Command Center palette (cyan on near-black). Dark-only ops console, no runtime theme
    // or light/dark switching.
    options.DefaultTheme = new CommandCenterTheme();
    options.DefaultMode = ThemeMode.Dark;
    options.RegisterAllBuiltInThemes = false;
});
builder.Services.AddFlareTheme(new CommandCenterTheme());
builder.Services.AddFlareIde();

// PWA update detection: Flare's version-check service registers the service worker and polls the
// deployed asset manifest for a new build, raising NewVersionAvailable (surfaced by PwaUpdater as a
// snackbar). Replaces the bespoke register-sw.js update bridge.
builder.Services.AddFlareVersionCheck(options =>
{
    options.UseServiceWorker = true;
    options.ServiceWorkerPath = "service-worker.js";
    options.Interval = TimeSpan.FromMinutes(30);
});

builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<WeirAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<WeirAuthStateProvider>());

builder.Services.AddScoped(sp =>
{
    var handler = new BearerHandler(
        sp.GetRequiredService<TokenStore>(),
        sp.GetRequiredService<WeirAuthStateProvider>())
    {
        InnerHandler = new HttpClientHandler(),
    };
    return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});
builder.Services.AddScoped<WeirApiClient>();

await builder.Build().RunAsync();
