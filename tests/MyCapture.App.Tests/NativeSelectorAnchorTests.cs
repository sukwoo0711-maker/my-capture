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
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void DragAnchorMatchesChipGeometryAndReusesItsStampAcrossPointerMoves(double scale) => StaTestHost.Run(() =>
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
            DrawingGroup drawing = Assert.IsType<DrawingGroup>(chrome.AnchorDrawingForTest);
            RectangleGeometry geometry = Assert.Single(drawing.Children.OfType<GeometryDrawing>()
                .Select(item => item.Geometry).OfType<RectangleGeometry>(), item => item.RadiusX > 0);
            Assert.Equal(chip, geometry.Rect);
            Assert.Equal(7, geometry.RadiusX);
            Assert.Equal(7, geometry.RadiusY);
            Assert.Equal(scale, chrome.AnchorScaleForTest);
            FontFamily family = Application.Current?.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");
            var label = new FormattedText(UiText.Get("CaptureSelectionStart"), UiText.Culture,
                FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                TypeScale.Caption, Brushes.Black, scale);
            Assert.Equal(label.Width + 18, chip.Width, 8);
            Assert.Equal(label.Height + 10, chip.Height, 8);
            Assert.Equal(Math.Clamp(start.X / scale + 11, 8, bounds.Width / scale - chip.Width - 8), chip.Left, 8);
            Assert.Equal(Math.Clamp(start.Y / scale + 10, 8, bounds.Height / scale - chip.Height - 8), chip.Top, 8);
            Assert.True(chip.Left >= 8 && chip.Top >= 8 && chip.Right <= bounds.Width / scale - 8 + 1e-6
                && chip.Bottom <= bounds.Height / scale - 8 + 1e-6);
            Border readout = Assert.IsAssignableFrom<Border>(chrome.Windows[1].Content);
            Assert.Equal(new Thickness(9, 5, 9, 5), readout.Padding);
            Assert.Equal(new CornerRadius(7), readout.CornerRadius);
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
            chrome.Detach();
            Assert.Null(chrome.AnchorStampForTest);
            Assert.Null(chrome.AnchorDrawingForTest);
        }
    });
}
