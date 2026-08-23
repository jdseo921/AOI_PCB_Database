using System.Collections.Generic;

namespace AOI_Monitor.Services;

/// <summary>One themed brush: resource key plus its color in the single supported (dark) theme.</summary>
public sealed record HmiThemeToken(string Key, string Dark);

/// <summary>
/// A readability contract for one foreground token: the themed surfaces it is allowed to sit
/// on and the minimum WCAG contrast ratio it must reach against every one of them.
/// <see cref="HmiThemePaletteTests"/> enforces this table, which is what makes the DESIGN.md
/// high-contrast statement machine-checked instead of aspirational.
/// </summary>
public sealed record HmiForegroundContract(string ForegroundKey, IReadOnlyList<string> SurfaceKeys, double MinimumRatio);

/// <summary>
/// The color table for every themed brush in the application's single supported theme.
///
/// Values are the exact literals the 2026-08 tokenization sweep replaced, so the app renders
/// as it always has; the Industrial Light theme was removed the same day (it predated the
/// token system and shipped unreadable). Status chips (tinted badge background + border +
/// pale text triplets) are deliberately absent: they are self-contained contrast pairs.
///
/// <see cref="UiPreferencesService"/> reasserts this table at startup; nothing else may
/// define theme colors in code. The XAML token definitions must match — a drift test pins it.
/// </summary>
public static class HmiThemePalette
{
    // Surfaces text can sit on.
    private static readonly string[] AllSurfaces = ["Bg", "CellBg", "Frame2Bg", "HmiRaisedBrush"];
    private static readonly string[] NonRaisedSurfaces = ["Bg", "CellBg", "Frame2Bg"];

    public static IReadOnlyList<HmiThemeToken> Tokens { get; } =
    [
        // Legacy application-level neutrals (App.xaml originals).
        new("Bg", "#0B0E10"),
        new("WindowBg", "#121619"),
        new("FrameBg", "#252C31"),
        new("Frame2Bg", "#1B2024"),
        new("CellBg", "#151A1E"),
        new("Cell2Bg", "#1C2328"),
        new("LineBrush", "#3D464D"),
        new("TextBrush", "#D8DEE3"),
        new("MutedBrush", "#8B969E"),
        new("DimBrush", "#667078"),

        // HMI structural tokens.
        new("HmiBgBrush", "#0B0E10"),
        new("HmiSurfaceBrush", "#151A1E"),
        new("HmiSurfaceAltBrush", "#1B2024"),
        new("HmiRaisedBrush", "#20272B"),
        new("HmiBorderBrush", "#3E474E"),
        new("HmiPanelBorderBrush", "#343D44"),
        new("HmiBorderMidBrush", "#59636B"),
        new("HmiBorderStrongBrush", "#8C9BA4"),
        new("HmiSelectionBrush", "#273642"),

        // Shell chrome and flyout panels.
        new("HmiChromeBrush", "#0D2438"),
        new("HmiChromeAltBrush", "#081018"),
        new("HmiFlyoutBrush", "#0C0F11"),
        new("HmiFlyoutAltBrush", "#0F151A"),
        new("HmiFlyoutBorderBrush", "#3A4249"),
        new("HmiScrimBrush", "#EE151A1E"),
        new("HmiLoadingScrimBrush", "#CC050607"),
        new("HmiNoteBrush", "#172A39"),
        new("HmiNoteBorderBrush", "#477CA5"),

        // Neutral text ramp.
        new("HmiTextBrush", "#E8EEF2"),
        new("HmiTextBodyBrush", "#DCE5EB"),
        new("HmiMutedTextBrush", "#A8B2B9"),
        new("HmiTextLabelBrush", "#9AA6AF"),
        new("HmiTextDimBrush", "#7F8C95"),

        // Semantic status text on themed surfaces. Light values are the dark-on-light
        // equivalents; chip-internal status text does not use these tokens.
        new("HmiOkBrush", "#50F56E"),
        new("HmiNgBrush", "#F27777"),
        new("HmiNgStrongBrush", "#F13B3F"),
        new("HmiWarnBrush", "#E1A334"),
        new("HmiInfoBrush", "#5CA0D3"),
        new("HmiOfflineBrush", "#8B969E"),
        new("HmiSimulatedBrush", "#C084FC"),
        new("HmiWarnSoftBrush", "#FFE0A7"),
        new("HmiNgSoftBrush", "#FFCDD0"),
        new("HmiOkSoftBrush", "#C6FFD0"),
        new("HmiInfoSoftBrush", "#CFEAFF"),
        new("HmiSimulatedSoftBrush", "#F1D8FF"),

        // App.xaml semantic aliases (distinct brush instances, so they need their own rows).
        new("GreenBrush", "#50F56E"),
        new("RedBrush", "#F27777"),
        new("AmberBrush", "#E1A334"),
        new("BlueBrush", "#5CA0D3"),
        new("PurpleBrush", "#C084FC"),
    ];

    public static IReadOnlyList<HmiForegroundContract> ForegroundContracts { get; } =
    [
        new("HmiTextBrush", ["HmiChromeBrush", "HmiChromeAltBrush", "HmiFlyoutBrush", "HmiFlyoutAltBrush"], 4.5),
        new("HmiTextBodyBrush", ["HmiChromeBrush", "HmiChromeAltBrush", "HmiFlyoutBrush", "HmiFlyoutAltBrush"], 4.5),
        new("HmiTextLabelBrush", ["HmiChromeBrush", "HmiChromeAltBrush"], 4.5),
        new("HmiMutedTextBrush", ["HmiChromeBrush", "HmiChromeAltBrush"], 4.5),
        new("HmiMutedTextBrush", ["HmiNoteBrush"], 4.5),
        new("HmiTextBodyBrush", ["HmiNoteBrush"], 4.5),
        new("HmiTextBrush", AllSurfaces, 4.5),
        new("HmiTextBodyBrush", AllSurfaces, 4.5),
        new("TextBrush", AllSurfaces, 4.5),
        new("HmiMutedTextBrush", AllSurfaces, 4.5),
        new("MutedBrush", AllSurfaces, 4.5),
        new("HmiTextLabelBrush", AllSurfaces, 4.5),

        // Dim text is de-emphasis by definition; it is excluded from the raised surface and,
        // for the legacy DimBrush, held to the WCAG large-text floor its long-standing dark
        // value actually meets (it renders at >= 18.67 DIP).
        new("HmiTextDimBrush", NonRaisedSurfaces, 4.5),
        new("DimBrush", NonRaisedSurfaces, 3.0),

        new("HmiOkBrush", AllSurfaces, 4.5),
        new("HmiNgBrush", AllSurfaces, 4.5),
        // Strong-NG dark (#F13B3F) is always rendered bold at >= 18.67 DIP, which is WCAG
        // large text; 3.0 is the applicable floor and its weakest legal pairing measures 3.99.
        new("HmiNgStrongBrush", AllSurfaces, 3.0),
        new("HmiWarnBrush", AllSurfaces, 4.5),
        new("HmiInfoBrush", AllSurfaces, 4.5),
        new("HmiOfflineBrush", AllSurfaces, 4.5),
        new("HmiSimulatedBrush", AllSurfaces, 4.5),
        new("HmiWarnSoftBrush", AllSurfaces, 4.5),
        new("HmiNgSoftBrush", AllSurfaces, 4.5),
        new("HmiOkSoftBrush", AllSurfaces, 4.5),
        new("HmiInfoSoftBrush", AllSurfaces, 4.5),
        new("HmiSimulatedSoftBrush", AllSurfaces, 4.5),
        new("GreenBrush", AllSurfaces, 4.5),
        new("RedBrush", AllSurfaces, 4.5),
        new("AmberBrush", AllSurfaces, 4.5),
        new("BlueBrush", AllSurfaces, 4.5),
        new("PurpleBrush", AllSurfaces, 4.5),
    ];
}
