using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Exercise compiled resources: parsing the source XAML does not expose BAML key aliasing.</summary>
public sealed class CompiledControlResourceTests
{
    [Fact]
    public void NumericAndGeometryKeysKeepTheirOwnValuesAfterCompilation() => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        foreach ((string key, double expected) in new[]
        {
            ("Button.DisabledOpacity", .46),
            ("Button.Height", 36d),
            ("CommandButton.Height.Standard", 44d),
            ("CommandButton.Height.Compact", 36d),
            ("Size.HitTarget", 36d),
            ("Size.HitTarget.Compact", 32d),
            ("Space.Xs", 4d),
            ("Space.Sm", 8d),
            ("Space.Md", 12d),
            ("Space.Lg", 16d),
            ("Space.Xl", 24d),
            ("Space.Xxl", 32d),
        })
            Assert.Equal(expected, Assert.IsType<double>(resources[key]));

        foreach ((string key, double expected) in new[]
        {
            ("Combo.Radius", 6d),
            ("Radius.Control", 6d),
            ("Radius.Floating", 8d),
            ("Radius.Content", 12d),
            ("Radius.Sm", 6d),
            ("Radius.Md", 8d),
            ("Radius.Lg", 12d),
        })
            Assert.Equal(new CornerRadius(expected), Assert.IsType<CornerRadius>(resources[key]));
    });

    [Theory]
    [InlineData("Button.Secondary")]
    [InlineData("Button.Command")]
    [InlineData("Button.Ghost")]
    [InlineData("Button.Danger")]
    [InlineData("Button.InputField")]
    public void DisabledButtonsRemainValidWhenTheirTemplateIsReplaced(string styleKey) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        var button = new Button
        {
            Content = "Save",
            Resources = resources,
            Style = (Style)resources[styleKey],
        };
        button.ApplyTemplate();
        Assert.Equal(FoundationMetrics.ControlHeight, button.Height);
        Assert.Equal(1d, button.Opacity);

        button.IsEnabled = false;
        Assert.Equal(.46, button.Opacity);
        button.IsEnabled = true;
        Assert.Equal(1d, button.Opacity);
        button.IsEnabled = false;
        Assert.Equal(.46, button.Opacity);

        // Screen construction and theme changes can replace a template while the command
        // is disabled. The old disabled trigger must leave a valid default opacity behind.
        button.Style = null;
        Assert.Equal(1d, button.Opacity);
        button.Style = (Style)resources[styleKey];
        button.ApplyTemplate();
        Assert.Equal(.46, button.Opacity);
        button.IsEnabled = true;
        Assert.Equal(1d, button.Opacity);
    });
}
