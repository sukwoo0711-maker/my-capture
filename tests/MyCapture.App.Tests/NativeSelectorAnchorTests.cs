using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorAnchorTests
{
    [Theory]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(1.5, true)]
    [InlineData(1.0, false)]
    [InlineData(1.5, false)]
    public void DragAnchorSitsOutsideTheSelectionBehindTheDragAndReusesItsStampAcrossPointerMoves(double scale, bool rounded) => StaTestHost.Run(() =>
    {
        // Windows 11 rounds the read-out's window and the chip takes its radius; on Windows 10 both are square.
        bool? previousRounding = NativeSelectorChrome.DwmRoundingOverrideForTest;
        NativeSelectorChrome.DwmRoundingOverrideForTest = rounded;
        try { AssertAnchor(scale, rounded); }
        finally { NativeSelectorChrome.DwmRoundingOverrideForTest = previousRounding; }
    });

    private static void AssertAnchor(double scale, bool rounded)
    {
        using var language = UiText.UseLanguage("ko-KR");
        var frame = CaptureCueEvidenceTests.Desktop();
        RectD bounds = frame.ScreenBounds;
        MonitorInfo[] monitors = [new("fixture", bounds, bounds, (uint)Math.Round(96 * scale), true)];
        foreach ((PointD start, PointD end) in new[]
        {
            (new PointD(300, 250), new PointD(520, 430)),
            (new PointD(1279, 719), new PointD(1000, 500)),
        })
        {
            PointD pointer = start;
            using var owner = new NativeCaptureOverlay(bounds, false, false)
            {
                ReadCursor = () => pointer, ReadMonitors = () => monitors, PlaceCursor = _ => false,
            };
            owner.AttachFrame(frame);
            using var chrome = new NativeSelectorChrome(owner, false, false);
            Assert.Null(chrome.AnchorStampForTest);
            Assert.Equal(0, chrome.AnchorBuildCountForTest);
            owner.BeginSelection(start);
            pointer = end;
            owner.UpdatePointer(false, false);
            owner.MoveSelection(end);
            chrome.Update(pointer, owner.SelectionRectangle);
            _ = NativeSelectorTestRenderer.Render(owner, chrome);

            Rect chip = Assert.IsType<Rect>(chrome.AnchorChipBoundsForTest);
            double pixel = 1 / scale;
            // Whole physical pixels, so the one-pixel edge is crisp.
            foreach (double edge in new[] { chip.Left, chip.Top, chip.Right, chip.Bottom })
                Assert.Equal(Math.Round(edge * scale), edge * scale, 6);
            DrawingGroup drawing = Assert.IsType<DrawingGroup>(chrome.AnchorDrawingForTest);
            GeometryDrawing face = Assert.Single(drawing.Children.OfType<GeometryDrawing>(),
                item => item.Geometry is RectangleGeometry && item.Pen is not null);
            var geometry = (RectangleGeometry)face.Geometry;
            // One rectangle: the chip surface with a one-pixel chip-border edge along its inside,
            // whose outer silhouette has the read-out window's 4-DIP DWM radius, or none beside a
            // square read-out.
            Assert.Equal(rounded, chrome.IsRoundedForTest(chrome.Windows[1]));
            Assert.Equal(new Rect(chip.X + pixel / 2, chip.Y + pixel / 2, chip.Width - pixel, chip.Height - pixel), geometry.Rect);
            double radius = rounded ? NativeSelectorAnchor.Radius - pixel / 2 : 0;
            Assert.Equal(NativeSelectorAnchor.Radius, DwmWindowAttributes.CornerRadius(DwmCornerPreference.RoundSmall));
            Assert.Equal(radius, geometry.RadiusX, 8);
            Assert.Equal(radius, geometry.RadiusY, 8);
            IReadOnlyDictionary<string, Brush> palette = CaptureGuidePalette.Resolve();
            Assert.Equal(((SolidColorBrush)palette["CaptureGuide.ChipSurface"]).Color, ((SolidColorBrush)face.Brush).Color);
            Assert.Equal(((SolidColorBrush)palette["CaptureGuide.ChipBorder"]).Color, ((SolidColorBrush)face.Pen.Brush).Color);
            Assert.Equal(pixel, face.Pen.Thickness, 8);
            Assert.Equal(scale, chrome.AnchorScaleForTest);
            FontFamily family = Application.Current?.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");
            var label = new FormattedText(UiText.Get("CaptureSelectionStart"), UiText.Culture,
                FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                TypeScale.Caption, Brushes.Black, scale);
            Assert.Equal(label.Width + 18, chip.Width, pixel + 1e-6);
            // As tall as the read-out beside it, the label centred in it.
            Assert.Equal(NativeSelectorChrome.ReadoutHeight, chip.Height, pixel + 1e-6);
            Assert.True(label.Height + 2 * 5 <= chip.Height + pixel + 1e-6);
            Assert.True(chip.Left >= 8 - pixel && chip.Top >= 8 - pixel && chip.Right <= bounds.Width / scale - 8 + pixel
                && chip.Bottom <= bounds.Height / scale - 8 + pixel);
            // Outside the selection, on the side the drag leaves behind, clear of the frame drawn
            // around it: dragging right and down from (300, 250) puts it above and to the left.
            Rect selection = new(new Point(Math.Min(start.X, end.X) / scale, Math.Min(start.Y, end.Y) / scale),
                new Point(Math.Max(start.X, end.X) / scale, Math.Max(start.Y, end.Y) / scale));
            double frameOutset = CaptureFrameStyle.Outset * pixel;
            Rect framed = Rect.Inflate(selection, frameOutset, frameOutset);
            if (end.X > start.X)
            {
                Assert.True(chip.Right <= framed.Left - 6 + pixel, $"{chip} left of {framed}");
                Assert.True(chip.Bottom <= framed.Top - 6 + pixel, $"{chip} above {framed}");
            }
            else
            {
                // The start is the screen's bottom-right pixel: no room behind the drag, so the
                // chip falls back inside the selection's corner, still on screen.
                Assert.True(selection.Contains(chip), $"{chip} inside {selection}");
            }
            Border readout = Assert.IsAssignableFrom<Border>(chrome.Windows[1].Content);
            Assert.Equal(new Thickness(NativeSelectorChrome.ReadoutPaddingX, 0, NativeSelectorChrome.ReadoutPaddingX, 0), readout.Padding);
            Assert.Equal(NativeSelectorChrome.ReadoutHeight, readout.Height);
            Assert.Equal(new CornerRadius(0), readout.CornerRadius);
            Assert.True(chrome.IsReadoutVisibleForTest);
            RectD sizeBounds = chrome.SizeChipBoundsForTest;
            Assert.True(sizeBounds.Left >= Math.Floor(8 * scale) && sizeBounds.Top >= Math.Floor(8 * scale));
            Assert.True(sizeBounds.Right <= bounds.Width - 8 * scale + 1
                && sizeBounds.Bottom <= bounds.Height - 8 * scale + 1);

            NativeSelectorStamp stamp = Assert.IsType<NativeSelectorStamp>(chrome.AnchorStampForTest);
            Assert.Equal(1, chrome.AnchorBuildCountForTest);
            var stampBounds = new RectD(stamp.Bounds.X, stamp.Bounds.Y, stamp.Bounds.Width, stamp.Bounds.Height);
            Assert.True(stampBounds.Contains(new PointD(chip.Left * scale, chip.Top * scale)));
            Assert.True(stampBounds.Contains(new PointD(chip.Right * scale, chip.Bottom * scale)));
            for (int index = 1; index <= 20; index++)
            {
                pointer = end.Offset(index, index);
                owner.UpdatePointer(false, false);
                owner.MoveSelection(pointer);
                chrome.Update(pointer, owner.SelectionRectangle);
                Assert.Same(stamp, chrome.AnchorStampForTest);
                Assert.Same(drawing, chrome.AnchorDrawingForTest);
                Assert.Equal(1, chrome.AnchorBuildCountForTest);
            }
            // Crossing the start point turns the drag around: the chip moves to the other side once.
            pointer = new PointD(2 * start.X - end.X, 2 * start.Y - end.Y);
            owner.UpdatePointer(false, false);
            owner.MoveSelection(pointer);
            chrome.Update(pointer, owner.SelectionRectangle);
            Assert.Equal(2, chrome.AnchorBuildCountForTest);
            Assert.NotSame(stamp, chrome.AnchorStampForTest);
            Rect turned = Assert.IsType<Rect>(chrome.AnchorChipBoundsForTest);
            if (end.X > start.X) Assert.True(turned.Left >= start.X / scale + frameOutset + 6 - pixel, $"{turned} right of the start");
            chrome.Detach();
            Assert.Null(chrome.AnchorStampForTest);
            Assert.Null(chrome.AnchorDrawingForTest);
        }
    }
}
