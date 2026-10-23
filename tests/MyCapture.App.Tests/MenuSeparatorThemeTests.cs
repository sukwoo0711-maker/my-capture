using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// WPF gives a Separator inside a menu the <see cref="MenuItem.SeparatorStyleKey"/> style instead
/// of the implicit Separator style. The theme defines that key, so tray, editor and library menu
/// rules use the theme's subtle border rather than the system theme's light grey line.
/// </summary>
public sealed class MenuSeparatorThemeTests
{
    [Theory]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.GlassLight)]
    public void MenuSeparatorsUseTheThemeRule(AppTheme theme) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
        Style style = Assert.IsType<Style>(resources[MenuItem.SeparatorStyleKey]);
        Assert.Equal(typeof(Separator), style.TargetType);
        Assert.Same(resources[typeof(Separator)], style.BasedOn);

        var separator = new Separator();
        var menu = new Menu
        {
            Resources = resources,
            Items = { new MenuItem { Header = "One" }, separator, new MenuItem { Header = "Two" } },
        };
        var window = new Window
        {
            Content = menu,
            Width = 240,
            Height = 120,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Same(style, separator.Style);
            Border rule = Assert.IsType<Border>(VisualTreeHelper.GetChild(separator, 0));
            Assert.Equal(((SolidColorBrush)resources["Border.Subtle"]).Color, ((SolidColorBrush)rule.Background).Color);
        }
        finally
        {
            window.Close();
        }
    });
}
