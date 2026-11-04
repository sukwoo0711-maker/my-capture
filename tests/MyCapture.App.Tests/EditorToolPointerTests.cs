using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The editor's tool pointers: the shape crosshair, the pen and highlighter pointer beside the
/// brush footprint, the footprint's own colour and edge, the unavailable step pointer, the
/// arrow while a caption is typed, [ and ] for the brush width, and the highlighter's own width.
/// </summary>
public sealed class EditorToolPointerTests : KoreanCaptionTest
{
    private static readonly ColorRgba Red = ColorRgba.FromRgb(230, 48, 60);
    private static readonly EditorCursorStyle White = EditorCursorStyle.White;

    // ---- Shape crosshair ------------------------------------------------------------------

    [Theory]
    [InlineData((int)EditorTool.Rectangle)]
    [InlineData((int)EditorTool.Ellipse)]
    [InlineData((int)EditorTool.Arrow)]
    public void ShapeToolsShowTheEditorsOwnCrosshairAndHighContrastFallsBackToTheSystemCross(int tool) => StaTestHost.Run(() =>
    {
        Assert.Equal(EditorCursorKind.Crosshair, EditorCursorPolicy.Resolve((EditorTool)tool, default));
        Assert.True(EditorCursorPolicy.IsCustom(EditorCursorKind.Crosshair));
        Assert.Null(EditorCursors.SystemCursor(EditorCursorKind.Crosshair, highContrast: false));
        Assert.Same(Cursors.Cross, EditorCursors.SystemCursor(EditorCursorKind.Crosshair, highContrast: true));
        Assert.Same(Cursors.Cross, EditorCursors.Get(EditorCursorKind.Crosshair, 32, 0, Red, highContrast: true, White));
        Assert.NotSame(Cursors.Cross, EditorCursors.Get(EditorCursorKind.Crosshair, 32, 0, Red, highContrast: false, White));
    });

    [Fact]
    public void ObscureAndImageToolsKeepTheirBadgedCrosshairs()
    {
        foreach ((EditorTool tool, EditorCursorKind kind) in new[]
        {
            (EditorTool.Mosaic, EditorCursorKind.Mosaic), (EditorTool.Blur, EditorCursorKind.Blur),
            (EditorTool.Image, EditorCursorKind.Image), (EditorTool.StepNumber, EditorCursorKind.StepNumber),
        })
        {
            Assert.Equal(kind, EditorCursorPolicy.Resolve(tool, default));
        }
    }

    [Fact]
    public void CustomKindsAreExactlyTheOnesWindowsDoesNotProvide()
    {
        foreach (EditorCursorKind kind in Enum.GetValues<EditorCursorKind>())
            Assert.Equal(EditorCursorPolicy.IsCustom(kind), EditorCursors.SystemCursor(kind, highContrast: false) is null);

        Assert.True(EditorCursorKind.Unavailable < EditorCursorKind.Pen);
        Assert.False(EditorCursorPolicy.IsCustom(EditorCursorKind.Unavailable));
        Assert.Same(Cursors.No, EditorCursors.SystemCursor(EditorCursorKind.Unavailable, highContrast: false));
        Assert.Same(Cursors.No, EditorCursors.SystemCursor(EditorCursorKind.Unavailable, highContrast: true));
        Assert.True(EditorCursorPolicy.IsCustom(EditorCursorKind.Pen));
        Assert.True(EditorCursorPolicy.IsCustom(EditorCursorKind.Highlighter));
        Assert.True(EditorCursorPolicy.IsCustom(EditorCursorKind.Crosshair));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    public void CrosshairHasNoBadgeAndClicksOnTheCrosshairCentre(int size) => StaTestHost.Run(() =>
    {
        EditorCursorImage cross = EditorCursors.Render(EditorCursorKind.Crosshair, size, 0, Red, White);
        EditorCursorImage mosaic = EditorCursors.Render(EditorCursorKind.Mosaic, size, 0, Red, White);
        double k = size / 32.0;
        Assert.Equal(size, cross.Bitmap.PixelWidth);
        Assert.Equal(size, cross.Bitmap.PixelHeight);
        Assert.Equal((int)Math.Floor(10.5 * k), cross.HotSpotX);
        Assert.Equal((int)Math.Floor(10.5 * k), cross.HotSpotY);
        Assert.Equal((mosaic.HotSpotX, mosaic.HotSpotY), (cross.HotSpotX, cross.HotSpotY));

        byte[] pixels = Pixels(cross.Bitmap);
        Assert.True(Alpha(pixels, size, cross.HotSpotX, cross.HotSpotY) >= 160);
        // The tool badges sit in the lower-right quarter; the plain crosshair leaves it empty.
        int from = (int)Math.Ceiling(16 * k);
        bool BadgeArea(byte[] image) => Enumerable.Range(from, size - from)
            .SelectMany(y => Enumerable.Range(from, size - from).Select(x => Alpha(image, size, x, y))).Any(alpha => alpha > 0);
        Assert.False(BadgeArea(pixels));
        Assert.True(BadgeArea(Pixels(mosaic.Bitmap)));
        // Dark outline and light body, like the other tool cursors.
        Assert.Contains(Enumerable.Range(0, size * size), i => pixels[(i * 4) + 3] > 200 && pixels[i * 4] < 40 && pixels[(i * 4) + 2] < 40);
        Assert.Contains(Enumerable.Range(0, size * size), i => pixels[(i * 4) + 3] > 200 && pixels[i * 4] > 220 && pixels[(i * 4) + 1] > 220);
    });

    // ---- Pen and highlighter pointer beside the footprint ---------------------------------

    [Theory]
    [InlineData(32, 0.25, 1)]
    [InlineData(32, 6.0, 5)]
    [InlineData(32, 30.0, 22)]
    [InlineData(48, 12.5, 10)]
    [InlineData(32, 400.0, 224)]
    [InlineData(200, 100.0, 56)]
    [InlineData(256, 30.0, 0)]
    public void BrushOffsetPutsTheNibOnePixelClearOfTheFootprintAtFortyFiveDegrees(int size, double radius, int expected)
    {
        int offset = EditorCursors.BrushOffset(size, radius);
        Assert.Equal(expected, offset);
        Assert.InRange(offset, 0, 256 - size);
        if (offset > 0 && offset < 256 - size)
        {
            // The nib is (offset, -offset) from the click point: at least radius + 1 away, and no
            // further than one whole pixel step beyond that.
            Assert.True(offset * Math.Sqrt(2) >= radius + 1 - 1e-9);
            Assert.True((offset - 1) * Math.Sqrt(2) < radius + 1);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void WithoutAFootprintTheBrushCursorKeepsTodaysLayout(double radius) =>
        Assert.Equal(0, EditorCursors.BrushOffset(32, radius));

    [Theory]
    [InlineData((int)EditorCursorKind.Pen, 32, 0)]
    [InlineData((int)EditorCursorKind.Pen, 32, 5)]
    [InlineData((int)EditorCursorKind.Pen, 48, 8)]
    [InlineData((int)EditorCursorKind.Pen, 64, 22)]
    [InlineData((int)EditorCursorKind.Pen, 96, 40)]
    [InlineData((int)EditorCursorKind.Highlighter, 32, 5)]
    [InlineData((int)EditorCursorKind.Highlighter, 48, 8)]
    [InlineData((int)EditorCursorKind.Highlighter, 96, 22)]
    public void BrushCursorMovesThePencilBesideTheFootprintAndClicksAtItsCentre(int kindValue, int size, int offset) => StaTestHost.Run(() =>
    {
        var kind = (EditorCursorKind)kindValue;
        EditorCursorImage today = EditorCursors.Render(kind, size, 1, Red, White);
        EditorCursorImage moved = EditorCursors.Render(kind, size, 1, Red, White, offset);
        int full = size + offset;
        Assert.Equal(full, moved.Bitmap.PixelWidth);
        Assert.Equal(full, moved.Bitmap.PixelHeight);
        Assert.Equal(today.HotSpotX, moved.HotSpotX);
        Assert.Equal(today.HotSpotY + offset, moved.HotSpotY);

        // The glyph is exactly today's, moved right; everything else is empty.
        byte[] before = Pixels(today.Bitmap), after = Pixels(moved.Bitmap);
        for (int y = 0; y < full; y++)
        {
            for (int x = 0; x < full; x++)
            {
                bool glyph = y < size && x >= offset;
                for (int channel = 0; channel < 4; channel++)
                {
                    int expected = glyph ? before[(((y * size) + x - offset) * 4) + channel] : 0;
                    Assert.InRange(after[(((y * full) + x) * 4) + channel], Math.Max(0, expected - 2), Math.Min(255, expected + 2));
                }
            }
        }

        // The nib lands at the click point plus (offset, -offset): outside the footprint, up and to the right.
        Assert.True(Alpha(after, full, moved.HotSpotX + offset, moved.HotSpotY - offset) >= 160);
        if (offset > 0) Assert.Equal(0, Alpha(after, full, moved.HotSpotX, moved.HotSpotY));
        Assert.Same(Cursors.Pen, EditorCursors.Get(kind, size, 0, Red, highContrast: true, White, offset));
    });

    [Fact]
    public void BrushCursorStaysWithin256PixelsWhateverTheOffset() => StaTestHost.Run(() =>
    {
        EditorCursorImage clamped = EditorCursors.Render(EditorCursorKind.Pen, 200, 1, Red, White, 100);
        Assert.Equal(256, clamped.Bitmap.PixelWidth);
        Assert.Equal(256, clamped.Bitmap.PixelHeight);
        Assert.Equal(EditorCursors.Render(EditorCursorKind.Pen, 200, 1, Red, White).HotSpotY + 56, clamped.HotSpotY);

        EditorCursorImage largest = EditorCursors.Render(EditorCursorKind.Highlighter, 256, 1, Red, White, 30);
        Assert.Equal(256, largest.Bitmap.PixelWidth);
        Assert.Equal(EditorCursors.Render(EditorCursorKind.Highlighter, 256, 1, Red, White).HotSpotY, largest.HotSpotY);

        // The size is part of the file header; 256 is stored as 0.
        byte[] file = EditorCursors.BuildCursorFile(clamped);
        Assert.Equal(0, file[6]);
        Assert.Equal(0, file[7]);
        using var cursor = new Cursor(new MemoryStream(file), scaleWithDpi: false);
    });

    [Fact]
    public void PenCursorUsesTheSharedPenIconDrawnLikeTheHighlighter() => StaTestHost.Run(() =>
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var symbols = new ResourceDictionary { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Symbols.xaml", UriKind.Absolute) };
        var shared = PathGeometry.CreateFromGeometry(Assert.IsAssignableFrom<Geometry>(symbols["Icon.Pen"]));
        string text = Assert.IsType<string>(typeof(EditorCursors)
            .GetField("PenFallback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue());
        var fallback = PathGeometry.CreateFromGeometry(Geometry.Parse(text));

        // The cursor places the icon by its nib, the first figure's start point.
        Assert.Equal(new Point(4.1, 15.9), shared.Figures[0].StartPoint);
        Assert.Equal(shared.Figures.Count, fallback.Figures.Count);
        for (int i = 0; i < shared.Figures.Count; i++)
            Assert.Equal(shared.Figures[i].StartPoint, fallback.Figures[i].StartPoint);

        foreach (int size in new[] { 32, 64 })
        {
            EditorCursorImage pen = EditorCursors.Render(EditorCursorKind.Pen, size, 1, Red, White);
            EditorCursorImage highlighter = EditorCursors.Render(EditorCursorKind.Highlighter, size, 1, Red, White);
            // Same nib position as the highlighter, low on the left; the pencil rises to the upper right.
            Assert.Equal((highlighter.HotSpotX, highlighter.HotSpotY), (pen.HotSpotX, pen.HotSpotY));
            byte[] pixels = Pixels(pen.Bitmap);
            double k = size / 32.0;
            Assert.True(Alpha(pixels, size, pen.HotSpotX, pen.HotSpotY) >= 160);
            Assert.True(pen.HotSpotX < size * .2 && pen.HotSpotY > size * .8);
            Assert.Equal(0, Alpha(pixels, size, pen.HotSpotX - (int)(3 * k), pen.HotSpotY - (int)(6 * k)));
            int top = Enumerable.Range(0, size).First(y => Enumerable.Range(0, size).Any(x => Alpha(pixels, size, x, y) > 0));
            Assert.True(pen.HotSpotY - top >= 12 * k, $"{size}px: the pen ends {pen.HotSpotY - top}px above the nib.");
        }
    });

    [Fact]
    public void BrushCursorsAreCachedPerFootprintAndTheCacheStaysBounded() => StaTestHost.Run(() =>
    {
        Cursor first = EditorCursors.Get(EditorCursorKind.Pen, 32, 0, Red, highContrast: false, White, 3);
        Assert.Same(first, EditorCursors.Get(EditorCursorKind.Pen, 32, 5, ColorRgba.White, highContrast: false, White, 3));
        Assert.NotSame(first, EditorCursors.Get(EditorCursorKind.Pen, 32, 0, Red, highContrast: false, White, 4));
        Assert.NotSame(first, EditorCursors.Get(EditorCursorKind.Highlighter, 32, 0, Red, highContrast: false, White, 3));

        // The footprint is no part of any other cursor's identity.
        Cursor cross = EditorCursors.Get(EditorCursorKind.Crosshair, 32, 0, Red, highContrast: false, White);
        Assert.Same(cross, EditorCursors.Get(EditorCursorKind.Crosshair, 32, 0, Red, highContrast: false, White, 12));

        // Zooming through many footprints keeps at most the limit.
        for (int offset = 1; offset <= 40; offset++) EditorCursors.Get(EditorCursorKind.Pen, 32, 0, Red, highContrast: false, White, offset);
        Assert.Equal(EditorCursors.BrushCursorLimit, EditorCursors.BrushCursorCount);
        Assert.Equal(8, EditorCursors.BrushCursorLimit);
        Assert.Same(cross, EditorCursors.Get(EditorCursorKind.Crosshair, 32, 0, Red, highContrast: false, White));

        // Least recently used goes first: the one used again stays, the oldest unused one is made anew.
        Cursor kept = EditorCursors.Get(EditorCursorKind.Pen, 40, 0, Red, highContrast: false, White, 100);
        Cursor oldest = EditorCursors.Get(EditorCursorKind.Pen, 40, 0, Red, highContrast: false, White, 101);
        for (int offset = 102; offset <= 107; offset++) EditorCursors.Get(EditorCursorKind.Pen, 40, 0, Red, highContrast: false, White, offset);
        Assert.Same(kept, EditorCursors.Get(EditorCursorKind.Pen, 40, 0, Red, highContrast: false, White, 100));
        EditorCursors.Get(EditorCursorKind.Pen, 40, 0, Red, highContrast: false, White, 108);
        Assert.Equal(EditorCursors.BrushCursorLimit, EditorCursors.BrushCursorCount);
        Assert.Same(kept, EditorCursors.Get(EditorCursorKind.Pen, 40, 0, Red, highContrast: false, White, 100));
        Assert.NotSame(oldest, EditorCursors.Get(EditorCursorKind.Pen, 40, 0, Red, highContrast: false, White, 101));
    });

    // ---- Footprint colour and edge --------------------------------------------------------

    [Fact]
    public void ThePenInkIsOneRuleForTheStrokeAndThePreview()
    {
        Assert.Equal(Red, AnnotationRenderer.PenInk(Red, isHighlighter: false));
        Assert.Equal(new ColorRgba(110, 230, 48, 60), AnnotationRenderer.PenInk(Red, isHighlighter: true));
        var faint = new ColorRgba(60, 230, 48, 60);
        Assert.Equal(faint, AnnotationRenderer.PenInk(faint, isHighlighter: true));
        Assert.Equal(110, AnnotationRenderer.HighlighterMaxAlpha);
    }

    [Fact]
    public void PenFootprintIsFilledWithTheStrokeColourAndEdgedInDarkAndLightRings() => StaTestHost.Run(() =>
    {
        (AnnotationEditorSurface surface, AnnotationEditorController controller) = GreySurface();
        controller.Tool = EditorTool.Pen;
        controller.StrokeColor = Red;
        controller.StrokeThickness = 24;
        var point = new PointD(150, 110);
        byte[] bare = RenderSurface(surface);
        surface.UpdatePointerPreview(point);
        byte[] shown = RenderSurface(surface);
        Point centre = surface.ToSurfacePoint(point);
        int cx = (int)Math.Floor(centre.X), cy = (int)Math.Floor(centre.Y);

        // Inside the footprint: the stroke colour itself.
        foreach ((int dx, int dy) in new[] { (0, 0), (-8, 3), (7, -6) })
            AssertPixel(shown, cx + dx, cy + dy, 60, 48, 230, 2);
        // Outside it, the ring pixel: dark, then light, on a mid-grey capture.
        int dark = Pixel(shown, cx + 12, cy).R, light = Pixel(shown, cx + 13, cy).R, grey = Pixel(bare, cx + 12, cy).R;
        Assert.True(dark < grey - 40, $"dark ring {dark} on {grey}");
        Assert.True(light > grey + 40, $"light ring {light} on {grey}");
        // Beyond the rings the capture is untouched.
        Assert.Equal(grey, Pixel(shown, cx + 15, cy).R);
    });

    [Fact]
    public void HighlighterFootprintShowsTheStrokesCappedAlphaAndMatchesTheRealStroke() => StaTestHost.Run(() =>
    {
        (AnnotationEditorSurface surface, AnnotationEditorController controller) = GreySurface();
        controller.Tool = EditorTool.Highlighter;
        controller.StrokeColor = Red;
        controller.HighlighterThickness = 24;
        var point = new PointD(150, 110);
        surface.UpdatePointerPreview(point);
        byte[] preview = RenderSurface(surface);
        Point centre = surface.ToSurfacePoint(point);
        int cx = (int)Math.Floor(centre.X), cy = (int)Math.Floor(centre.Y);

        // A translucent head: the capture shows through at alpha 110 of 255, not at the picked colour.
        (byte r, byte g, byte b) = Blend(0x80, 230, 48, 60, 110);
        AssertPixel(preview, cx, cy, b, g, r, 2);
        Assert.NotEqual(60, Pixel(preview, cx, cy).B);

        // The same colour as a stroke drawn there afterwards.
        surface.UpdatePointerPreview(null);
        var stroke = new PenAnnotation { IsHighlighter = true, Stroke = Red, StrokeThickness = 24, Points = [new PointD(150, 110)] };
        controller.Document.Add(stroke);
        byte[] drawn = RenderSurface(surface);
        AssertPixel(drawn, cx, cy, Pixel(preview, cx, cy).B, Pixel(preview, cx, cy).G, Pixel(preview, cx, cy).R, 1);
    });

    [Fact]
    public void WhileAStrokeIsDrawnOnlyTheEdgeStaysSoAHighlighterHeadIsNotDoubled() => StaTestHost.Run(() =>
    {
        (AnnotationEditorSurface surface, AnnotationEditorController controller) = GreySurface();
        controller.Tool = EditorTool.Highlighter;
        controller.StrokeColor = Red;
        controller.HighlighterThickness = 24;
        var point = new PointD(150, 110);
        surface.UpdatePointerPreview(point);
        Point centre = surface.ToSurfacePoint(point);
        int cx = (int)Math.Floor(centre.X), cy = (int)Math.Floor(centre.Y);
        byte[] bare = RenderSurface(surface);
        (byte r, byte g, byte b) = Blend(0x80, 230, 48, 60, 110);
        Assert.False(controller.IsDrawingStroke);
        AssertPixel(bare, cx, cy, b, g, r, 2);

        controller.PointerDown(point);
        Assert.True(controller.IsDrawingStroke);
        byte[] drawing = RenderSurface(surface);
        // The stroke's own head is there once; a filled preview on top would show it twice.
        AssertPixel(drawing, cx, cy, b, g, r, 2);
        // The edge is still drawn around it.
        Assert.True(Pixel(drawing, cx + 12, cy).R < Pixel(drawing, cx + 11, cy).R - 40);
        Assert.True(Pixel(drawing, cx + 13, cy).R > 0x80 + 40);

        controller.PointerUp(point);
        Assert.False(controller.IsDrawingStroke);
    });

    [Fact]
    public void FootprintFollowsTheToolsOwnWidth() => StaTestHost.Run(() =>
    {
        (AnnotationEditorSurface surface, AnnotationEditorController controller) = GreySurface();
        controller.StrokeThickness = 3;
        controller.HighlighterThickness = 20;
        controller.Tool = EditorTool.Pen;
        surface.UpdatePointerPreview(new PointD(150, 110));
        Assert.Equal(3 * surface.DipPerPixel, surface.BrushOutlineDip!.Value.Width, 6);
        controller.Tool = EditorTool.Highlighter;
        Assert.Equal(20 * surface.DipPerPixel, surface.BrushOutlineDip!.Value.Width, 6);
    });

    // ---- Typing a caption -----------------------------------------------------------------

    [Fact]
    public void TypingACaptionShowsTheArrowOnTheCanvasAroundTheBoxAndHoverReturnsAfterwards() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        Select(editor, EditorTool.Text);
        Point click = editor.ViewportPointOf(new PointD(200, 150));
        editor.SimulatePointerMove(click, false);
        Assert.Equal(EditorCursorKind.TextBeam, editor.CanvasCursorKind);

        editor.SimulatePointerDown(click);
        Assert.NotNull(Field<TextBox?>(editor, "_activeTextBox"));
        // Right after the box opens, with the pointer still over the new caption.
        Assert.Equal(EditorCursorKind.Arrow, editor.CanvasCursorKind);
        Assert.Same(Cursors.Arrow, editor.CanvasCursor);
        // Over it, beside it and far from it.
        foreach (PointD where in new PointD[] { new(200, 150), new(206, 156), new(450, 300), new(20, 20) })
        {
            editor.SimulatePointerMove(editor.ViewportPointOf(where), false);
            Assert.Equal(EditorCursorKind.Arrow, editor.CanvasCursorKind);
            Assert.Same(Cursors.Arrow, editor.CanvasCursor);
        }

        // Another refresh while typing keeps the arrow.
        Invoke(editor, "RefreshPointerFeedback");
        Assert.Equal(EditorCursorKind.Arrow, editor.CanvasCursorKind);

        // Ending the entry gives hover back: the committed caption can be moved again.
        Field<TextBox>(editor, "_activeTextBox").Text = "memo";
        editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(450, 300)));
        Assert.Null(Field<TextBox?>(editor, "_activeTextBox"));
        TextAnnotation caption = Assert.Single(controller.Document.Items.OfType<TextAnnotation>());
        editor.SimulatePointerMove(editor.ViewportPointOf(caption.Rect.Center), false);
        Assert.Equal(EditorCursorKind.Move, editor.CanvasCursorKind);
        Assert.Same(Cursors.SizeAll, editor.CanvasCursor);
    });

    // ---- Step number that cannot be added -------------------------------------------------

    [Fact]
    public void StepPointerIsUnavailableWhenNoNumberCanBeAddedUnlessOverTheSelectedBadge()
    {
        var c = new AnnotationEditorController(AnnotationDocument.CreateFor(900, 520), new UndoStack(mergeWindow: TimeSpan.Zero))
        {
            Tool = EditorTool.StepNumber, DipPerPixel = 1, HitTolerance = 6,
        };
        PointerIntent open = c.ResolvePointerIntent(new PointD(300, 200));
        Assert.Equal(PointerAction.AddStepNumber, open.Action);
        Assert.Equal(EditorCursorKind.StepNumber, open.Cursor);

        Assert.True(c.TrySetNextStepNumber(999));
        TextAnnotation badge = c.AddStepNumber(new PointD(300, 200));
        Assert.Equal("999", badge.Text);
        Assert.False(c.CanAddStepNumber);

        // Selected badge: it can still be moved and resized, so the pointer says so.
        PointerIntent over = c.ResolvePointerIntent(badge.Rect.Center);
        Assert.Equal(PointerAction.Move, over.Action);
        Assert.Equal(EditorCursorKind.Move, over.Cursor);
        PointerIntent corner = c.ResolvePointerIntent(new PointD(badge.Rect.Right, badge.Rect.Bottom));
        Assert.Equal(PointerAction.Resize, corner.Action);
        Assert.NotEqual(EditorCursorKind.Unavailable, corner.Cursor);

        // Anywhere else, a click would do nothing.
        PointerIntent elsewhere = c.ResolvePointerIntent(new PointD(600, 400));
        Assert.Equal(PointerAction.None, elsewhere.Action);
        Assert.Equal(EditorCursorKind.Unavailable, elsewhere.Cursor);

        // Not selected, the badge is not the pointer's target either.
        c.SetSelected(null);
        Assert.Equal(EditorCursorKind.Unavailable, c.ResolvePointerIntent(badge.Rect.Center).Cursor);

        // Other tools never show it.
        c.Tool = EditorTool.Rectangle;
        Assert.Equal(EditorCursorKind.Crosshair, c.ResolvePointerIntent(new PointD(600, 400)).Cursor);
    }

    [Fact]
    public void StepPointerIsUnavailableOnAnImageTooSmallForABadge()
    {
        var c = new AnnotationEditorController(AnnotationDocument.CreateFor(20, 20), new UndoStack())
        {
            Tool = EditorTool.StepNumber, DipPerPixel = 1, HitTolerance = 6,
        };
        Assert.False(c.CanFitStepNumber);
        PointerIntent intent = c.ResolvePointerIntent(new PointD(10, 10));
        Assert.Equal(PointerAction.None, intent.Action);
        Assert.Equal(EditorCursorKind.Unavailable, intent.Cursor);
    }

    [Fact]
    public void TheCanvasShowsTheWindowsUnavailablePointerForTheStepToolAndKeepsItsStatusMessage() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        Select(editor, EditorTool.StepNumber);
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(400, 250)), false);
        Assert.Equal(EditorCursorKind.StepNumber, editor.CanvasCursorKind);

        Assert.True(controller.TrySetNextStepNumber(999));
        TextAnnotation badge = controller.AddStepNumber(new PointD(200, 150));
        Assert.False(controller.CanAddStepNumber);

        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(400, 250)), false);
        Assert.Equal(EditorCursorKind.Unavailable, editor.CanvasCursorKind);
        Assert.Same(Cursors.No, editor.CanvasCursor);
        editor.SimulatePointerMove(editor.ViewportPointOf(badge.Rect.Center), false);
        Assert.Equal(EditorCursorKind.Move, editor.CanvasCursorKind);
        Assert.Same(Cursors.SizeAll, editor.CanvasCursor);

        // The click still explains itself in the status line and adds nothing.
        int count = controller.Document.Items.Count;
        editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(400, 250)));
        editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(400, 250)));
        Assert.Equal(count, controller.Document.Items.Count);
        Assert.Equal(UiText.Get("EditorStep_NumberRange"), editor.StatusText);
    });

    // ---- [ and ] ---------------------------------------------------------------------------

    [Theory]
    [InlineData(1, true, 2)]
    [InlineData(3, true, 4)]
    [InlineData(4, true, 6)]
    [InlineData(5, true, 6)]
    [InlineData(5.5, false, 4)]
    [InlineData(12, true, 16)]
    [InlineData(13, true, 16)]
    [InlineData(48, true, 64)]
    [InlineData(64, true, 64)]
    [InlineData(64, false, 48)]
    [InlineData(6, false, 4)]
    [InlineData(2, false, 1)]
    [InlineData(1, false, 1)]
    public void BrushWidthStepsAreTheListedOnesAndAWidthBetweenStepsMovesToTheNextOne(double current, bool thicker, double expected) =>
        Assert.Equal(expected, AnnotationEditorController.NextBrushWidth(current, thicker));

    [Theory]
    [InlineData(12, false, 12)]
    [InlineData(14, false, 12)]
    [InlineData(16, false, 12)]
    [InlineData(12, true, 16)]
    [InlineData(10, false, 12)]
    public void TheHighlighterNeverGoesBelowTwelve(double current, bool thicker, double expected) =>
        Assert.Equal(expected, AnnotationEditorController.NextBrushWidth(current, thicker, AnnotationEditorController.MinimumHighlighterThickness));

    [Fact]
    public void BracketKeysStepThePenWidthAndTheControlsThePointerAndTheStatusLineFollow() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        controller.StrokeThickness = 3;
        Select(editor, EditorTool.Pen);
        Slider slider = Field<Slider>(editor, "_thicknessSlider");
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(300, 180)), false);
        Cursor startCursor = editor.CanvasCursor!;
        Assert.Same(ExpectedBrushCursor(editor, EditorCursorKind.Pen), startCursor);

        foreach (double expected in new double[] { 4, 6, 8, 10, 12, 16, 20, 24, 32, 48, 64, 64 })
        {
            Assert.True(Press(editor, Key.OemCloseBrackets, ime: false));
            Assert.Equal(expected, controller.StrokeThickness);
            Assert.Equal(expected, slider.Value);
            Assert.Equal(UiText.Format("Text_DBAF1D995258", expected), editor.StatusText);
            Assert.Equal(12, controller.HighlighterThickness);
            Assert.Same(ExpectedBrushCursor(editor, EditorCursorKind.Pen), editor.CanvasCursor);
        }

        // A wider brush moves the pencil further out.
        Assert.NotSame(startCursor, editor.CanvasCursor);
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        Assert.Equal(64 * surface.DipPerPixel, surface.BrushOutlineDip!.Value.Width, 6);

        foreach (double expected in new double[] { 48, 32, 24, 20, 16, 12, 10, 8, 6, 4, 3, 2, 1, 1 })
        {
            Assert.True(Press(editor, Key.OemOpenBrackets, ime: false));
            Assert.Equal(expected, controller.StrokeThickness);
            Assert.Equal(expected, slider.Value);
            Assert.Same(ExpectedBrushCursor(editor, EditorCursorKind.Pen), editor.CanvasCursor);
        }

        // A width between steps moves to the next step that way.
        controller.StrokeThickness = 5;
        Assert.True(Press(editor, Key.OemOpenBrackets, ime: false));
        Assert.Equal(4, controller.StrokeThickness);
        controller.StrokeThickness = 5;
        Assert.True(Press(editor, Key.OemCloseBrackets, ime: false));
        Assert.Equal(6, controller.StrokeThickness);
        Assert.Empty(controller.Document.Items);
    });

    [Fact]
    public void BracketKeysStepTheHighlighterFromItsOwnWidthAndNeverBelowTwelve() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        controller.StrokeThickness = 3;
        Select(editor, EditorTool.Highlighter);
        Slider slider = Field<Slider>(editor, "_thicknessSlider");
        Assert.Equal(12, slider.Minimum);
        Assert.Equal(12, slider.Value);
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(300, 180)), false);

        Assert.True(Press(editor, Key.OemOpenBrackets, ime: false));
        Assert.Equal(12, controller.HighlighterThickness);
        Assert.Equal(12, slider.Value);
        foreach (double expected in new double[] { 16, 20, 24 })
        {
            Assert.True(Press(editor, Key.OemCloseBrackets, ime: false));
            Assert.Equal(expected, controller.HighlighterThickness);
            Assert.Equal(expected, slider.Value);
            Assert.Equal(UiText.Format("Text_DBAF1D995258", expected), editor.StatusText);
            Assert.Same(ExpectedBrushCursor(editor, EditorCursorKind.Highlighter), editor.CanvasCursor);
        }

        // The pen's width was never touched.
        Assert.Equal(3, controller.StrokeThickness);
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        Assert.Equal(24 * surface.DipPerPixel, surface.BrushOutlineDip!.Value.Width, 6);
        Assert.True(Press(editor, Key.OemOpenBrackets, ime: false));
        Assert.Equal(20, controller.HighlighterThickness);
    });

    [Theory]
    [InlineData((int)EditorTool.Select)]
    [InlineData((int)EditorTool.Rectangle)]
    [InlineData((int)EditorTool.Arrow)]
    [InlineData((int)EditorTool.StepNumber)]
    [InlineData((int)EditorTool.Text)]
    public void BracketKeysPassThroughWithAnyOtherTool(int tool) => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        controller.StrokeThickness = 5;
        Select(editor, (EditorTool)tool);
        Assert.False(editor.HandleShortcut(Key.OemOpenBrackets, ModifierKeys.None));
        Assert.False(editor.HandleShortcut(Key.OemCloseBrackets, ModifierKeys.None));
        Assert.False(Press(editor, Key.OemCloseBrackets, ime: false));
        Assert.Equal(5, controller.StrokeThickness);
        Assert.Equal(12, controller.HighlighterThickness);
    });

    [Fact]
    public void ControlBracketsStillReorderLayersAndShiftedBracketsAreNotTheWidthKeys() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        var first = new RectangleAnnotation { Rect = new RectD(20, 20, 80, 60) };
        var second = new RectangleAnnotation { Rect = new RectD(60, 40, 80, 60) };
        controller.Document.Add(first);
        controller.Document.Add(second);
        Select(editor, EditorTool.Select);
        controller.SetSelected(first);
        Assert.Equal(0, controller.Document.IndexOf(first));

        Assert.True(editor.HandleShortcut(Key.OemCloseBrackets, ModifierKeys.Control));
        Assert.Equal(1, controller.Document.IndexOf(first));
        Assert.True(editor.HandleShortcut(Key.OemOpenBrackets, ModifierKeys.Control));
        Assert.Equal(0, controller.Document.IndexOf(first));

        // With the pen active the Ctrl chords keep their meaning and the width stays.
        controller.StrokeThickness = 5;
        Select(editor, EditorTool.Pen);
        Assert.True(editor.HandleShortcut(Key.OemOpenBrackets, ModifierKeys.Control));
        Assert.Equal(5, controller.StrokeThickness);
        Assert.False(editor.HandleShortcut(Key.OemOpenBrackets, ModifierKeys.Shift));
        Assert.Equal(5, controller.StrokeThickness);
    });

    [Fact]
    public void BracketKeysAreCatalogedWithLocalizedLabelsAndOneHelpRowEach()
    {
        Assert.Equal("StrokeThinner", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.OemOpenBrackets, ModifierKeys.None));
        Assert.Equal("StrokeThicker", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.OemCloseBrackets, ModifierKeys.None));
        Assert.Equal("SendBackward", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.OemOpenBrackets, ModifierKeys.Control));
        Assert.Equal("BringForward", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.OemCloseBrackets, ModifierKeys.Control));
        Assert.Equal("[", ShortcutCatalog.Get(ShortcutScope.Annotation, "StrokeThinner").Hint);
        Assert.Equal("]", ShortcutCatalog.Get(ShortcutScope.Annotation, "StrokeThicker").Hint);

        ShortcutHelpEntry[] rows = ShortcutCatalog.Help(ShortcutScope.Annotation).ToArray();
        foreach ((string key, string korean, string english) in new[]
        {
            ("Shortcuts.StrokeThinner", "선 두께 줄이기", "Thinner stroke"),
            ("Shortcuts.StrokeThicker", "선 두께 늘리기", "Thicker stroke"),
        })
        {
            ShortcutHelpEntry row = Assert.Single(rows, entry => entry.Action == key);
            Assert.Single(row.Shortcuts);
            Assert.Equal(korean, UiText.Get(key));
            using (UiText.UseLanguage("en-US")) Assert.Equal(english, UiText.Get(key));
        }
    }

    [Fact]
    public void EveryCatalogedAnnotationCommandHasAnAction() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        foreach (ShortcutDefinition definition in ShortcutCatalog.ForScope(ShortcutScope.Annotation))
            Assert.True(host.Editor.ShortcutActions.ContainsKey(definition.Command), definition.Command);
    });

    [Fact]
    public void PointerFollowsTheToolAndTheZoomWithoutTheMouseMoving() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        controller.StrokeThickness = 20;
        controller.HighlighterThickness = 40;
        Select(editor, EditorTool.Pen);
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(300, 180)), false);
        Assert.Same(ExpectedBrushCursor(editor, EditorCursorKind.Pen), editor.CanvasCursor);

        // The property bar width, with the pointer parked over the canvas.
        Cursor before = editor.CanvasCursor!;
        Field<Slider>(editor, "_thicknessSlider").Value = 60;
        Assert.Equal(60, controller.StrokeThickness);
        Assert.Same(ExpectedBrushCursor(editor, EditorCursorKind.Pen), editor.CanvasCursor);
        Assert.NotSame(before, editor.CanvasCursor);

        // Another tool, with its own width.
        Select(editor, EditorTool.Highlighter);
        Assert.Equal(EditorCursorKind.Highlighter, editor.CanvasCursorKind);
        Assert.Same(ExpectedBrushCursor(editor, EditorCursorKind.Highlighter), editor.CanvasCursor);

        // A smaller canvas scale: the same width covers fewer screen pixels.
        double scaleBefore = surface.DipPerPixel;
        Cursor cursorBefore = editor.CanvasCursor!;
        host.Window.Width = 520;
        host.Window.Height = 430;
        host.Window.UpdateLayout();
        Assert.True(surface.DipPerPixel < scaleBefore, $"{surface.DipPerPixel} should be below {scaleBefore}.");
        Assert.Same(ExpectedBrushCursor(editor, EditorCursorKind.Highlighter), editor.CanvasCursor);
        Assert.NotSame(cursorBefore, editor.CanvasCursor);
    });

    // ---- The highlighter's own width ------------------------------------------------------

    [Fact]
    public void PenAndHighlighterRemembertheirOwnWidthAcrossToolSwitchesAndDrawWithIt()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        c.StrokeThickness = 3;
        c.Tool = EditorTool.Highlighter;
        Assert.Equal(12, c.HighlighterThickness);
        c.ApplyStrokeThickness(20);
        Assert.Equal(20, c.HighlighterThickness);
        Assert.Equal(3, c.StrokeThickness);

        c.Tool = EditorTool.Pen;
        Assert.Equal(3, c.BrushThickness);
        c.ApplyStrokeThickness(5);
        Assert.Equal(5, c.StrokeThickness);
        Assert.Equal(20, c.HighlighterThickness);

        c.Tool = EditorTool.Highlighter;
        Assert.Equal(20, c.BrushThickness);
        c.PointerDown(new PointD(30, 25));
        c.PointerUp(new PointD(80, 25));
        c.Tool = EditorTool.Pen;
        c.PointerDown(new PointD(30, 60));
        c.PointerUp(new PointD(80, 60));
        PenAnnotation[] strokes = doc.Items.OfType<PenAnnotation>().ToArray();
        Assert.Equal(2, strokes.Length);
        Assert.True(strokes[0].IsHighlighter);
        Assert.Equal(20, strokes[0].StrokeThickness);
        Assert.False(strokes[1].IsHighlighter);
        Assert.Equal(5, strokes[1].StrokeThickness);

        // Shapes share the pen's width, not the highlighter's.
        c.Tool = EditorTool.Rectangle;
        c.PointerDown(new PointD(100, 100));
        c.PointerMove(new PointD(200, 160));
        c.PointerUp(new PointD(200, 160));
        Assert.Equal(5, doc.Items.OfType<RectangleAnnotation>().Single().StrokeThickness);
    }

    [Fact]
    public void WidthEditsGoToTheHighlighterOnlyForTheHighlighterToolOrAHighlighterSelection()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        c.StrokeThickness = 3;
        var highlighter = new PenAnnotation { IsHighlighter = true, Points = [new PointD(40, 40), new PointD(120, 40)], StrokeThickness = 14 };
        var pen = new PenAnnotation { Points = [new PointD(40, 90), new PointD(120, 90)], StrokeThickness = 2 };
        doc.Add(highlighter);
        doc.Add(pen);

        c.Tool = EditorTool.Select;
        c.SetSelected(highlighter);
        int undoBefore = undo.UndoCount;
        c.ApplyStrokeThickness(30);
        Assert.Equal(30, highlighter.StrokeThickness);
        Assert.Equal(30, c.HighlighterThickness);
        Assert.Equal(3, c.StrokeThickness);
        Assert.Equal(undoBefore + 1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(14, highlighter.StrokeThickness);

        c.SetSelected(pen);
        c.ApplyStrokeThickness(8);
        Assert.Equal(8, pen.StrokeThickness);
        Assert.Equal(8, c.StrokeThickness);
        Assert.Equal(30, c.HighlighterThickness);

        // A mixed selection is not a highlighter selection.
        c.SelectAll();
        c.ApplyStrokeThickness(6);
        Assert.Equal(6, c.StrokeThickness);
        Assert.Equal(30, c.HighlighterThickness);

        c.SetSelected(null);
        c.Tool = EditorTool.Highlighter;
        c.ApplyStrokeThickness(40);
        Assert.Equal(40, c.HighlighterThickness);
        Assert.Equal(6, c.StrokeThickness);
        c.ApplyStrokeThickness(double.NaN);
        Assert.Equal(40, c.HighlighterThickness);

        c.Tool = EditorTool.Rectangle;
        c.ApplyStrokeThickness(9);
        Assert.Equal(9, c.StrokeThickness);
        Assert.Equal(40, c.HighlighterThickness);
    }

    [Theory]
    [InlineData(5, 12)]
    [InlineData(12, 12)]
    [InlineData(33.5, 33.5)]
    [InlineData(64, 64)]
    [InlineData(100, 64)]
    [InlineData(double.NaN, 12)]
    [InlineData(double.PositiveInfinity, 12)]
    public void HighlighterWidthStaysWithinTwelveToSixtyFour(double value, double expected)
    {
        AnnotationEditorController c = NewController(out _, out _);
        c.HighlighterThickness = value;
        Assert.Equal(expected, c.HighlighterThickness);
    }

    [Fact]
    public void ThePropertyBarShowsAndEditsTheWidthOfTheActiveTool() => StaTestHost.Run(() =>
    {
        using var preferences = new PreferencesScope(new AnnotationDefaults { StrokeThickness = 3, HighlighterThickness = 20, LastTool = "Rectangle" });
        using var host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        Assert.Equal(3, controller.StrokeThickness);
        Assert.Equal(20, controller.HighlighterThickness);

        Select(editor, EditorTool.Highlighter);
        Slider slider = Field<Slider>(editor, "_thicknessSlider");
        Assert.Equal(12, slider.Minimum);
        Assert.Equal(20, slider.Value);
        slider.Value = 30;
        Assert.Equal(30, controller.HighlighterThickness);
        Assert.Equal(3, controller.StrokeThickness);

        Select(editor, EditorTool.Pen);
        Assert.Equal(1, slider.Minimum);
        Assert.Equal(3, slider.Value);
        slider.Value = 7;
        Assert.Equal(7, controller.StrokeThickness);
        Assert.Equal(30, controller.HighlighterThickness);

        Select(editor, EditorTool.Highlighter);
        Assert.Equal(30, slider.Value);
        Assert.Equal(30 * Field<AnnotationEditorSurface>(editor, "_surface").DipPerPixel,
            ExpectedFootprintDiameter(editor), 6);
    });

    [Fact]
    public void HighlighterWidthIsLoadedClampedAndSavedWithTheEditorDefaults() => StaTestHost.Run(() =>
    {
        Func<AnnotationDefaults>? read = AnnotationEditorPreferences.Read;
        try
        {
            foreach ((double saved, double expected) in new[] { (20.0, 20.0), (5.0, 12.0), (100.0, 64.0), (double.NaN, 12.0), (0.0, 12.0) })
            {
                AnnotationEditorPreferences.Read = () => new AnnotationDefaults { HighlighterThickness = saved, StrokeThickness = 3, LastTool = "Rectangle" };
                using var host = EditorWindowHost.Create();
                Assert.Equal(expected, Controller(host.Editor).HighlighterThickness);
                Assert.Equal(3, Controller(host.Editor).StrokeThickness);
            }
        }
        finally
        {
            AnnotationEditorPreferences.Read = read;
        }

        using var scope = new PreferencesScope(new AnnotationDefaults { HighlighterThickness = 20, StrokeThickness = 3, LastTool = "Rectangle" });
        using (var host = EditorWindowHost.Create())
        {
            AnnotationEditorControl editor = host.Editor;
            Select(editor, EditorTool.Highlighter);
            Assert.True(Press(editor, Key.OemCloseBrackets, ime: false));
            Assert.Equal(24, Controller(editor).HighlighterThickness);
            Select(editor, EditorTool.Pen);
            Assert.True(Press(editor, Key.OemCloseBrackets, ime: false));
        }

        AnnotationDefaults persisted = scope.Read();
        Assert.Equal(24, persisted.HighlighterThickness);
        Assert.Equal(4, persisted.StrokeThickness);

        using var reopened = EditorWindowHost.Create();
        Assert.Equal(24, Controller(reopened.Editor).HighlighterThickness);
        Assert.Equal(4, Controller(reopened.Editor).StrokeThickness);
    });

    // ---- Helpers ----------------------------------------------------------------------------

    private static void Select(AnnotationEditorControl editor, EditorTool tool) => Invoke(editor, "SelectTool", tool);

    private static AnnotationEditorController NewController(out AnnotationDocument doc, out UndoStack undo)
    {
        doc = AnnotationDocument.CreateFor(900, 520);
        undo = new UndoStack(mergeWindow: TimeSpan.Zero);
        return new AnnotationEditorController(doc, undo) { DipPerPixel = 1, HitTolerance = 6 };
    }

    /// <summary>The cursor the editor should show for the brush tools now, built the way the pointer itself is.</summary>
    private static Cursor ExpectedBrushCursor(AnnotationEditorControl editor, EditorCursorKind kind)
    {
        double dpi = VisualTreeHelper.GetDpi(editor).DpiScaleX;
        int size = EditorCursors.PixelSize(Field<int>(editor, "_cursorBaseSize"), dpi);
        int offset = EditorCursors.BrushOffset(size, ExpectedFootprintDiameter(editor) * dpi / 2);
        return EditorCursors.Get(kind, size, 0, Controller(editor).StrokeColor, highContrast: false,
            Field<EditorCursorStyle>(editor, "_cursorStyle"), offset);
    }

    private static double ExpectedFootprintDiameter(AnnotationEditorControl editor) =>
        Controller(editor).BrushThickness * Field<AnnotationEditorSurface>(editor, "_surface").DipPerPixel;

    private static (AnnotationEditorSurface Surface, AnnotationEditorController Controller) GreySurface()
    {
        BitmapSource bitmap = Solid(320, 240, 0x80);
        var rect = new RectD(0, 0, 320, 240);
        var controller = new AnnotationEditorController(AnnotationDocument.CreateFor(320, 240), new UndoStack());
        var surface = new AnnotationEditorSurface(new FrozenFrame(bitmap, rect, null, 0), rect, controller,
            new AnnotationRenderer(new AnnotationImageStore()));
        var size = new Size(352, 272);
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
        Assert.Equal(1, surface.DipPerPixel, 6);
        return (surface, controller);
    }

    private static byte[] RenderSurface(AnnotationEditorSurface surface)
    {
        surface.InvalidateVisual();
        surface.UpdateLayout();
        var target = new RenderTargetBitmap(352, 272, 96, 96, PixelFormats.Pbgra32);
        target.Render(surface);
        return Pixels(target);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth;
        byte[] pixels = new byte[width * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static byte Alpha(byte[] pixels, int width, int x, int y) => pixels[(((y * width) + x) * 4) + 3];

    private static (byte B, byte G, byte R) Pixel(byte[] pixels, int x, int y)
    {
        int index = ((y * 352) + x) * 4;
        return (pixels[index], pixels[index + 1], pixels[index + 2]);
    }

    private static void AssertPixel(byte[] pixels, int x, int y, int blue, int green, int red, int tolerance)
    {
        (byte b, byte g, byte r) = Pixel(pixels, x, y);
        Assert.True(Math.Abs(b - blue) <= tolerance && Math.Abs(g - green) <= tolerance && Math.Abs(r - red) <= tolerance,
            $"({x},{y}) is R{r} G{g} B{b}; expected R{red} G{green} B{blue} within {tolerance}.");
    }

    /// <summary>A colour at <paramref name="alpha"/> over an opaque grey, as the renderer composites it.</summary>
    private static (byte R, byte G, byte B) Blend(byte grey, byte r, byte g, byte b, byte alpha)
    {
        byte Mix(byte over) => (byte)Math.Round((grey * (255 - alpha) + over * alpha) / 255.0);
        return (Mix(r), Mix(g), Mix(b));
    }

    private sealed class PreferencesScope : IDisposable
    {
        private readonly Func<AnnotationDefaults>? _read = AnnotationEditorPreferences.Read;
        private readonly Action<AnnotationDefaults>? _write = AnnotationEditorPreferences.Write;
        private string _json;

        internal PreferencesScope(AnnotationDefaults defaults)
        {
            _json = JsonSerializer.Serialize(defaults);
            AnnotationEditorPreferences.Read = Read;
            AnnotationEditorPreferences.Write = value => _json = JsonSerializer.Serialize(value);
        }

        internal AnnotationDefaults Read() => JsonSerializer.Deserialize<AnnotationDefaults>(_json)!;

        public void Dispose()
        {
            AnnotationEditorPreferences.Read = _read;
            AnnotationEditorPreferences.Write = _write;
        }
    }
}
