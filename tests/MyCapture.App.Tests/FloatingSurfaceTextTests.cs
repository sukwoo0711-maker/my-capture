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
