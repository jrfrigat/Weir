using Flare.Abstractions;
using Flare.Abstractions.Tokens;
using Flare.Theme.VisualStudio;

namespace Weir.Admin.Theming;

/// <summary>
/// The Weir "Command Center" theme: the Visual Studio 2026 design (dense, square IDE geometry and its
/// base stylesheets) recoloured with a cyan-on-near-black ops-console palette. Colours live in the
/// <see cref="CommandCenterPalette"/>; light/dark is a mode, and the theme defaults to dark.
/// </summary>
public sealed class CommandCenterTheme : ITheme
{
    /// <summary>Stable theme id (also the <c>flare-theme-{Id}</c> CSS class suffix).</summary>
    public const string ThemeId = "command-center";

    /// <summary>The Visual Studio base stylesheets are reused so components render correctly.</summary>
    private static readonly IReadOnlyList<string> VsStyleAssets = new VisualStudioTheme().StyleAssets;

    /// <inheritdoc />
    public string Id => ThemeId;

    /// <inheritdoc />
    public string DisplayName => "Command Center";

    /// <inheritdoc />
    public DesignTokens Design => VisualStudio.DesignReference;

    /// <inheritdoc />
    public string DefaultPaletteId => CommandCenterPalette.PaletteId;

    /// <inheritdoc />
    public IReadOnlyList<Palette> Palettes => [CommandCenterPalette.Palette];

    /// <inheritdoc />
    public IReadOnlyList<string> StyleAssets => VsStyleAssets;
}

/// <summary>
/// The Command Center colour palette: a cyan-on-near-black dark scheme (the ops-console look) plus a
/// light teal variant so the theme still works in light mode. Derived from the Visual Studio schemes,
/// overriding only the roles that give the Command Center its identity.
/// </summary>
public static class CommandCenterPalette
{
    /// <summary>Stable palette id (also the <c>flare-palette-{Id}</c> CSS class suffix).</summary>
    public const string PaletteId = "command-center";

    /// <summary>The registered Command Center palette.</summary>
    public static readonly Palette Palette = new()
    {
        Id = PaletteId,
        Name = "Command Center",
        Source = "Weir",
        Dark = BuildDark(),
        Light = BuildLight(),
    };

    /// <summary>The cyan-on-near-black dark scheme (the primary ops-console look).</summary>
    /// <returns>The dark colour scheme.</returns>
    private static ColorScheme BuildDark() => VisualStudio.DarkColors with
    {
        Primary = "#4fd6c9",
        OnPrimary = "#08131a",
        PrimaryContainer = "#123a37",
        OnPrimaryContainer = "#7fe6db",
        Secondary = "#4ea1ff",
        OnSecondary = "#04121f",
        SecondaryContainer = "#16324e",
        OnSecondaryContainer = "#a9d3ff",
        Tertiary = "#4ea1ff",
        OnTertiary = "#04121f",
        TertiaryContainer = "#16324e",
        OnTertiaryContainer = "#a9d3ff",
        Info = "#4ea1ff",
        OnInfo = "#04121f",
        InfoContainer = "#16324e",
        OnInfoContainer = "#a9d3ff",
        Success = "#7ec97e",
        OnSuccess = "#0b1f0b",
        SuccessContainer = "#1e3a24",
        OnSuccessContainer = "#a6e0a6",
        Warning = "#d6a53a",
        OnWarning = "#1a1305",
        WarningContainer = "#3a2e12",
        OnWarningContainer = "#f0cd7a",
        Error = "#f07a6a",
        OnError = "#1f0a07",
        ErrorContainer = "#3a1e1a",
        OnErrorContainer = "#ffb4a8",
        Background = "#0f1011",
        OnBackground = "#e7e7e8",
        Surface = "#0f1011",
        OnSurface = "#e7e7e8",
        SurfaceVariant = "#16181a",
        OnSurfaceVariant = "#a3a3a8",
        OnSurfaceVariant2 = "#6d6d73",
        SurfaceContainerLow = "#131416",
        SurfaceContainer = "#16181a",
        SurfaceContainerHigh = "#1c1f22",
        SurfaceContainerHighest = "#232629",
        Outline = "#26282b",
        OutlineVariant = "#3a3a3f",
        InversePrimary = "#0e8a9c",
    };

    /// <summary>A light teal variant so the Command Center theme is usable in light mode too.</summary>
    /// <returns>The light colour scheme.</returns>
    private static ColorScheme BuildLight() => VisualStudio.LightColors with
    {
        Primary = "#0e8a9c",
        OnPrimary = "#ffffff",
        PrimaryContainer = "#b8ece6",
        OnPrimaryContainer = "#05343c",
        Secondary = "#2a78d6",
        Tertiary = "#2a78d6",
        Info = "#2a78d6",
    };
}
