using System.Threading;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Verifies the shipped theme dictionaries load together and expose every resource used by the
/// warm-yellow/charcoal UI. Missing resources otherwise fail only when a screen is first shown.
/// </summary>
public sealed class ThemeResourceAvailabilityTests
{
    private static readonly string[] IconKeys =
    [
        "Icon.Select", "Icon.Rectangle", "Icon.Arrow", "Icon.Pen", "Icon.Text", "Icon.Image",
        "Icon.Undo", "Icon.Redo", "Icon.Delete", "Icon.Copy", "Icon.Check", "Icon.Close",
        "Icon.Save", "Icon.SaveAs", "Icon.ChevronDown", "Icon.More", "Icon.Settings", "Icon.Capture",
        "Icon.Shortcuts", "Icon.Storage", "Icon.Export", "Icon.Pin", "Icon.Edit", "Icon.Ocr", "Icon.Search",
        "Icon.ZoomIn", "Icon.ZoomOut",
    ];

    private static readonly string[] BrushKeys =
    [
        "Surface.Canvas", "Surface.Base", "Surface.Raised", "Surface.Overlay", "Surface.Sunken",
        "Surface.Hover", "Text.Primary", "Text.Secondary", "Text.Muted", "Text.OnAccent",
        "Accent.Default", "Accent.Hover", "Accent.Pressed", "Accent.Subtle", "Accent.Cool",
        "Border.Subtle", "Border.Focus", "Border.Accent", "Overlay.SelectionBorder",
        "State.Warning", "State.DangerHover",
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
            Assert.IsType<SolidColorBrush>(theme["Accent.Gradient"]);
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
    public void WarmPaletteHasOrderedSurfacesAndAccessibleTextPairings()
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

            AssertContrastAtLeast(theme, "Text.Primary", "Surface.Base", 7.0);
            AssertContrastAtLeast(theme, "Text.Secondary", "Surface.Base", 4.5);
            AssertContrastAtLeast(theme, "Text.Muted", "Surface.Base", 4.5);
            AssertContrastAtLeast(theme, "Text.Muted", "Surface.Sunken", 4.5);
            AssertContrastAtLeast(theme, "Text.Muted", "Surface.Raised", 4.5);
        });
    }

    [Fact]
    public void YellowActionsUseDarkInkWithAaContrastInEveryState()
    {
        StaTestHost.Run(() =>
        {
            ResourceDictionary theme = LoadMergedTheme();
            Color ink = Assert.IsType<Color>(theme["Primitive.Ink"]);
            Color onAccent = BrushFor(theme, "Text.OnAccent").Color;

            Assert.Equal(ink, onAccent);
            Assert.NotEqual(Colors.White, onAccent);
            AssertContrastAtLeast(theme, "Text.OnAccent", "Accent.Default", 4.5);
            AssertContrastAtLeast(theme, "Text.OnAccent", "Accent.Hover", 4.5);
            AssertContrastAtLeast(theme, "Text.OnAccent", "Accent.Pressed", 4.5);
        });
    }

    [Fact]
    public void FocusAndSelectionSemanticsUseTheBrightYellowPrimitive()
    {
        StaTestHost.Run(() =>
        {
            ResourceDictionary theme = LoadMergedTheme();
            Color brightYellow = Assert.IsType<Color>(theme["Primitive.Yellow300"]);

            Assert.Equal(brightYellow, BrushFor(theme, "Accent.Cool").Color);
            Assert.Equal(brightYellow, BrushFor(theme, "Border.Focus").Color);
            Assert.Equal(brightYellow, BrushFor(theme, "Overlay.SelectionBorder").Color);
            AssertContrastAtLeast(theme, "Border.Focus", "Surface.Base", 3.0);
            AssertContrastAtLeast(theme, "Border.Focus", "Surface.Raised", 3.0);
            AssertContrastAtLeast(theme, "Overlay.SelectionBorder", "Surface.Canvas", 3.0);
        });
    }

    private static void AssertContrastAtLeast(
        ResourceDictionary theme,
        string foregroundKey,
        string backgroundKey,
        double minimum)
    {
        double ratio = ContrastRatio(
            BrushFor(theme, foregroundKey).Color,
            BrushFor(theme, backgroundKey).Color);
        Assert.True(ratio >= minimum,
            $"{foregroundKey} on {backgroundKey} contrast was {ratio:F2}:1; expected at least {minimum:F1}:1.");
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

    private static void EnsurePackSchemeRegistered()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
    }
}

/// <summary>Runs WPF resource and visual assertions on a dedicated STA thread.</summary>
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
