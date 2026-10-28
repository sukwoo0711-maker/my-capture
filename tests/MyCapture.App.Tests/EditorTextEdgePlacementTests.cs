using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Text typed near an edge or a corner of the image is drawn, once committed, exactly as the live
/// box showed it: whole, inside the image, with the same lines and at the same place. 4.9.0 kept a
/// caption started there at the clicked point, so the committed caption ran past the image (which
/// clips it) while the live box, laid over the editor's backdrop, still showed every glyph.
/// </summary>
public sealed class EditorTextEdgePlacementTests
{
    private const string Words = "버전으로 핀으로 abc";
    private const string Lines = "첫 줄\n둘째 줄\n셋째 줄";

    [Theory]
    [InlineData(640, 360, false)]
    [InlineData(1600, 900, false)]
    [InlineData(640, 360, true)]
    [InlineData(120, 80, false)]
    public void TextTypedNearEveryEdgeAndCornerCommitsAsTheLiveBoxShowedIt(int sourceWidth, int sourceHeight, bool croppedAndRotated) => StaTestHost.Run(() =>
    {
        string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_EDGE_TEXT_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidence)) evidence = DiagnosticOutputPaths.Create(evidence);
        using EditorWindowHost host = EditorWindowHost.Create(Solid(sourceWidth, sourceHeight, 0xF0));
        AnnotationEditorController controller = Controller(host.Editor);
        if (croppedAndRotated)
        {
            host.Editor.ApplyCrop(new RectD(100, 40, 420, 260));
            Invoke(host.Editor, "RotateCapture", 1);
            host.Window.UpdateLayout();
        }
        double width = controller.Document.CanvasWidth, height = controller.Document.CanvasHeight;
        string scenario = croppedAndRotated ? $"{sourceWidth}-cropped-rotated" : $"{sourceWidth}";
        AnnotationEditorSurface surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        Grid viewport = Field<Grid>(host.Editor, "_viewport");
        var image = new RectD(0, 0, width, height);
        var failures = new List<string>();
        foreach ((string place, PointD at) in Places(width, height))
            foreach (string text in new[] { Words, Lines })
            {
                string name = $"{scenario} {width}x{height} {place} '{text.Replace('\n', '|')}'";
                controller.Tool = EditorTool.Text;
                Point dip = surface.TranslatePoint(surface.ToSurfacePoint(at), viewport);
                host.Editor.SimulatePointerDown(dip);
                host.Editor.SimulatePointerUp(dip);
                TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
                foreach (char typed in text)
                {
                    box.SelectedText = typed.ToString();
                    box.CaretIndex = box.Text.Length;
                }
                host.Window.UpdateLayout();

                RectD shown = LiveGlyphBounds(box, surface);
                int liveLines = box.LineCount;
                TextAnnotation liveCaption = Assert.Single(host.Editor.CreateCurrentImageSnapshot().Items.OfType<TextAnnotation>());
                RectD live = liveCaption.Rect;
                Point firstGlyph = box.TranslatePoint(box.GetRectFromCharacterIndex(0).TopLeft, surface);
                Point drawnAt = surface.ToSurfacePoint(new PointD(live.X, live.Y));
                byte[] livePixels = Pixels(host.Editor.RenderCurrentImageAsync().GetAwaiter().GetResult());
                Save(evidence, viewport, $"{scenario}-{place}-{(text == Words ? "words" : "lines")}-live.png");

                Invoke(host.Editor, "CommitActiveText");
                host.Window.UpdateLayout();
                TextAnnotation caption = Assert.Single(controller.Document.Items.OfType<TextAnnotation>());
                byte[] committedPixels = Pixels(host.Editor.RenderCurrentImageAsync().GetAwaiter().GetResult());
                Save(evidence, viewport, $"{scenario}-{place}-{(text == Words ? "words" : "lines")}-committed.png");

                if (!Inside(caption.Rect, image)) failures.Add($"{name}: committed box {caption.Rect} leaves the image");
                if (at.X >= width - 8)
                {
                    double minimumWidth = Math.Min(10 * caption.FontSize, width - TextAnnotationLayout.OutlineMargin(caption));
                    if (caption.Rect.Width < minimumWidth - 0.01)
                        failures.Add($"{name}: caption width {caption.Rect.Width} is below the readable minimum {minimumWidth}");
                    if (width < 10 * caption.FontSize && Math.Abs(caption.Rect.Width - minimumWidth) > 0.01)
                        failures.Add($"{name}: narrow-image width {caption.Rect.Width} differs from the cap {minimumWidth}");
                }
                if (!Inside(shown, image, 1)) failures.Add($"{name}: the live box shows glyphs at {shown}, outside the image");
                if (!Inside(shown, caption.Rect, 1.5)) failures.Add($"{name}: live glyphs {shown} lie outside the committed box {caption.Rect}");
                if (caption.Rect != live) failures.Add($"{name}: committed box {caption.Rect} differs from the live fit {live}");
                if (caption.AutoSize != liveCaption.AutoSize
                    || TextAnnotationLayout.KeyOf(caption, 1) != TextAnnotationLayout.KeyOf(liveCaption, 1))
                    failures.Add($"{name}: committed text formatting differs from the live fit");
                if (Math.Abs(box.FontSize / surface.DipPerPixel - caption.FontSize) > 0.01)
                    failures.Add($"{name}: live and committed font sizes differ");
                if (Math.Abs(firstGlyph.X - drawnAt.X) > 0.75 || Math.Abs(firstGlyph.Y - drawnAt.Y) > 0.75)
                    failures.Add($"{name}: live first glyph at {firstGlyph}, committed caption draws it at {drawnAt}");
                if (liveLines != CommittedLines(caption)) failures.Add($"{name}: {liveLines} live lines, {CommittedLines(caption)} committed");
                if (!livePixels.AsSpan().SequenceEqual(committedPixels)) failures.Add($"{name}: committed pixels differ from the live image");
                if (Unclipped(caption) is { } ink && !Inside(ink, image, 1)) failures.Add($"{name}: committed ink {ink} is cut at the image edge");

                controller.SetSelected(null);
                while (controller.Document.Items.Count > 0 && controller.PerformUndo()) { }
            }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    });

    [Fact]
    public void CaptionsNearAnEdgeMoveInsideTheImageAndKeepTheirWrapping() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, 0xF0));
        AnnotationEditorController controller = Controller(host.Editor);
        var probe = new TextAnnotation { FontSize = 18, FontFamily = controller.DefaultFontFamily, AutoSize = true };

        // Too close to the right edge: a readable ten-em box moves left to end at the edge.
        TextLayout edge = controller.FitText(probe, "abc", 18, autoSize: true, new RectD(636, 100, 18, 25));
        Assert.False(edge.AutoSize);
        Assert.Equal(180, edge.Rect.Width, 6);
        Assert.Equal(640, edge.Rect.Right, 6);
        Assert.Equal(100, edge.Rect.Y);

        // With room, the caption keeps its place and wraps at the edge, as before.
        TextLayout roomy = controller.FitText(probe, Words + " " + Words + " " + Words, 18, autoSize: true, new RectD(400, 100, 18, 25));
        Assert.False(roomy.AutoSize);
        Assert.Equal(400, roomy.Rect.X);
        Assert.Equal(640, roomy.Rect.Right, 6);

        // Lines that would run below the image move the caption up; its wrapping does not change.
        TextLayout low = controller.FitText(probe, Lines, 18, autoSize: true, new RectD(40, 350, 18, 25));
        TextLayout high = controller.FitText(probe, Lines, 18, autoSize: true, new RectD(40, 100, 18, 25));
        Assert.Equal(360, low.Rect.Bottom, 6);
        Assert.Equal(40, low.Rect.X);
        Assert.Equal(high.Rect.Width, low.Rect.Width, 6);
        Assert.Equal(high.Rect.Height, low.Rect.Height, 6);

        // A caption started before the top-left corner starts at the corner.
        TextLayout before = controller.FitText(probe, "abc", 18, autoSize: true, new RectD(-6, -6, 18, 25));
        Assert.True(before.AutoSize);
        Assert.Equal(new PointD(0, 0), new PointD(before.Rect.X, before.Rect.Y));

        // Resizing with a handle does not move the caption.
        TextLayout dragged = controller.FitText(probe, Lines, 18, autoSize: true, new RectD(600, 350, 18, 25), keepInsideImage: false);
        Assert.Equal(new PointD(600, 350), new PointD(dragged.Rect.X, dragged.Rect.Y));
    });

    private static IEnumerable<(string Name, PointD At)> Places(double width, double height) =>
    [
        ("top-left", new PointD(2, 2)),
        ("top-right", new PointD(width - 4, 2)),
        ("bottom-left", new PointD(2, height - 4)),
        ("bottom-right", new PointD(width - 4, height - 4)),
        ("right-edge", new PointD(width - 8, height / 2)),
        ("bottom-edge", new PointD(width / 2, height - 3)),
        ("past-bottom-right", new PointD(width + 6, height + 6)),
        ("before-top-left", new PointD(-6, -6)),
    ];

    /// <summary>Every glyph the live box lays out, in image pixels.</summary>
    private static RectD LiveGlyphBounds(TextBox box, AnnotationEditorSurface surface)
    {
        Rect union = Rect.Empty;
        for (int index = 0; index < box.Text.Length; index++)
        {
            // Trailing spaces hang past the wrapping width in both layouts; only ink counts.
            if (char.IsWhiteSpace(box.Text[index])) continue;
            // The leading and trailing edges of the glyph.
            Rect glyph = box.GetRectFromCharacterIndex(index);
            Rect trailing = box.GetRectFromCharacterIndex(index, true);
            if (glyph.IsEmpty || trailing.IsEmpty) continue;
            glyph.Union(trailing);
            union.Union(new Rect(box.TranslatePoint(glyph.TopLeft, surface), box.TranslatePoint(glyph.BottomRight, surface)));
        }
        PointD topLeft = surface.ToImagePoint(union.TopLeft), bottomRight = surface.ToImagePoint(union.BottomRight);
        return RectD.FromCorners(topLeft, bottomRight);
    }

    private static int CommittedLines(TextAnnotation caption)
    {
        var single = (TextAnnotation)caption.Clone();
        single.Text = "가";
        single.AutoSize = true;
        double line = TextAnnotationLayout.Format(single, 1.0).Height;
        return (int)Math.Round(TextAnnotationLayout.Format(caption, 1.0).Height / line);
    }

    /// <summary>The caption's ink drawn on a canvas larger than the image, so nothing clips it.</summary>
    private static RectD? Unclipped(TextAnnotation caption)
    {
        const int Margin = 400;
        var moved = (TextAnnotation)caption.Clone();
        moved.Rect = moved.Rect with { X = moved.Rect.X + Margin, Y = moved.Rect.Y + Margin };
        int width = (int)Math.Ceiling(caption.Rect.Right) + 2 * Margin, height = (int)Math.Ceiling(caption.Rect.Bottom) + 2 * Margin;
        BitmapSource background = Solid(width, height, 0xF0);
        AnnotationDocument document = AnnotationDocument.CreateFor(width, height);
        document.Add(moved);
        byte[] before = Pixels(background), after = Pixels(AnnotationFlattener.Flatten(background, document,
            new AnnotationRenderer(new AnnotationImageStore())));
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int i = 0; i < before.Length; i += 4)
        {
            if (before[i] == after[i] && before[i + 1] == after[i + 1] && before[i + 2] == after[i + 2]) continue;
            int x = (i / 4) % width, y = (i / 4) / width;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < 0 ? null : new RectD(left - Margin, top - Margin, right - left + 1, bottom - top + 1);
    }

    private static bool Inside(RectD inner, RectD outer, double tolerance = 0.01) =>
        inner.X >= outer.X - tolerance && inner.Y >= outer.Y - tolerance
        && inner.Right <= outer.Right + tolerance && inner.Bottom <= outer.Bottom + tolerance;

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }

    private static void Save(string? directory, FrameworkElement element, string name)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(output);
    }
}
