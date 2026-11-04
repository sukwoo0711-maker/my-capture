using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorPointerSurfaceTests
{
    [Theory]
    [InlineData((int)EditorTool.Select)]
    [InlineData((int)EditorTool.Rectangle)]
    public void HoverWithoutPointerPreviewDoesNotRenderTheDocumentAgain(int tool) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        var controller = Controller(editor);
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        var renderer = Field<AnnotationRenderer>(surface, "_renderer");
        controller.Document.Add(new RectangleAnnotation { Rect = new RectD(40, 40, 100, 60) });
        controller.Tool = (EditorTool)tool;
        RenderSurface();
        int before = Field<int>(renderer, "_pass");
        Assert.True(before > 0);
        for (int index = 0; index < 4; index++)
        {
            editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(100 + index * 12, 80)), leftPressed: false);
            RenderSurface();
        }
        Assert.Equal(before, Field<int>(renderer, "_pass"));

        // Positive control: brush hover still redraws the visible footprint.
        controller.Tool = EditorTool.Pen;
        RenderSurface();
        before = Field<int>(renderer, "_pass");
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(160, 90)), leftPressed: false);
        RenderSurface();
        Assert.True(Field<int>(renderer, "_pass") > before);

        void RenderSurface()
        {
            host.Window.UpdateLayout();
            editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
                (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
        }
    });

    [Theory]
    [InlineData(.77, 3)]
    [InlineData(1, 3)]
    [InlineData(3.52, 3)]
    [InlineData(.77, 24)]
    [InlineData(1, 24)]
    [InlineData(3.52, 24)]
    public void BrushFootprintMatchesStrokeAndDisplayScale(double scale, double strokeWidth)
    {
        var center = new Point(85, 47);
        Rect footprint = AnnotationEditorSurface.BrushPreviewBounds(center, strokeWidth, scale);
        Assert.Equal(strokeWidth * scale, footprint.Width, 10);
        Assert.Equal(strokeWidth * scale, footprint.Height, 10);
        Assert.Equal(center.X, footprint.X + footprint.Width / 2, 10);
        Assert.Equal(center.Y, footprint.Y + footprint.Height / 2, 10);
    }

    [Fact]
    public void BrushPreviewUpdatesEffectiveWidthAndHidesOutsideImage() => StaTestHost.Run(() =>
    {
        var (surface, controller) = EditorPointerSurfaceEvidenceTests.Surface(.5);
        controller.Tool = EditorTool.Pen;
        controller.StrokeThickness = 3;
        surface.UpdatePointerPreview(new PointD(80, 60));
        Assert.Equal(1.5, surface.BrushOutlineDip!.Value.Width);
        controller.StrokeThickness = 24;
        Assert.Equal(12, surface.BrushOutlineDip!.Value.Width);
        controller.Tool = EditorTool.Highlighter;
        controller.StrokeThickness = 3;
        Assert.Equal(6, surface.BrushOutlineDip!.Value.Width);
        surface.UpdatePointerPreview(new PointD(-1, 60));
        Assert.Null(surface.BrushOutlineDip);
        surface.UpdatePointerPreview(new PointD(80, 60));
        surface.UpdatePointerPreview(null);
        Assert.Null(surface.BrushOutlineDip);
        surface.UpdatePointerPreview(new PointD(80, 60));
        controller.Tool = EditorTool.Select;
        Assert.Null(surface.BrushOutlineDip);
        Assert.Empty(controller.Document.Items);
    });

    [Theory]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(256)]
    public void PenCursorWritesFromItsNibLowOnTheLeftAtEveryPointerSize(int size) => StaTestHost.Run(() =>
    {
        // Without a brush offset the pen is drawn like the highlighter: the pen icon, nib on the hot spot.
        EditorCursorImage cursor = EditorCursors.Render(EditorCursorKind.Pen, size, 1, ColorRgba.White, EditorCursorStyle.White);
        EditorCursorImage highlighter = EditorCursors.Render(EditorCursorKind.Highlighter, size, 1, ColorRgba.White, EditorCursorStyle.White);
        Assert.Equal((highlighter.HotSpotX, highlighter.HotSpotY), (cursor.HotSpotX, cursor.HotSpotY));
        Assert.InRange(cursor.HotSpotX, 0, size * .2);
        Assert.InRange(cursor.HotSpotY, size * .8, size - 1);
        byte[] pixels = new byte[size * size * 4];
        cursor.Bitmap.CopyPixels(pixels, size * 4, 0);
        Assert.True(pixels[(cursor.HotSpotY * size + cursor.HotSpotX) * 4 + 3] >= 160);
        Assert.Same(Cursors.Pen, EditorCursors.SystemCursor(EditorCursorKind.Pen, highContrast: true));
    });

    [Theory]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(256)]
    public void HighlighterCursorIsTheEditIconWritingFromItsNib(int size) => StaTestHost.Run(() =>
    {
        EditorCursorImage cursor = EditorCursors.Render(EditorCursorKind.Highlighter, size, 1, ColorRgba.White, EditorCursorStyle.White);
        byte[] pixels = new byte[size * size * 4];
        cursor.Bitmap.CopyPixels(pixels, size * 4, 0);
        byte Alpha(int x, int y) => pixels[(y * size + x) * 4 + 3];
        double k = size / 32.0;

        // The hot spot is the drawn pencil nib, low on the left of the cursor.
        Assert.True(Alpha(cursor.HotSpotX, cursor.HotSpotY) >= 160);
        Assert.InRange(cursor.HotSpotX, 0, size * .2);
        Assert.InRange(cursor.HotSpotY, size * .8, size - 1);

        int left = size, top = size, right = -1, bottom = -1;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (Alpha(x, y) == 0) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        }

        // The whole icon fits: nothing touches the cursor's edges.
        Assert.True(left > 0 && top > 0 && right < size - 1 && bottom < size - 1, $"{size}px: drawn {left},{top} to {right},{bottom}.");
        // The nib is the icon's left end with only its outline beside it, and the base line sits
        // just below it. The pencil rises to the upper right and the base line runs right, each
        // about 15 design pixels, so this is the whole edit icon.
        Assert.InRange(cursor.HotSpotX - left, 1.5 * k, 2.5 * k + 1);
        Assert.InRange(bottom - cursor.HotSpotY, 2 * k, 3.5 * k + 1);
        Assert.True(right - cursor.HotSpotX >= 16 * k, $"{size}px: the base line ends {right - cursor.HotSpotX}px right of the nib.");
        Assert.True(cursor.HotSpotY - top >= 15.5 * k, $"{size}px: the pencil ends {cursor.HotSpotY - top}px above the nib.");
        // The pencil leans right: nothing is drawn up and to the left of the nib.
        Assert.Equal(0, Alpha(cursor.HotSpotX - (int)(3 * k), cursor.HotSpotY - (int)(6 * k)));

        Assert.Same(Cursors.Pen, EditorCursors.SystemCursor(EditorCursorKind.Highlighter, highContrast: true));
        Assert.Null(EditorCursors.SystemCursor(EditorCursorKind.Highlighter, highContrast: false));
    });

    [Fact]
    public void HighlighterCursorFallbackIsTheSharedEditIcon() => StaTestHost.Run(() =>
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var symbols = new ResourceDictionary { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Symbols.xaml", UriKind.Absolute) };
        var shared = PathGeometry.CreateFromGeometry(Assert.IsAssignableFrom<Geometry>(symbols["Icon.Edit"]));
        string text = Assert.IsType<string>(typeof(EditorCursors)
            .GetField("EditFallback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue());
        var fallback = PathGeometry.CreateFromGeometry(Geometry.Parse(text));

        // The cursor places the icon by its nib, the first figure's start point.
        Assert.Equal(new Point(3.75, 16.25), shared.Figures[0].StartPoint);
        Assert.Equal(shared.Figures.Count, fallback.Figures.Count);
        for (int i = 0; i < shared.Figures.Count; i++)
            Assert.Equal(shared.Figures[i].StartPoint, fallback.Figures[i].StartPoint);
        Assert.Equal(shared.Bounds.Left, fallback.Bounds.Left, 3);
        Assert.Equal(shared.Bounds.Top, fallback.Bounds.Top, 3);
        Assert.Equal(shared.Bounds.Right, fallback.Bounds.Right, 3);
        Assert.Equal(shared.Bounds.Bottom, fallback.Bounds.Bottom, 3);
    });

    [Fact]
    public void SelectionHandlesOnlyAppearForAvailableSingleItemTransforms() => StaTestHost.Run(() =>
    {
        var (surface, controller) = EditorPointerSurfaceEvidenceTests.Surface(1);
        var rect = new RectangleAnnotation { Rect = new RectD(60, 40, 100, 80), Fill = ColorRgba.White };
        controller.Document.Add(rect);
        controller.Tool = EditorTool.Select;
        controller.SetSelected(rect);
        Assert.Equal(8, controller.GetSelectionHandles(surface.DipPerPixel).Count);
        controller.Tool = EditorTool.Rectangle;
        controller.SetSelected(rect);
        Assert.Empty(controller.GetSelectionHandles(surface.DipPerPixel));
        var step = new TextAnnotation { IsStepNumber = true, Text = "1", Rect = new RectD(60, 40, 36, 36) };
        controller.Document.Add(step);
        controller.Tool = EditorTool.StepNumber;
        controller.SetSelected(step);
        Assert.Equal(4, controller.GetSelectionHandles(surface.DipPerPixel).Count);
        Assert.DoesNotContain(controller.GetSelectionHandles(surface.DipPerPixel), h => h.Handle is
            ResizeHandle.TopCenter or ResizeHandle.BottomCenter or ResizeHandle.MiddleLeft or ResizeHandle.MiddleRight);
    });
}
