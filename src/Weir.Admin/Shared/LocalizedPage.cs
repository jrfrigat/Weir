// Base component for anything that renders AdminStrings. Switching the language does not touch any
// component parameter, so Blazor has no reason to re-render on its own: each localized component has
// to listen for the change itself. This base does exactly that, and nothing else.
using Microsoft.AspNetCore.Components;
using Weir.Admin.Services;

namespace Weir.Admin.Shared;

/// <summary>
/// A <see cref="ComponentBase"/> that re-renders when the UI language changes. Pages and components
/// that display localized text derive from it via <c>@inherits LocalizedPage</c>.
/// </summary>
/// <remarks>
/// Derived components that override <see cref="OnInitialized"/> must call <c>base.OnInitialized()</c>,
/// and those that override <see cref="Dispose"/> must call <c>base.Dispose()</c>, or the subscription
/// leaks. The same applies, less obviously, to a component that additionally implements
/// <see cref="IAsyncDisposable"/>: the renderer calls only DisposeAsync when both are present, so such
/// a component has to call <see cref="Dispose"/> from its DisposeAsync (see Dashboard).
/// </remarks>
public abstract class LocalizedPage : ComponentBase, IDisposable
{
    /// <summary>The language service this component follows.</summary>
    [Inject]
    protected LanguageService Lang { get; set; } = null!;

    /// <summary>Subscribes to language changes.</summary>
    protected override void OnInitialized() => Lang.LanguageChanged += OnLanguageChanged;

    /// <summary>Unsubscribes from language changes.</summary>
    public virtual void Dispose()
    {
        Lang.LanguageChanged -= OnLanguageChanged;
        GC.SuppressFinalize(this);
    }

    /// <summary>Re-renders on the renderer's synchronization context after a language switch.</summary>
    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);
}
