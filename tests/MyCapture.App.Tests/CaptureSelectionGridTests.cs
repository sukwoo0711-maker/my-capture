using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureSelectionGridTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadyPreview_RendersSyntheticDesktop(bool enabled) => StaTestHost.Run(() =>
    {
        using var language = Core.Localization.UiText.UseLanguage("ko-KR");
        FrozenFrame frame = CreateFrame();
        var view = new CaptureOverlayView(frame, showMagnifier: false, showSelectionGrid: enabled);
        try
        {
            SetCursor(view, new PointD(360, 280));
            RenderTargetBitmap rendered = Render(view);
            Assert.Equal(900, rendered.PixelWidth);
            Assert.Equal(600, rendered.PixelHeight);
            SaveEvidence(rendered, "ready-guides.png", enabled ? "MYCAPTURE_GRID_EVIDENCE" : "MYCAPTURE_GRID_OFF_EVIDENCE");
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ReadyGuides_FollowCursorAcrossFullAxes_WithoutChangingFrame(double scale) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = CreateFrame();
        byte[] original = Pixels(frame.Bitmap);
        var on = new CaptureOverlayView(frame, showMagnifier: false); // Default must show guides.
        var off = new CaptureOverlayView(frame, showMagnifier: false, showSelectionGrid: false);
        try
        {
            foreach (PointD cursor in new[] { new PointD(360, 280), new PointD(625, 410) })
            {
                SetCursor(on, cursor);
                SetCursor(off, cursor);
                byte[] enabled = Pixels(Render(on, scale));
                byte[] disabled = Pixels(Render(off, scale));
                Assert.Null(on.Selection);
                Assert.Null(off.Selection);
                int changed = 0;
                for (int y = 0; y < 600; y++)
                for (int x = 0; x < 900; x++)
                {
                    int offset = (y * 900 + x) * 4;
                    if (enabled.AsSpan(offset, 4).SequenceEqual(disabled.AsSpan(offset, 4))) continue;
                    changed++;
                    // The 3-DIP dark under-stroke can partially cover adjacent pixels.
                    double reach = 1.5 * scale + 0.5;
                    Assert.True(Math.Abs(x + 0.5 - cursor.X) <= reach || Math.Abs(y + 0.5 - cursor.Y) <= reach,
                        $"Unexpected changed pixel ({x}, {y}) for cursor {cursor} at scale {scale}.");
                }
                Assert.InRange(changed, 1000, 15000);
                // Both guides reach the viewport edges, rather than a selected rectangle.
                AssertAmber(enabled, 0, (int)cursor.Y);
                AssertAmber(enabled, 899, (int)cursor.Y);
                AssertAmber(enabled, (int)cursor.X, 0);
                AssertAmber(enabled, (int)cursor.X, 599);
            }
            Assert.Equal(original, Pixels(frame.Bitmap));
        }
        finally { on.ReleaseResources(); off.ReleaseResources(); }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void DragAndCompletedSelection_HideLongGuides_AndKeepPointerAndAnchor(double scale) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = CreateFrame();
        var on = new CaptureOverlayView(frame, showMagnifier: false);
        var off = new CaptureOverlayView(frame, showMagnifier: false, showSelectionGrid: false);
        PointD start = new(180, 120);
        PointD end = new(720, 450);
        try
        {
            SetCursor(on, start);
            SetCursor(off, start);
            _ = Render(on, scale);
            _ = Render(off, scale);
            foreach (CaptureOverlayView view in new[] { on, off })
                InvokeInput(view, "OnMouseLeftButtonDown", new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            Assert.Equal(Pixels(Render(on, scale)), Pixels(Render(off, scale)));
            foreach (CaptureOverlayView view in new[] { on, off })
            {
                SetCursor(view, end);
                InvokeInput(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 1)
                    { RoutedEvent = Mouse.MouseMoveEvent });
            }
            byte[] dragging = Pixels(Render(on, scale));
            Assert.Equal(dragging, Pixels(Render(off, scale)));
            Assert.Equal(new RectD(180, 120, 540, 330), on.Selection);
            // Existing cyan anchor ring remains distinct from the small white live pointer.
            int anchorPixel = ((int)start.Y * 900 + (int)(start.X - 8 * scale)) * 4;
            Assert.True(dragging[anchorPixel] > dragging[anchorPixel + 2] + 25
                && dragging[anchorPixel + 1] > dragging[anchorPixel + 2] + 25);
            int pointerPixel = ((int)end.Y * 900 + (int)(end.X + 8 * scale)) * 4;
            Assert.True(dragging[pointerPixel] > 180 && dragging[pointerPixel + 1] > 180 && dragging[pointerPixel + 2] > 180);

            // A completed or keyboard-set selection also has no long/interior grid lines.
            foreach (CaptureOverlayView view in new[] { on, off })
            {
                FieldInfo interaction = typeof(CaptureOverlayView).GetField("_interaction", BindingFlags.Instance | BindingFlags.NonPublic)!;
                interaction.SetValue(view, Enum.Parse(interaction.FieldType, "None"));
            }
            Assert.Equal(Pixels(Render(on, scale)), Pixels(Render(off, scale)));
        }
        finally { on.ReleaseResources(); off.ReleaseResources(); }
    });

    [Fact]
    public void BeforePointerInitialization_NoGuideAppearsAtAnInventedPosition() => StaTestHost.Run(() =>
    {
        FrozenFrame frame = CreateFrame();
        var on = new CaptureOverlayView(frame, showMagnifier: false);
        var off = new CaptureOverlayView(frame, showMagnifier: false, showSelectionGrid: false);
        try { Assert.Equal(Pixels(Render(on)), Pixels(Render(off))); }
        finally { on.ReleaseResources(); off.ReleaseResources(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedSelection_ExportsOriginalPixelsWithoutGrid(bool enabled) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = CreateFrame();
        RectD region = new(180, 120, 540, 330);
        var window = new CaptureOverlayWindow(frame, abortOnFocusLoss: false, showMagnifier: false,
            showSelectionGrid: enabled);
        var view = Assert.IsType<CaptureOverlayView>(window.Content);
        CaptureSelectionCompletedEventArgs? completed = null;
        window.SelectionCompleted += (_, args) => completed = args;
        try
        {
            SetCursor(view, new PointD(360, 280));
            _ = Render(view); // Render ready-state guides before completing the capture.
            SetSelection(view, region);
            _ = Render(view);
            window.CompleteSelection(region);
            Assert.NotNull(completed);
            Assert.Equal(region, completed.BitmapRegion);
            BitmapSource expected = ScreenCaptureEngine.Crop(frame, region);
            Assert.Equal(Pixels(expected), Pixels(completed.SelectedBitmap));
            Assert.Equal(540, completed.SelectedBitmap.PixelWidth);
            Assert.Equal(330, completed.SelectedBitmap.PixelHeight);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordingSelection_ReturnsOnlyPhysicalGeometry(bool enabled) => StaTestHost.Run(() =>
    {
        var window = new CaptureOverlayWindow(new RectD(-900, -100, 900, 600), abortOnFocusLoss: false,
            showMagnifier: false, showSelectionGrid: enabled);
        RectD? completed = null;
        bool emittedScreenshot = false;
        window.GeometrySelectionCompleted = region => completed = region;
        window.SelectionCompleted += (_, _) => emittedScreenshot = true;
        try
        {
            var view = Assert.IsType<CaptureOverlayView>(window.Content);
            SetSelection(view, new RectD(180, 120, 540, 330));
            _ = Render(view);
            window.CompleteSelection(new RectD(180, 120, 540, 330));
            Assert.Equal(new RectD(-720, 20, 540, 330), completed);
            Assert.False(emittedScreenshot);
        }
        finally { window.Close(); }
    });

    private static byte[] Pixels(BitmapSource bitmap)
    {
        byte[] result = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(result, bitmap.PixelWidth * 4, 0);
        return result;
    }

    private static void SetSelection(CaptureOverlayView view, RectD selection) =>
        typeof(CaptureOverlayView).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, selection);

    private static void SetCursor(CaptureOverlayView view, PointD cursor)
    {
        view.ReadCursor = () => cursor;
        view.InitializePointer();
    }

    private static void InvokeInput(CaptureOverlayView view, string method, InputEventArgs args) =>
        typeof(CaptureOverlayView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [args]);

    private static void AssertAmber(byte[] pixels, int x, int y)
    {
        int offset = (y * 900 + x) * 4;
        Assert.True(pixels[offset + 2] > pixels[offset + 1] + 20 && pixels[offset + 1] > pixels[offset] + 20,
            $"Expected amber guide at ({x}, {y}), got RGB {pixels[offset + 2]}, {pixels[offset + 1]}, {pixels[offset]}.");
    }

    private static RenderTargetBitmap Render(CaptureOverlayView view, double scale = 1)
    {
        view.InvalidateVisual();
        view.Measure(new Size(900 / scale, 600 / scale));
        view.Arrange(new Rect(0, 0, 900 / scale, 600 / scale));
        view.UpdateLayout();
        var image = new RenderTargetBitmap(900, 600, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(view);
        return image;
    }

    private static void SaveEvidence(BitmapSource image, string name, string variable = "MYCAPTURE_GRID_EVIDENCE")
    {
        string? directory = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static FrozenFrame CreateFrame()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)), null, new Rect(0, 0, 900, 600));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x3B, 0x53)), null, new Rect(0, 0, 900, 72));
            Text(dc, "PROJECT NOTES", 32, 25, 18, Brushes.White);
            Text(dc, "Capture selection sample", 180, 135, 28, Brushes.Black);
            Text(dc, "Plan, align, and share your work.", 180, 179, 16, Brushes.DimGray);
            for (int row = 0; row < 3; row++)
            {
                double y = 230 + row * 64;
                dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), new Rect(180, y, 540, 48), 6, 6);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x46, 0x81, 0xA5)), null, new Point(204, y + 24), 7, 7);
                Text(dc, new[] { "Layout and composition", "Review selected content", "Save the original image" }[row], 226, y + 13, 16, Brushes.Black);
            }
            Text(dc, "Synthetic test content", 32, 554, 13, Brushes.DimGray);
        }
        var bitmap = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 900, 600), null, 0);
    }

    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, 1), new Point(x, y));
}
