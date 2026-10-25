// Per-user UI language for the admin PWA. The choice is a client-side preference (localStorage),
// deliberately not a control-plane setting: it belongs to the browser, not the gateway, and must
// resolve before the first paint even when the app is offline.
using System.Globalization;
using Flare.Abstractions;
using Flare.Components;
using Microsoft.AspNetCore.Components;
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

    /// <summary>
    /// The resource whose value is the language name in that language. Used to tell a loaded
    /// satellite from the neutral fallback: no other key is guaranteed to differ between the two.
    /// </summary>
    private const string ProbeKey = "Culture_SelfName";

    /// <summary>How many times to look again for a language that has not arrived yet.</summary>
    private const int ResourceLoadAttempts = 20;

    /// <summary>How long to wait between those looks.</summary>
    private static readonly TimeSpan ResourceLoadPollDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>JS bridge used to read and write the stored preference.</summary>
    private readonly IJSRuntime _js;

    /// <summary>Used to restart the app when a language cannot be applied in place.</summary>
    private readonly NavigationManager _navigation;

    /// <summary>Used to say so when a language needs a restart to take effect.</summary>
    private readonly ISnackbarService _snackbar;

    /// <summary>Initializes the service with the JS runtime used to reach localStorage.</summary>
    /// <param name="js">The Blazor JS interop runtime.</param>
    /// <param name="navigation">Navigation manager, used to restart the app as a last resort.</param>
    /// <param name="snackbar">Snackbar service, used to report a language that needs a restart.</param>
    public LanguageService(IJSRuntime js, NavigationManager navigation, ISnackbarService snackbar)
    {
        _js = js;
        _navigation = navigation;
        _snackbar = snackbar;
    }

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

        if (await ResourcesReadyAsync(cultureCode))
        {
            LanguageChanged?.Invoke();
            return;
        }

        // The strings for this language did not arrive, and a re-render would only repaint the old
        // ones. The choice is already stored, so a reload starts the app in the right language.
        //
        // Said before it is attempted, and said persistently, because the reload is not guaranteed
        // to happen: it goes through the browser, so a page holding unsaved work stops it - measured
        // on Settings with a changed field. When the reload does go through, the notice leaves with
        // the page and nobody reads it; when it is stopped, it is the only thing telling the reader
        // why the console is still in the old language, and it carries the way to finish the job.
        _snackbar.Show(
            AdminStrings.Language_ReloadNeeded,
            SnackbarSeverity.Info,
            0,
            AdminStrings.Language_ReloadAction,
            ReloadAsync,
            true);

        _navigation.NavigateTo(_navigation.Uri, forceLoad: true);
    }

    /// <summary>Restarts the app, which is what applies a language whose strings are not loaded.</summary>
    /// <returns>A completed task; the page is on its way out.</returns>
    private Task ReloadAsync()
    {
        _navigation.NavigateTo(_navigation.Uri, forceLoad: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits for the strings of a culture to become usable, and says whether they did.
    /// </summary>
    /// <remarks>
    /// The non-neutral language lives in a satellite assembly that is not downloaded at startup. The
    /// first lookup after the switch asks for it, which makes the runtime FETCH it - asynchronously,
    /// while the lookup itself, being synchronous, has already fallen back to the neutral language
    /// and been cached that way. That is why switching to Russian used to do nothing until the page
    /// was reloaded: the file arrived (measured: 200 on
    /// <c>/_framework/ru/Weir.Admin.resources.*.wasm</c>) but nothing ever looked again.
    ///
    /// So: ask again, dropping the cached answer each time, until the probe string comes back
    /// translated. The probe is a resource that says the language's own name, so a difference from
    /// the neutral value means the satellite is live - no file names and no runtime internals.
    /// </remarks>
    /// <param name="cultureCode">The culture being switched to.</param>
    /// <returns>True when the strings are usable; false when they never arrived.</returns>
    private static async Task<bool> ResourcesReadyAsync(string cultureCode)
    {
        // The neutral language is compiled into the main assembly and is always there.
        if (string.Equals(cultureCode, DefaultCulture, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var neutral = AdminStrings.ResourceManager.GetString(ProbeKey, CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < ResourceLoadAttempts; attempt++)
        {
            AdminStrings.ResourceManager.ReleaseAllResources();
            var translated = AdminStrings.ResourceManager.GetString(ProbeKey, AdminStrings.Culture);

            // Three outcomes, and only one of them means ready. A null is the lookup failing outright
            // while the file is still in flight - mistaking it for "different from the neutral value"
            // is what made the first version of this report success and leave the console in English.
            if (!string.IsNullOrEmpty(translated) && !string.Equals(translated, neutral, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(ResourceLoadPollDelay);
        }

        return false;
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
