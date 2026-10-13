using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The editor writes colors the way people do: six digits unless the color is see-through.</summary>
public sealed class EditorColorCodeTests
{
    [Theory]
    [InlineData(0xFF, 0xEF, 0x44, 0x44, "#EF4444")]
    [InlineData(0x80, 0xEF, 0x44, 0x44, "#80EF4444")]
    [InlineData(0x00, 0x11, 0x22, 0x33, "#00112233")]
    public void ColorCodesDropTheAlphaPairOnlyWhenOpaque(byte a, byte r, byte g, byte b, string expected) =>
        Assert.Equal(expected, AnnotationEditorControl.ColorCode(new ColorRgba(a, r, g, b)));

    [Fact]
    public void ColorInputShowsSixDigitsAndAcceptsBothForms() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        byte[] pixels = new byte[160 * 90 * 4];
        BitmapSource bitmap = BitmapSource.Create(160, 90, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4);
        var region = new RectD(0, 0, 160, 90);
        var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap);
        TextBox input = editor.CustomColorInputForTest;
        Assert.Matches("^#[0-9A-F]{6}$", input.Text);

        foreach (var (typed, shown) in new[] { ("#80112233", "#80112233"), ("ef4444", "#EF4444"), ("#FF3B82F6", "#3B82F6"), ("#f00", "#FF0000") })
        {
            input.Text = typed;
            PressEnter(input);
            Assert.Equal(shown, input.Text);
            Assert.Equal(UiText.Format("Text_67CAE5B118A3", shown), editor.StatusText);
        }

        input.Text = "#12";
        PressEnter(input);
        Assert.Equal(UiText.Get("Tools.InvalidColor"), editor.StatusText);
    });

    private static void PressEnter(TextBox input) =>
        input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new FakePresentationSource(), 0, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });

    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
