using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// A caption being typed looks like the caption it commits: the live box is clear, the surface
/// draws the plate and outline from the typed text with the committed caption's renderer, and the
/// box hugs the text at every image scale. The composition an IME shows stays in the box.
/// </summary>
/// <remarks>
/// No real keyboard or IME runs here: the text a Korean IME is composing is already part of the
/// box's text, so the box's own layout queries (what TSF reads for the composition and candidate
/// window) are checked against the committed layout instead.
/// </remarks>
public sealed class EditorLiveCaptionLookTests(ITestOutputHelper output) : KoreanCaptionTest
{
    private const byte Image = 0x80;
    private static readonly Color ImageColor = Color.FromRgb(Image, Image, Image);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void TypingShowsTheCommittedOutlineAndThePictureBetweenTheGlyphs(double scale) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, Image));
        UseDisplayScale(host, scale);
        AnnotationEditorController controller = Controller(host.Editor);
        controller.DefaultFontSize = 40;
        Invoke(host.Editor, "PlaceTextBox", new PointD(60, 50));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        box.Text = "주석 HHH 가나";
        host.Window.UpdateLayout();
        var caption = Assert.IsType<TextAnnotation>(Assert.Single(controller.Document.Items));
        Assert.True(caption.HasOutline);
        Assert.Equal(ColorRgba.Transparent, caption.Background);
        Assert.Equal(0, Assert.IsType<SolidColorBrush>(box.Background).Color.A);

        var view = new Viewport(host, scale);
        (int caretLeft, int caretRight) = view.CaretColumns(box);
        Pixels live = view.Render();
        Invoke(host.Editor, "CommitActiveText");
        controller.SetSelected(null);
        host.Window.UpdateLayout();
        Pixels committed = view.Render();
        Int32Rect area = view.DeviceRect(caption.Rect);

        int compared = 0, matched = 0, committedOutline = 0, liveOutline = 0, picture = 0, pictureKept = 0;
        for (int y = area.Y; y < area.Y + area.Height; y++)
        for (int x = area.X; x < area.X + area.Width; x++)
        {
            if (x >= caretLeft && x <= caretRight) continue;
            Color expected = committed[x, y];
            compared++;
            if (live.HasNear(x, y, expected, 40)) matched++;
            if (IsOutline(expected)) committedOutline++;
            if (IsOutline(live[x, y])) liveOutline++;
            if (committed.IsAround(x, y, ImageColor, 2))
            {
                picture++;
                if (Close(live[x, y], ImageColor, 3)) pictureKept++;
            }
        }

        output.WriteLine($"{scale * 100}%: compared={compared} matched={matched} outline committed={committedOutline} live={liveOutline} picture={picture} kept={pictureKept}");
        Assert.True(matched >= compared * 0.98, $"Only {matched} of {compared} live pixels match the committed caption.");
        Assert.True(committedOutline > 40, $"The fixture must show its outline ({committedOutline} pixels).");
        Assert.InRange(liveOutline, committedOutline * 0.8, committedOutline * 1.25);
        Assert.True(picture > 100, $"The fixture must leave the picture visible between glyphs ({picture} pixels).");
        Assert.Equal(picture, pictureKept);
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void ThePlateIsDrawnByTheRendererOverTheCommittedRectangleWhileTyping(double scale) => StaTestHost.Run(() =>
    {
        Color plate = Color.FromRgb(0x1E, 0x5A, 0xA0);
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, Image));
        UseDisplayScale(host, scale);
        AnnotationEditorController controller = Controller(host.Editor);
        controller.DefaultFontSize = 40;
        Invoke(host.Editor, "PlaceTextBox", new PointD(60, 50));
        Field<TextBox>(host.Editor, "_activeTextBox").Text = "주석 배경";
        Invoke(host.Editor, "CommitActiveText");
        var caption = Assert.IsType<TextAnnotation>(Assert.Single(controller.Document.Items));
        caption.Background = ColorRgba.FromRgb(plate.R, plate.G, plate.B);
        controller.SetSelected(null);
        Field<AnnotationEditorSurface>(host.Editor, "_surface").InvalidateVisual();
        host.Window.UpdateLayout();
        var view = new Viewport(host, scale);
        Pixels committed = view.Render();

        controller.SetSelected(caption);
        Assert.True(host.Editor.HandleShortcut(Key.F2, ModifierKeys.None));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        host.Window.UpdateLayout();
        Assert.Equal(0, Assert.IsType<SolidColorBrush>(box.Background).Color.A);
        (int caretLeft, int caretRight) = view.CaretColumns(box);
        Pixels live = view.Render();

        Int32Rect area = view.DeviceRect(caption.Rect);
        int plated = 0;
        for (int y = area.Y; y < area.Y + area.Height; y++)
        for (int x = area.X; x < area.X + area.Width; x++)
        {
            if ((x >= caretLeft && x <= caretRight) || !committed.IsAround(x, y, plate, 2)) continue;
            plated++;
            Assert.True(Close(live[x, y], plate, 3), $"({x},{y}) is {live[x, y]} while typing, the plate is {plate}.");
        }
        Assert.True(plated > 200, $"The plate must show between the glyphs ({plated} pixels).");

        // Beside the plate, inside the box's frame, the clear box shows the picture as the
        // committed caption does.
        Rect plateRect = view.InRoot(caption.Rect);
        Rect inner = box.TransformToAncestor(view.Root).TransformBounds(new Rect(box.RenderSize));
        inner.Inflate(-box.BorderThickness.Left, -box.BorderThickness.Top);
        Assert.True(plateRect.Left - inner.Left >= 2 && inner.Right - plateRect.Right >= 2,
            $"The fixture must leave room beside the plate {plateRect} inside the box {inner}.");
        double middle = plateRect.Top + plateRect.Height / 2;
        foreach (Point probe in new[]
        {
            new Point((inner.Left + plateRect.Left) / 2, middle),
            new Point((plateRect.Right + inner.Right) / 2, middle),
        })
        {
            int px = (int)Math.Floor(probe.X * scale), py = (int)Math.Floor(probe.Y * scale);
            Assert.True(Close(live[px, py], ImageColor, 3), $"{probe}: {live[px, py]} inside the box, the picture is {ImageColor}.");
            Assert.True(Close(committed[px, py], ImageColor, 3), $"{probe}: {committed[px, py]} after commit.");
        }
    });

    [Theory]
    [InlineData(760.0, 1.0)]
    [InlineData(1000.0, 1.5)]
    public void TheLiveBoxHugsTheTextAtEveryImageScale(double windowWidth, double scale) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(1600, 900, Image), beforeShow: window => window.Width = windowWidth);
        UseDisplayScale(host, scale);
        AnnotationEditorController controller = Controller(host.Editor);
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        Assert.True(surface.DipPerPixel < 0.8, $"The fixture must show the image reduced ({surface.DipPerPixel} DIP per pixel).");

        foreach (string content in new[] { "", "가", "주석 가나다" })
        {
            Invoke(host.Editor, "PlaceTextBox", new PointD(200, 200));
            TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
            box.Text = content;
            host.Window.UpdateLayout();
            TextAnnotation live = Assert.IsType<TextAnnotation>(controller.LiveTextForPaint());
            Assert.Equal(content, live.Text);
            Rect target = surface.ToSurfaceRect(live.Rect);
            Point inset = Field<Point>(host.Editor, "_liveTextInset");
            double sides = 2 * inset.X;
            double ends = inset.Y + box.Padding.Bottom + box.BorderThickness.Bottom;
            double em = box.FontSize;
            output.WriteLine($"{windowWidth}/{scale * 100}% '{content}': box={box.ActualWidth:0.00}x{box.ActualHeight:0.00} caption={target.Width:0.00}x{target.Height:0.00} em={em:0.00}");
            // The text and its caret room, or one em, inside the chrome; nothing more.
            Assert.InRange(box.ActualWidth, target.Width + sides - 0.5, Math.Max(target.Width + sides + 2, em + sides) + 0.5);
            Assert.InRange(box.ActualHeight, em + ends - 0.5, target.Height + ends + 0.5);
            Assert.True(box.ActualWidth < 160 && box.ActualHeight < 32, "No fixed minimum widens or heightens a small caption's box.");
            Invoke(host.Editor, "CommitActiveText");
        }
    });

    [Fact]
    public void ColourPlateAndOutlineChangesShowOnTheOpenCaptionAtOnce() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, Image));
        AnnotationEditorController controller = Controller(host.Editor);
        controller.DefaultFontSize = 40;
        Invoke(host.Editor, "PlaceTextBox", new PointD(60, 50));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        box.Text = "주석 HHH";
        host.Window.UpdateLayout();
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        Int32Rect area = DeviceRect(surface, Assert.IsType<TextAnnotation>(controller.LiveTextForPaint()).Rect, 1);

        // The surface alone: the plate and outline drawn for the open caption, without its glyphs.
        Assert.True(Count(RenderSurface(host, surface), area, IsOutline) > 40);

        // The property bar commits the caption before a toggle applies. The same changes applied
        // to the open caption (as a swatch does under a simulated pointer) show on the next paint.
        Invoke(host.Editor, "ApplyTextOutline", false);
        Assert.Equal(0, Count(RenderSurface(host, surface), area, IsOutline));
        controller.ApplyTextBackground(true);
        Assert.True(Count(RenderSurface(host, surface), area, color => Close(color, Colors.Black, 2)) > area.Width * area.Height * 0.8);
        controller.ApplyTextBackground(false);
        Invoke(host.Editor, "ApplyTextOutline", true);
        controller.ApplyStrokeColor(ColorRgba.FromRgb(20, 20, 20));
        Assert.True(Count(RenderSurface(host, surface), area, color => color.R > 235 && color.G > 235 && color.B > 235) > 40,
            "A dark caption's outline turns light at once.");
        Assert.Equal(Color.FromRgb(20, 20, 20), Assert.IsType<SolidColorBrush>(box.Foreground).Color);
        // Its caret turns black inside a light ring with it.
        Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(box.CaretBrush).Color);
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(EditorTextEntryFrameTests.CaretRing(box).Fill).Color);
        Assert.Same(box, Field<TextBox>(host.Editor, "_activeTextBox"));
    });

    [Fact]
    public void ALongCaptionKeepsItsPlateWhileTypingAndItsOutlineForTheCommit() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, Image));
        AnnotationEditorController controller = Controller(host.Editor);
        Invoke(host.Editor, "PlaceTextBox", new PointD(20, 20));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        string longest = string.Concat(Enumerable.Repeat("가나다라마바사 ", AnnotationEditorController.LiveOutlineLimit / 8 + 1))[..AnnotationEditorController.LiveOutlineLimit];
        box.Text = longest;
        host.Window.UpdateLayout();
        var caption = Assert.IsType<TextAnnotation>(Assert.Single(controller.Document.Items));
        controller.ApplyTextBackground(true);
        Assert.True(Assert.IsType<TextAnnotation>(controller.LiveTextForPaint()).HasOutline);

        // One character more and typing no longer traces the outline; the plate stays.
        box.Text = longest + "끝";
        host.Window.UpdateLayout();
        TextAnnotation live = Assert.IsType<TextAnnotation>(controller.LiveTextForPaint());
        Assert.False(live.HasOutline);
        Assert.Equal(ColorRgba.Black, live.Background);
        Assert.True(caption.HasOutline, "Only the live view leaves the outline out; the caption keeps it.");
        Invoke(host.Editor, "CommitActiveText");
        Assert.Equal(longest + "끝", caption.Text);
        Assert.True(caption.HasOutline);
        Assert.Null(controller.LiveTextForPaint());
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void AScrolledCaptionKeepsItsOutlineOnTheGlyphsTheBoxShows(double scale) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, Image));
        UseDisplayScale(host, scale);
        AnnotationEditorController controller = Controller(host.Editor);
        controller.DefaultFontSize = 40;
        Invoke(host.Editor, "PlaceTextBox", new PointD(60, 20));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        // Taller than the canvas and short enough to keep its outline while typing; no two
        // neighbouring lines alike, so an outline a line off would not match.
        box.Text = string.Join("\n", Enumerable.Range(1, 14).Select(line => new string('가', 1 + (line % 4)) + new string('H', line % 3)));
        Assert.True(box.Text.Length <= AnnotationEditorController.LiveOutlineLimit);
        box.CaretIndex = 0;
        host.Window.UpdateLayout();
        var scroll = Assert.IsType<ScrollViewer>(box.Template.FindName("PART_ContentHost", box));
        Assert.True(scroll.ScrollableHeight > 60, $"The fixture must overflow the canvas ({scroll.ScrollableHeight:0.0} DIP).");
        Assert.Equal(0, Assert.NotNull(controller.LiveTextScroll).Offset);

        // Forty DIP: a whole number of device pixels at both scales, and less than a line.
        const double offset = 40;
        scroll.ScrollToVerticalOffset(offset);
        host.Window.UpdateLayout();
        Assert.Equal(offset, scroll.VerticalOffset, 3);
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        LiveCaptionScroll live = Assert.NotNull(controller.LiveTextScroll);
        Assert.Equal(offset / surface.DipPerPixel, live.Offset, 3);

        var view = new Viewport(host, scale);
        (int caretLeft, int caretRight) = view.CaretColumns(box);
        var presenter = Assert.IsType<ScrollContentPresenter>(scroll.Template.FindName("PART_ScrollContentPresenter", scroll));
        Rect shownText = presenter.TransformToAncestor(view.Root).TransformBounds(new Rect(presenter.RenderSize));
        Pixels shown = view.Render();
        Invoke(host.Editor, "CommitActiveText");
        var caption = Assert.IsType<TextAnnotation>(Assert.Single(controller.Document.Items));
        controller.SetSelected(null);
        host.Window.UpdateLayout();
        Pixels committed = view.Render();

        // Where the box shows text over the picture, it shows the committed caption moved up by
        // the scroll: the glyphs from the box, the outline from the surface.
        int shift = (int)Math.Round(offset * scale);
        Int32Rect picture = view.DeviceRect(new RectD(0, 0, 640, 360));
        Int32Rect area = view.DeviceRect(caption.Rect);
        int top = Math.Max((int)Math.Ceiling(shownText.Top * scale) + 1, Math.Max(area.Y, picture.Y));
        int bottom = Math.Min((int)Math.Floor(shownText.Bottom * scale) - 1, picture.Y + picture.Height - shift - 1);
        int compared = 0, matched = 0, outline = 0;
        for (int y = top; y < bottom; y++)
        for (int x = area.X; x < area.X + area.Width; x++)
        {
            if (x >= caretLeft && x <= caretRight) continue;
            Color expected = committed[x, y + shift];
            compared++;
            if (shown.HasNear(x, y, expected, 40)) matched++;
            if (IsOutline(expected)) outline++;
        }

        output.WriteLine($"{scale * 100}%: rows {top}-{bottom} compared={compared} matched={matched} outline={outline}");
        Assert.True(compared > 2000, $"The fixture must compare a visible band ({compared} pixels).");
        Assert.True(outline > 40, $"The band must hold outline ({outline} pixels).");
        Assert.True(matched >= compared * 0.98, $"Only {matched} of {compared} scrolled pixels match the committed caption.");
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void TheCaretOfAWhiteCaptionShowsOverAWhitePicture(double scale) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, 0xFF));
        UseDisplayScale(host, scale);
        AnnotationEditorController controller = Controller(host.Editor);
        controller.DefaultFontSize = 40;
        controller.StrokeColor = ColorRgba.White;
        Invoke(host.Editor, "PlaceTextBox", new PointD(60, 50));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        // After a space, so no glyph covers the caret.
        box.Text = "자막 ";
        box.CaretIndex = box.Text.Length;
        host.Window.UpdateLayout();
        TextAnnotation live = Assert.IsType<TextAnnotation>(controller.LiveTextForPaint());
        Assert.Equal(ColorRgba.White, live.Foreground);
        Assert.True(live.HasOutline);
        // A white caret inside a black ring: white like the glyphs, outlined like them.
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(box.CaretBrush).Color);

        var view = new Viewport(host, scale);
        Rect caret = box.GetRectFromCharacterIndex(box.CaretIndex);
        Rect inRoot = box.TransformToAncestor(view.Root).TransformBounds(caret);
        (int left, int right) = view.CaretColumns(box);
        int top = (int)Math.Ceiling(inRoot.Top * scale) + 1, bottom = (int)Math.Floor(inRoot.Bottom * scale) - 1;
        Pixels pixels = view.Render();
        int dark = 0;
        for (int y = top; y < bottom; y++)
        for (int x = left; x <= right; x++)
            if (IsOutline(pixels[x, y])) dark++;

        // The caret box shows no caret off screen; its ring, a caret width and a DIP either side,
        // is what marks the insertion point here.
        int rows = bottom - top;
        output.WriteLine($"{scale * 100}%: rows={rows} dark={dark} columns {left}-{right}");
        Assert.True(rows > 20, $"The caret must span the line ({rows} rows).");
        Assert.True(dark >= rows * 2, $"Only {dark} dark pixels mark a caret {rows} rows tall over the white picture.");
        // Between the ring and the box's frame, the picture stays white: the dark pixels are the ring's.
        Assert.Equal(0, Count(pixels, new Int32Rect(right + 1, top, (int)Math.Floor(3 * scale), rows), IsOutline));
    });

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.0, true)]
    [InlineData(1.5, false)]
    [InlineData(1.5, true)]
    public void KoreanCompositionSitsWhereTheCommittedCaptionDrawsIt(double scale, bool middle) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, 0xF0));
        UseDisplayScale(host, scale);
        AnnotationEditorController controller = Controller(host.Editor);
        controller.DefaultFontSize = 32;
        Invoke(host.Editor, "PlaceTextBox", new PointD(60, 50));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        Assert.True(InputMethod.GetIsInputMethodEnabled(box));
        // The syllable being composed is part of the box's text, at the caret.
        string text = middle ? "주석 가 끝맺음" : "주석 가";
        int syllable = text.IndexOf('가');
        box.Text = text;
        box.CaretIndex = syllable + 1;
        host.Window.UpdateLayout();
        Assert.Equal(255, Assert.IsType<SolidColorBrush>(box.Foreground).Color.A);

        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        TextAnnotation live = Assert.IsType<TextAnnotation>(controller.LiveTextForPaint());
        Assert.Equal(text, live.Text);
        FormattedText layout = TextAnnotationLayout.Format(live, VisualTreeHelper.GetDpi(surface).PixelsPerDip);
        var origin = new Point(live.Rect.X, live.Rect.Y);
        Rect cell = layout.BuildHighlightGeometry(origin, syllable, 1).Bounds;
        Point committedStart = surface.ToSurfacePoint(new PointD(cell.Left, cell.Top));
        Point committedEnd = surface.ToSurfacePoint(new PointD(cell.Right, cell.Top));
        double line = cell.Height * surface.DipPerPixel;

        Rect start = box.GetRectFromCharacterIndex(syllable);
        Rect caret = box.GetRectFromCharacterIndex(box.CaretIndex);
        Point liveStart = box.TranslatePoint(start.TopLeft, surface);
        Point liveCaret = box.TranslatePoint(caret.TopLeft, surface);
        output.WriteLine($"{scale * 100}% middle={middle}: syllable {liveStart} vs {committedStart}, caret {liveCaret} vs {committedEnd}, height {caret.Height:0.000} vs {line:0.000}");
        // The same 0.75 DIP the first glyph keeps from the caption origin (EditorTextEntryFrameTests).
        const double near = 0.75;
        Assert.InRange(liveStart.X - committedStart.X, -near, near);
        Assert.InRange(liveStart.Y - committedStart.Y, -near, near);
        Assert.InRange(liveCaret.X - committedEnd.X, -near, near);
        Assert.InRange(liveCaret.Y - committedEnd.Y, -near, near);
        Assert.InRange(caret.Height - line, -near, near);
        Assert.InRange(start.Height - line, -near, near);

        // The outline under the composing syllable is drawn from the same text.
        Int32Rect under = DeviceRect(surface, new RectD(cell.X, cell.Y, cell.Width, cell.Height), scale);
        Assert.True(Count(RenderSurface(host, surface, scale), under, IsOutline) > 10, "The live outline must reach the syllable being composed.");
    });

    private static void UseDisplayScale(EditorWindowHost host, double scale)
    {
        if (scale == 1) return;
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        DpiScale old = VisualTreeHelper.GetDpi(surface);
        VisualTreeHelper.SetRootDpi(host.Window, new DpiScale(scale, scale));
        Invoke(surface, "OnDpiChanged", old, new DpiScale(scale, scale));
        host.Window.UpdateLayout();
        Assert.Equal(scale, VisualTreeHelper.GetDpi(surface).PixelsPerDip, 6);
    }

    private static bool IsOutline(Color color) => color.R < 50 && color.G < 50 && color.B < 50;

    private static bool Close(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static int Count(Pixels pixels, Int32Rect area, Func<Color, bool> match)
    {
        int count = 0;
        for (int y = area.Y; y < area.Y + area.Height; y++)
        for (int x = area.X; x < area.X + area.Width; x++)
            if (match(pixels[x, y])) count++;
        return count;
    }

    private static Int32Rect DeviceRect(AnnotationEditorSurface surface, RectD image, double scale)
    {
        Rect dip = surface.ToSurfaceRect(image);
        int left = (int)Math.Ceiling(dip.Left * scale), top = (int)Math.Ceiling(dip.Top * scale);
        int right = (int)Math.Floor(dip.Right * scale), bottom = (int)Math.Floor(dip.Bottom * scale);
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static Pixels RenderSurface(EditorWindowHost host, AnnotationEditorSurface surface, double scale = 1)
    {
        host.Window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * scale),
            (int)Math.Ceiling(surface.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        return new Pixels(bitmap);
    }

    /// <summary>The editor's canvas: the surface with the live box over it.</summary>
    private sealed class Viewport(EditorWindowHost host, double scale)
    {
        internal Grid Root { get; } = Field<Grid>(host.Editor, "_viewport");

        private AnnotationEditorSurface Surface { get; } = Field<AnnotationEditorSurface>(host.Editor, "_surface");

        internal Pixels Render()
        {
            host.Window.UpdateLayout();
            double width = Root.ActualWidth, height = Root.ActualHeight;
            var drawing = new DrawingVisual();
            using (DrawingContext dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(Root)
                {
                    Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height),
                }, null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            return new Pixels(bitmap);
        }

        internal Rect InRoot(RectD image) => Surface.TransformToAncestor(Root).TransformBounds(Surface.ToSurfaceRect(image));

        internal Int32Rect DeviceRect(RectD image)
        {
            Rect dip = InRoot(image);
            int left = (int)Math.Ceiling(dip.Left * scale), top = (int)Math.Ceiling(dip.Top * scale);
            int right = (int)Math.Floor(dip.Right * scale), bottom = (int)Math.Floor(dip.Bottom * scale);
            return new Int32Rect(left, top, right - left, bottom - top);
        }

        /// <summary>Device columns the blinking caret may cover, two DIP either side of it.</summary>
        internal (int Left, int Right) CaretColumns(TextBox box)
        {
            Point caret = box.TranslatePoint(box.GetRectFromCharacterIndex(box.CaretIndex).TopLeft, Root);
            return ((int)Math.Floor((caret.X - 2) * scale), (int)Math.Ceiling((caret.X + 2) * scale));
        }
    }

    private sealed class Pixels
    {
        private readonly byte[] _bytes;

        internal Pixels(BitmapSource bitmap)
        {
            Width = bitmap.PixelWidth;
            Height = bitmap.PixelHeight;
            _bytes = new byte[Width * Height * 4];
            bitmap.CopyPixels(_bytes, Width * 4, 0);
        }

        internal int Width { get; }

        internal int Height { get; }

        internal Color this[int x, int y]
        {
            get
            {
                int at = (Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)) * 4;
                return Color.FromRgb(_bytes[at + 2], _bytes[at + 1], _bytes[at]);
            }
        }

        /// <summary>Whether a pixel within one device pixel of (x, y) is within <paramref name="tolerance"/> of <paramref name="color"/>.</summary>
        internal bool HasNear(int x, int y, Color color, int tolerance)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (Close(this[x + dx, y + dy], color, tolerance)) return true;
            return false;
        }

        /// <summary>Whether (x, y) and every pixel next to it are <paramref name="color"/>.</summary>
        internal bool IsAround(int x, int y, Color color, int tolerance)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (!Close(this[x + dx, y + dy], color, tolerance)) return false;
            return true;
        }
    }
}
