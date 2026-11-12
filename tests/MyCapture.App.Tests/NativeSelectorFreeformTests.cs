using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorFreeformTests(ITestOutputHelper output)
{
    private static readonly RectD Bounds = new(-30000, -30000, 160, 120);
    private static readonly MonitorInfo[] Monitors = [new("Synthetic", Bounds, Bounds, 96, true)];

    [Fact]
    public void KeyAndModeBarEnterAndLeaveWithoutRememberingMode() => StaTestHost.Run(() =>
    {
        using (var overlay = Create())
        {
            Assert.Equal(CaptureSelectorMode.Capture, overlay.Mode);
            Assert.True(overlay.HandleKey(Key.F, ModifierKeys.None));
            Assert.Equal(CaptureSelectorMode.Freeform, overlay.Mode);
            Assert.True(overlay.ModeBar.FreeformButton.IsChecked);
            overlay.HandleKey(Key.F, ModifierKeys.None);
            Assert.Equal(CaptureSelectorMode.Capture, overlay.Mode);
            overlay.ModeBar.ActivateButton(overlay.ModeBar.FreeformButton);
            Assert.Equal(CaptureSelectorMode.Freeform, overlay.Mode);
            overlay.SelectMode(CaptureSelectorMode.Text);
            Assert.Equal(CaptureSelectorMode.Text, overlay.Mode);
            overlay.HandleKey(Key.F, ModifierKeys.None);
            Assert.Equal(CaptureSelectorMode.Freeform, overlay.Mode);
        }
        using var next = Create();
        Assert.Equal(CaptureSelectorMode.Capture, next.Mode);
        Assert.False(next.FreeformStrokeActive);
        Assert.Empty(next.FreeformPoints);
    });

    [Theory]
    [InlineData("text")]
    [InlineData("colour")]
    [InlineData("recording")]
    [InlineData("scrolling")]
    [InlineData("countdown")]
    [InlineData("editor")]
    public void DisabledRoutesKeepAnExplainedDisabledEntryAndIgnoreKey(string reason) => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        overlay.FreeformUnavailableReason = reason;
        Assert.False(overlay.FreeformAvailable);
        Assert.False(overlay.ModeBar.FreeformButton.IsEnabled);
        Assert.Contains(reason, overlay.ModeBar.FreeformButton.ToolTip?.ToString() ?? string.Empty);
        overlay.HandleKey(Key.F, ModifierKeys.None);
        Assert.Equal(CaptureSelectorMode.Capture, overlay.Mode);
        overlay.SelectMode(CaptureSelectorMode.Freeform);
        Assert.Equal(CaptureSelectorMode.Capture, overlay.Mode);
    });

    [Fact]
    public void RecordingIsUnavailableWithoutDependingOnCallerReason() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        overlay.RecordingSelection = true;
        Assert.False(overlay.FreeformAvailable);
        Assert.False(overlay.ModeBar.FreeformButton.IsEnabled);
        Assert.NotNull(overlay.ModeBar.FreeformButton.ToolTip);
        overlay.HandleKey(Key.F, ModifierKeys.None);
        Assert.Equal(CaptureSelectorMode.Capture, overlay.Mode);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticDragDeliversStraightAlphaFrozenPixelsThroughRealCompletion(bool pin) => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        FrozenFrame original = overlay.Frame!;
        CaptureSelectionCompletedEventArgs? completed = null;
        overlay.SelectionCompleted += (_, result) => completed = result;
        overlay.SelectRatio(SelectionRatio.Ratio16x9);
        overlay.PresetSize = new System.Drawing.Size(100, 100);
        overlay.SelectMode(CaptureSelectorMode.Freeform);
        Assert.Equal(SelectionRatio.Free, overlay.ActiveRatio);
        Assert.False(overlay.PlaceFixedSize(new FixedSelectionSize(100, 100)));
        overlay.BeginSelection(new PointD(10, 10));
        overlay.MoveSelection(new PointD(60, 10));
        overlay.MoveSelection(new PointD(10, 60));
        if (pin) overlay.ConfirmSelectionAsPin();
        else overlay.EndSelection(new PointD(10, 60));
        Assert.NotNull(completed);
        Assert.Same(original, completed.Frame);
        Assert.Equal(new RectD(10, 10, 50, 50), completed.BitmapRegion);
        Assert.Equal(pin, completed.PinRequested);
        Assert.False(completed.RecordForRepeat);
        Assert.False(completed.TextCapture);
        Assert.True(completed.SelectedBitmap.IsFrozen);
        Assert.Equal(PixelFormats.Bgra32, completed.SelectedBitmap.Format);
        byte[] result = Pixels(completed.SelectedBitmap);
        Assert.Equal(new byte[] { 40, 80, 160, 255 }, result[..4]);
        Assert.Equal((byte)0, result[((49 * 50 + 49) * 4) + 3]);
        int edge = (24 * 50 + 25) * 4;
        Assert.InRange(result[edge + 3], (byte)1, (byte)254);
        Assert.Equal(new byte[] { 40, 80, 160 }, result[edge..(edge + 3)]);
        Assert.True(overlay.IsClosed);
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeAndRightButtonDiscardOnlyTheStrokeAndKeepDrawingReady(bool rightButton) => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        int completed = 0;
        overlay.SelectionCompleted += (_, _) => completed++;
        overlay.SelectMode(CaptureSelectorMode.Freeform);
        overlay.BeginSelection(new PointD(10, 10));
        overlay.MoveSelection(new PointD(50, 10));
        overlay.MoveSelection(new PointD(10, 50));
        Assert.True(overlay.FreeformStrokeActive);
        if (rightButton) _ = SendMessage(overlay.Handle, 0x0204, IntPtr.Zero, IntPtr.Zero);
        else overlay.HandleKey(Key.Escape, ModifierKeys.None);
        Assert.False(overlay.IsClosed);
        Assert.Equal(CaptureSelectorMode.Freeform, overlay.Mode);
        Assert.False(overlay.FreeformStrokeActive);
        Assert.Empty(overlay.FreeformPoints);
        Assert.Null(overlay.Selection);
        Assert.Equal(0, completed);
        overlay.BeginSelection(new PointD(20, 20));
        Assert.True(overlay.FreeformStrokeActive);
        overlay.DiscardFreeformStroke();
        overlay.HandleKey(Key.Escape, ModifierKeys.None);
        Assert.True(overlay.IsClosed);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallAndEmptyStrokesCaptureNothingAndPermitRetry(bool empty) => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        int completed = 0;
        overlay.SelectionCompleted += (_, _) => completed++;
        overlay.SelectMode(CaptureSelectorMode.Freeform);
        overlay.BeginSelection(new PointD(10, 10));
        if (empty)
        {
            overlay.MoveSelection(new PointD(80, 80));
            overlay.EndSelection(new PointD(10, 10));
        }
        else
        {
            overlay.MoveSelection(new PointD(17, 10));
            overlay.EndSelection(new PointD(10, 17));
        }
        Assert.Equal(0, completed);
        Assert.False(overlay.IsClosed);
        Assert.False(overlay.FreeformStrokeActive);
        Assert.Equal(CaptureSelectorMode.Freeform, overlay.Mode);
        overlay.BeginSelection(new PointD(10, 10));
        overlay.MoveSelection(new PointD(50, 10));
        overlay.EndSelection(new PointD(10, 50));
        Assert.Equal(1, completed);
    });

    [Fact]
    public void ChangingModeMidStrokeDiscardsWithoutLeakingShapeToRectangle() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        overlay.SelectMode(CaptureSelectorMode.Freeform);
        overlay.BeginSelection(new PointD(10, 10));
        overlay.MoveSelection(new PointD(40, 10));
        overlay.MoveSelection(new PointD(10, 40));
        overlay.HandleKey(Key.F, ModifierKeys.None);
        Assert.False(overlay.FreeformStrokeActive);
        Assert.Empty(overlay.FreeformPoints);
        Assert.Null(overlay.Selection);
        CaptureSelectionCompletedEventArgs? completed = null;
        overlay.SelectionCompleted += (_, result) => completed = result;
        overlay.BeginSelection(new PointD(10, 10));
        overlay.EndSelection(new PointD(40, 40));
        Assert.NotNull(completed);
        Assert.All(Pixels(completed.SelectedBitmap).Where((_, index) => index % 4 == 3), alpha => Assert.Equal((byte)255, alpha));
        Assert.True(completed.RecordForRepeat);
    });

    [Fact]
    public void IncrementalPreviewKeepsNonzeroWindingAndUpdatesOnlyChangedSpans() => StaTestHost.Run(() =>
    {
        BitmapSource source = Source();
        using var dib = new NativeSelectorDib(source, new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)));
        dib.BeginFreeform();
        long initial = dib.PixelsUpdated;
        PointD a = new(10, 10), b = new(60, 10), c = new(10, 60);
        dib.AppendFreeformTriangle(a, b, c, IntPtr.Zero);
        long single = dib.PixelsUpdated;
        Assert.InRange(single - initial, 1, 50 * 50);
        AssertPixel(dib, 15, 15, 40, 80, 160);
        AssertPixel(dib, 100, 100, 20, 40, 80);
        dib.AppendFreeformTriangle(a, b, c, IntPtr.Zero);
        Assert.Equal(single, dib.PixelsUpdated); // Winding 1 -> 2 does not change brightness.
        dib.AppendFreeformTriangle(a, c, b, IntPtr.Zero);
        Assert.Equal(single, dib.PixelsUpdated); // Winding 2 -> 1 remains selected.
        AssertPixel(dib, 15, 15, 40, 80, 160);
        dib.AppendFreeformTriangle(a, c, b, IntPtr.Zero);
        AssertPixel(dib, 15, 15, 20, 40, 80);
        Assert.Equal(Pixels(Source()), Pixels(source));
    });

    [Fact]
    public void PrecisionPointerStillFeedsFreeformStrokeAndClipsAtDesktopEdges() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        PointD cursor = new(Bounds.Left + 20, Bounds.Top + 20);
        overlay.ReadCursor = () => cursor;
        overlay.PlaceCursor = point => { cursor = point; return true; };
        overlay.SelectMode(CaptureSelectorMode.Freeform);
        overlay.BeginSelection(overlay.UpdatePointer(precision: true, active: true));
        cursor = cursor.Offset(20, 20);
        PointD slow = overlay.UpdatePointer(precision: true, active: true);
        overlay.MoveSelection(slow);
        Assert.True(overlay.PrecisionActive);
        Assert.InRange(slow.X, 20, 39);
        Assert.Equal(slow, overlay.FreeformPoints[^1]);
        overlay.MoveSelection(new PointD(200, 200));
        Assert.Equal(new PointD(160, 120), overlay.FreeformPoints[^1]);
    });

    [Fact]
    public void PointMovesReuseRasterStorageAndAllocateOnlyWhenPointCapacityGrows() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        overlay.SelectMode(CaptureSelectorMode.Freeform);
        overlay.BeginSelection(new PointD(10, 10));
        for (int i = 0; i < 24; i++) overlay.MoveSelection(new PointD(70 + i, i % 2 == 0 ? 40 : 65));
        long pixelsBefore = overlay.DisplayDib!.PixelsUpdated;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 24; i++) overlay.MoveSelection(new PointD(100 + i, i % 2 == 0 ? 40 : 65));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(0, allocated);
        Assert.True(overlay.DisplayDib.PixelsUpdated > pixelsBefore);
        Assert.Equal(49, overlay.FreeformPoints.Count);
        Assert.Equal(1, overlay.DibAllocations);
    });

    [Fact]
    public void RenderSyntheticNativeStrokeAtOneHundredPercent() => StaTestHost.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_FREEFORM_EVIDENCE") != "1") return;
        string directory = OwnedTestDirectory.Create("freeform-native-evidence-");
        output.WriteLine("Freeform native evidence: " + directory);
        using var language = MyCapture.Core.Localization.UiText.UseLanguage("ko-KR");
        FrozenFrame frame = CaptureCueEvidenceTests.Desktop();
        MonitorInfo[] monitors = [new("freeform-evidence", frame.ScreenBounds, frame.ScreenBounds, 96, true)];
        PointD pointer = new(850, 465);
        using var overlay = new NativeCaptureOverlay(frame.ScreenBounds, false, true)
        {
            ReadCursor = () => pointer,
            PlaceCursor = _ => false,
            ReadMonitors = () => monitors,
        };
        overlay.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(overlay, true, false);
        overlay.SelectMode(CaptureSelectorMode.Freeform);
        overlay.BeginSelection(new PointD(430, 240));
        PointD[] points = [new(540, 205), new(710, 225), new(830, 290), new(880, 380), new(850, 465),
            new(735, 520), new(595, 505), new(490, 450), new(450, 360)];
        foreach (PointD point in points) overlay.MoveSelection(point);
        pointer = points[^1];
        chrome.Update(pointer, overlay.SelectionRectangle);
        BitmapSource rendered = NativeSelectorTestRenderer.Render(overlay, chrome);
        Assert.Equal(96, rendered.DpiX);
        Assert.Equal(96, rendered.DpiY);
        Assert.True(overlay.FreeformStrokeActive);
        TestDiagnosticArtifacts.SavePng(directory, "native-stroke-100.png", rendered);
    });

    private static NativeCaptureOverlay Create()
    {
        var overlay = new NativeCaptureOverlay(Bounds, false, false)
        {
            PlaceCursor = _ => false,
            ReadCursor = () => new PointD(Bounds.Left + 20, Bounds.Top + 20),
            ReadMonitors = () => Monitors,
            TextCaptureAvailable = true,
        };
        overlay.AttachFrame(new FrozenFrame(Source(), Bounds, null, 0));
        return overlay;
    }

    private static BitmapSource Source()
    {
        byte[] pixels = new byte[160 * 120 * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        { pixels[offset] = 40; pixels[offset + 1] = 80; pixels[offset + 2] = 160; pixels[offset + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(160, 120, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }

    private static void AssertPixel(NativeSelectorDib dib, int x, int y, byte blue, byte green, byte red)
    {
        int offset = y * dib.Stride + x * 4;
        Assert.Equal(blue, Marshal.ReadByte(dib.Bits, offset));
        Assert.Equal(green, Marshal.ReadByte(dib.Bits, offset + 1));
        Assert.Equal(red, Marshal.ReadByte(dib.Bits, offset + 2));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
