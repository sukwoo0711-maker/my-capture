using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The themed TextBox insets its text once. TextBoxBase applies Padding inside PART_ContentHost,
/// so a template margin on the host doubled the inset and made every box about 53 DIP tall.
/// </summary>
public sealed class TextBoxTemplateTests
{
    [Theory]
    [InlineData("파일 이름")]
    [InlineData("MyCapture 2026")]
    public void ThemedTextBoxIsOneHitTargetTallWithASingleInset(string text) => ControlStateMotionTests.Run(false, (theme, host) =>
    {
        // Windows use FontSize.Body (14) through Window.Standard.
        var normal = new TextBox { Text = text, FontSize = 14, Style = (Style)theme[typeof(TextBox)] };
        // The compact form other screens use: library search, dialog fields.
        var compact = new TextBox
        {
            Text = text, FontSize = 14, Style = (Style)theme[typeof(TextBox)], MinHeight = 32, Padding = new Thickness(8, 4, 8, 4),
        };
        host.Children.Add(normal);
        host.Children.Add(compact);
        ControlStateMotionTests.Drain(host);

        Assert.Equal(36, normal.ActualHeight, 1);
        Assert.Equal(32, compact.ActualHeight, 1);
        foreach (TextBox box in new[] { normal, compact })
        {
            var contentHost = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
            Assert.Equal(new Thickness(0), contentHost.Margin);
            // The host fills the box unless centred content is asked for, so multi-line boxes scroll in it.
            Assert.Equal(VerticalAlignment.Stretch, contentHost.VerticalAlignment);
            Point origin = ((UIElement)contentHost.Content).TranslatePoint(default, box);
            double expected = box.BorderThickness.Left + box.Padding.Left;
            // TextBoxView adds its own 2px caret gutter; a doubled padding would add Padding.Left again.
            Assert.InRange(origin.X, expected - 0.5, expected + 2.5);
            Assert.InRange(origin.Y, box.BorderThickness.Top + box.Padding.Top - 0.5, box.BorderThickness.Top + box.Padding.Top + 0.5);
        }
    });

    [Fact]
    public void CentredContentStaysCentredInATallerBox() => ControlStateMotionTests.Run(false, (theme, host) =>
    {
        // The video editor's trim time boxes: MinHeight 36, Padding 8,4 and centred content.
        var box = new TextBox
        {
            Text = "00:00:02.999", FontSize = 14, MinHeight = 36, Padding = new Thickness(8, 4, 8, 4),
            VerticalContentAlignment = VerticalAlignment.Center, Style = (Style)theme[typeof(TextBox)],
        };
        host.Children.Add(box);
        ControlStateMotionTests.Drain(host);

        var contentHost = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
        var view = (FrameworkElement)contentHost.Content;
        double top = view.TranslatePoint(default, box).Y;
        double bottom = box.ActualHeight - top - view.ActualHeight;
        Assert.Equal(36, box.ActualHeight, 1);
        Assert.InRange(top - bottom, -1, 1);
    });
}
