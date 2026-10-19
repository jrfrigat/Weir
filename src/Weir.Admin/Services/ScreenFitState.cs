using Microsoft.AspNetCore.Components;

namespace Weir.Admin.Services;

/// <summary>
/// Shares the current page's screen-fit preference with the shell. A list page turns this on while it is
/// shown, so <see cref="Layout.MainLayout"/> can give its content region a fixed height (the page header,
/// filters and pager stay put and only the grid rows scroll) without every other page losing its ordinary
/// whole-page scrolling.
/// <para>
/// Scoped to the Blazor circuit: one value per connected admin, shared between the page that sets it and
/// the layout that reads it. The layout turns it off on every navigation, and an opting-in page turns it
/// on after its first render. The preference remembers its route so a late navigation notification
/// cannot clear a preference the new page has already set.
/// </para>
/// </summary>
public sealed class ScreenFitState
{
    /// <summary>The navigation whose current URI owns a page's preference.</summary>
    private readonly NavigationManager _navigation;

    /// <summary>The URI for which the preference was last set.</summary>
    private string? _location;

    /// <summary>Creates the state for the current browser navigation.</summary>
    /// <param name="navigation">The browser navigation manager.</param>
    public ScreenFitState(NavigationManager navigation) => _navigation = navigation;

    /// <summary>Whether the current page wants a screen-fit content region.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Raised when <see cref="Enabled"/> changes, so the shell can re-render.</summary>
    public event Action? Changed;

    /// <summary>Sets the preference, raising <see cref="Changed"/> only when it actually changes.</summary>
    /// <param name="enabled">The new preference.</param>
    public void Set(bool enabled)
    {
        _location = _navigation.Uri;
        if (Enabled == enabled)
        {
            return;
        }

        Enabled = enabled;
        Changed?.Invoke();
    }

    /// <summary>Clears the previous page's preference without clearing one set by the destination.</summary>
    /// <param name="location">The destination URI reported by navigation.</param>
    public void ResetForNavigation(string location)
    {
        if (!string.Equals(_location, location, StringComparison.Ordinal))
        {
            Set(false);
        }
    }
}
