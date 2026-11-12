using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A clipboard pin opens at the size its content had on screen: whole when it fits the work area,
/// and a spreadsheet's picture of its cells as large as the cells looked in the sheet window.
/// </summary>
public sealed class PinOriginalSizeTests
{
    private const string EvidenceGate = "MYCAPTURE_PIN_SIZE_EVIDENCE";
    private const string CellText = "a\tb\r\nc\td\r\n";
    // A 10 x 20 cell range at default widths: 480 pt x 300 pt (640 x 400 DIP at 100% sheet zoom).
    private const double RangeWidthPt = 480, RangeHeightPt = 300;

    [Theory]
    [InlineData(80, 1.0, 640, 400, 0.8)]
    [InlineData(100, 1.0, 640, 400, 1.0)]
    [InlineData(150, 1.0, 640, 400, 1.5)]
    [InlineData(80, 1.5, 960, 600, 0.8)]
    [InlineData(100, 1.5, 960, 600, 1.0)]
    [InlineData(150, 1.5, 960, 600, 1.5)]
    public void PasteSpreadsheetPicture_OpensAtTheCellsOnScreenSize(
        double sheetZoom, double scale, int pictureWidth, int pictureHeight, double expectedZoom) => StaTestHost.Run(() =>
    {
        PinManager manager = Manager(scale);
        int reads = 0;
        manager.BeginSpreadsheetViewRead = () =>
        {
            reads++;
            return Task.FromResult<SpreadsheetSelectionView?>(new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt, sheetZoom));
        };
        try
        {
            PinWindow pin = Paste(manager, PinContent.FromTablePicture(Picture(pictureWidth, pictureHeight), CellText));
            Assert.Equal(1, reads);
            Assert.Equal(expectedZoom, pin.State.Zoom, 6);
            // The picture keeps its own 100% (its pixels on this monitor); only the opening zoom follows the sheet.
            Assert.Equal(pictureWidth / scale, pin.State.ImageWidthDip, 6);
            Assert.Equal(640 * sheetZoom / 100, pin.Width, 6);
            Assert.Equal(400 * sheetZoom / 100, pin.Height, 6);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void PasteSpreadsheetPicture_SelectionChangedAfterTheCopy_KeepsThePictureSize() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager(1);
        // The user selected a taller range after copying: the shape no longer matches the picture.
        manager.BeginSpreadsheetViewRead = () =>
            Task.FromResult<SpreadsheetSelectionView?>(new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt * 2, 150));
        try
        {
            PinWindow pin = Paste(manager, PinContent.FromTablePicture(Picture(640, 400), CellText));
            Assert.Equal(1.0, pin.State.Zoom, 6);
            Assert.Equal((640.0, 400.0), (pin.Width, pin.Height));
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void PasteSpreadsheetPicture_ReadPastTheDeadline_OpensAtPictureSizeAndNeverJumpsLater() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager(1);
        var late = new TaskCompletionSource<SpreadsheetSelectionView?>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.BeginSpreadsheetViewRead = () => late.Task;
        manager.SpreadsheetViewWait = TimeSpan.FromMilliseconds(120);
        try
        {
            var elapsed = Stopwatch.StartNew();
            PinWindow pin = Paste(manager, PinContent.FromTablePicture(Picture(640, 400), CellText));
            elapsed.Stop();
            Assert.Equal(1.0, pin.State.Zoom, 6);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(100), $"The paste did not wait for the read: {elapsed.Elapsed}.");
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"The paste waited past its deadline: {elapsed.Elapsed}.");

            late.SetResult(new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt, 150));
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(1.0, pin.State.Zoom, 6);
            Assert.Equal((640.0, 400.0), (pin.Width, pin.Height));
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void PasteSpreadsheetPicture_ReadThatFailsOrThrows_OpensAtPictureSize() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager(1);
        try
        {
            manager.BeginSpreadsheetViewRead = () => Task.FromException<SpreadsheetSelectionView?>(new InvalidOperationException("busy"));
            Assert.Equal(1.0, Paste(manager, PinContent.FromTablePicture(Picture(640, 400), CellText)).State.Zoom, 6);
            manager.BeginSpreadsheetViewRead = () => throw new InvalidOperationException("no window");
            Assert.Equal(1.0, Paste(manager, PinContent.FromTablePicture(Picture(640, 400), CellText)).State.Zoom, 6);
            manager.BeginSpreadsheetViewRead = () => null;
            Assert.Equal(1.0, Paste(manager, PinContent.FromTablePicture(Picture(640, 400), CellText)).State.Zoom, 6);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void PasteOtherContent_IgnoresTheSpreadsheetView() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager(1);
        manager.BeginSpreadsheetViewRead = () =>
            Task.FromResult<SpreadsheetSelectionView?>(new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt, 150));
        try
        {
            // A plain picture of the same size, and a table MyCapture drew itself, have no sheet zoom to follow.
            Assert.Equal(1.0, Paste(manager, PinContent.FromImage(Picture(640, 400))).State.Zoom, 6);
            BitmapSource grid = ClipboardTextRenderer.Render(CellText, true);
            Assert.Equal(1.0, Paste(manager, PinContent.FromText(grid, CellText, isTable: true)).State.Zoom, 6);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void PasteLargePicture_ThatFitsTheWorkArea_OpensAtFullSize() => StaTestHost.Run(() =>
    {
        // 1700 x 900 on a 1920 x 1040 work area: 88.5% x 86.5%, which shrank to 80% before.
        PinManager manager = Manager(1);
        try
        {
            BitmapSource picture = Picture(1700, 900);
            PinWindow pin = Paste(manager, PinContent.FromImage(picture));
            Assert.Equal(1.0, pin.State.Zoom, 6);
            Assert.Equal((1700.0, 900.0), (pin.Width, pin.Height));
            Assert.InRange(pin.Left, 0, 1920 - 1700);
            Assert.InRange(pin.Top, 0, 1040 - 900);

            // Larger than the work area: just small enough to fit it, not 80% of it.
            PinWindow huge = Paste(manager, PinContent.FromImage(Picture(3000, 1000)));
            Assert.Equal(1920.0 / 3000, huge.State.Zoom, 6);
            Assert.Equal(1920, huge.Width, 6);

            double oldZoom = Math.Min(1, Math.Min(1920 * 0.8 / 1700, 1040 * 0.8 / 900));
            SaveEvidence(WorkAreaDiagram(picture, oldZoom, "before-80pct"), "large-picture-initial-size-before.png");
            SaveEvidence(WorkAreaDiagram(picture, pin.State.Zoom, "after-100pct"), "large-picture-initial-size-after.png");
        }
        finally { manager.CloseAllForExit(); }
    });

    private static PinWindow Paste(PinManager manager, PinContent content)
    {
        var before = manager.SnapshotOpenPinWindows().ToHashSet();
        Task<PasteResult> paste = manager.PasteFromClipboardAsync(
            () => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(content)));
        Wait(paste);
        Assert.Equal(PasteResult.Pinned, paste.Result);
        return Assert.Single(manager.SnapshotOpenPinWindows(), pin => !before.Contains(pin));
    }

    private static void Wait(Task task)
    {
        if (task.IsCompleted) return;
        var frame = new DispatcherFrame();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Send,
            (_, _) => frame.Continue = false, dispatcher);
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Assert.True(task.IsCompleted, "The paste did not finish.");
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static PinManager Manager(double scale) => new(() => new PinSettings(), NullLogger.Instance)
    {
        ReadPlacementMonitorForTest = () => new MonitorInfo("fake",
            new RectD(0, 0, 1920 * scale, 1080 * scale), new RectD(0, 0, 1920 * scale, 1040 * scale), (uint)(96 * scale), true),
        BeforeShowForTest = pin => pin.ShowActivated = false,
        ActivatePinForTest = _ => { },
    };

    private static BitmapSource Picture(int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * stride) + (x * 4);
                bool line = x % 64 == 0 || y % 20 == 0;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = line ? (byte)0xC8 : (byte)0xFF;
                pixels[i + 3] = 0xFF;
            }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The work area (1920 x 1040, drawn at half size) with the pin's opening rectangle.</summary>
    private static BitmapSource WorkAreaDiagram(BitmapSource picture, double zoom, string label)
    {
        const double half = 0.5;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)), null, new Rect(0, 0, 1920 * half, 1040 * half));
            double width = picture.PixelWidth * zoom * half, height = picture.PixelHeight * zoom * half;
            Rect pin = new((1920 * half - width) / 2, (1040 * half - height) / 2, width, height);
            dc.DrawImage(picture, pin);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xDC, 0x14, 0x3C)), 2), pin);
            var text = new FormattedText($"{label}: {picture.PixelWidth}x{picture.PixelHeight} at {zoom:P0} on a 1920x1040 work area",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14,
                Brushes.White, 1);
            dc.DrawText(text, new Point(8, 6));
        }
        var bitmap = new RenderTargetBitmap((int)(1920 * half), (int)(1040 * half), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal static void SaveEvidence(BitmapSource image, string name)
    {
        string? folder = Environment.GetEnvironmentVariable(EvidenceGate);
        if (string.IsNullOrWhiteSpace(folder)) return;
        Assert.True(Path.IsPathFullyQualified(folder), "Evidence output must be absolute.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream output = File.Create(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(folder), name));
        encoder.Save(output);
    }
}
