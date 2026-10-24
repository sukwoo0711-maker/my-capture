using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ButtonAppearanceTests
{
    [Theory]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.Midnight, false)]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.GlassLight, true)]
    [InlineData(AppTheme.Midnight, true)]
    public void ChromeKeepsTheHitTargetAndContrastThemeWhileSeparatingButtonTiers(AppTheme theme, bool systemContrast) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        Assert.Equal(FontWeights.Normal, resources["Button.FontWeight"]);
        Assert.Equal(new Thickness(0, 4, 0, 4), resources["Button.ChromeInset"]);
        Assert.Equal(new Thickness(0, 2, 0, 2), resources["Button.ChromeInset.Compact"]);
        // Apply the opposite state first: live theme changes must reset every button resource.
        ThemeService.ApplyResources(theme, resources, systemHighContrast: !systemContrast);
        ThemeService.ApplyResources(theme, resources, systemHighContrast: systemContrast);
        bool contrast = systemContrast || theme == AppTheme.HighContrast;
        var sharedOutline = resources["Border.Control"];
        var sharedFill = resources["Surface.Overlay"];
        ThemeService.ApplyButtonResources(resources, theme, systemContrast);
        Assert.Same(sharedOutline, resources["Border.Control"]);
        Assert.Same(sharedFill, resources["Surface.Overlay"]);
        if (contrast)
        {
            Assert.Same(sharedOutline, resources["Button.BorderBrush"]);
            Assert.Same(sharedFill, resources["Button.Fill"]);
        }
        else
        {
            AssertOutline(Assert.IsAssignableFrom<Brush>(resources["Button.BorderBrush"]), theme);
            if (AppThemeNames.IsLight(theme))
                Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(resources["Button.Fill"]).Color);
            else
            {
                Assert.Equal(Color.FromRgb(0x25, 0x32, 0x3C), Assert.IsType<SolidColorBrush>(resources["Button.Fill"]).Color);
            }
        }

        foreach (string style in new[] { "Button.Secondary", "Button.Command", "Button.Ghost", "Button.Primary", "Button.Danger" })
        foreach (bool compact in new[] { false, true })
        {
            var button = new Button { Content = "저장", Resources = resources, Style = (Style)resources[style], Width = 100 };
            if (compact)
            {
                button.SetResourceReference(FrameworkElement.HeightProperty, "CommandButton.Height.Compact");
                button.SetResourceReference(FrameworkElement.MinHeightProperty, "Size.HitTarget.Compact");
                button.SetResourceReference(Control.PaddingProperty, "CommandButton.Padding.Compact");
            }
            double height = compact ? 36 : 44;
            double inset = contrast ? 0 : compact ? 2 : 4;
            double focusGap = style == "Button.Primary" ? 2 : 1;
            button.ApplyTemplate();
            button.Measure(new Size(100, height));
            button.Arrange(new Rect(0, 0, 100, height));
            Assert.Equal(height, button.Height);
            Assert.Equal(compact ? 32 : style == "Button.Command" ? 44 : 36, button.MinHeight);
            Assert.Equal(height, button.ActualHeight);
            Assert.Equal(compact ? new Thickness(10, 0, 10, 0) : new Thickness(10, 4, 10, 4), button.Padding);
            Assert.Equal(contrast || style is "Button.Primary" or "Button.Danger" ? FontWeights.SemiBold : FontWeights.Normal, button.FontWeight);
            var chrome = Assert.IsType<Border>(button.Template.FindName("Chrome", button));
            foreach (string part in new[] { "Chrome", "HoverLayer", "PressedLayer" })
            {
                var layer = Assert.IsType<Border>(button.Template.FindName(part, button));
                Assert.Equal(new Thickness(0, inset, 0, inset), layer.Margin);
                Assert.Equal(height - 2 * inset, layer.ActualHeight);
            }
            if (style is "Button.Secondary" or "Button.Command" or "Button.Danger")
                Assert.Equal(new Thickness(1), chrome.BorderThickness);
            var ring = Assert.IsType<Border>(button.Template.FindName("FocusRing", button));
            Assert.Equal(new Thickness(focusGap, inset + focusGap, focusGap, inset + focusGap), ring.Margin);
            Assert.False(ring.IsHitTestVisible);
            var root = Assert.IsType<Grid>(VisualTreeHelper.GetParent(chrome));
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(root.Background).Color);
            Assert.NotNull(VisualTreeHelper.HitTest(button, new Point(50, 1)));
            Assert.NotNull(VisualTreeHelper.HitTest(button, new Point(50, height - 1)));
        }
        Assert.Equal(36d, resources["Size.HitTarget"]);
        Assert.Equal(32d, resources["Size.HitTarget.Compact"]);
    });

    internal static void AssertOutline(Brush brush, AppTheme theme)
    {
        var gradient = Assert.IsType<LinearGradientBrush>(brush);
        bool light = AppThemeNames.IsLight(theme);
        Color top = (Color)ColorConverter.ConvertFromString(light ? "#E2E6EC" : "#3A4853");
        Color bottom = (Color)ColorConverter.ConvertFromString(light ? "#B8C0CB" : "#1E2830");
        Assert.Equal(new Point(0, 0), gradient.StartPoint);
        Assert.Equal(new Point(0, 1), gradient.EndPoint);
        Assert.Equal(new[] { 0d, 0.8, 1d }, gradient.GradientStops.Select(stop => stop.Offset));
        Assert.Equal(new[] { top, top, bottom }, gradient.GradientStops.Select(stop => stop.Color));
        Assert.True(gradient.IsFrozen);
    }
}
