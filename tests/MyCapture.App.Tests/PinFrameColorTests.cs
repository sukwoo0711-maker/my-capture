using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Pin outlines retain the brand while video layer selection stays neutral.
/// Rest opacity and live theme changes are retained; contrast palettes keep their system pair.
/// </summary>
public sealed class PinFrameColorTests
{
    private static readonly AppTheme[] PaletteThemes =
        [AppTheme.Midnight, AppTheme.Glass, AppTheme.Workspace, AppTheme.Daylight, AppTheme.GlassLight];

    [Theory]
    [InlineData(AppTheme.Glass, AppAccent.Red, 0xE2, 0x4F, 0x47)]
    [InlineData(AppTheme.Glass, AppAccent.Violet, 0xE2, 0x4F, 0x47)]
    [InlineData(AppTheme.GlassLight, AppAccent.Red, 0xCA, 0x3E, 0x36)]
    [InlineData(AppTheme.GlassLight, AppAccent.Violet, 0xCA, 0x3E, 0x36)]
    public void OutlineRestsInAQuietAccentAndTurnsOpaqueWhenEmphasized(AppTheme theme, AppAccent accent, byte r, byte g, byte b) =>
        StaTestHost.Run(() =>
        {
            ResourceDictionary resources = LoadTokens();
            ThemeService.ApplyResources(theme, resources, systemHighContrast: false, accent: accent);

            Assert.Equal(Color.FromArgb(0x99, r, g, b), Resolve(resources, contrast: false, emphasized: false));
            Assert.Equal(Color.FromArgb(0xFF, r, g, b), Resolve(resources, contrast: false, emphasized: true));
        });

    [Fact]
    public void EveryPaletteFramesPinsInBrandRedIndependentlyOfSelectionInk() => StaTestHost.Run(() =>
    {
        foreach (AppTheme theme in PaletteThemes)
        foreach (AppAccent accent in Enum.GetValues<AppAccent>())
        {
            ResourceDictionary resources = LoadTokens();
            ThemeService.ApplyResources(theme, resources, systemHighContrast: false, accent: accent);
            IReadOnlyDictionary<string, ThemeColor> palette = ThemeCatalog.ColorsFor(theme, accent);
            Color emphasized = Resolve(resources, contrast: false, emphasized: true);
            Color rest = Resolve(resources, contrast: false, emphasized: false);

            Assert.Equal(ToColor(palette["Pin.Frame"]), emphasized);
            Assert.Equal(Color.FromArgb(0x99, emphasized.R, emphasized.G, emphasized.B), rest);
            Assert.NotEqual(ToColor(palette["Overlay.SelectionBorder"]), emphasized);
            Assert.Equal(ToColor(palette["Text.Primary"]), ToColor(palette["Overlay.SelectionBorder"]));
            Assert.True(emphasized.R > emphasized.G && emphasized.R > emphasized.B);
        }
    });

    [Fact]
    public void ContrastThemesKeepTheOpaqueSystemPair() => StaTestHost.Run(() =>
    {
        foreach (AppAccent accent in Enum.GetValues<AppAccent>())
        {
            ResourceDictionary resources = LoadTokens();
            ThemeService.ApplyResources(AppTheme.HighContrast, resources, systemHighContrast: false, accent: accent);
            Assert.Equal(Colors.White, Resolve(resources, contrast: true, emphasized: false));
            Assert.Equal(Color.FromRgb(0xFF, 0xD0, 0x00), Resolve(resources, contrast: true, emphasized: true));
        }

        Color windowText = Color.FromRgb(0xFF, 0xFF, 0x00);
        Color highlight = Color.FromRgb(0x1A, 0xEB, 0xFF);
        ThemeService.SystemColorsOverrideForTest = role => role switch
        {
            SystemColorRole.WindowText => windowText,
            SystemColorRole.Highlight => highlight,
            _ => Colors.Black,
        };
        try
        {
            foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            foreach (AppAccent accent in Enum.GetValues<AppAccent>())
            {
                ResourceDictionary resources = LoadTokens();
                ThemeService.ApplyResources(theme, resources, systemHighContrast: true, accent: accent);
                Assert.Equal(windowText, Resolve(resources, contrast: true, emphasized: false));
                Assert.Equal(highlight, Resolve(resources, contrast: true, emphasized: true));
            }
        }
        finally
        {
            ThemeService.SystemColorsOverrideForTest = null;
        }
    });

    [Fact]
    public void MissingTokensFallBackToTheDefaultPalette()
    {
        Assert.Equal(Color.FromRgb(0xE2, 0x4F, 0x47), PinFrameColor.FallbackAccent);
        Assert.Equal(Color.FromArgb(0x99, 0xE2, 0x4F, 0x47), PinFrameColor.Resolve(null, null, contrast: false, emphasized: false));
        Assert.Equal(PinFrameColor.FallbackAccent, PinFrameColor.Resolve(null, null, contrast: false, emphasized: true));
        Assert.Equal(PinFrameColor.FallbackNeutral, PinFrameColor.Resolve(null, null, contrast: true, emphasized: false));
        Assert.Equal(PinFrameColor.FallbackAccent,
            PinFrameColor.Resolve(new LinearGradientBrush(Colors.Black, Colors.White, 0), null, contrast: false, emphasized: true));
    }

    [Fact]
    public Task OpenPinFollowsHoverActivationThemeAccentAndContrast() =>
        SettingsThemePreviewTests.RunIsolated(nameof(OpenPinFollowsHoverActivationThemeAccentAndContrast), VerifyOpenPin,
            typeof(PinFrameColorTests));

    private static void VerifyOpenPin()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        ThemeService.SystemHighContrastOverrideForTest = false;
        ThemeService.ApplyFromSettings(AppThemeNames.Glass, null, AppAccentNames.Red);
        BitmapSource image = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, new byte[40 * 30 * 4], 40 * 4);
        var pin = new PinWindow(PinContent.FromImage(image), new PinViewState(40, 30, 1, 1, 0.1), -10000, -10000, () => new PinSettings());
        try
        {
            Assert.Equal(Expected(AppTheme.Glass, AppAccent.Red, emphasized: false), pin.FrameColorForTest);
            Hover(pin, Mouse.MouseEnterEvent);
            Assert.Equal(Expected(AppTheme.Glass, AppAccent.Red, emphasized: true), pin.FrameColorForTest);
            Hover(pin, Mouse.MouseLeaveEvent);
            Assert.Equal(Expected(AppTheme.Glass, AppAccent.Red, emphasized: false), pin.FrameColorForTest);
            pin.SetChromeActiveForTest(true);
            Assert.Equal(Expected(AppTheme.Glass, AppAccent.Red, emphasized: true), pin.FrameColorForTest);
            pin.SetChromeActiveForTest(false);
            Assert.Equal(Expected(AppTheme.Glass, AppAccent.Red, emphasized: false), pin.FrameColorForTest);

            // Settings apply a new accent and theme while the pin stays open.
            ThemeService.ApplyFromSettings(AppThemeNames.Glass, null, AppAccentNames.Violet);
            Assert.Equal(Expected(AppTheme.Glass, AppAccent.Violet, emphasized: false), pin.FrameColorForTest);
            ThemeService.ApplyFromSettings(AppThemeNames.GlassLight, null, AppAccentNames.Red);
            Assert.Equal(Expected(AppTheme.GlassLight, AppAccent.Red, emphasized: false), pin.FrameColorForTest);
            Hover(pin, Mouse.MouseEnterEvent);
            Assert.Equal(Expected(AppTheme.GlassLight, AppAccent.Red, emphasized: true), pin.FrameColorForTest);
            Hover(pin, Mouse.MouseLeaveEvent);

            ThemeService.ApplyFromSettings(AppThemeNames.HighContrast, null, AppAccentNames.Red);
            Assert.Equal(Colors.White, pin.FrameColorForTest);
            Hover(pin, Mouse.MouseEnterEvent);
            Assert.Equal(Color.FromRgb(0xFF, 0xD0, 0x00), pin.FrameColorForTest);
            Hover(pin, Mouse.MouseLeaveEvent);

            // A Windows contrast theme turning on and off.
            Color windowText = Color.FromRgb(0xFF, 0xFF, 0x00);
            Color highlight = Color.FromRgb(0x1A, 0xEB, 0xFF);
            ThemeService.SystemColorsOverrideForTest = role => role switch
            {
                SystemColorRole.WindowText => windowText,
                SystemColorRole.Highlight => highlight,
                _ => Colors.Black,
            };
            ThemeService.SystemHighContrastOverrideForTest = true;
            ThemeService.ApplyFromSettings(AppThemeNames.Glass, null, AppAccentNames.Red);
            Assert.Equal(windowText, pin.FrameColorForTest);
            pin.SetChromeActiveForTest(true);
            Assert.Equal(highlight, pin.FrameColorForTest);
            pin.SetChromeActiveForTest(false);
            ThemeService.SystemHighContrastOverrideForTest = false;
            ThemeService.SystemColorsOverrideForTest = null;
            ThemeService.ApplyFromSettings(AppThemeNames.Glass, null, AppAccentNames.Red);
            Assert.Equal(Expected(AppTheme.Glass, AppAccent.Red, emphasized: false), pin.FrameColorForTest);
        }
        finally
        {
            pin.Close();
            ThemeService.SystemHighContrastOverrideForTest = null;
            ThemeService.SystemColorsOverrideForTest = null;
            FluidMotion.AnimationsEnabledOverrideForTest = null;
            application.Shutdown();
        }
    }

    [Fact]
    public void VideoLayerSelectionStaysNeutralAndFollowsAPaletteChange() => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = LoadTokens();
        ThemeService.ApplyResources(AppTheme.Glass, resources, systemHighContrast: false, accent: AppAccent.Red);
        var document = VideoEditDocument.CreateFor(320, 240, 3_000);
        var layer = new FrameEditLayer { StartMs = 0, EndMs = 1_000, Bounds = new VideoLayerBounds(0.25, 0.25, 0.5, 0.5) };
        document.FrameEditLayers.Add(layer);
        var canvas = new VideoLayerCanvas();
        canvas.SetDocument(document);
        canvas.Select(layer.Id);
        var host = new Border { Width = 320, Height = 240, Resources = resources, Child = canvas };

        AssertSelectionColour(host, ((SolidColorBrush)resources["Text.Primary"]).Color, "dark neutral selection");
        Assert.Same(resources["Text.Primary"], canvas.SelectionBrush);

        ThemeService.ApplyResources(AppTheme.GlassLight, resources, systemHighContrast: false, accent: AppAccent.Violet);
        AssertSelectionColour(host, ((SolidColorBrush)resources["Text.Primary"]).Color, "light neutral selection");
        Assert.Same(resources["Surface.Raised"], canvas.HandleFillBrush);
    });

    /// <summary>The selected layer's top edge (y = 60 in a 320×240 canvas) is drawn in <paramref name="expected"/>.</summary>
    private static void AssertSelectionColour(FrameworkElement host, Color expected, string label)
    {
        host.Measure(new Size(320, 240));
        host.Arrange(new Rect(0, 0, 320, 240));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(320, 240, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[320 * 240 * 4];
        converted.CopyPixels(pixels, 320 * 4, 0);
        bool found = false;
        foreach (int y in new[] { 59, 60 })
        {
            int index = ((y * 320) + 160) * 4;
            if (pixels[index + 3] == 0) continue;
            Color shown = Color.FromRgb(pixels[index + 2], pixels[index + 1], pixels[index]);
            Assert.True(Math.Abs(shown.R - expected.R) <= 3 && Math.Abs(shown.G - expected.G) <= 3 && Math.Abs(shown.B - expected.B) <= 3,
                $"{label}: the selection edge shows {shown}, not {expected}");
            found = true;
        }

        Assert.True(found, $"{label}: no selection edge was drawn at the layer's top");
    }

    private static Color Expected(AppTheme theme, AppAccent accent, bool emphasized)
    {
        ThemeColor focus = ThemeCatalog.ColorsFor(theme, accent)[PinFrameColor.AccentKey];
        return Color.FromArgb(emphasized ? focus.A : (byte)0x99, focus.R, focus.G, focus.B);
    }

    private static void Hover(PinWindow pin, RoutedEvent routedEvent) =>
        pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent });

    private static Color Resolve(ResourceDictionary resources, bool contrast, bool emphasized) =>
        PinFrameColor.Resolve(resources[PinFrameColor.AccentKey] as Brush, resources[PinFrameColor.NeutralKey] as Brush, contrast, emphasized);

    private static ResourceDictionary LoadTokens()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml") });
        return resources;
    }

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static double Hue(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        if (delta == 0) return 0;
        double hue = max == r ? (g - b) / delta % 6 : max == g ? ((b - r) / delta) + 2 : ((r - g) / delta) + 4;
        return (hue * 60 + 360) % 360;
    }
}
