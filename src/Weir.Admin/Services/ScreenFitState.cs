namespace Weir.Admin.Services;

/// <summary>
/// Shares the current page's screen-fit preference with the shell. A list page turns this on while it is
/// shown, so <see cref="Layout.MainLayout"/> can give its content region a fixed height (the page header,
/// filters and pager stay put and only the grid rows scroll) without every other page losing its ordinary
/// whole-page scrolling.
/// <para>
/// Scoped to the Blazor circuit: one value per connected admin, shared between the page that sets it and
/// the layout that reads it. The layout turns it off on every navigation, and an opting-in page turns it
/// on after its first render, so the two never race.
/// </para>
/// </summary>
public sealed class ScreenFitState
{
    /// <summary>Whether the current page wants a screen-fit content region.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Raised when <see cref="Enabled"/> changes, so the shell can re-render.</summary>
    public event Action? Changed;

    /// <summary>Sets the preference, raising <see cref="Changed"/> only when it actually changes.</summary>
    /// <param name="enabled">The new preference.</param>
    public void Set(bool enabled)
    {
        if (Enabled == enabled)
        {
            return;
        }

        Enabled = enabled;
        Changed?.Invoke();
    }
}
