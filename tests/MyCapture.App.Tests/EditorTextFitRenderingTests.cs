using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Captions are drawn whole: no ellipsis, no lines cut at the box, the box around the ink, and
/// pixels that were right before stay byte for byte the same.
/// </summary>
public sealed class EditorTextFitRenderingTests
{
    [Fact]
    public void LegacyCaptionsThatFitTheirBoxKeepTheirExactPixels() => StaTestHost.Run(() =>
    {
        BitmapSource background = Solid(640, 360, 0xF0);
        AnnotationDocument document = AnnotationDocument.TryFromJson("""
            {"schemaVersion":1,"canvasWidth":640,"canvasHeight":360,"items":[
              {"kind":"text","zIndex":0,"rect":{"x":20,"y":20,"width":140,"height":40},"text":"확인 필요","fontSize":22},
              {"kind":"text","zIndex":1,"rect":{"x":20,"y":80,"width":140,"height":100},"text":"두 줄로 감싸지는 긴 문장입니다 정말로","fontSize":18},
              {"kind":"text","zIndex":2,"rect":{"x":200,"y":20,"width":130,"height":40},"text":"Plate","fontSize":20,"bold":true,"background":"#FF1E3A8A","foreground":"#FFFFFFFF"},
              {"kind":"text","zIndex":3,"rect":{"x":300,"y":120,"width":100,"height":50},"text":"회전","fontSize":24,"rotationDegrees":15},
              {"kind":"text","zIndex":4,"isStepNumber":true,"rect":{"x":420,"y":40,"width":36,"height":36},"text":"3","fontSize":18,"bold":true,"background":"#FFEF4444","foreground":"#FFFFFFFF"},
              {"kind":"text","zIndex":5,"rect":{"x":420,"y":200,"width":180,"height":70},"text":"첫 줄\n둘째 줄","fontSize":20,"hasOutline":false}
            ]}
            """)!;
        Assert.All(document.Items.OfType<TextAnnotation>(), text => Assert.False(text.AutoSize));

        byte[] legacy = Pixels(LegacyFlatten(background, document));
        byte[] current = Pixels(AnnotationFlattener.Flatten(background, document, new AnnotationRenderer(new AnnotationImageStore())));

        Assert.Equal(legacy, current);
    });

    [Fact]
    public void CaptionsTheOldRendererCutShortNowShowAllTheirText() => StaTestHost.Run(() =>
    {
        BitmapSource background = Solid(640, 360, 0xF0);
        // The reported case: the size slider enlarged a one-letter caption without widening
        // its box (old renderer: an ellipsis only), and lines below a short box.
        foreach (TextAnnotation cut in new[]
        {
            new TextAnnotation { Rect = new RectD(100, 40, 34, 84), Text = "ㅅ", FontSize = 60 },
            new TextAnnotation { Rect = new RectD(300, 200, 120, 30), Text = "첫 줄\n둘째 줄\n셋째 줄", FontSize = 18 },
        })
        {
            AnnotationDocument document = Single(cut, 640, 360);
            byte[] legacy = Pixels(LegacyFlatten(background, document));
            byte[] current = Pixels(AnnotationFlattener.Flatten(background, document, new AnnotationRenderer(new AnnotationImageStore())));
            byte[] whole = Pixels(ReferenceFlatten(background, cut, wrapWidth: null));

            Assert.NotEqual(legacy, current);
            Assert.Equal(whole, current);
        }
    });

    [Fact]
    public void EditorCaptionsKeepTheirWholeTextInsideTheirBox() => StaTestHost.Run(() =>
    {
        BitmapSource background = Solid(640, 360, 0xF0);
        using EditorWindowHost host = EditorWindowHost.Create(background);
        AnnotationEditorController controller = Controller(host.Editor);
        var fontSize = Field<Slider>(host.Editor, "_fontSizeSlider");

        TextAnnotation enlarged = Type(host, new PointD(100, 40), "ㅅ");
        controller.SetSelected(enlarged);
        host.Window.UpdateLayout();
        fontSize.Value = 96;
        Assert.Equal(96, enlarged.FontSize);
        controller.SetSelected(null);
        fontSize.Value = 18;

        TextAnnotation edge = Type(host, new PointD(500, 140), "오른쪽 끝 근처에서 길게 입력한 문장은 이미지 안에서 줄바꿈됩니다");
        TextAnnotation lines = Type(host, new PointD(40, 220), "a\nb");

        Assert.True(enlarged.AutoSize);
        Assert.False(edge.AutoSize);
        Assert.Equal(640, edge.Rect.Right, 6);
        Assert.True(lines.AutoSize);

        foreach (TextAnnotation caption in new[] { enlarged, edge, lines })
        {
            AnnotationDocument document = Single((TextAnnotation)caption.Clone(), 640, 360);
            BitmapSource flattened = AnnotationFlattener.Flatten(background, document, new AnnotationRenderer(new AnnotationImageStore()));
            double? wrap = caption.AutoSize ? null : caption.Rect.Width;
            Assert.Equal(Pixels(ReferenceFlatten(background, caption, wrap)), Pixels(flattened));

            Int32Rect ink = InkBounds(background, flattened);
            RectD box = caption.Rect;
            Assert.True(ink.X >= Math.Floor(box.Left) - 1 && ink.Y >= Math.Floor(box.Top) - 1
                && ink.X + ink.Width <= Math.Ceiling(box.Right) + 1 && ink.Y + ink.Height <= Math.Ceiling(box.Bottom) + 1,
                $"'{caption.Text}': ink {ink} is outside its box {box}.");
        }
    });

    [Fact]
    public void LegacyNarrowBoxWrapsAtItsStoredWidthWithoutTrimming() => StaTestHost.Run(() =>
    {
        BitmapSource background = Solid(320, 240, 0xF0);
        AnnotationDocument document = AnnotationDocument.TryFromJson("""
            {"schemaVersion":1,"canvasWidth":320,"canvasHeight":240,"items":[
              {"kind":"text","zIndex":0,"rect":{"x":20,"y":20,"width":45,"height":30},"text":"가나다라마바사아","fontSize":20}
            ]}
            """)!;
        var text = (TextAnnotation)document.Items[0];

        BitmapSource flattened = AnnotationFlattener.Flatten(background, document, new AnnotationRenderer(new AnnotationImageStore()));

        Assert.False(text.AutoSize);
        Assert.Equal(Pixels(ReferenceFlatten(background, text, wrapWidth: 45)), Pixels(flattened));
        Assert.True(InkBounds(background, flattened).Height > 60, "Eight syllables at 45 px must take several lines.");
    });

    [Fact]
    public void OpeningAnOldLayerGrowsOnlyBoxesThatCutTheirText() => StaTestHost.Run(() =>
    {
        BitmapSource background = Solid(640, 360, 0xF0);
        AnnotationDocument document = AnnotationDocument.TryFromJson("""
            {"schemaVersion":1,"canvasWidth":640,"canvasHeight":360,"items":[
              {"kind":"text","zIndex":0,"rect":{"x":20,"y":20,"width":140,"height":40},"text":"확인 필요","fontSize":22,"background":"#FF1E3A8A"},
              {"kind":"text","zIndex":1,"rect":{"x":200,"y":40,"width":34,"height":84},"text":"ㅅ","fontSize":60},
              {"kind":"text","zIndex":2,"rect":{"x":300,"y":200,"width":120,"height":30},"text":"첫 줄\n둘째 줄\n셋째 줄","fontSize":18}
            ]}
            """)!;
        TextAnnotation[] captions = [.. document.Items.OfType<TextAnnotation>()];
        RectD fitting = captions[0].Rect;

        using EditorWindowHost host = EditorWindowHost.Create(background, document);

        Assert.Equal(fitting, captions[0].Rect);
        foreach (TextAnnotation cut in captions[1..])
        {
            Assert.False(cut.AutoSize);
            BitmapSource alone = AnnotationFlattener.Flatten(background, Single((TextAnnotation)cut.Clone(), 640, 360),
                new AnnotationRenderer(new AnnotationImageStore()));
            Int32Rect ink = InkBounds(background, alone);
            RectD box = cut.Rect;
            Assert.True(ink.X + ink.Width <= Math.Ceiling(box.Right) + 1 && ink.Y + ink.Height <= Math.Ceiling(box.Bottom) + 1,
                $"'{cut.Text}': ink {ink} is outside its repaired box {box}.");
        }
    });

    [Fact]
    public void LiveSnapshotCommitAndReopenedLayerRenderTheSamePixels() => StaTestHost.Run(() =>
    {
        BitmapSource background = Solid(640, 360, 0xF0);
        byte[] live, committed;
        string json;
        using (EditorWindowHost host = EditorWindowHost.Create(background))
        {
            Invoke(host.Editor, "PlaceTextBox", new PointD(520, 60));
            host.Window.UpdateLayout();
            Field<TextBox>(host.Editor, "_activeTextBox").Text = "편집 중인 스냅샷 문장이 오른쪽 끝에서 줄바꿈됩니다";

            json = host.Editor.CreateCurrentImageSnapshot().ToJson();
            live = Pixels(host.Editor.RenderCurrentImageAsync().GetAwaiter().GetResult());
            Invoke(host.Editor, "CommitActiveText");
            committed = Pixels(host.Editor.RenderCurrentImageAsync().GetAwaiter().GetResult());
        }

        using EditorWindowHost reopened = EditorWindowHost.Create(background, AnnotationDocument.TryFromJson(json));
        byte[] again = Pixels(reopened.Editor.RenderCurrentImageAsync().GetAwaiter().GetResult());

        Assert.Equal(live, committed);
        Assert.Equal(live, again);
    });

    [Fact]
    public void LiveBoxGlyphsSitAndWrapWhereTheCommittedCaptionDrawsThem() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, 0xF0));
        // The real editor window renders display-mode text; the live box must not.
        TextOptions.SetTextFormattingMode(host.Window, TextFormattingMode.Display);
        AnnotationEditorSurface surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");

        foreach ((PointD at, string text, TextWrapping wrapping) in new[]
        {
            (new PointD(60, 50), "가나다 abc", TextWrapping.NoWrap),
            (new PointD(470, 150), "오른쪽 끝에서 줄바꿈되는 입력 중 문장과 영문 words together", TextWrapping.Wrap),
        })
        {
            Invoke(host.Editor, "PlaceTextBox", at);
            TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
            box.Text = text;
            host.Window.UpdateLayout();

            Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(box));
            Assert.Equal(wrapping, box.TextWrapping);
            Point glyph = box.TranslatePoint(box.GetRectFromCharacterIndex(0).TopLeft, surface);
            Point expected = surface.ToSurfacePoint(at);
            Assert.True(Math.Abs(glyph.X - expected.X) <= 0.75 && Math.Abs(glyph.Y - expected.Y) <= 0.75,
                $"First glyph at {glyph}, committed caption at {expected}.");
            int liveLines = box.LineCount;

            Invoke(host.Editor, "CommitActiveText");
            TextAnnotation caption = Controller(host.Editor).Document.Items.OfType<TextAnnotation>().Last();
            var single = (TextAnnotation)caption.Clone();
            single.Text = "가";
            single.AutoSize = true;
            double lineHeight = TextAnnotationLayout.Format(single, 1.0).Height;
            Assert.Equal(liveLines, (int)Math.Round(TextAnnotationLayout.Format(caption, 1.0).Height / lineHeight));
            if (wrapping == TextWrapping.Wrap) Assert.True(liveLines > 1);
        }
    });

    private static TextAnnotation Type(EditorWindowHost host, PointD at, string text)
    {
        Invoke(host.Editor, "PlaceTextBox", at);
        host.Window.UpdateLayout();
        Field<TextBox>(host.Editor, "_activeTextBox").Text = text;
        Invoke(host.Editor, "CommitActiveText");
        return Controller(host.Editor).Document.Items.OfType<TextAnnotation>().Last();
    }

    private static AnnotationDocument Single(TextAnnotation text, int width, int height)
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(width, height);
        document.Add(text);
        return document;
    }

    /// <summary>The text with every glyph laid out and nothing trimmed, drawn without the product's layout code.</summary>
    private static BitmapSource ReferenceFlatten(BitmapSource background, TextAnnotation text, double? wrapWidth) =>
        Flatten(background, dc =>
        {
            FormattedText formatted = Formatted(text, 1.0);
            formatted.Trimming = TextTrimming.None;
            if (wrapWidth is double wrap) formatted.MaxTextWidth = Math.Max(wrap, text.FontSize);
            DrawGlyphs(dc, text, formatted, new Point(text.Rect.X, text.Rect.Y));
        });

    /// <summary>The renderer as it was before captions were fitted: the box as maximum width and height, default trimming.</summary>
    private static BitmapSource LegacyFlatten(BitmapSource background, AnnotationDocument document) =>
        Flatten(background, dc =>
        {
            foreach (TextAnnotation text in document.Items.OfType<TextAnnotation>())
            {
                Rect box = new(text.Rect.X, text.Rect.Y, text.Rect.Width, text.Rect.Height);
                bool rotated = Math.Abs(text.RotationDegrees) > 1e-6;
                if (rotated) dc.PushTransform(new RotateTransform(text.RotationDegrees, box.Left + (box.Width / 2), box.Top + (box.Height / 2)));
                if (text.Background.ToBrushOrNull() is { } plate) dc.DrawRectangle(plate, null, box);
                FormattedText formatted = Formatted(text, 1.0);
                formatted.MaxTextWidth = Math.Max(1, box.Width);
                formatted.MaxTextHeight = Math.Max(1, box.Height);
                Point origin = text.IsStepNumber
                    ? new Point(box.Left + Math.Max(0, (box.Width - formatted.Width) / 2), box.Top + Math.Max(0, (box.Height - formatted.Height) / 2))
                    : new Point(box.Left, box.Top);
                DrawGlyphs(dc, text, formatted, origin);
                if (rotated) dc.Pop();
            }
        });

    private static FormattedText Formatted(TextAnnotation text, double pixelsPerDip) => new(
        text.Text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily(text.FontFamily), text.Italic ? FontStyles.Italic : FontStyles.Normal,
            text.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
        Math.Max(1, text.FontSize),
        text.Foreground.ToBrush(),
        pixelsPerDip);

    private static void DrawGlyphs(DrawingContext dc, TextAnnotation text, FormattedText formatted, Point origin)
    {
        if (text.HasOutline)
        {
            ColorRgba c = text.Foreground;
            double luma = (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);
            ColorRgba outline = luma > 140 ? new ColorRgba(c.A, 0, 0, 0) : new ColorRgba(c.A, 255, 255, 255);
            var pen = new Pen(outline.ToBrush(), Math.Max(1, text.FontSize / 12)) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, formatted.BuildGeometry(origin));
        }

        dc.DrawText(formatted, origin);
    }

    private static BitmapSource Flatten(BitmapSource background, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(background, new Rect(0, 0, background.PixelWidth, background.PixelHeight));
            draw(dc);
        }

        var target = new RenderTargetBitmap(background.PixelWidth, background.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static Int32Rect InkBounds(BitmapSource background, BitmapSource rendered)
    {
        byte[] a = Pixels(background);
        byte[] b = Pixels(rendered);
        int width = rendered.PixelWidth;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2]) continue;
            int x = (i / 4) % width, y = (i / 4) / width;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }

        Assert.True(right >= 0, "Nothing was drawn.");
        return new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }
}
