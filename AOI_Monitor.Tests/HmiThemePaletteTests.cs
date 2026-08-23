using System.Text.RegularExpressions;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// Machine-checks the DESIGN.md high-contrast claim for the single supported (dark) theme.
/// These tests fail the build if any themed foreground/surface pairing regresses below its
/// contracted WCAG ratio, or if the palette drifts from the XAML token definitions. (The
/// Industrial Light theme was removed 2026-08-23; its worst measured pairing was 1.05:1.)
/// </summary>
public sealed class HmiThemePaletteTests
{
    [Fact]
    public void EveryForegroundContractHolds()
    {
        var tokens = HmiThemePalette.Tokens.ToDictionary(t => t.Key);
        var failures = new List<string>();

        foreach (var contract in HmiThemePalette.ForegroundContracts)
        {
            Assert.True(tokens.ContainsKey(contract.ForegroundKey), $"contract references unknown token {contract.ForegroundKey}");
            foreach (var surfaceKey in contract.SurfaceKeys)
            {
                Assert.True(tokens.ContainsKey(surfaceKey), $"contract references unknown surface {surfaceKey}");
                var fg = tokens[contract.ForegroundKey].Dark;
                var bg = tokens[surfaceKey].Dark;
                var ratio = ContrastRatio(fg, bg);
                if (ratio < contract.MinimumRatio)
                    failures.Add($"{contract.ForegroundKey} ({fg}) on {surfaceKey} ({bg}) = {ratio:F2}:1 < {contract.MinimumRatio:F1}:1");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void PaletteDarkValuesMatchTheXamlTokenDefinitions()
    {
        // The dark theme is the enforced default and must render exactly as the XAML declares;
        // a palette that silently disagreed with the resource dictionaries would restyle the
        // app on the first theme round-trip.
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "AOI_Monitor", "Styles", "FactoryHmiLayout.xaml")) +
                   File.ReadAllText(Path.Combine(root, "AOI_Monitor", "App.xaml"));

        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(xaml, "<Color x:Key=\"([^\"]+)\">(#[0-9A-Fa-f]{6})</Color>"))
            colors[m.Groups[1].Value] = m.Groups[2].Value.ToUpperInvariant();

        var brushes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(xaml, @"<SolidColorBrush x:Key=""([^""]+)""\s+Color=""(#[0-9A-Fa-f]{6,8}|\{StaticResource ([^}]+)})""\s*/>"))
        {
            var value = m.Groups[2].Value;
            brushes[m.Groups[1].Value] = value.StartsWith('#')
                ? value.ToUpperInvariant()
                : colors.GetValueOrDefault(m.Groups[3].Value, string.Empty);
        }

        var mismatches = new List<string>();
        foreach (var token in HmiThemePalette.Tokens)
        {
            if (!brushes.TryGetValue(token.Key, out var declared) || string.IsNullOrEmpty(declared))
            {
                mismatches.Add($"{token.Key}: no XAML SolidColorBrush definition found");
                continue;
            }

            if (!string.Equals(declared, token.Dark.ToUpperInvariant(), StringComparison.Ordinal))
                mismatches.Add($"{token.Key}: palette dark {token.Dark} != XAML {declared}");
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void PaletteKeysAreUniqueAndColorsAreValidHex()
    {
        Assert.Equal(HmiThemePalette.Tokens.Count, HmiThemePalette.Tokens.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (var token in HmiThemePalette.Tokens)
        {
            Assert.Matches("^#[0-9A-F]{6}$|^#[0-9A-F]{8}$", token.Dark.ToUpperInvariant());
        }
    }

    private static double ContrastRatio(string hexA, string hexB)
    {
        var la = RelativeLuminance(hexA);
        var lb = RelativeLuminance(hexB);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        var r = Channel(Convert.ToInt32(hex.Substring(1, 2), 16));
        var g = Channel(Convert.ToInt32(hex.Substring(3, 2), 16));
        var b = Channel(Convert.ToInt32(hex.Substring(5, 2), 16));
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AOI_PCB_Database.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root was not found from the test base directory.");
    }
}
