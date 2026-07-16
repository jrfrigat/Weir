// Per-user UI language for the admin PWA. The choice is a client-side preference (localStorage),
// deliberately not a control-plane setting: it belongs to the browser, not the gateway, and must
// resolve before the first paint even when the app is offline.
using System.Globalization;
using Microsoft.JSInterop;
using Weir.Admin.Resources;

namespace Weir.Admin.Services;

/// <summary>
/// Owns the admin console's UI language: resolves it at startup, switches it at runtime and tells
/// subscribed components to re-render. Culture is applied to the resource lookup
/// (<see cref="AdminStrings.Culture"/>) and to the thread defaults so number and date formatting
/// follows the language too.
/// </summary>
public sealed class LanguageService
{
    /// <summary>localStorage key holding the explicit language choice.</summary>
    private const string StorageKey = "weirCulture";

    /// <summary>Fallback language when nothing is stored and the browser asks for an unsupported one.</summary>
    private const string DefaultCulture = "en";

    /// <summary>JS bridge used to read and write the stored preference.</summary>
    private readonly IJSRuntime _js;

    /// <summary>Initializes the service with the JS runtime used to reach localStorage.</summary>
    /// <param name="js">The Blazor JS interop runtime.</param>
    public LanguageService(IJSRuntime js) => _js = js;

    /// <summary>
    /// The languages the admin console ships, as (culture code, native display name). The names are
    /// deliberately not localized: a language picker shows every option in its own language, so the
    /// entry you are looking for reads the same whatever the console is currently set to.
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string DisplayName)> SupportedCultures =
    [
        ("en", "English"),
        ("ru", "Русский"),
    ];

    /// <summary>The active language code ("en" or "ru").</summary>
    public string CurrentCulture { get; private set; } = DefaultCulture;

    /// <summary>Raised after the language changes so localized components can re-render.</summary>
    public event Action? LanguageChanged;

    /// <summary>
    /// Resolves the startup language and applies it. Priority: the stored choice, then the browser
    /// language (the Blazor WASM runtime seeds CurrentUICulture from navigator.language), then English.
    /// Must run before the host starts so the first render is already in the right language.
    /// </summary>
    /// <returns>A task that completes once the culture is applied.</returns>
    public async Task InitializeCultureAsync()
    {
        var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        Apply(Resolve(stored) ?? Resolve(CultureInfo.CurrentUICulture.Name) ?? DefaultCulture);
    }

    /// <summary>
    /// Switches the UI language, persists the choice so it survives a reload (and an offline start),
    /// and notifies subscribers. A no-op when the language is already active.
    /// </summary>
    /// <param name="cultureCode">A code from <see cref="SupportedCultures"/>.</param>
    /// <returns>A task that completes once the choice is stored and subscribers are notified.</returns>
    public async Task SetCultureAsync(string cultureCode)
    {
        if (cultureCode == CurrentCulture)
        {
            return;
        }

        Apply(cultureCode);
        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, cultureCode);
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// Applies a culture to the resource lookup and to the thread defaults. Blazor WASM is
    /// single-threaded, so setting the DefaultThreadCurrent* properties is what actually changes
    /// CurrentCulture / CurrentUICulture for the app.
    /// </summary>
    /// <param name="cultureCode">The culture code to activate.</param>
    private void Apply(string cultureCode)
    {
        CurrentCulture = cultureCode;
        var culture = new CultureInfo(cultureCode);

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        AdminStrings.Culture = culture;
    }

    /// <summary>
    /// Maps an arbitrary culture code ("ru-RU", "en-GB") onto a supported one by its language prefix.
    /// </summary>
    /// <param name="code">The candidate code, possibly null or region-qualified.</param>
    /// <returns>The supported code, or null when the language is not shipped.</returns>
    private static string? Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        foreach (var (supported, _) in SupportedCultures)
        {
            if (code.StartsWith(supported, StringComparison.OrdinalIgnoreCase))
            {
                return supported;
            }
        }

        return null;
    }
}
