using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The renderer keeps what it built for earlier paints. These tests hold it to the pixels of the
/// renderer before that cache (an independent copy kept below), on the first paint, on repeated
/// paints, and after every kind of edit, undo, redo, DPI and culture change.
/// </summary>
public sealed class AnnotationRenderCacheTests
{
    private const int Width = 640;
    private const int Height = 400;

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(1.5, true)]
    [InlineData(1.25, false)]
    public void ColdAndWarmPaintsMatchThePreviousRendererPixels(double scale, bool withBase) => StaTestHost.Run(() =>
    {
        var scene = new Scene();
        BitmapSource? blurSource = withBase ? scene.Base : null;
        byte[] Previous() => Pixels(dc => PreviousRenderer.Render(dc, scene.Document, scale, scene.Store, blurSource, scene.Title), scale);

        // Every attempt of the cold check paints with a renderer that has drawn nothing yet.
        AssertSamePaint(Previous, () => Pixels(dc => Paint(new AnnotationRenderer(scene.Store), dc, scene, scale, blurSource), scale), scale, "first paint");

        var renderer = new AnnotationRenderer(scene.Store);
        _ = Pixels(dc => Paint(renderer, dc, scene, scale, blurSource), scale);
        AssertSamePaint(Previous, () => Pixels(dc => Paint(renderer, dc, scene, scale, blurSource), scale), scale, "repeated paint");
    });

    [Fact]
    public void EveryEditUndoRedoDpiAndCultureChangeRepaintsTheSamePixelsAsAFreshPaint() => StaTestHost.Run(() =>
    {
        var scene = new Scene();
        var renderer = new AnnotationRenderer(scene.Store);
        var undo = new UndoStack();
        double scale = 1.0;
        int checks = 0;

        void Check(string step)
        {
            AssertSamePaint(
                () => Pixels(dc => PreviousRenderer.Render(dc, scene.Document, scale, scene.Store, scene.Base, scene.Title), scale),
                () => Pixels(dc => Paint(renderer, dc, scene, scale, scene.Base), scale),
                scale,
                step);
            checks++;
        }

        void Change<TItem, TValue>(TItem item, Action<TItem, TValue> setter, TValue before, TValue after, string step)
            where TItem : AnnotationItem
        {
            undo.Execute(new PropertyChangeCommand<TItem, TValue>(item, step, setter, before, after));
            Check(step);
        }

        Check("first paint");
        Check("second paint");

        TextAnnotation caption = scene.Caption;
        Change(caption, static (t, v) => t.Text = v, caption.Text, "Edited caption 편집", "text");
        Change(caption, static (t, v) => t.FontFamily = v, caption.FontFamily, "Consolas", "font family");
        Change(caption, static (t, v) => t.FontSize = v, caption.FontSize, 40.0, "font size");
        Change(caption, static (t, v) => t.Bold = v, caption.Bold, !caption.Bold, "bold");
        Change(caption, static (t, v) => t.Italic = v, caption.Italic, !caption.Italic, "italic");
        Change(caption, static (t, v) => t.Foreground = v, caption.Foreground, ColorRgba.FromRgb(0x10, 0x10, 0x10), "foreground");
        Change(caption, static (t, v) => t.OutlineStyle = v, caption.OutlineStyle, TextOutlineStyle.Legacy, "outline style");
        Change(caption, static (t, v) => t.HasOutline = v, caption.HasOutline, false, "outline off");
        Change(caption, static (t, v) => t.HasOutline = v, caption.HasOutline, true, "outline on");
        Change(caption, static (t, v) => t.Background = v, caption.Background, new ColorRgba(200, 255, 235, 59), "plate");
        Change(caption, static (t, v) => t.RotationDegrees = v, caption.RotationDegrees, 17.5, "rotation");
        Change(caption, static (t, v) => t.AutoSize = v, caption.AutoSize, false, "fixed width");
        Change(caption, static (t, v) => t.Rect = v, caption.Rect, new RectD(caption.Rect.X, caption.Rect.Y, 90, caption.Rect.Height), "wrap width");
        undo.Execute(new TranslateAnnotationsCommand([caption, scene.Wrapped, scene.Badge], 13.25, -7.5));
        Check("moved captions");
        for (int i = 0; i < 6; i++)
        {
            Assert.True(undo.Undo());
            Check($"undo {i + 1}");
        }

        for (int i = 0; i < 3; i++)
        {
            Assert.True(undo.Redo());
            Check($"redo {i + 1}");
        }

        PenAnnotation ink = scene.Ink;
        ink.Points[10] = new PointD(310, 170);
        Check("ink point replaced in place");
        ink.Points.Add(new PointD(20, 390));
        Check("ink point appended in place");
        List<PointD> inkBefore = [.. ink.Points];
        undo.Execute(new ReplacePointsCommand(ink, inkBefore, [.. inkBefore.Select(p => new PointD(p.X + 12, p.Y + 7))]));
        Check("ink points replaced");
        ink.Translate(-4.5, 3.25);
        Check("ink moved");
        Change(ink, static (p, v) => p.StrokeThickness = v, ink.StrokeThickness, 13.0, "ink width");
        Change(ink, static (p, v) => p.IsHighlighter = v, ink.IsHighlighter, true, "ink highlighter");
        Assert.True(undo.Undo());
        Assert.True(undo.Undo());
        Assert.True(undo.Undo());
        Check("ink undone");

        PolylineAnnotation arrow = scene.Arrow;
        arrow.Points[1] = new PointD(arrow.Points[1].X + 30, arrow.Points[1].Y - 12);
        Check("arrow tip moved in place");
        Change(arrow, static (a, v) => a.HeadAtStart = v, arrow.HeadAtStart, true, "second head");
        Change(arrow, static (a, v) => a.StrokeThickness = v, arrow.StrokeThickness, 6.0, "arrow width");
        Change(arrow, static (a, v) => a.Stroke = v, arrow.Stroke, ColorRgba.FromRgb(0, 120, 215), "arrow colour");
        arrow.HeadSizeFactor = 2.5;
        Check("head size");
        Change(arrow, static (a, v) => a.HeadAtEnd = v, arrow.HeadAtEnd, false, "end head off");

        RectangleAnnotation box = scene.Box;
        Change(box, static (r, v) => r.StrokeStyle = v, box.StrokeStyle, AnnotationStrokeStyle.Dotted, "stroke style");
        Change(box, static (r, v) => r.Stroke = v, box.Stroke, ColorRgba.FromRgb(20, 160, 60), "stroke colour");
        Change(box, static (r, v) => r.Fill = v, box.Fill, ColorRgba.Transparent, "fill off");
        undo.Execute(new TransformAnnotationCommand(box, box.Bounds, new RectD(300, 40, 120, 70)));
        Check("box resized");

        ImageAnnotation picture = scene.Picture;
        Change(picture, static (i, v) => i.RotationDegrees = v, picture.RotationDegrees, 45.0, "image rotation");
        picture.Translate(10, 10);
        Check("image moved");

        scene.Blur.Translate(25, 15);
        Check("blur moved");

        scale = 1.5;
        Check("150 % DPI");
        scale = 1.0;
        Check("back to 100 % DPI");

        CultureInfo culture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Check("English UI culture");
            CultureInfo.CurrentUICulture = new CultureInfo("ko-KR");
            Check("Korean UI culture");
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
        }

        undo.Execute(new RemoveAnnotationCommand(scene.Document, caption));
        Check("caption deleted");
        Assert.True(undo.Undo());
        Check("caption restored");

        scene.Title = "Another\twindow title that is long enough to be cut with an ellipsis at this width";
        Check("source title changed");
        scene.Title = null;
        Check("source title off");

        Assert.True(checks > 50);
    });

    [Fact]
    public void UnchangedItemsReuseTheirFrozenResourcesAndAllocateLittle() => StaTestHost.Run(() =>
    {
        var scene = new Scene();
        var renderer = new AnnotationRenderer(scene.Store);
        List<object> first = Resources(Record(renderer, scene));
        List<object> second = Resources(Record(renderer, scene));

        Assert.Equal(first.Count, second.Count);
        Assert.All(first.Zip(second), pair => Assert.Same(pair.First, pair.Second));
        Assert.All(first.OfType<Freezable>(), resource => Assert.True(resource.IsFrozen));

        // Twenty outlined captions: a warm paint formats nothing and builds no geometry.
        AnnotationDocument captions = AnnotationDocument.CreateFor(Width, Height);
        for (int i = 0; i < 20; i++)
        {
            captions.Add(new TextAnnotation
            {
                Rect = new RectD(10 + (i % 4 * 150), 10 + (i / 4 * 70), 140, 60),
                Text = $"Caption {i} 캡션 outline",
                FontSize = 22,
                AutoSize = true,
                OutlineStyle = TextOutlineStyle.Consistent,
            });
        }

        var captionRenderer = new AnnotationRenderer(new AnnotationImageStore());
        long cold = Allocated(() => Record(captionRenderer, captions));
        long warm = Allocated(() => Record(captionRenderer, captions));
        Assert.True(warm * 10 < cold, $"A warm paint allocated {warm} bytes; the cold one {cold}.");
    });

    [Fact]
    public void ItemsNoLongerDrawnAreReleasedAfterLaterPaints() => StaTestHost.Run(() =>
    {
        var renderer = new AnnotationRenderer(new AnnotationImageStore());
        AnnotationDocument document = AnnotationDocument.CreateFor(Width, Height);
        WeakReference removed = AddPaintAndRemove(renderer, document);
        for (int i = 0; i < 64; i++)
        {
            _ = Record(renderer, document);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(removed.IsAlive, "A deleted caption is still held by the renderer's cache.");
    });

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AddPaintAndRemove(AnnotationRenderer renderer, AnnotationDocument document)
    {
        var caption = new TextAnnotation { Rect = new RectD(10, 10, 200, 40), Text = "Deleted caption", FontSize = 24, AutoSize = true };
        var ink = new PenAnnotation { Points = [new PointD(10, 80), new PointD(90, 120)], StrokeThickness = 5 };
        document.Add(caption);
        document.Add(ink);
        document.Add(new RectangleAnnotation { Rect = new RectD(300, 20, 50, 40), StrokeThickness = 2 });
        _ = Record(renderer, document);
        document.Remove(caption);
        document.Remove(ink);
        return new WeakReference(caption);
    }

    [Fact]
    public void MeasureAndGrowToTextMatchAFreshLayoutForEveryInput() => StaTestHost.Run(() =>
    {
        var text = new TextAnnotation { Rect = new RectD(10, 20, 120, 30), Text = string.Empty, FontSize = 18, AutoSize = true };
        int checks = 0;

        void Check(string step)
        {
            Assert.True(PreviousLayout.Measure(text) == TextAnnotationLayout.Measure(text), $"Measure differs after: {step}");
            Assert.True(PreviousLayout.Measure(text) == TextAnnotationLayout.Measure(text), $"Repeated Measure differs after: {step}");
            checks++;
        }

        Check("empty caption");
        text.Text = "Caption 가나다";
        Check("text");
        text.Text = "Caption 가나다 ";
        Check("trailing space");
        text.FontSize = 30;
        Check("size");
        text.Bold = true;
        Check("bold");
        text.Italic = true;
        Check("italic");
        text.FontFamily = "Consolas";
        Check("family");
        text.Foreground = ColorRgba.FromRgb(1, 2, 3);
        Check("colour");
        text.AutoSize = false;
        Check("fixed width");
        text.Rect = new RectD(10, 20, 60, 30);
        Check("narrow wrap");
        text.Text = "line one\nline two that wraps";
        Check("paragraphs");
        text.Rect = new RectD(500, 300, 60, 30);
        Check("moved");
        text.Text = string.Empty;
        Check("emptied fixed-width caption");
        CultureInfo culture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            text.Text = "English UI";
            Check("culture");
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
        }

        text.IsStepNumber = true;
        Check("badge");
        Assert.Equal(15, checks);

        foreach ((string content, RectD box) in new[]
        {
            ("Fits", new RectD(0, 0, 300, 80)),
            ("A caption an older version cut with an ellipsis", new RectD(4, 5, 70, 20)),
            ("Two\nlines", new RectD(4, 5, 400, 20)),
        })
        {
            TextAnnotation legacy = new() { Rect = box, Text = content, FontSize = 24 };
            TextAnnotation current = (TextAnnotation)legacy.Clone();
            Assert.Equal(PreviousLayout.GrowToText(legacy), TextAnnotationLayout.GrowToText(current));
            Assert.Equal(legacy.Rect, current.Rect);
        }
    });

    [Fact]
    public void SourceTitleBadgeMatchesThePreviousPixelsAndFollowsTitleAndSize() => StaTestHost.Run(() =>
    {
        foreach ((string? title, int width, int height) in new (string?, int, int)[]
        {
            ("Visual Studio Code — main.cs", 640, 400),
            ("Visual Studio Code — main.cs", 640, 400),
            ("Visual Studio Code — main.cs", 300, 120),
            ("A\r\nmulti-line\ttitle with tabs and a long tail that cannot fit in a small image", 300, 120),
            ("   ", 300, 120),
            ("Tiny", 30, 14),
            (null, 640, 400),
        })
        {
            AssertSamePaint(
                () => Pixels(dc => PreviousRenderer.DrawTitle(dc, title, width, height), 1, width, height),
                () => Pixels(dc => SourceWindowTitleRenderer.Draw(dc, title, width, height), 1, width, height),
                1,
                $"badge for {title} at {width}x{height}",
                width);
        }
    });

    private static void Paint(AnnotationRenderer renderer, DrawingContext dc, Scene scene, double pixelsPerDip, BitmapSource? blurSource)
    {
        renderer.Render(dc, scene.Document, pixelsPerDip, blurSource: blurSource);
        SourceWindowTitleRenderer.Draw(dc, scene.Title, Width, Height);
    }

    private static DrawingGroup Record(AnnotationRenderer renderer, Scene scene)
    {
        var group = new DrawingGroup();
        using (DrawingContext dc = group.Open())
        {
            renderer.Render(dc, scene.Document, 1.0, blurSource: scene.Base);
        }

        return group;
    }

    private static DrawingGroup Record(AnnotationRenderer renderer, AnnotationDocument document)
    {
        var group = new DrawingGroup();
        using (DrawingContext dc = group.Open())
        {
            renderer.Render(dc, document, 1.0);
        }

        return group;
    }

    /// <summary>
    /// Asserts that <paramref name="actual"/> paints the bytes <paramref name="expected"/> paints.
    /// </summary>
    /// <remarks>
    /// WPF rasterizes a glyph at a new size or sub-pixel position lazily, and on a loaded machine
    /// the first paint that needs it can differ from the next paints for either renderer. A
    /// mismatch is therefore painted again, both ways; a cache that drew something stale differs
    /// on every attempt.
    /// </remarks>
    private static void AssertSamePaint(Func<byte[]> expected, Func<byte[]> actual, double scale, string step, int width = Width)
    {
        string? difference = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[] want = expected();
            byte[] got = actual();
            if (want.SequenceEqual(got))
            {
                return;
            }

            difference = Difference(want, got, (int)Math.Ceiling(width * scale));
        }

        Assert.Fail($"The cached renderer paints differently from the previous renderer after: {step}; {difference}.");
    }

    /// <summary>Where two paints differ, for a failure message.</summary>
    private static string Difference(byte[] expected, byte[] actual, int width)
    {
        int count = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, largest = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] == actual[i]) continue;
            count++;
            largest = Math.Max(largest, Math.Abs(expected[i] - actual[i]));
            int pixel = i / 4;
            (int x, int y) = (pixel % width, pixel / width);
            (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
        }

        return $"{count} bytes differ (largest {largest}) within x {left}..{right}, y {top}..{bottom}";
    }

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Pens, brushes, geometries, recorded glyphs and transforms a recorded paint references.</summary>
    private static List<object> Resources(DrawingGroup group)
    {
        var found = new List<object>();
        foreach (Drawing drawing in group.Children)
        {
            switch (drawing)
            {
                case GeometryDrawing geometry:
                    // Rectangles, lines and ellipses get their geometry from the drawing context.
                    if (geometry.Geometry is not (RectangleGeometry or LineGeometry or EllipseGeometry)) found.Add(geometry.Geometry);
                    if (geometry.Brush is not null) found.Add(geometry.Brush);
                    if (geometry.Pen is not null) found.Add(geometry.Pen);
                    break;
                case DrawingGroup nested when nested.IsFrozen:
                    // A cached glyph recording, reused as a whole.
                    found.Add(nested);
                    break;
                case DrawingGroup nested:
                    if (nested.Transform is { } transform && transform is not MatrixTransform) found.Add(transform);
                    found.AddRange(Resources(nested));
                    break;
            }
        }

        return found;
    }

    private static byte[] Pixels(Action<DrawingContext> draw, double scale, int width = Width, int height = Height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            draw(dc);
            dc.Pop();
        }

        int pixelWidth = (int)Math.Ceiling(width * scale);
        int pixelHeight = (int)Math.Ceiling(height * scale);
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] pixels = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
        return pixels;
    }

    /// <summary>A document with every annotation kind and style the renderer draws.</summary>
    private sealed class Scene
    {
        internal Scene()
        {
            Base = Bitmap(Width, Height, static (x, y) => ((byte)(x * 255 / Width), (byte)(y * 255 / Height), (byte)((x ^ y) & 0xFF)));
            BitmapSource asset = Bitmap(48, 32, static (x, y) => ((byte)(x * 5), (byte)(200 - (y * 4)), 90));
            BitmapSource mosaic = Bitmap(64, 48, static (x, y) => ((byte)((x / 8) * 30), (byte)((y / 8) * 40), 120));
            Store = AnnotationImageStore.FromDecoded(new Dictionary<string, BitmapSource>
            {
                ["asset.png"] = asset,
                ["mosaic.png"] = mosaic,
            });

            Document = AnnotationDocument.CreateFor(Width, Height);
            ColorRgba red = ColorRgba.FromRgb(0xEF, 0x44, 0x44);
            Box = new RectangleAnnotation
            {
                Rect = new RectD(20.5, 20.25, 140, 80), Stroke = red, StrokeThickness = 3,
                Fill = ShapeAnnotation.FillForTransparency(red, 50), FillMatchesStroke = true,
            };
            Document.Add(Box);
            Document.Add(new RectangleAnnotation { Rect = new RectD(180, 20, 100, 60), Stroke = red, StrokeThickness = 4, StrokeStyle = AnnotationStrokeStyle.Dashed, CornerRadius = 6 });
            Document.Add(new RectangleAnnotation { Rect = new RectD(300, 20, 100, 60), Stroke = ColorRgba.FromRgb(0, 120, 215), StrokeThickness = 3, StrokeStyle = AnnotationStrokeStyle.Dotted });
            Document.Add(new RectangleAnnotation { Rect = new RectD(420, 20, 100, 60), Stroke = red, StrokeThickness = 6, StrokeStyle = AnnotationStrokeStyle.ThickDashed });
            Document.Add(new RectangleAnnotation { Rect = new RectD(540, 20, 80, 50), Stroke = red, StrokeThickness = 0, Fill = ColorRgba.FromRgb(20, 20, 20) });
            Document.Add(new EllipseAnnotation
            {
                Rect = new RectD(30, 120, 120, 70), Stroke = ColorRgba.FromRgb(20, 160, 60), StrokeThickness = 5,
                Fill = new ColorRgba(80, 20, 160, 60), Opacity = 0.7,
            });
            Arrow = PolylineAnnotation.CreateArrow(new PointD(170, 130), new PointD(300, 190.5), red, 4);
            Document.Add(Arrow);
            Document.Add(new PolylineAnnotation
            {
                Points = [new PointD(320, 130), new PointD(450, 150)], Stroke = ColorRgba.Black, StrokeThickness = 3,
                HeadAtStart = true, HeadAtEnd = true,
            });
            Document.Add(new PolylineAnnotation
            {
                Points = [new PointD(470, 120), new PointD(520, 180), new PointD(600, 130), new PointD(610, 190)],
                Stroke = ColorRgba.FromRgb(0, 120, 215), StrokeThickness = 2, HeadAtEnd = false,
            });
            Document.Add(PolylineAnnotation.CreateArrow(new PointD(600, 220), new PointD(600, 220), red, 3));
            Ink = new PenAnnotation
            {
                Stroke = new ColorRgba(220, 190, 25, 120), StrokeThickness = 7, Opacity = 0.8,
                Points = [.. Enumerable.Range(0, 300).Select(static i => new PointD(20 + (i % 275), 260 + (Math.Sin(i * 0.13) * 40)))],
            };
            Document.Add(Ink);
            Document.Add(new PenAnnotation
            {
                IsHighlighter = true, Stroke = ColorRgba.FromRgb(255, 235, 59), StrokeThickness = 16,
                Points = [new PointD(300, 300), new PointD(360, 310), new PointD(420, 300), new PointD(480, 320)],
            });
            Document.Add(new PenAnnotation { Points = [new PointD(500, 360)], StrokeThickness = 9, Stroke = red });
            Caption = new TextAnnotation
            {
                Rect = new RectD(40.25, 200.5, 10, 10), Text = "Outlined caption 캡션", FontSize = 26,
                AutoSize = true, OutlineStyle = TextOutlineStyle.Consistent, Foreground = red,
            };
            Caption.Rect = TextAnnotationLayout.Measure(Caption);
            Document.Add(Caption);
            Wrapped = new TextAnnotation
            {
                Rect = new RectD(330, 210, 150, 40), Text = "A fixed-width legacy caption that wraps onto several lines\nand a second paragraph",
                FontSize = 15, Bold = true, Italic = true, Background = new ColorRgba(220, 0, 0, 0), Foreground = ColorRgba.White,
            };
            Document.Add(Wrapped);
            Document.Add(new TextAnnotation
            {
                Rect = new RectD(480, 240, 140, 50), Text = "Rotated plate", FontSize = 18, HasOutline = false,
                Background = ColorRgba.FromRgb(0, 120, 215), Foreground = ColorRgba.White, RotationDegrees = 30,
            });
            Badge = new TextAnnotation
            {
                IsStepNumber = true, Text = "7", Rect = new RectD(560, 330, 40, 40), FontSize = 20,
                Background = red, Foreground = ColorRgba.White, Bold = true, HasOutline = true,
            };
            Document.Add(Badge);
            Picture = new ImageAnnotation { AssetFileName = "asset.png", SourceWidth = 48, SourceHeight = 32, Rect = new RectD(170, 320, 96, 64), RotationDegrees = 15 };
            Document.Add(Picture);
            Document.Add(new ImageAnnotation { AssetFileName = "mosaic.png", SourceWidth = 64, SourceHeight = 48, Rect = new RectD(280, 330, 64, 48) });
            Document.Add(new ImageAnnotation { AssetFileName = "missing.png", SourceWidth = 10, SourceHeight = 10, Rect = new RectD(360, 340, 40, 30) });
            Blur = new BlurAnnotation { Rect = new RectD(410.4, 330, 70, 50.2), Radius = 8 };
            Document.Add(Blur);
            Title = "Visual Studio Code — AnnotationRenderer.cs";
        }

        internal BitmapSource Base { get; }
        internal AnnotationImageStore Store { get; }
        internal AnnotationDocument Document { get; }
        internal RectangleAnnotation Box { get; }
        internal PolylineAnnotation Arrow { get; }
        internal PenAnnotation Ink { get; }
        internal TextAnnotation Caption { get; }
        internal TextAnnotation Wrapped { get; }
        internal TextAnnotation Badge { get; }
        internal ImageAnnotation Picture { get; }
        internal BlurAnnotation Blur { get; }
        internal string? Title { get; set; }

        private static BitmapSource Bitmap(int width, int height, Func<int, int, (byte R, byte G, byte B)> color)
        {
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    (byte r, byte g, byte b) = color(x, y);
                    int i = ((y * width) + x) * 4;
                    pixels[i] = b;
                    pixels[i + 1] = g;
                    pixels[i + 2] = r;
                    pixels[i + 3] = 255;
                }
            }

            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    /// <summary>The caption layout before measurements were cached, formatting afresh each time.</summary>
    private static class PreviousLayout
    {
        internal static FormattedText Format(TextAnnotation text, double pixelsPerDip)
        {
            var typeface = new Typeface(
                new FontFamily(text.FontFamily),
                text.Italic ? FontStyles.Italic : FontStyles.Normal,
                text.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);
            var brush = new SolidColorBrush(Color.FromArgb(text.Foreground.A, text.Foreground.R, text.Foreground.G, text.Foreground.B));
            brush.Freeze();
            var formatted = new FormattedText(
                text.Text ?? string.Empty,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                Math.Max(1, text.FontSize),
                brush,
                pixelsPerDip)
            {
                Trimming = TextTrimming.None,
            };

            if (WrapWidth(text) is double wrap)
            {
                formatted.MaxTextWidth = wrap;
            }

            return formatted;
        }

        internal static RectD Measure(TextAnnotation text)
        {
            RectD rect = text.Rect;
            if (text.IsStepNumber)
            {
                return rect;
            }

            FormattedText formatted = Format(text, 1.0);
            double margin = Math.Max(1, text.FontSize / 12);
            double lines = formatted.Height > 0 ? formatted.Height : LineHeight(text);
            double width = WrapWidth(text) is double wrap
                ? Math.Max(wrap, formatted.Width)
                : formatted.WidthIncludingTrailingWhitespace + margin;
            return new RectD(rect.X, rect.Y, width, lines + margin);
        }

        internal static bool GrowToText(TextAnnotation text)
        {
            if (text.IsStepNumber || text.AutoSize)
            {
                return false;
            }

            RectD rect = text.Rect;
            FormattedText formatted = Format(text, 1.0);
            if (formatted.Width <= rect.Width + 0.01 && formatted.Height <= rect.Height + 0.01)
            {
                return false;
            }

            RectD measured = Measure(text);
            text.Rect = new RectD(rect.X, rect.Y, Math.Max(rect.Width, measured.Width), Math.Max(rect.Height, measured.Height));
            return true;
        }

        internal static Rect PlateBounds(Rect box, FormattedText formatted) => new(
            box.Left,
            box.Top,
            Math.Max(box.Width, formatted.WidthIncludingTrailingWhitespace),
            Math.Max(box.Height, formatted.Height));

        private static double? WrapWidth(TextAnnotation text) =>
            text.AutoSize || text.IsStepNumber ? null : Math.Max(text.Rect.Width, Math.Max(1, text.FontSize));

        private static double LineHeight(TextAnnotation text)
        {
            var probe = (TextAnnotation)text.Clone();
            probe.Text = " ";
            return Format(probe, 1.0).Height;
        }
    }

    /// <summary>
    /// The renderer and source-title badge exactly as they drew before resources were cached: every
    /// pen, brush, geometry, layout and transform built afresh on every paint. An independent pixel
    /// oracle for the cached renderer.
    /// </summary>
    private static class PreviousRenderer
    {
        internal static void Render(DrawingContext dc, AnnotationDocument document, double pixelsPerDip, AnnotationImageStore images, BitmapSource? blurSource, string? title)
        {
            foreach (AnnotationItem item in document.Items)
            {
                bool needsLayer = item.Opacity < 1.0;
                if (needsLayer) dc.PushOpacity(item.Opacity);
                switch (item)
                {
                    case RectangleAnnotation rect:
                        {
                            Brush? fill = BrushOrNull(rect.Fill);
                            Pen? pen = rect.StrokeThickness > 0 ? StrokePen(rect.Stroke, rect.EffectiveStrokeThickness, rect.StrokeStyle) : null;
                            Rect bounds = ToRect(rect.Rect);
                            if (rect.CornerRadius > 0) dc.DrawRoundedRectangle(fill, pen, bounds, rect.CornerRadius, rect.CornerRadius);
                            else dc.DrawRectangle(fill, pen, bounds);
                            break;
                        }

                    case EllipseAnnotation ellipse:
                        {
                            Brush? fill = BrushOrNull(ellipse.Fill);
                            Pen? pen = ellipse.StrokeThickness > 0 ? StrokePen(ellipse.Stroke, ellipse.EffectiveStrokeThickness, ellipse.StrokeStyle) : null;
                            Rect b = ToRect(ellipse.Rect);
                            dc.DrawEllipse(fill, pen, new Point(b.Left + (b.Width / 2), b.Top + (b.Height / 2)), b.Width / 2, b.Height / 2);
                            break;
                        }

                    case PolylineAnnotation polyline:
                        DrawPolyline(dc, polyline);
                        break;
                    case PenAnnotation ink:
                        DrawPen(dc, ink);
                        break;
                    case TextAnnotation text:
                        DrawText(dc, text, pixelsPerDip);
                        break;
                    case ImageAnnotation image:
                        DrawImage(dc, image, images);
                        break;
                    case BlurAnnotation blur:
                        DrawBlur(dc, blur, blurSource);
                        break;
                }

                if (needsLayer) dc.Pop();
            }

            DrawTitle(dc, title, document.CanvasWidth, document.CanvasHeight);
        }

        internal static void DrawTitle(DrawingContext dc, string? title, double width, double height)
        {
            if (string.IsNullOrWhiteSpace(title) || width < 24 || height < 12) return;
            string text = string.Join(" ", title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length == 0) return;
            double margin = Math.Min(8, Math.Min(width, height) / 12);
            double padding = Math.Min(5, height / 12);
            double fontSize = Math.Min(13, (height - 2 * margin - 2 * padding) / 1.5);
            double availableWidth = Math.Min(680, width - 2 * margin - 2 * padding);
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize, Brushes.White, 1)
            {
                MaxTextWidth = availableWidth,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            double badgeWidth = Math.Min(availableWidth, formatted.WidthIncludingTrailingWhitespace) + 2 * padding;
            var bounds = new Rect(width - margin - badgeWidth, margin,
                badgeWidth, Math.Min(height - 2 * margin, formatted.Height + 2 * padding));
            dc.PushClip(new RectangleGeometry(bounds));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(215, 24, 24, 27)), null,
                bounds, Math.Min(4, padding), Math.Min(4, padding));
            dc.DrawText(formatted, new Point(bounds.Left + padding, bounds.Top + padding));
            dc.Pop();
        }

        private static void DrawPolyline(DrawingContext dc, PolylineAnnotation polyline)
        {
            IReadOnlyList<PointD> points = polyline.Points;
            if (points.Count < 2) return;
            Pen pen = StrokePen(polyline.Stroke, polyline.StrokeThickness);
            double headLength = polyline.StrokeThickness * polyline.HeadSizeFactor;
            int last = points.Count - 2;
            for (int i = 0; i <= last; i++)
            {
                Point from = ToPoint(points[i]);
                Point to = ToPoint(points[i + 1]);
                Vector along = to - from;
                double length = along.Length;
                double startInset = i == 0 && polyline.HeadAtStart ? headLength : 0;
                double endInset = i == last && polyline.HeadAtEnd ? headLength : 0;
                if (startInset + endInset == 0)
                {
                    dc.DrawLine(pen, from, to);
                }
                else if (length > startInset + endInset)
                {
                    along /= length;
                    dc.DrawLine(pen, from + (along * startInset), to - (along * endInset));
                }
            }

            if (polyline.HeadAtEnd) DrawArrowHead(dc, polyline, points[^2], points[^1]);
            if (polyline.HeadAtStart) DrawArrowHead(dc, polyline, points[1], points[0]);
        }

        private static void DrawArrowHead(DrawingContext dc, PolylineAnnotation polyline, PointD from, PointD to)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= double.Epsilon) return;
            double ux = dx / length;
            double uy = dy / length;
            double headLength = polyline.StrokeThickness * polyline.HeadSizeFactor;
            double headWidth = headLength * 0.6;
            var baseCenter = new Point(to.X - (ux * headLength), to.Y - (uy * headLength));
            double px = -uy;
            double py = ux;
            var left = new Point(baseCenter.X + (px * headWidth / 2), baseCenter.Y + (py * headWidth / 2));
            var right = new Point(baseCenter.X - (px * headWidth / 2), baseCenter.Y - (py * headWidth / 2));
            var figure = new PathFigure { StartPoint = ToPoint(to), IsClosed = true, IsFilled = true };
            figure.Segments.Add(new LineSegment(left, isStroked: false));
            figure.Segments.Add(new LineSegment(right, isStroked: false));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            dc.DrawGeometry(Brush(polyline.Stroke), null, geometry);
        }

        private static void DrawPen(DrawingContext dc, PenAnnotation ink)
        {
            IReadOnlyList<PointD> points = ink.Points;
            if (points.Count == 0) return;
            ColorRgba color = ink.IsHighlighter ? ink.Stroke.WithAlpha((byte)Math.Min((int)ink.Stroke.A, 110)) : ink.Stroke;
            if (points.Count == 1)
            {
                double r = Math.Max(0.5, ink.StrokeThickness / 2);
                dc.DrawEllipse(Brush(color), null, ToPoint(points[0]), r, r);
                return;
            }

            Pen strokePen = StrokePen(color, ink.StrokeThickness);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(ToPoint(points[0]), isFilled: false, isClosed: false);
                for (int i = 1; i < points.Count; i++) context.LineTo(ToPoint(points[i]), isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, strokePen, geometry);
        }

        private static void DrawText(DrawingContext dc, TextAnnotation text, double pixelsPerDip)
        {
            Rect box = ToRect(text.Rect);
            bool rotated = Math.Abs(text.RotationDegrees) > 1e-6;
            if (rotated) dc.PushTransform(new RotateTransform(text.RotationDegrees, box.Left + (box.Width / 2), box.Top + (box.Height / 2)));
            FormattedText formatted = PreviousLayout.Format(text, pixelsPerDip);
            Brush? plate = BrushOrNull(text.Background);
            if (plate is not null) dc.DrawRectangle(plate, null, text.IsStepNumber ? box : PreviousLayout.PlateBounds(box, formatted));
            var origin = text.IsStepNumber
                ? new Point(box.Left + Math.Max(0, (box.Width - formatted.Width) / 2), box.Top + Math.Max(0, (box.Height - formatted.Height) / 2))
                : new Point(box.Left, box.Top);
            if (text.HasOutline)
            {
                Geometry geometry = formatted.BuildGeometry(origin);
                bool consistent = text.OutlineStyle == TextOutlineStyle.Consistent;
                ColorRgba c = text.Foreground;
                double luma = (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);
                ColorRgba outline = luma > (consistent ? 45 : 140) ? new ColorRgba(c.A, 0, 0, 0) : new ColorRgba(c.A, 255, 255, 255);
                double thickness = consistent ? text.FontSize / 18 : Math.Max(1, text.FontSize / 12);
                var outlinePen = new Pen(Brush(outline), thickness) { LineJoin = PenLineJoin.Round };
                outlinePen.Freeze();
                dc.DrawGeometry(null, outlinePen, geometry);
            }

            dc.DrawText(formatted, origin);
            if (rotated) dc.Pop();
        }

        private static void DrawImage(DrawingContext dc, ImageAnnotation image, AnnotationImageStore images)
        {
            BitmapSource? bitmap = images.Get(image.AssetFileName);
            Rect box = ToRect(image.Rect);
            bool rotated = Math.Abs(image.RotationDegrees) > 1e-6;
            if (rotated) dc.PushTransform(new RotateTransform(image.RotationDegrees, box.Left + (box.Width / 2), box.Top + (box.Height / 2)));
            if (bitmap is not null)
            {
                dc.DrawImage(bitmap, box);
            }
            else
            {
                var pen = new Pen(Brush(ColorRgba.FromRgb(0x88, 0x88, 0x88)), 1);
                pen.Freeze();
                dc.DrawRectangle(null, pen, box);
            }

            if (rotated) dc.Pop();
        }

        private static void DrawBlur(DrawingContext dc, BlurAnnotation blur, BitmapSource? source)
        {
            if (source is null)
            {
                var cover = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
                cover.Freeze();
                dc.DrawRectangle(cover, null, ToRect(blur.Rect));
                return;
            }

            RectD region = blur.PixelRegionWithin(source.PixelWidth, source.PixelHeight);
            if (region.IsEmpty) return;
            dc.DrawImage(AnnotationBlurPixels.BlurRegion(source, region, blur.Radius), new Rect(region.X, region.Y, region.Width, region.Height));
        }

        private static Pen StrokePen(ColorRgba color, double thickness, AnnotationStrokeStyle style = AnnotationStrokeStyle.Solid)
        {
            var pen = new Pen(Brush(color), Math.Max(0.01, thickness))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
                DashCap = style == AnnotationStrokeStyle.Dotted ? PenLineCap.Round : PenLineCap.Flat,
                DashStyle = style switch
                {
                    AnnotationStrokeStyle.Dashed or AnnotationStrokeStyle.ThickDashed => DashStyles.Dash,
                    AnnotationStrokeStyle.Dotted => DashStyles.Dot,
                    _ => DashStyles.Solid,
                },
            };
            pen.Freeze();
            return pen;
        }

        private static SolidColorBrush Brush(ColorRgba c)
        {
            var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush? BrushOrNull(ColorRgba c) => c.A == 0 ? null : Brush(c);

        private static Rect ToRect(RectD r)
        {
            RectD n = r.Normalized();
            return new Rect(n.Left, n.Top, n.Width, n.Height);
        }

        private static Point ToPoint(PointD p) => new(p.X, p.Y);
    }
}
