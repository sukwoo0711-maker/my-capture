using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The pin bar and the recording bar keep their own chrome but render text as the standard
/// windows do: the UI font, its formatting, antialiasing and literal shaping, and pixel
/// snapping. Menus, which are their own popup roots, take the same settings from the shared
/// ContextMenu style.
/// </summary>
public sealed class FloatingSurfaceTextTests
{
    [Fact]
    public void PinBarAndRecordingBarUseTheStandardTextSettings() => StaTestHost.Run(() =>
    {
        var bar = new PinHoverBar();
        var recording = new RecordingControlWindow(new RectD(100, 100, 640, 360), new RecordingSettings(),
            () => throw new InvalidOperationException("No recorder in this presentation test."), () => "unused.mp4", NullLogger.Instance);
        try
        {
            foreach (Window window in new Window[] { bar, recording })
            {
                string name = window.GetType().Name;
                Assert.Equal(ThemeFonts.Formatting, TextOptions.GetTextFormattingMode(window));
                Assert.Equal(ThemeFonts.Rendering, TextOptions.GetTextRenderingMode(window));
                Assert.Equal(ThemeFonts.Hinting, TextOptions.GetTextHintingMode(window));
                Assert.Equal(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag, window.Language.IetfLanguageTag, ignoreCase: true);
                Assert.False(Typography.GetContextualAlternates(window), $"{name} draws text as typed");
                Assert.False(Typography.GetStandardLigatures(window), $"{name} draws text as typed");
                Assert.True(window.UseLayoutRounding, $"{name} snaps its layout to device pixels");
                Assert.True(window.SnapsToDevicePixels, $"{name} snaps its edges to device pixels");
                Assert.Equal("Font.Ui", ResourceKey(window, Control.FontFamilyProperty));
            }
        }
        finally
        {
            bar.Close();
            recording.CompleteAndClose();
        }
    });

    /// <summary>
    /// The pin's bottom message ("Copied", a failure, translation progress) is interface text: it
    /// takes the UI font and text settings of the hover bar above it. The pin window itself keeps
    /// the system font, so its selection views keep the fonts their pictures were drawn with.
    /// </summary>
    [Fact]
    public void PinMessageUsesTheUiFontAndItsTextSettings() => StaTestHost.Run(() =>
    {
        var image = System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4);
        image.Freeze();
        var pin = new PinWindow(PinContent.FromImage(image), new MyCapture.Core.Pin.PinViewState(320, 180, 1, 1, .1), -10000, -10000)
        {
            ShowActivated = false,
        };
        try
        {
            FrameworkElement message = pin.FeedbackSurfaceForTest;
            Assert.Equal("Font.Ui", ResourceKey(message, Control.FontFamilyProperty));
            Assert.Equal(ThemeFonts.Formatting, TextOptions.GetTextFormattingMode(message));
            Assert.Equal(ThemeFonts.Rendering, TextOptions.GetTextRenderingMode(message));
            Assert.Equal(ThemeFonts.Hinting, TextOptions.GetTextHintingMode(message));
            Assert.Equal(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag, message.Language.IetfLanguageTag, ignoreCase: true);
            Assert.False(Typography.GetContextualAlternates(message), "The pin message draws text as typed");
            Assert.Null(ResourceKey(pin, Control.FontFamilyProperty));
        }
        finally { pin.Close(); }
    });

    /// <summary>
    /// The pin's message is a floating surface like the hover bar above it, so it carries the
    /// same one-DIP Border.Subtle outline. The outline takes its DIP from the padding, so the
    /// message keeps its size and its text stays where it was, in DIPs; at fractional scales the
    /// border's device-pixel rounding can still move them by a pixel.
    /// </summary>
    [Fact]
    public void PinMessageHasTheHoverBarOutline() => StaTestHost.Run(() =>
    {
        var image = System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4);
        image.Freeze();
        var pin = new PinWindow(PinContent.FromImage(image), new MyCapture.Core.Pin.PinViewState(320, 180, 1, 1, .1), -10000, -10000)
        {
            ShowActivated = false,
        };
        var bar = new PinHoverBar();
        try
        {
            Border message = Assert.IsType<Border>(pin.FeedbackSurfaceForTest);
            Border surface = Assert.IsType<Border>(bar.Content);
            Assert.Equal(new Thickness(1), surface.BorderThickness);
            Assert.Equal(surface.BorderThickness, message.BorderThickness);
            Assert.Equal("Border.Subtle", ResourceKey(surface, Border.BorderBrushProperty));
            Color subtle = Application.Current?.Resources["Border.Subtle"] is SolidColorBrush token
                ? token.Color
                : Color.FromRgb(0x2B, 0x3A, 0x50);
            Assert.Equal(subtle, Assert.IsType<SolidColorBrush>(message.BorderBrush).Color);
            Assert.Equal(new Thickness(10, 5, 10, 5), new Thickness(
                message.Padding.Left + message.BorderThickness.Left, message.Padding.Top + message.BorderThickness.Top,
                message.Padding.Right + message.BorderThickness.Right, message.Padding.Bottom + message.BorderThickness.Bottom));
        }
        finally
        {
            bar.Close();
            pin.Close();
        }
    });

    [Fact]
    public void MenusUseTheUiFontAndItsTextSettings() => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        Style style = Assert.IsType<Style>(resources[typeof(ContextMenu)]);
        Setter Setter(DependencyProperty property) => style.Setters.OfType<Setter>().Single(setter => setter.Property == property);
        Assert.Equal("Font.Ui", Assert.IsType<DynamicResourceExtension>(Setter(Control.FontFamilyProperty).Value).ResourceKey);
        Assert.Equal(ThemeFonts.Formatting, Setter(TextOptions.TextFormattingModeProperty).Value);
        Assert.Equal(ThemeFonts.Rendering, Setter(TextOptions.TextRenderingModeProperty).Value);
        Assert.Equal(ThemeFonts.Hinting, Setter(TextOptions.TextHintingModeProperty).Value);
        Assert.Equal(false, Setter(Typography.ContextualAlternatesProperty).Value);
        Assert.Equal(true, Setter(FrameworkElement.UseLayoutRoundingProperty).Value);
    });

    private static string? ResourceKey(DependencyObject element, DependencyProperty property) =>
        element.ReadLocalValue(property) is { } value && value.GetType().Name == "ResourceReferenceExpression"
            ? value.GetType().GetProperty("ResourceKey")?.GetValue(value) as string
            : null;
}
