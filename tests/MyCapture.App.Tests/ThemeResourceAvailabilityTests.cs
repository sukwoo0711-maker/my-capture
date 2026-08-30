using System.Threading;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Verifies the shipped theme dictionaries (Tokens, Symbols, Controls) load together and expose
/// every resource key the redesigned surfaces reference at runtime. A missing key surfaces as a
/// <c>ResourceReferenceKeyNotFoundException</c> only when a screen is shown, so pinning the whole
/// vocabulary here turns that latent runtime failure into a fast unit failure.
/// </summary>
public sealed class ThemeResourceAvailabilityTests
{
    // Every Icon.* geometry the editor, gallery, settings, and secondary windows resolve by key.
    private static readonly string[] IconKeys =
    [
        "Icon.Select", "Icon.Rectangle", "Icon.Arrow", "Icon.Pen", "Icon.Text", "Icon.Image",
        "Icon.Undo", "Icon.Redo", "Icon.Delete", "Icon.Copy", "Icon.Check", "Icon.Close",
        "Icon.Save", "Icon.SaveAs", "Icon.ChevronDown", "Icon.More", "Icon.Settings", "Icon.Capture",
        "Icon.Shortcuts", "Icon.Storage", "Icon.Export", "Icon.Pin", "Icon.Edit", "Icon.Ocr", "Icon.Search",
        "Icon.ZoomIn", "Icon.ZoomOut",
    ];

    // Token + control style keys the redesigned XAML and editor code reference by name.
    private static readonly string[] BrushKeys =
    [
        "Surface.Canvas", "Surface.Base", "Surface.Raised", "Surface.Overlay", "Surface.Sunken",
        "Text.Primary", "Text.Secondary", "Text.Muted", "Text.OnAccent",
        "Accent.Default", "Accent.Cool", "Border.Subtle", "Border.Focus", "Overlay.SelectionBorder",
        "State.DangerHover",
    ];

    private static readonly string[] StyleKeys =
    [
        "Button.Primary", "Button.Secondary", "Button.Ghost", "Button.Danger", "Button.GhostCompact",
        "ToggleButton.Tool", "Rail.ToolButton",
    ];

    [Fact]
    public void MergedThemeExposesEveryIconAndCoreKey()
    {
        StaTestHost.Run(() =>
        {
            ResourceDictionary theme = LoadMergedTheme();

            foreach (string key in IconKeys)
            {
                object? value = theme[key];
                Assert.True(value is not null, $"Missing icon resource '{key}'.");
                Assert.IsAssignableFrom<Geometry>(value);
            }

            foreach (string key in BrushKeys)
            {
                object? value = theme[key];
                Assert.True(value is not null, $"Missing brush resource '{key}'.");
                Assert.IsAssignableFrom<Brush>(value);
            }

            foreach (string key in StyleKeys)
            {
                object? value = theme[key];
                Assert.True(value is not null, $"Missing style resource '{key}'.");
                Assert.IsAssignableFrom<Style>(value);
            }
        });
    }

    [Fact]
    public void AccentGradientIsFlatSolidBrushNotDecorativeGradient()
    {
        StaTestHost.Run(() =>
        {
            ResourceDictionary theme = LoadMergedTheme();

            // The design direction bans decorative gradients; the compatibility alias must be a
            // flat solid brush so nothing reintroduces a gradient wash.
            object? accent = theme["Accent.Gradient"];
            Assert.IsType<SolidColorBrush>(accent);
        });
    }

    [Fact]
    public void RailToolStyleUsesStableFortyPixelTargets()
    {
        StaTestHost.Run(() =>
        {
            ResourceDictionary theme = LoadMergedTheme();
            Style style = Assert.IsType<Style>(theme["Rail.ToolButton"]);

            Setter width = Assert.Single(style.Setters.OfType<Setter>(),
                setter => setter.Property == FrameworkElement.WidthProperty);
            Setter height = Assert.Single(style.Setters.OfType<Setter>(),
                setter => setter.Property == FrameworkElement.HeightProperty);

            Assert.Equal(40d, Assert.IsType<double>(width.Value));
            Assert.Equal(40d, Assert.IsType<double>(height.Value));
        });
    }

    [Fact]
    public void DarkPaletteHasOrderedSurfacesAndAccessibleTextAndAccentContrast()
    {
        StaTestHost.Run(() =>
        {
            ResourceDictionary theme = LoadMergedTheme();

            double canvas = RelativeLuminance(BrushFor(theme, "Surface.Canvas").Color);
            double @base = RelativeLuminance(BrushFor(theme, "Surface.Base").Color);
            double raised = RelativeLuminance(BrushFor(theme, "Surface.Raised").Color);
            double overlay = RelativeLuminance(BrushFor(theme, "Surface.Overlay").Color);

            Assert.True(canvas < @base && @base < raised && raised < overlay,
                $"Expected Canvas < Base < Raised < Overlay luminance, got {canvas:F4}, {@base:F4}, {raised:F4}, {overlay:F4}.");

            double mutedContrast = ContrastRatio(
                BrushFor(theme, "Text.Muted").Color,
                BrushFor(theme, "Surface.Base").Color);
            double accentContrast = ContrastRatio(
                BrushFor(theme, "Text.OnAccent").Color,
                BrushFor(theme, "Accent.Default").Color);

            Assert.True(mutedContrast >= 4.5, $"Muted text contrast was only {mutedContrast:F2}:1.");
            Assert.True(accentContrast >= 4.5, $"Accent text contrast was only {accentContrast:F2}:1.");
        });
    }

    [Fact]
    public void FocusAndSelectionSemanticsUseTheBrightBluePrimitive()
    {
        StaTestHost.Run(() =>
        {
            ResourceDictionary theme = LoadMergedTheme();
            Color brightBlue = Assert.IsType<Color>(theme["Primitive.Blue300"]);

            Assert.Equal(brightBlue, BrushFor(theme, "Accent.Cool").Color);
            Assert.Equal(brightBlue, BrushFor(theme, "Border.Focus").Color);
            Assert.Equal(brightBlue, BrushFor(theme, "Overlay.SelectionBorder").Color);
        });
    }

    private static SolidColorBrush BrushFor(ResourceDictionary theme, string key) =>
        Assert.IsType<SolidColorBrush>(theme[key]);

    private static double ContrastRatio(Color first, Color second)
    {
        double l1 = RelativeLuminance(first);
        double l2 = RelativeLuminance(second);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126 * LinearChannel(color.R))
        + (0.7152 * LinearChannel(color.G))
        + (0.0722 * LinearChannel(color.B));

    private static double LinearChannel(byte channel)
    {
        double value = channel / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static ResourceDictionary LoadMergedTheme()
    {
        EnsurePackSchemeRegistered();

        var merged = new ResourceDictionary();
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/MyCapture;component/Themes/{name}.xaml",
                    UriKind.Absolute),
            };
            merged.MergedDictionaries.Add(dictionary);
        }

        return merged;
    }

    /// <summary>
    /// Registers the WPF <c>pack://application</c> URI scheme. WPF registers it lazily the first
    /// time a WPF <see cref="System.Windows.Application"/> or WPF element is created, so when this
    /// test runs before any other WPF-touching test the scheme is absent and constructing a pack
    /// URI throws <see cref="UriFormatException"/>. Creating a throwaway WPF element forces
    /// PresentationFramework's module initializer to run, which performs the registration, making
    /// the loader independent of test execution order.
    /// </summary>
    private static void EnsurePackSchemeRegistered()
    {
        // Registers the base "pack" scheme (WindowsBase).
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

        // Instantiating any FrameworkElement initialises PresentationFramework, whose startup
        // registers the "pack://application" authority. The instance is intentionally discarded.
        _ = new System.Windows.FrameworkElement();
    }
}

/// <summary>
/// Runs a test body on a dedicated STA thread, required for WPF resource dictionaries and visuals.
/// Shared by the WPF-touching integration tests in this assembly.
/// </summary>
internal static class StaTestHost
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }
}
