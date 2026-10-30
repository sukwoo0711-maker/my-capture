using System.Windows;
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
    public void BrushLocatorHasCenteredHotspotAtEveryPointerSize(int size) => StaTestHost.Run(() =>
    {
        foreach (EditorCursorKind kind in new[] { EditorCursorKind.Pen, EditorCursorKind.Highlighter })
        {
            EditorCursorImage cursor = EditorCursors.Render(kind, size, 1, ColorRgba.White, EditorCursorStyle.White);
            Assert.Equal(cursor.HotSpotX, cursor.HotSpotY);
            Assert.InRange(cursor.HotSpotX, size * .45, size * .51);
            byte[] pixels = new byte[size * size * 4];
            cursor.Bitmap.CopyPixels(pixels, size * 4, 0);
            Assert.True(pixels[(cursor.HotSpotY * size + cursor.HotSpotX) * 4 + 3] >= 160);
            Assert.NotNull(EditorCursors.SystemCursor(kind, highContrast: true));
        }
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
