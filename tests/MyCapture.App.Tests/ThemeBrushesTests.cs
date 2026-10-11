using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A theme change must not recolour a shared brush: WPF's composition engine can call a freed
/// listener when a brush with hundreds of text-run listeners changes (wpfgfx_cor3 0xC0000005 /
/// 0xC0000409 during the UX review sweep).
/// </summary>
public sealed class ThemeBrushesTests
{
    [Fact]
    public void ApplyingPalette_SwapsEveryTokenBrushWithoutRecolouringTheSharedOne() => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = LoadTokens();
        ThemeService.ApplyResources(AppTheme.Midnight, resources);
        var midnight = TokenBrushes(resources);
        Assert.NotEmpty(midnight);

        ThemeService.ApplyResources(AppTheme.Daylight, resources);

        IReadOnlyDictionary<string, ThemeColor> daylight = ThemeCatalog.ColorsFor(AppTheme.Daylight);
        foreach ((string key, SolidColorBrush shared) in midnight)
        {
            Assert.True(shared.IsFrozen, key);
            Assert.Equal(ToColor(ThemeCatalog.ColorsFor(AppTheme.Midnight)[key]), shared.Color);
            var swapped = (SolidColorBrush)resources[key];
            Assert.NotSame(shared, swapped);
            Assert.True(swapped.IsFrozen, key);
            Assert.Equal(ToColor(daylight[key]), swapped.Color);
        }
    });

    [Fact]
    public void Follow_GivesEachCallerItsOwnBrushThatTracksTheToken() => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = LoadTokens();
        ThemeService.ApplyResources(AppTheme.Midnight, resources);
        Brush first = ThemeBrushes.Follow(resources, "Text.Primary", Brushes.Red);
        Brush second = ThemeBrushes.Follow(resources, "Text.Primary", Brushes.Red);
        var frozen = (SolidColorBrush)ThemeBrushes.Follow(resources, "Text.Primary", Brushes.Red);
        frozen.Freeze();

        Assert.NotSame(first, second);
        Assert.NotSame(resources["Text.Primary"], first);
        Assert.False(first.IsFrozen);

        ThemeService.ApplyResources(AppTheme.Daylight, resources);

        Color daylight = ToColor(ThemeCatalog.ColorsFor(AppTheme.Daylight)["Text.Primary"]);
        Assert.Equal(daylight, ((SolidColorBrush)first).Color);
        Assert.Equal(daylight, ((SolidColorBrush)second).Color);
        Assert.Equal(ToColor(ThemeCatalog.ColorsFor(AppTheme.Midnight)["Text.Primary"]), frozen.Color);
        Assert.Same(Brushes.Red, ThemeBrushes.Follow(resources, "Missing.Token", Brushes.Red));
    });

    [Fact]
    public void Follow_KeepsTheTokenOpacity() => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = LoadTokens();
        ThemeService.ApplyResources(AppTheme.Midnight, resources);
        var token = (SolidColorBrush)resources["Overlay.Dimmer"];

        var follower = (SolidColorBrush)ThemeBrushes.Follow(resources, "Overlay.Dimmer", Brushes.Black);

        Assert.InRange(token.Opacity, 0.01, 0.99);
        Assert.Equal(token.Opacity, follower.Opacity);
        Assert.Equal(token.Color, follower.Color);
    });

    [Fact]
    public void WorkspaceOverrides_AreSwappedNotRecoloured() => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = LoadTokens();
        WorkspaceTheme.Apply(resources, WorkspaceRole.Gallery, AppTheme.Workspace);
        var muted = (SolidColorBrush)resources["Text.Muted"];
        Brush follower = ThemeBrushes.Follow(resources, "Text.Muted", Brushes.Red);
        Color workspaceMuted = (Color)ColorConverter.ConvertFromString(
            WorkspaceTheme.WorkspaceOverrides(WorkspaceRole.Gallery)["Text.Muted"]);
        Assert.True(muted.IsFrozen);
        Assert.Equal(workspaceMuted, muted.Color);

        WorkspaceTheme.Apply(resources, WorkspaceRole.Gallery, AppTheme.Midnight);

        Color midnightMuted = ToColor(ThemeCatalog.ColorsFor(AppTheme.Midnight)["Text.Muted"]);
        Assert.Equal(workspaceMuted, muted.Color);
        Assert.Equal(midnightMuted, ((SolidColorBrush)resources["Text.Muted"]).Color);
        Assert.Equal(midnightMuted, ((SolidColorBrush)follower).Color);
    });

    private static ResourceDictionary LoadTokens()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml", UriKind.Absolute),
        });
        return resources;
    }

    private static Dictionary<string, SolidColorBrush> TokenBrushes(ResourceDictionary resources) =>
        ThemeCatalog.ColorsFor(AppTheme.Midnight).Keys
            .Where(key => resources[key] is SolidColorBrush)
            .ToDictionary(key => key, key => (SolidColorBrush)resources[key]);

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
