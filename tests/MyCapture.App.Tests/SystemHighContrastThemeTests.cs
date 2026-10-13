using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A Windows contrast theme replaces every semantic token with the user's system colours, live,
/// and turning it off brings the chosen palette back. The real Windows setting is never touched:
/// the tests simulate it per thread.
/// </summary>
public sealed class SystemHighContrastThemeTests
{
    // Night sky, so the simulated system colours differ from every app palette.
    internal static readonly IReadOnlyDictionary<SystemColorRole, Color> NightSky = new Dictionary<SystemColorRole, Color>
    {
        [SystemColorRole.Window] = Color.FromRgb(0x00, 0x00, 0x00),
        [SystemColorRole.WindowText] = Color.FromRgb(0xFF, 0xFF, 0xFF),
        [SystemColorRole.ButtonFace] = Color.FromRgb(0x00, 0x00, 0x00),
        [SystemColorRole.ButtonText] = Color.FromRgb(0xFF, 0xEE, 0x32),
        [SystemColorRole.Highlight] = Color.FromRgb(0xD6, 0xB4, 0xFD),
        [SystemColorRole.HighlightText] = Color.FromRgb(0x2B, 0x2B, 0x2B),
        [SystemColorRole.GrayText] = Color.FromRgb(0xA6, 0xA6, 0xA6),
        [SystemColorRole.HotTrack] = Color.FromRgb(0x80, 0x80, 0xFF),
    };

    [Theory]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.Daylight)]
    [InlineData(AppTheme.Midnight)]
    [InlineData(AppTheme.HighContrast)]
    public void EverySemanticTokenTakesItsSystemColourAndTheChosenPaletteComesBack(AppTheme theme) => Simulate(() =>
    {
        ResourceDictionary resources = LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
        var consumer = new Border { Resources = resources };
        consumer.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        consumer.SetResourceReference(UIElement.EffectProperty, "Shadow.Card");
        SolidColorBrush chosen = Brush(resources, "Surface.Raised");
        Effect? shadow = consumer.Effect;
        if (theme != AppTheme.HighContrast) Assert.IsType<DropShadowEffect>(shadow);

        ThemeService.ApplyResources(theme, resources, systemHighContrast: true);

        foreach (string key in ThemeCatalog.ColorsFor(theme).Keys)
        {
            SolidColorBrush brush = Brush(resources, key);
            Assert.Equal(NightSky[HighContrastPalette.RoleFor(key)], brush.Color);
            Assert.Equal(0xFF, brush.Color.A);
            Assert.True(brush.IsFrozen, key);
        }

        Assert.Equal(NightSky[SystemColorRole.GrayText], Brush(resources, "Text.Disabled").Color);
        Assert.Equal(NightSky[SystemColorRole.Highlight], Brush(resources, "Border.Hover").Color);
        // The folder card's hover edge takes Highlight and its wash, which would dim that edge, turns clear.
        Assert.Equal(NightSky[SystemColorRole.Highlight], Brush(resources, "Border.FolderHover").Color);
        Assert.Equal(0, Brush(resources, "Surface.FolderHoverWash").Color.A);
        Assert.Equal(new Thickness(2), (Thickness)resources["Focus.RingThickness"]);
        Assert.Equal(2d, (double)resources["Focus.RingWidth"]);
        Assert.Equal(0, Assert.IsType<DropShadowEffect>(consumer.Effect).Opacity);
        // The shared brush that was showing is swapped, never recoloured in place (ThemeBrushes).
        Assert.NotSame(chosen, consumer.Background);
        Assert.Equal(NightSky[SystemColorRole.Window], ((SolidColorBrush)consumer.Background).Color);
        Assert.Equal(ToColor(ThemeCatalog.ColorsFor(theme)["Surface.Raised"]), chosen.Color);
        // The capture dimmer keeps its translucency so the screen stays visible beneath it.
        Assert.InRange(Brush(resources, "Overlay.Dimmer").Opacity, 0.01, 0.99);

        ThemeService.ApplyResources(theme, resources, systemHighContrast: false);

        foreach ((string key, ThemeColor color) in ThemeCatalog.ColorsFor(theme))
            Assert.Equal(ToColor(color), Brush(resources, key).Color);
        Assert.Equal(0, Brush(resources, "Border.Hover").Color.A);
        // App palettes alias the very Accent.Folder and Surface.Hover brushes, so the card looks as before.
        Assert.Same(resources["Accent.Folder"], resources["Border.FolderHover"]);
        Assert.Same(resources["Surface.Hover"], resources["Surface.FolderHoverWash"]);
        bool appContrast = theme == AppTheme.HighContrast;
        Assert.Equal(new Thickness(appContrast ? 2 : 1), (Thickness)resources["Focus.RingThickness"]);
        Assert.Equal(ToColor(ThemeCatalog.ColorsFor(theme)["Surface.Raised"]), ((SolidColorBrush)consumer.Background).Color);
        if (!appContrast) Assert.Same(shadow, consumer.Effect);
    });

    [Fact]
    public void WorkspaceRolePalettesAreReplacedByTheContrastThemeAndRestored() => Simulate(() =>
    {
        ResourceDictionary gallery = WorkspaceTheme.Create(WorkspaceRole.Gallery);
        WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.Workspace, systemHighContrast: false);
        Color inline = (Color)ColorConverter.ConvertFromString(WorkspaceTheme.WorkspaceOverrides(WorkspaceRole.Gallery)["Surface.Base"]);
        Assert.Equal(inline, Brush(gallery, "Surface.Base").Color);
        Assert.Equal((Color)ColorConverter.ConvertFromString("#7a8d86"), Brush(gallery, "Border.Control").Color);
        // The workspace's own Surface.Hover replaces the palette's; the folder card's wash follows it.
        Assert.Same(gallery["Surface.Hover"], gallery["Surface.FolderHoverWash"]);

        WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.Workspace, systemHighContrast: true);
        Assert.Equal(NightSky[SystemColorRole.Window], Brush(gallery, "Surface.Base").Color);
        Assert.Equal(NightSky[SystemColorRole.WindowText], Brush(gallery, "Text.Primary").Color);
        Assert.Equal(NightSky[SystemColorRole.ButtonText], Brush(gallery, "Border.Control").Color);
        Assert.Equal(NightSky[SystemColorRole.Highlight], Brush(gallery, "Accent.Default").Color);

        WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.Workspace, systemHighContrast: false);
        Assert.Equal(inline, Brush(gallery, "Surface.Base").Color);
    });

    [Fact]
    public void LiveRefreshAppliesAndRestoresTheContrastPaletteOnOpenContent() => Simulate(() =>
    {
        ResourceDictionary resources = LoadTheme();
        ThemeService.ApplyResources(ThemeService.Current, resources, systemHighContrast: false);
        var button = new Button { Content = "Sample", Resources = resources };
        button.Style = (Style)resources[typeof(Button)];
        button.ApplyTemplate();
        var chrome = (Border)button.Template.FindName("Chrome", button);
        var ring = (Border)button.Template.FindName("FocusRing", button);
        Assert.NotEqual(NightSky[SystemColorRole.ButtonText], ((SolidColorBrush)chrome.BorderBrush).Color);

        bool contrast = true;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherOperation on = ThemeService.ScheduleSystemThemeRefresh(dispatcher, resources, () => contrast);
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        Assert.Equal(DispatcherOperationStatus.Completed, on.Status);
        Assert.Equal(NightSky[SystemColorRole.ButtonText], ((SolidColorBrush)chrome.BorderBrush).Color);
        Assert.Equal(NightSky[SystemColorRole.ButtonFace], ((SolidColorBrush)chrome.Background).Color);
        Assert.Equal(NightSky[SystemColorRole.WindowText], ((SolidColorBrush)button.Foreground).Color);
        Assert.Equal(new Thickness(2), ring.BorderThickness);

        contrast = false;
        ThemeService.ScheduleSystemThemeRefresh(dispatcher, resources, () => contrast);
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        // ThemeService.Current is process-wide and other tests may switch it meanwhile, so the
        // restored outline is checked as "an app palette's control outline", not one value.
        Color restored = ((SolidColorBrush)chrome.BorderBrush).Color;
        Assert.Contains(Enum.GetValues<AppTheme>(), palette => ToColor(ThemeCatalog.ColorsFor(palette)["Border.Control"]) == restored);
        Assert.NotEqual(NightSky[SystemColorRole.ButtonText], restored);
        Assert.Equal(new Thickness(ThemeService.Current == AppTheme.HighContrast ? 2 : 1), ring.BorderThickness);
    });

    [Fact]
    public void CaptionFollowsTheSimulatedContrastSetting() => Simulate(() =>
    {
        // The window chrome reads the same switch as the palette, so both change together.
        Assert.True(ThemeService.IsSystemHighContrast);
        WindowChromePlan plan = ModernWindowChrome.ResolveChrome(AppTheme.Glass, WorkspaceRole.Gallery,
            ThemeService.IsSystemHighContrast);
        Assert.Null(plan.CaptionColor);
    });

    internal static void Simulate(Action body) => StaTestHost.Run(() =>
    {
        bool? previous = ThemeService.SystemHighContrastOverrideForTest;
        Func<SystemColorRole, Color>? previousColors = ThemeService.SystemColorsOverrideForTest;
        ThemeService.SystemHighContrastOverrideForTest = true;
        ThemeService.SystemColorsOverrideForTest = role => NightSky[role];
        try
        {
            body();
        }
        finally
        {
            ThemeService.SystemHighContrastOverrideForTest = previous;
            ThemeService.SystemColorsOverrideForTest = previousColors;
        }
    });

    internal static ResourceDictionary LoadTheme()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary();
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml", UriKind.Absolute),
            });
        }

        return resources;
    }

    private static SolidColorBrush Brush(ResourceDictionary resources, string key) =>
        Assert.IsType<SolidColorBrush>(resources[key]);

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
