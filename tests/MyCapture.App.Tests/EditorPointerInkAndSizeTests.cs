using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// How large the editor's tool pointers are within the pointer frame, and the drawing colour they
/// show: shape, text, step and brush pointers take the colour at once when it changes.
/// </summary>
public sealed class EditorPointerInkAndSizeTests
{
    private static readonly ColorRgba Red = ColorRgba.FromRgb(0xEF, 0x44, 0x44);
    private static readonly ColorRgba Blue = ColorRgba.FromRgb(0x3B, 0x82, 0xF6);
    private static readonly EditorCursorStyle White = EditorCursorStyle.White;

    public static TheoryData<int> Sizes() => [32, 40, 48, 64, 96];

    // The share of the pointer frame each glyph may cover, width and height. Windows' own
    // precision cross covers about 19 of 32 pixels and its arrow 13 x 19; the badge, step plate
    // and crop corners add a little to that, and the brush icons stay well below it.
    private static (double Width, double Height) Cap(EditorCursorKind kind) => kind switch
    {
        EditorCursorKind.Crosshair => (0.62, 0.62),
        EditorCursorKind.Mosaic or EditorCursorKind.Blur or EditorCursorKind.Image => (0.78, 0.78),
        EditorCursorKind.StepNumber => (0.82, 0.82),
        EditorCursorKind.Crop => (0.66, 0.66),
        EditorCursorKind.Pen or EditorCursorKind.Highlighter => (0.5, 0.5),
        EditorCursorKind.TextInk => (0.36, 0.66),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EachToolPointerStaysWithinItsShareOfThePointerFrame(int size) => StaTestHost.Run(() =>
    {
        foreach (EditorCursorKind kind in Enum.GetValues<EditorCursorKind>().Where(EditorCursorPolicy.IsCustom))
        {
            if (kind == EditorCursorKind.Eraser) continue;
            EditorCursorImage image = EditorCursors.Render(kind, size, 7, Red, White);
            Assert.Equal(size, image.Bitmap.PixelWidth);
            Assert.Equal(size, image.Bitmap.PixelHeight);
            (int width, int height) = EditorToolCursorEvidenceTests.GlyphSize(image.Bitmap);
            (double capWidth, double capHeight) = Cap(kind);
            Assert.True(width <= capWidth * size, $"{kind} at {size}: {width} px wide, at most {capWidth * size:0.#}.");
            Assert.True(height <= capHeight * size, $"{kind} at {size}: {height} px high, at most {capHeight * size:0.#}.");
            // Still large enough to read: at least a quarter of the frame.
            Assert.True(Math.Max(width, height) >= size / 4, $"{kind} at {size}: {width} x {height} px.");
            Assert.InRange(image.HotSpotX, 0, size - 1);
            Assert.InRange(image.HotSpotY, 0, size - 1);
        }
    });

    [Theory]
    [InlineData(32, 1.0, 32)]
    [InlineData(32, 1.25, 40)]
    [InlineData(32, 1.5, 48)]
    [InlineData(64, 1.5, 96)]
    [InlineData(48, 2.0, 96)]
    public void TheCursorFileHoldsTheFinalSizeSoWindowsDoesNotScaleItAgain(int baseSize, double scale, int expected) => StaTestHost.Run(() =>
    {
        int size = EditorCursors.PixelSize(baseSize, scale);
        Assert.Equal(expected, size);
        byte[] file = EditorCursors.BuildCursorFile(EditorCursors.Render(EditorCursorKind.Crosshair, size, 0, Red, White));
        Assert.Equal(expected % 256, file[6]);
        Assert.Equal(expected % 256, file[7]);
    });

    [Theory]
    [InlineData(40)]
    [InlineData(96)]
    public void TheEraserRingIsAsLargeAsItsReachAndTheIconStaysSmall(int size) => StaTestHost.Run(() =>
    {
        double k = size / 32.0;
        double reach = AnnotationEditorControl.EraserCursorRadius(k);
        EditorCursorImage eraser = EditorCursors.RenderEraser(size, reach, White);
        byte[] pixels = Pixels(eraser.Bitmap);
        int width = eraser.Bitmap.PixelWidth;
        // The hot spot is the ring's centre and is left clear, so the target shows through.
        Assert.Equal(0, Alpha(pixels, width, eraser.HotSpotX, eraser.HotSpotY));
        // The ring passes just outside the reach on all four sides.
        int ring = (int)Math.Ceiling(reach) + 1;
        foreach ((int dx, int dy) in new[] { (ring, 0), (-ring, 0), (0, ring), (0, -ring) })
            Assert.True(Alpha(pixels, width, eraser.HotSpotX + dx, eraser.HotSpotY + dy) >= 160, $"ring at ({dx},{dy}), {size}px");
        Assert.True(eraser.Bitmap.PixelWidth <= 256 && eraser.Bitmap.PixelHeight <= 256);
        (int glyphWidth, int glyphHeight) = EditorToolCursorEvidenceTests.GlyphSize(eraser.Bitmap);
        Assert.True(glyphWidth <= (2 * reach) + (18 * k) && glyphHeight <= (2 * reach) + (18 * k), $"{glyphWidth} x {glyphHeight}");
        // The ring and its icon follow the pointer style, not the ink.
        Assert.Same(
            EditorCursors.Get(EditorCursorKind.Eraser, size, 0, Red, highContrast: false, White, eraserRadius: reach),
            EditorCursors.Get(EditorCursorKind.Eraser, size, 0, Blue, highContrast: false, White, eraserRadius: reach));
    });

    [Fact]
    public void ToolsThatDrawInAColourShowItAndTheOthersKeepThePointerStyle() => StaTestHost.Run(() =>
    {
        foreach (EditorCursorKind kind in Enum.GetValues<EditorCursorKind>().Where(EditorCursorPolicy.IsCustom))
        {
            Cursor red = EditorCursors.Get(kind, 32, 3, Red, highContrast: false, White);
            Cursor blue = EditorCursors.Get(kind, 32, 3, Blue, highContrast: false, White);
            if (EditorCursorPolicy.ShowsInk(kind))
            {
                Assert.NotSame(red, blue);
                Assert.True(HasColour(Pixels(EditorCursors.Render(kind, 32, 3, Blue, White).Bitmap), Blue), $"{kind} does not show the ink.");
            }
            else
            {
                Assert.Same(red, blue);
            }
        }

        Assert.True(EditorCursorPolicy.ShowsInk(EditorCursorKind.Crosshair));
        Assert.True(EditorCursorPolicy.ShowsInk(EditorCursorKind.TextInk));
        Assert.True(EditorCursorPolicy.ShowsInk(EditorCursorKind.Pen));
        Assert.True(EditorCursorPolicy.ShowsInk(EditorCursorKind.Highlighter));
        Assert.True(EditorCursorPolicy.ShowsInk(EditorCursorKind.StepNumber));
        Assert.False(EditorCursorPolicy.ShowsInk(EditorCursorKind.Crop));
        Assert.False(EditorCursorPolicy.ShowsInk(EditorCursorKind.Eraser));
        Assert.False(EditorCursorPolicy.ShowsInk(EditorCursorKind.Mosaic));
    });

    [Theory]
    [InlineData(0xFF, 0xFF, 0xFF, 0xFF)]
    [InlineData(0xFF, 0x00, 0x00, 0x00)]
    [InlineData(0xFF, 0x11, 0x18, 0x27)]
    [InlineData(0xFF, 0xFA, 0xCC, 0x15)]
    [InlineData(0x08, 0x3B, 0x82, 0xF6)]
    [InlineData(0xFF, 0xEF, 0x44, 0x44)]
    [InlineData(0xFF, 0x80, 0x80, 0x80)]
    public void AnyInkStaysVisibleInsideAContrastingEdge(byte a, byte r, byte g, byte b) => StaTestHost.Run(() =>
    {
        var ink = new ColorRgba(a, r, g, b);
        var opaque = ColorRgba.FromRgb(r, g, b);
        foreach (EditorCursorKind kind in new[] { EditorCursorKind.Crosshair, EditorCursorKind.TextInk, EditorCursorKind.Pen, EditorCursorKind.Highlighter })
        {
            byte[] pixels = Pixels(EditorCursors.Render(kind, 48, 1, ink, White).Bitmap);
            // The body is the colour itself, opaque even for a nearly clear ink.
            Assert.True(HasColour(pixels, opaque), $"{kind}: no opaque body in {r:X2}{g:X2}{b:X2}.");
            // And an edge in at least 4.3:1 contrast with it (above the 3:1 WCAG asks of a graphic),
            // so it reads on both a white and a black capture.
            Color bodyColour = Color.FromRgb(r, g, b);
            Assert.True(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[(i * 4) + 3] > 240
                && EditorCursorStyle.ContrastRatio(bodyColour, Color.FromRgb(pixels[(i * 4) + 2], pixels[(i * 4) + 1], pixels[i * 4])) >= 4.3),
                $"{kind}: no contrasting edge around {r:X2}{g:X2}{b:X2}.");
        }
    });

    [Theory]
    [InlineData((int)EditorTool.Rectangle)]
    [InlineData((int)EditorTool.Ellipse)]
    [InlineData((int)EditorTool.Arrow)]
    [InlineData((int)EditorTool.Text)]
    [InlineData((int)EditorTool.StepNumber)]
    [InlineData((int)EditorTool.Pen)]
    [InlineData((int)EditorTool.Highlighter)]
    public void PickingAColourRepaintsThePointerWithoutTheMouseMoving(int toolValue) => StaTestHost.Run(() =>
    {
        var tool = (EditorTool)toolValue;
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        Invoke(editor, "SelectTool", tool);
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(300, 180)), false);
        EditorCursorKind kind = editor.CanvasCursorKind;
        Assert.Equal(EditorCursorPolicy.Resolve(tool, default), kind);
        Cursor before = editor.CanvasCursor!;
        Assert.Same(Expected(editor, kind), before);

        // A swatch the pointer is not over: the click is the only event.
        ButtonBase swatch = Descendants(editor).OfType<ButtonBase>()
            .First(button => button.Tag is ColorRgba color && color != controller.StrokeColor);
        var picked = (ColorRgba)swatch.Tag;
        swatch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(picked, controller.StrokeColor);
        Assert.Equal(kind, editor.CanvasCursorKind);
        Assert.NotSame(before, editor.CanvasCursor);
        Assert.Same(Expected(editor, kind), editor.CanvasCursor);

        // The colour box too.
        Assert.True((bool)typeof(AnnotationEditorControl).GetMethod("ApplyCustomColor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, ["#22C55E"])!);
        Assert.Equal(ColorRgba.FromRgb(0x22, 0xC5, 0x5E), controller.StrokeColor);
        Assert.Same(Expected(editor, kind), editor.CanvasCursor);
    });

    private static Cursor Expected(AnnotationEditorControl editor, EditorCursorKind kind)
    {
        AnnotationEditorController controller = Controller(editor);
        double dpi = VisualTreeHelper.GetDpi(editor).DpiScaleX;
        int size = EditorCursors.PixelSize(Field<int>(editor, "_cursorBaseSize"), dpi);
        return EditorCursors.Get(kind, size, kind == EditorCursorKind.StepNumber ? controller.NextStepNumber : 0,
            controller.StrokeColor, highContrast: false, Field<EditorCursorStyle>(editor, "_cursorStyle"),
            EditorCursors.IsBrush(kind) ? editor.BrushCursorOffset(size, dpi) : 0);
    }

    private static bool HasColour(byte[] pixels, ColorRgba colour) => Enumerable.Range(0, pixels.Length / 4).Any(i =>
        pixels[(i * 4) + 3] > 240 && Math.Abs(pixels[(i * 4) + 2] - colour.R) <= 6
        && Math.Abs(pixels[(i * 4) + 1] - colour.G) <= 6 && Math.Abs(pixels[i * 4] - colour.B) <= 6);

    private static byte[] Pixels(BitmapSource bitmap)
    {
        if (bitmap.Format != PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth;
        byte[] pixels = new byte[width * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static byte Alpha(byte[] pixels, int width, int x, int y) => pixels[(((y * width) + x) * 4) + 3];
}
