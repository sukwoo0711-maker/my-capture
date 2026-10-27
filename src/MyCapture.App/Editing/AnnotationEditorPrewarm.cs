using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Editing;

/// <summary>
/// Builds the image editor once, off screen, while the resident app is idle after launch, so the
/// first editor after a capture does not pay first-use costs: the editor's code, the command bar's
/// and inspector's templates, icons and fonts, and the first layout and drawing of its surface.
/// </summary>
/// <remarks>
/// Only the control is built, measured and drawn into an off-screen bitmap, then dropped: no window,
/// draft session, preference write or clipboard access. Preferences are read as an editor reads
/// them and the control never becomes ready to save them.
/// </remarks>
internal static class AnnotationEditorPrewarm
{
    private const int ImageWidth = 64, ImageHeight = 36;

    /// <summary>The editor's opening size on a common display (DIPs), so the command bar lays out fully.</summary>
    private static readonly Size Viewport = new(1000, 640);

    internal static void Run()
    {
        BitmapSource bitmap = BitmapSource.Create(ImageWidth, ImageHeight, 96, 96, PixelFormats.Bgr32, null,
            new byte[ImageWidth * ImageHeight * 4], ImageWidth * 4);
        bitmap.Freeze();
        var region = new RectD(0, 0, ImageWidth, ImageHeight);
        var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap);
        // Match the host window's inherited typography, so its first real layout can reuse
        // the measured inspector groups even when the capture opens at another width.
        TextElement.SetFontFamily(editor, Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI"));
        // Window.Standard supplies these inherited values to a real editor. A detached
        // Grid otherwise measures with 12-DIP text and default shaping instead.
        if (Application.Current?.TryFindResource(MyCapture.App.Themes.StandardWindowTheme.ResourceKey) is Style)
        {
            editor.SetResourceReference(TextElement.FontSizeProperty, "FontSize.Body");
            MyCapture.App.Themes.ThemeFonts.ApplyText(editor);
        }
        editor.UseLayoutRounding = true;
        editor.SnapsToDevicePixels = true;
        editor.Measure(Viewport);
        editor.Arrange(new Rect(Viewport));
        var target = new RenderTargetBitmap((int)Viewport.Width, (int)Viewport.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(editor);
    }
}
