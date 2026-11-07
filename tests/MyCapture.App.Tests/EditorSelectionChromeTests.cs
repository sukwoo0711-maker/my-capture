using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Selection chrome is checked against the surface's recorded drawings and rendered pixels.</summary>
public sealed class EditorSelectionChromeTests(ITestOutputHelper output) : KoreanCaptionTest
{
    private static readonly ColorRgba Ink = ColorRgba.FromRgb(230, 48, 60);

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void CaptionFrameDrawnHandlesAndPointerIntentShareGeometry(double dpi) => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(dpi);
        TextAnnotation text = host.Caption("주석 선택 손잡이");
        RectD ink = TextAnnotationLayout.InkBounds(text);
        RectD frame = PointerIntentGeometry.SelectionFrame(text, host.Scale);
        double offset = PointerIntentGeometry.CaptionFrameOffsetDip / host.Scale;
        AssertRect(new RectD(ink.X - offset, ink.Y - offset, ink.Width + offset * 2, ink.Height + offset * 2), frame);
        _ = host.Render();
        GeometryDrawing dashed = Assert.Single(Drawings(host.Surface), drawing => drawing.Pen?.DashStyle.Dashes.Count == 2);
        AssertRect(host.Surface.ToSurfaceRect(frame), Assert.IsType<RectangleGeometry>(dashed.Geometry).Rect);

        IReadOnlyList<SelectionHandle> handles = host.Controller.GetSelectionHandles(host.Scale);
        Assert.Equal(6, handles.Count);
        Rect[] drawn = SquareHandles(host.Surface);
        Assert.Equal(handles.Count, drawn.Length);
        foreach (SelectionHandle handle in handles)
        {
            PointD position = handle.Position;
            Assert.True(Close(position.X, frame.Left) || Close(position.X, frame.Right));
            Point centre = host.Surface.ToSurfacePoint(position);
            Assert.Contains(drawn, rect => Close(rect.X + 4, centre.X) && Close(rect.Y + 4, centre.Y));
            Assert.Equal(handle.Handle, host.Controller.HandleAt(position, 8 / host.Scale));
            PointerIntent intent = host.Controller.ResolvePointerIntent(position);
            Assert.Equal(PointerAction.Resize, intent.Action);
            Assert.Equal(handle.Handle, intent.Handle);
        }

        // Away from the two corner handles, the top gap has no height handle of its own.
        PointD gap = new(frame.Center.X, (ink.Top + frame.Top) / 2);
        PointerIntent move = host.Controller.ResolvePointerIntent(gap);
        Assert.Equal(PointerAction.Move, move.Action);
        Assert.Equal(EditorCursorKind.Move, move.Cursor);
        RectD before = text.Rect;
        host.Controller.PointerDown(gap);
        Assert.Equal(move, host.Controller.ActivePointerIntent);
        host.Controller.PointerMove(gap.Offset(12, 9));
        Assert.Equal(before.X + 12, text.Rect.X, 7);
        Assert.Equal(before.Y + 9, text.Rect.Y, 7);
        host.Controller.CancelGesture();
        Assert.Equal(before, text.Rect);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    public void AnItemDrawnInTheCaptionFrameGapStillTakesItsOwnPress(double dpi) => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(dpi);
        TextAnnotation text = host.Caption("주석 선택 손잡이");
        RectD ink = TextAnnotationLayout.InkBounds(text);
        RectD frame = PointerIntentGeometry.SelectionFrame(text, host.Scale);
        PointD gap = new(frame.Center.X, (ink.Top + frame.Top) / 2);
        Assert.Same(text, host.Controller.ResolvePointerIntent(gap).Target);

        var mark = new RectangleAnnotation { Rect = new RectD(gap.X - 3, gap.Y - 3, 6, 6), Stroke = Ink, Fill = Ink, StrokeThickness = 1 };
        host.Controller.Document.Add(mark);
        host.Controller.SetSelected(text);
        PointerIntent press = host.Controller.ResolvePointerIntent(gap);
        Assert.Equal(PointerAction.Move, press.Action);
        Assert.Same(mark, press.Target);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void PaintedLeftHandleClearsOutlinedJuAndPreservesItsLeftmostInkColumn(double dpi) => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(dpi);
        TextAnnotation text = host.Caption("주석 선택 손잡이");
        host.Controller.SetSelected(null);
        Pixels bare = host.Render();
        host.Controller.SetSelected(text);
        Pixels selected = host.Render();
        SelectionHandle left = Assert.Single(host.Controller.GetSelectionHandles(host.Scale), handle => handle.Handle == ResizeHandle.MiddleLeft);
        Point centre = host.Surface.ToSurfacePoint(left.Position);
        Rect handle = Assert.Single(SquareHandles(host.Surface), rect => Close(rect.X + 4, centre.X) && Close(rect.Y + 4, centre.Y));
        // Read the actual surface pen: the eight-DIP rectangle paints ten DIP across.
        GeometryDrawing handleDrawing = Assert.Single(Drawings(host.Surface), drawing =>
            drawing.Brush is not null && drawing.Geometry is RectangleGeometry rect && rect.Rect == handle && drawing.Pen?.Thickness == 2);
        Rect paintedHandle = handleDrawing.Geometry.GetRenderBounds(handleDrawing.Pen);
        Assert.Equal(10, paintedHandle.Width, 7);
        Assert.Equal(10, paintedHandle.Height, 7);

        Geometry outlined = OutlinedFirstGlyph(text, dpi);
        Matrix documentToSurface = new(host.Scale, 0, 0, host.Scale, host.Surface.FrameOrigin.X, host.Surface.FrameOrigin.Y);
        outlined.Transform = new MatrixTransform(documentToSurface);
        Geometry intersection = Geometry.Combine(outlined, new RectangleGeometry(paintedHandle), GeometryCombineMode.Intersect, null);
        Assert.Equal(0, intersection.GetArea(), 7);
        double clearance = (outlined.Bounds.Left - paintedHandle.Right) * dpi;
        Assert.True(clearance >= 1 - 1e-7, $"{dpi * 100}%: painted handle-to-ink clearance is {clearance:F6} device pixels.");

        RectD inkBox = TextAnnotationLayout.InkBounds(text);
        double inkLeft = host.Surface.ToSurfacePoint(new PointD(inkBox.Left, inkBox.Center.Y)).X;
        double[] candidateClearances = Enumerable.Range(4, 4)
            .Select(offset => (outlined.Bounds.Left - (inkLeft - offset + 5)) * dpi).ToArray();
        output.WriteLine($"{dpi * 100}%: offset={PointerIntentGeometry.CaptionFrameOffsetDip} DIP; clearance={clearance:F6} device px; " +
            string.Join(", ", candidateClearances.Select((value, i) => $"offset {i + 4}: {value:F6}px")));
        int smallestWholeDip = Array.FindIndex(candidateClearances, value => value >= 1 - 1e-7) + 4;
        Assert.Equal(PointerIntentGeometry.CaptionFrameOffsetDip, smallestWholeDip);
        Assert.True(smallestWholeDip > 4);
        Assert.True(candidateClearances[smallestWholeDip - 5] < 1);

        // Find actual foreground pixels, not the outline or the selection pen, in the first glyph.
        Rect firstBounds = outlined.Bounds;
        int x0 = Math.Max(0, (int)Math.Floor(firstBounds.Left * dpi));
        int x1 = Math.Min(bare.Width - 1, (int)Math.Ceiling(firstBounds.Right * dpi));
        int y0 = Math.Max(0, (int)Math.Floor(firstBounds.Top * dpi));
        int y1 = Math.Min(bare.Height - 1, (int)Math.Ceiling(firstBounds.Bottom * dpi));
        int leftmost = Enumerable.Range(x0, x1 - x0 + 1).First(x =>
            Enumerable.Range(y0, y1 - y0 + 1).Any(y => IsCaptionInk(bare[x, y])));
        int kept = 0;
        for (int y = y0; y <= y1; y++)
        {
            if (!IsCaptionInk(bare[leftmost, y])) continue;
            Assert.Equal(bare[leftmost, y], selected[leftmost, y]);
            Assert.True(IsCaptionInk(selected[leftmost, y]));
            kept++;
        }
        Assert.True(kept > 0);
        if (dpi == 1.5) selected.SaveIfRequested("caption-selected-150.png");
    });

    [Fact]
    public void ItalicInkOutsideTheStoredBoxIsIncludedInTheFrame() => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(1);
        TextAnnotation text = host.Caption("f");
        text.FontFamily = "Times New Roman";
        text.Italic = true;
        text.FontSize = 64;
        text.Rect = TextAnnotationLayout.Measure(text);
        Geometry painted = OutlinedGlyphs(text, 1);
        RectD ink = TextAnnotationLayout.InkBounds(text);
        Assert.True(painted.Bounds.Left < text.Rect.Left || painted.Bounds.Right > text.Rect.Right,
            "The italic fixture must actually overhang its stored box.");
        // WPF widens translated paths at float precision; cached local bounds are doubles.
        // Allow only that subpixel rounding, not any visible overhang or handle overlap.
        const double geometryRounding = 0.00001;
        Assert.True(ink.Left <= painted.Bounds.Left + geometryRounding && ink.Right >= painted.Bounds.Right - geometryRounding,
            $"Stored={text.Rect}; cached ink={ink}; actual outlined glyph={painted.Bounds}; " +
            $"left delta={ink.Left - painted.Bounds.Left:R}; right delta={ink.Right - painted.Bounds.Right:R}");
        RectD frame = PointerIntentGeometry.SelectionFrame(text, 1);
        Assert.Equal(ink.Left - PointerIntentGeometry.CaptionFrameOffsetDip, frame.Left, 7);
        Assert.Equal(ink.Right + PointerIntentGeometry.CaptionFrameOffsetDip, frame.Right, 7);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void TinyCaptionsKeepTheTwentyFourDipHandleSeparation(double dpi) => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(dpi);
        TextAnnotation text = host.Caption(".");
        text.FontSize = 1;
        text.Rect = TextAnnotationLayout.Measure(text);
        RectD frame = PointerIntentGeometry.SelectionFrame(text, host.Scale);
        Assert.Equal(24, frame.Width * host.Scale, 7);
        Assert.Equal(24, frame.Height * host.Scale, 7);
        _ = host.Render();
        Rect[] drawn = SquareHandles(host.Surface);
        Assert.Equal(6, drawn.Length);
        foreach (SelectionHandle handle in host.Controller.GetSelectionHandles(host.Scale))
        {
            Point centre = host.Surface.ToSurfacePoint(handle.Position);
            Assert.Contains(drawn, rect => Close(rect.X + 4, centre.X) && Close(rect.Y + 4, centre.Y));
            Assert.Equal(handle.Handle, host.Controller.HandleAt(handle.Position, 8 / host.Scale));
        }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void MovedCaptionHandlesPreserveWrapDeltaCornerScalingAndUndo(double dpi) => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(dpi);
        TextAnnotation text = host.Caption("주석 선택 손잡이");
        RectD before = text.Rect;
        double font = text.FontSize;
        int history = host.Undo.UndoCount;
        PointD right = Handle(host, ResizeHandle.MiddleRight);
        PointD target = right.Offset(30 / host.Scale, 0);
        host.Controller.PointerDown(right);
        host.Controller.PointerMove(target);
        Assert.False(text.AutoSize);
        Assert.Equal(before.Width + 30 / host.Scale, text.Rect.Width, 6);
        Assert.Equal(target.X, Handle(host, ResizeHandle.MiddleRight).X, 6);
        Assert.Equal(font, text.FontSize);
        host.Controller.PointerUp(target);
        Assert.Equal(history + 1, host.Undo.UndoCount);
        Assert.True(host.Controller.PerformUndo());
        Assert.Equal(before, text.Rect);
        Assert.True(text.AutoSize);

        PointD corner = Handle(host, ResizeHandle.BottomRight);
        PointD grown = corner.Offset(before.Width / 2, before.Height / 2);
        host.Controller.PointerDown(corner);
        host.Controller.PointerMove(grown);
        host.Controller.PointerUp(grown);
        Assert.True(text.FontSize > font);
        Assert.True(text.Rect.Width > before.Width);
        Assert.Equal(history + 1, host.Undo.UndoCount);
        Assert.True(host.Controller.PerformUndo());
        Assert.Equal(font, text.FontSize);
        Assert.Equal(before, text.Rect);
    });

    [Fact]
    public void RotatedCaptionDrawsEveryHandleAtItsRotatedHitTarget() => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(1.5);
        TextAnnotation text = host.Caption("주석 회전 선택");
        text.RotationDegrees = 30;
        _ = host.Render();
        RectD frame = PointerIntentGeometry.SelectionFrame(text, host.Scale);
        Rect[] drawn = SquareHandles(host.Surface);
        foreach (SelectionHandle handle in host.Controller.GetSelectionHandles(host.Scale))
        {
            PointD expected = GeometryMath.Rotate(PointerIntentGeometry.HandlePoint(frame, handle.Handle), text.Rect.Center, 30);
            AssertPoint(expected, handle.Position);
            Point centre = host.Surface.ToSurfacePoint(expected);
            Assert.Contains(drawn, rect => Close(rect.X + 4, centre.X) && Close(rect.Y + 4, centre.Y));
            Assert.Equal(handle.Handle, host.Controller.HandleAt(expected, 8 / host.Scale));
            Assert.Equal(PointerAction.Resize, host.Controller.ResolvePointerIntent(expected).Action);
        }
        Assert.Equal(6, drawn.Length);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrowEndpointDragSnapsLikeCreationAndHasOneUndoOrCancel(bool start) => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(1.25);
        PolylineAnnotation line = PolylineAnnotation.CreateArrow(new(130, 130), new(330, 230), Ink, 3);
        host.Controller.Document.Add(line);
        host.Controller.SetSelected(line);
        PointD[] before = [.. line.Points];
        IReadOnlyList<SelectionHandle> handles = host.Controller.GetSelectionHandles(host.Scale);
        Assert.Equal(2, handles.Count);
        Assert.Equal(before[0], Assert.Single(handles, h => h.Handle == ResizeHandle.LineStart).Position);
        Assert.Equal(before[1], Assert.Single(handles, h => h.Handle == ResizeHandle.LineEnd).Position);
        ResizeHandle which = start ? ResizeHandle.LineStart : ResizeHandle.LineEnd;
        int moved = start ? 0 : 1, fixedIndex = 1 - moved;
        PointD press = before[moved], target = new(260, 280);
        Assert.Equal(which, host.Controller.HandleAt(press.Offset(4 / host.Scale, 0), 8 / host.Scale));
        PointerIntent intent = host.Controller.ResolvePointerIntent(press);
        Assert.Equal(PointerAction.Resize, intent.Action);
        Assert.Equal(which, intent.Handle);
        Assert.Equal(EditorCursorKind.ResizeNorthwestSoutheast, intent.Cursor);
        host.Controller.PointerDown(press);
        host.Controller.PointerMove(target);
        Assert.Equal(before[fixedIndex], line.Points[fixedIndex]);
        Assert.Equal(target, line.Points[moved]);
        host.Controller.PointerMove(target.Offset(23, -19));
        host.Controller.PointerUp(target);
        Assert.Equal(1, host.Undo.UndoCount);
        Assert.True(host.Controller.PerformUndo());
        Assert.Equal(before, line.Points);

        var creation = new SurfaceHost(1.25);
        creation.Controller.Tool = EditorTool.Arrow;
        creation.Controller.PointerDown(before[fixedIndex]);
        creation.Controller.PointerMove(target, shift: true);
        creation.Controller.PointerUp(target, shift: true);
        PointD snapped = Assert.Single(creation.Controller.Document.Items.OfType<PolylineAnnotation>()).Points[1];
        host.Controller.PointerDown(press, shift: true);
        host.Controller.PointerMove(target, shift: true);
        Assert.Equal(before[fixedIndex], line.Points[fixedIndex]);
        Assert.Equal(snapped, line.Points[moved]);
        host.Controller.PointerUp(target, shift: true);
        Assert.Equal(1, host.Undo.UndoCount);
        Assert.True(host.Controller.PerformUndo());
        Assert.Equal(before, line.Points);

        host.Controller.PointerDown(press);
        host.Controller.PointerMove(target);
        host.Controller.CancelGesture(); // The controller path used by Esc.
        Assert.Equal(before, line.Points);
        Assert.Equal(0, host.Undo.UndoCount);
        Assert.Null(host.Controller.ActivePointerIntent);
    });

    [Theory]
    // A 4K capture fitted at a quarter DIP per pixel: a 64-pixel arrow spans 16 DIP on screen.
    [InlineData(1.0, 3840, 2160, 0.25, true, 64.0, 0.0)]
    [InlineData(1.5, 3840, 2160, 0.25, true, 64.0, 0.0)]
    // A 45-degree line 22 DIP long, at 100 % and in the fitted 4K capture.
    [InlineData(1.0, 640, 360, 1.0, false, 15.556349186104045, 15.556349186104045)]
    [InlineData(1.0, 3840, 2160, 0.25, false, 62.22539674441618, 62.22539674441618)]
    public void AShortSelectedLineStillMovesOnTheSecondDragAndKeepsBothEnds(
        double dpi, int width, int height, double scale, bool arrow, double dx, double dy) => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(dpi, width, height, scale);
        var from = new PointD(width / 2.0, height / 2.0);
        PolylineAnnotation line = arrow
            ? PolylineAnnotation.CreateArrow(from, from.Offset(dx, dy), Ink, 3)
            : PolylineAnnotation.CreateLine(from, from.Offset(dx, dy), Ink, 3);
        host.Controller.Document.Add(line);
        double length = Math.Sqrt(dx * dx + dy * dy);
        PointD Along(double dip) => new(line.Points[0].X + dx / length * dip / scale, line.Points[0].Y + dy / length * dip / scale);
        double screenLength = length * scale;
        // Each end handle reaches eight DIP along both axes, so between them they cover the whole body.
        Assert.True(Math.Abs(dx) * scale <= 16 && Math.Abs(dy) * scale <= 16);

        for (int drag = 1; drag <= 2; drag++)
        {
            PointD[] before = [.. line.Points];
            PointD middle = Along(screenLength / 2);
            if (drag == 2)
            {
                Assert.Same(line, Assert.Single(host.Controller.SelectedItems));
                Assert.NotEqual(ResizeHandle.None, host.Controller.HandleAt(middle, host.Controller.HandleTolerance));
            }
            PointerIntent intent = host.Controller.ResolvePointerIntent(middle);
            Assert.Equal(PointerAction.Move, intent.Action);
            Assert.Same(line, intent.Target);
            Assert.Equal(EditorCursorKind.Move, intent.Cursor);
            var step = new PointD(30 / scale, 12 / scale);
            host.Controller.PointerDown(middle);
            host.Controller.PointerMove(middle.Offset(step.X, step.Y));
            host.Controller.PointerUp(middle.Offset(step.X, step.Y));
            AssertPoint(before[0].Offset(step.X, step.Y), line.Points[0]);
            AssertPoint(before[1].Offset(step.X, step.Y), line.Points[1]);
        }
        Assert.Equal(2, host.Undo.UndoCount);

        // The middle eight DIP move the line; either side of it, each end is still taken by its handle.
        Assert.Equal(PointerAction.Move, host.Controller.ResolvePointerIntent(Along(screenLength / 2 - 3.9)).Action);
        Assert.Equal(PointerAction.Move, host.Controller.ResolvePointerIntent(Along(screenLength / 2 + 3.9)).Action);
        foreach ((ResizeHandle handle, PointD press) in new[]
        {
            (ResizeHandle.LineStart, line.Points[0]), (ResizeHandle.LineStart, Along(screenLength / 2 - 4.1)),
            (ResizeHandle.LineEnd, line.Points[1]), (ResizeHandle.LineEnd, Along(screenLength / 2 + 4.1)),
        })
        {
            PointerIntent end = host.Controller.ResolvePointerIntent(press);
            Assert.Equal(PointerAction.Resize, end.Action);
            Assert.Equal(handle, end.Handle);
        }
        PointD[] moved = [.. line.Points];
        PointD target = line.Points[1].Offset(20 / scale, 20 / scale);
        host.Controller.PointerDown(line.Points[1]);
        host.Controller.PointerMove(target);
        host.Controller.PointerUp(target);
        AssertPoint(moved[0], line.Points[0]);
        AssertPoint(target, line.Points[1]);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void ArrowRingsAreHollowAndAllHeadStylesRemainVisuallyDistinct(double dpi) => StaTestHost.Run(() =>
    {
        var host = new SurfaceHost(dpi);
        PolylineAnnotation arrow = PolylineAnnotation.CreateArrow(new(150, 150), new(330, 150), Ink, 3);
        host.Controller.Document.Add(arrow);
        var images = new List<Pixels>();
        foreach ((bool atStart, bool atEnd, string name) in new[] { (false, true, "end"), (true, true, "both"), (false, false, "none") })
        {
            arrow.HeadAtStart = atStart;
            arrow.HeadAtEnd = atEnd;
            host.Controller.SetSelected(null);
            Pixels bare = host.Render();
            host.Controller.SetSelected(arrow);
            Pixels selected = host.Render();
            GeometryDrawing[] rings = Drawings(host.Surface).Where(drawing =>
                drawing.Geometry is EllipseGeometry ellipse && Close(ellipse.RadiusX, 4) && Close(ellipse.RadiusY, 4)
                && drawing.Pen?.Thickness == 2).ToArray();
            Assert.Equal(2, rings.Length);
            Assert.Empty(SquareHandles(host.Surface));
            Assert.DoesNotContain(Drawings(host.Surface), drawing => drawing.Pen?.DashStyle.Dashes.Count == 2);
            foreach (PointD endpoint in arrow.Points)
            {
                Point centre = host.Surface.ToSurfacePoint(endpoint);
                GeometryDrawing ring = Assert.Single(rings, drawing => ((EllipseGeometry)drawing.Geometry).Center == centre);
                Assert.Null(ring.Brush);
                // Even the four-DIP contrast pen leaves the centre two-DIP radius transparent.
                int coloured = 0;
                foreach ((int x, int y) in selected.Disk(centre, 1, dpi))
                {
                    Assert.Equal(bare[x, y], selected[x, y]);
                    if (bare[x, y].R > bare[x, y].G + 30) coloured++;
                }
                Assert.True(coloured > 0, $"{name} at {endpoint}: stroke/head ink must remain visible inside the ring.");
            }
            images.Add(selected);
            if (dpi == 1.5) selected.SaveIfRequested($"arrow-selected-{name}-150.png");
        }

        Point start = host.Surface.ToSurfacePoint(arrow.Points[0]), end = host.Surface.ToSurfacePoint(arrow.Points[1]);
        // End and Both share the same end head. Their start neighborhoods distinguish them.
        Assert.Equal(images[0].Region(end, 20, dpi), images[1].Region(end, 20, dpi));
        Assert.NotEqual(images[0].Region(start, 20, dpi), images[1].Region(start, 20, dpi));
        Assert.NotEqual(images[0].Region(end, 20, dpi), images[2].Region(end, 20, dpi));
        Assert.NotEqual(images[1].Region(start, 20, dpi), images[2].Region(start, 20, dpi));
    });

    private static PointD Handle(SurfaceHost host, ResizeHandle handle) =>
        Assert.Single(host.Controller.GetSelectionHandles(host.Scale), item => item.Handle == handle).Position;

    private static Geometry OutlinedFirstGlyph(TextAnnotation text, double dpi)
    {
        var first = new TextAnnotation
        {
            Text = "주", Rect = text.Rect, FontFamily = text.FontFamily, FontSize = text.FontSize,
            Bold = text.Bold, Italic = text.Italic, Foreground = text.Foreground,
            HasOutline = text.HasOutline, OutlineStyle = text.OutlineStyle, AutoSize = true,
        };
        return OutlinedGlyphs(first, dpi);
    }

    private static Geometry OutlinedGlyphs(TextAnnotation text, double dpi)
    {
        Geometry glyph = TextAnnotationLayout.Format(text, dpi).BuildGeometry(new Point(text.Rect.Left, text.Rect.Top));
        if (!text.HasOutline) return glyph;
        double thickness = text.OutlineStyle == TextOutlineStyle.Consistent ? text.FontSize / 18 : Math.Max(1, text.FontSize / 12);
        var pen = new Pen(Brushes.Black, thickness) { LineJoin = PenLineJoin.Round };
        return Geometry.Combine(glyph, glyph.GetWidenedPathGeometry(pen), GeometryCombineMode.Union, null);
    }

    private static IEnumerable<GeometryDrawing> Drawings(Visual visual) => Flatten(VisualTreeHelper.GetDrawing(visual));

    private static IEnumerable<GeometryDrawing> Flatten(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (GeometryDrawing nested in Flatten(child)) yield return nested;
    }

    private static Rect[] SquareHandles(Visual visual) => Drawings(visual)
        .Where(drawing => drawing.Brush is not null && drawing.Pen?.Thickness == 2
            && drawing.Geometry is RectangleGeometry rect && Close(rect.Rect.Width, 8) && Close(rect.Rect.Height, 8))
        .Select(drawing => ((RectangleGeometry)drawing.Geometry).Rect).ToArray();

    private static bool Close(double left, double right) => Math.Abs(left - right) < 1e-6;
    private static bool IsCaptionInk(Color color) => Math.Abs(color.R - Ink.R) <= 5 && Math.Abs(color.G - Ink.G) <= 5 && Math.Abs(color.B - Ink.B) <= 5;
    private static void AssertPoint(PointD expected, PointD actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }
    private static void AssertRect(RectD expected, RectD actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }
    private static void AssertRect(Rect expected, Rect actual) =>
        AssertRect(new RectD(expected.X, expected.Y, expected.Width, expected.Height), new RectD(actual.X, actual.Y, actual.Width, actual.Height));

    private sealed class SurfaceHost
    {
        internal AnnotationEditorController Controller { get; }
        internal UndoStack Undo { get; } = new(mergeWindow: TimeSpan.Zero);
        internal AnnotationEditorSurface Surface { get; }
        internal double Scale => Surface.DipPerPixel;
        private readonly double _dpi;

        /// <param name="scale">
        /// DIP per capture pixel the surface is sized to fit (a fitted large capture); by default the
        /// capture shows at 100 % of <paramref name="dpi"/>.
        /// </param>
        internal SurfaceHost(double dpi, int width = 640, int height = 360, double? scale = null)
        {
            _dpi = dpi;
            byte[] background = new byte[width * height * 4];
            for (int i = 0; i < background.Length; i += 4)
            {
                background[i] = 48; background[i + 1] = 39; background[i + 2] = 31; background[i + 3] = 255;
            }
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, background, width * 4);
            bitmap.Freeze();
            var area = new RectD(0, 0, width, height);
            Controller = new AnnotationEditorController(AnnotationDocument.CreateFor(width, height), Undo)
            {
                Tool = EditorTool.Select, TextFitter = TextAnnotationLayout.Measure, StrokeColor = Ink,
            };
            Surface = new AnnotationEditorSurface(new FrozenFrame(bitmap, area, null, 0), area, Controller,
                new AnnotationRenderer(new AnnotationImageStore()));
            VisualTreeHelper.SetRootDpi(Surface, new DpiScale(dpi, dpi));
            double fit = scale ?? 1 / dpi;
            var size = new Size(width * fit + 32, height * fit + 32);
            Surface.Measure(size);
            Surface.Arrange(new Rect(size));
            Surface.UpdateLayout();
            Controller.DipPerPixel = Scale;
            Controller.HitTolerance = 6 / Scale;
            Assert.Equal(fit, Scale, 7);
        }

        internal TextAnnotation Caption(string content)
        {
            TextAnnotation text = Controller.BeginTextAnnotation(new PointD(150, 130), 180, 40);
            Controller.CommitTextEdit(text, content);
            Controller.SetSelected(text);
            return text;
        }

        internal Pixels Render()
        {
            Surface.InvalidateVisual();
            Surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Surface.ActualWidth * _dpi),
                (int)Math.Ceiling(Surface.ActualHeight * _dpi), 96 * _dpi, 96 * _dpi, PixelFormats.Pbgra32);
            bitmap.Render(Surface);
            return new Pixels(bitmap);
        }
    }

    private sealed class Pixels
    {
        private readonly byte[] _bytes;
        private readonly RenderTargetBitmap _bitmap;
        internal int Width => _bitmap.PixelWidth;
        internal int Height => _bitmap.PixelHeight;
        internal Pixels(RenderTargetBitmap bitmap)
        {
            _bitmap = bitmap;
            _bytes = new byte[Width * Height * 4];
            bitmap.CopyPixels(_bytes, Width * 4, 0);
        }
        internal Color this[int x, int y]
        {
            get
            {
                int at = (y * Width + x) * 4;
                return Color.FromArgb(_bytes[at + 3], _bytes[at + 2], _bytes[at + 1], _bytes[at]);
            }
        }
        internal IEnumerable<(int X, int Y)> Disk(Point centre, double radius, double dpi)
        {
            for (int y = (int)Math.Floor((centre.Y - radius) * dpi); y <= (int)Math.Ceiling((centre.Y + radius) * dpi); y++)
            for (int x = (int)Math.Floor((centre.X - radius) * dpi); x <= (int)Math.Ceiling((centre.X + radius) * dpi); x++)
            {
                double dx = (x + .5) / dpi - centre.X, dy = (y + .5) / dpi - centre.Y;
                if (x >= 0 && y >= 0 && x < Width && y < Height && dx * dx + dy * dy <= radius * radius) yield return (x, y);
            }
        }
        internal byte[] Region(Point centre, double radius, double dpi) =>
            Disk(centre, radius, dpi).SelectMany(p => new[] { this[p.X, p.Y].R, this[p.X, p.Y].G, this[p.X, p.Y].B }).ToArray();
        internal void SaveIfRequested(string name)
        {
            if (Environment.GetEnvironmentVariable("MYCAPTURE_SELECTION_CHROME_OUTPUT") is not { Length: > 0 } directory) return;
            string root = DiagnosticOutputPaths.Create(directory);
            string path = DiagnosticOutputPaths.Child(root, name);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_bitmap));
            using FileStream file = File.Create(path);
            encoder.Save(file);
        }
    }
}
