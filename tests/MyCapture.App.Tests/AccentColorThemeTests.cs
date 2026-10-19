using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class AccentColorThemeTests
{
    [Fact]
    public void RedRoleBrushesMeetContrastOnEveryThemeAndWorkspaceSurface() => StaTestHost.Run(() =>
    {
        foreach ((AppTheme theme, WorkspaceRole? role) in ControlPalettes())
        {
            if (theme == AppTheme.HighContrast) continue;
            ResourceDictionary resources = LoadTokens();
            ApplyPalette(resources, theme, role, AppAccent.Red);
            foreach (string surface in new[] { "Surface.Canvas", "Surface.Base", "Surface.Raised", "Surface.Overlay",
                         "Surface.Floating", "Surface.Sunken", "Surface.Hover", "Surface.Pressed", "Accent.Subtle" })
            foreach (Color backdrop in new[] { Colors.Black, Colors.White })
            {
                ThemeColor background = ThemeContrast.Composite(BrushColor((Brush)resources[surface]), ToThemeColor(backdrop));
                foreach (string token in new[] { "Accent.Foreground", "Accent.Cool", "Accent.Indicator", "Border.Focus",
                             "Border.Accent", "Border.SelectedTool", "Overlay.SelectionBorder" })
                {
                    double minimum = token is "Accent.Foreground" or "Accent.Cool" ? 4.5 : 3;
                    double ratio = ThemeContrast.Ratio(BrushColor((Brush)resources[token]), background);
                    Assert.True(ratio >= minimum, $"{theme}/{role}/{token}/{surface}/{backdrop}: {ratio:F3}");
                }
            }
        }
    });

    [Fact]
    public void LegacyAccentSwitchSwapsFrozenBrushesAndKeepsFixedPalette() => StaTestHost.Run(() =>
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            ResourceDictionary resources = LoadTokens();
            ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
            var original = ThemeCatalog.ColorsFor(theme).Keys.ToDictionary(key => key, key => (SolidColorBrush)resources[key]);
            byte[] originalPixels = RenderTokens(resources);
            Brush follower = ThemeBrushes.Follow(resources, "Accent.Default", Brushes.Black);
            ThemeService.ApplyResources(theme, resources, systemHighContrast: false, accent: AppAccent.Red);
            foreach ((string key, SolidColorBrush shared) in original)
            {
                Assert.True(shared.IsFrozen, key);
                Assert.Equal(ToColor(ThemeCatalog.ColorsFor(ThemeService.ResolveTheme(theme))[key]), shared.Color);
                Assert.NotSame(shared, resources[key]);
                Assert.True(((SolidColorBrush)resources[key]).IsFrozen, key);
            }
            Assert.Equal(((SolidColorBrush)resources["Accent.Default"]).Color, ((SolidColorBrush)follower).Color);
            ThemeService.ApplyResources(theme, resources, systemHighContrast: false, accent: AppAccent.Violet);
            Assert.Equal(originalPixels, RenderTokens(resources));
            Assert.Equal(((SolidColorBrush)resources["Text.Primary"]).Color,
                ((SolidColorBrush)resources["Accent.Foreground"]).Color);
        }
    });

    [Fact]
    public void WorkspaceRoleSurfacesKeepRedForegroundFocusAndFillTextReadable() => StaTestHost.Run(() =>
    {
        foreach (WorkspaceRole role in Enum.GetValues<WorkspaceRole>())
        {
            ResourceDictionary resources = LoadTokens();
            WorkspaceTheme.Apply(resources, role, AppTheme.Workspace, false, AppAccent.Violet);
            byte[] original = RenderTokens(resources);
            WorkspaceTheme.Apply(resources, role, AppTheme.Workspace, false, AppAccent.Red);
            foreach (string surface in new[] { "Surface.Canvas", "Surface.Base", "Surface.Raised", "Surface.Overlay", "Surface.Hover", "Surface.Pressed", "Accent.Subtle" })
            {
                Assert.True(Ratio(resources, "Accent.Foreground", surface) >= 4.5, $"{role} foreground on {surface}");
                Assert.True(Ratio(resources, "Accent.Cool", surface) >= 4.5, $"{role} cool on {surface}");
                Assert.True(Ratio(resources, "Border.Focus", surface) >= 3, $"{role} focus on {surface}");
            }
            foreach (string fill in new[] { "Accent.Default", "Accent.Hover", "Accent.Pressed" })
                Assert.True(Ratio(resources, "Text.OnAccent", fill) >= 4.5, $"{role} text on {fill}");
            WorkspaceTheme.Apply(resources, role, AppTheme.Workspace, false, AppAccent.Violet);
            Assert.Equal(original, RenderTokens(resources));
        }
    });

    [Fact]
    public void SystemContrastOverridesBothAccentsWithoutMutatingOldBrushes() => StaTestHost.Run(() =>
    {
        ThemeService.SystemColorsOverrideForTest = role => role switch
        {
            SystemColorRole.Highlight => Colors.Yellow,
            SystemColorRole.HighlightText or SystemColorRole.Window or SystemColorRole.ButtonFace => Colors.Black,
            _ => Colors.White,
        };
        try
        {
            foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            {
                ResourceDictionary resources = LoadTokens();
                ThemeService.ApplyResources(theme, resources, systemHighContrast: true, accent: AppAccent.Violet);
                byte[] violet = RenderTokens(resources);
                var old = (SolidColorBrush)resources["Accent.Default"];
                ThemeService.ApplyResources(theme, resources, systemHighContrast: true, accent: AppAccent.Red);
                Assert.Equal(violet, RenderTokens(resources));
                Assert.Equal(Colors.Yellow, old.Color);
                Assert.NotSame(old, resources["Accent.Default"]);
                Assert.Equal(Colors.White, ((SolidColorBrush)resources["Accent.Foreground"]).Color);
            }
        }
        finally { ThemeService.SystemColorsOverrideForTest = null; }
    });

    private static ResourceDictionary LoadTokens()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml") });
        return resources;
    }

    private static byte[] RenderTokens(ResourceDictionary resources)
    {
        var panel = new StackPanel { Width = 320, Resources = resources };
        foreach (string key in ThemeCatalog.ColorsFor(AppTheme.Midnight).Keys.Append("Accent.Foreground"))
        {
            var swatch = new Border { Width = 320, Height = 8 };
            swatch.SetResourceReference(Border.BackgroundProperty, key);
            panel.Children.Add(swatch);
        }
        panel.Measure(new Size(320, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(320, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static double Ratio(ResourceDictionary resources, string foreground, string surface)
    {
        Color a = ((SolidColorBrush)resources[foreground]).Color;
        Color b = ((SolidColorBrush)resources[surface]).Color;
        return ThemeContrast.Ratio(ThemeColor.Rgb(a.R, a.G, a.B), ThemeColor.Rgb(b.R, b.G, b.B));
    }

    private static Color ToColor(ThemeColor value) => Color.FromArgb(value.A, value.R, value.G, value.B);
}
