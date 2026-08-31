using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Diagnostics;

/// <summary>Headless deterministic diagnostic for every advanced-capture mode and algorithm.</summary>
internal static class AdvancedCaptureSelfTest
{
    internal const string CommandLineSwitch = "--selftest-advanced";

    internal static int Run(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var report = new StringBuilder()
            .AppendLine("MyCapture advanced-capture self-test")
            .AppendLine($"UTC: {DateTimeOffset.UtcNow:O}")
            .AppendLine();

        int exitCode = 2;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                exitCode = RunCore(report, outputDirectory);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            report.AppendLine().AppendLine("RESULT: FAIL (unhandled exception)").AppendLine(failure.ToString());
            exitCode = 2;
        }

        File.WriteAllText(
            Path.Combine(outputDirectory, "advanced-selftest-report.txt"),
            report.ToString(),
            new UTF8Encoding(false));
        return exitCode;
    }

    private static int RunCore(StringBuilder report, string outputDirectory)
    {
        int limit = 3;
        var store = new LastRegionStore(() => limit);
        for (int i = 0; i < 5; i++)
        {
            store.Record(new RegionHistoryEntry(
                new RectD(i * 10, 0, 40, 40),
                "DISPLAY-A",
                new RectD(0, 0, 300, 200),
                96));
        }

        Check(report, "Region history bounded by limit", store.Count == 3);
        Check(report, "Most recent region is first", store.Last is { } last && last.Left == 40);
        RegionHistoryEntry entry = store.LastEntry!;
        RectD? mapped = entry.ResolveForMonitor(new RectD(500, 20, 600, 400), 192);
        Check(report, "Region remaps by monitor origin and DPI", mapped == new RectD(580, 20, 80, 80));

        var stitcher = new ScrollStitcher(4, ScrollStitchOptions.Default);
        stitcher.Append(RowCodedFrame(4, 20, 0));
        ScrollAppendResult appended = stitcher.Append(RowCodedFrame(4, 20, 5));
        Check(report, "Stitcher appends only verified rows",
            appended.Kind == ScrollAppendKind.Appended && appended.AppendedRows == 5 && stitcher.Height == 25);
        Check(report, "Stitcher detects end without appending",
            stitcher.Append(RowCodedFrame(4, 20, 5)).Kind == ScrollAppendKind.NoNewContent);

        var capped = new ScrollStitcher(
            4,
            ScrollStitchOptions.Default with { MaxOutputBytes = 4 * 24 * 4, MaxOutputHeight = 24 });
        capped.Append(RowCodedFrame(4, 20, 0));
        Check(report, "Stitcher rejects growth beyond limit before allocation",
            capped.Append(RowCodedFrame(4, 20, 5)).Kind == ScrollAppendKind.LimitReached
            && capped.Height == 20);

        var headerOptions = ScrollStitchOptions.Default with { MaxAutoHeaderRatio = 0.4 };
        var headerStitcher = new ScrollStitcher(4, headerOptions);
        headerStitcher.Append(HeaderFrame(4, 20, 4, 0));
        ScrollAppendResult headerAppend = headerStitcher.Append(HeaderFrame(4, 20, 4, 6));
        Check(report, "Stitcher auto-detects fixed header",
            headerStitcher.EffectiveHeaderHeight == 4 && headerAppend.Kind == ScrollAppendKind.Appended);

        var env = new FakeEnvironment();
        var sink = new FakeScrollSink();
        var service = NewService(env, store, sink);

        Check(report, "Full-screen capture completes", service.CaptureFullScreen().IsCompleted);
        env.Window = new WindowUnderCursor(
            new IntPtr(1),
            new RectD(250, 20, 200, 150),
            "메모장",
            new RectD(258, 50, 184, 112));
        Check(report, "Window capture completes", service.CaptureWindow().IsCompleted);
        Check(report, "Window capture freezes the complete requested frame",
            env.LastScreenCaptureBounds == env.Window.ScreenBounds
            && env.LastSelection?.Region == new RectD(0, 0, 200, 150));
        Check(report, "Window title reaches persistence path", env.LastSelection?.SourceTitle == "메모장");

        env.Window = null;
        Check(report, "No window produces nothing-to-capture",
            service.CaptureWindow().Kind == CaptureOutcomeKind.NothingToCapture);
        Check(report, "Fixed-size capture completes", service.CaptureFixedSize(100, 80).IsCompleted);
        Check(report, "Invalid fixed-size is rejected",
            service.CaptureFixedSize(0, 80).Kind == CaptureOutcomeKind.NothingToCapture);

        var emptyService = NewService(env, new LastRegionStore(() => 5), sink);
        Check(report, "Repeat without history is explicit",
            emptyService.RepeatLastRegion().Kind == CaptureOutcomeKind.NothingToCapture);

        var scrollEnv = new ScrollingEnvironment(totalHeight: 80, regionHeight: 20, width: 8);
        var scrollSink = new FakeScrollSink { Target = scrollEnv };
        var scrollService = NewService(scrollEnv, store, scrollSink);
        CaptureOutcome scrolling = scrollService.CaptureScrollingAsync(
            new IntPtr(1),
            new RectD(0, 0, 8, 20),
            ScrollStitchOptions.Default,
            maxFrames: 20).GetAwaiter().GetResult();
        Check(report, "Scrolling capture completes", scrolling.IsCompleted);
        Check(report, "Scroll input was driven", scrollSink.Calls >= 1);
        Check(report, "Stitched result is taller than one frame", scrollEnv.LastOpenedHeight > 20);
        Check(report, "Stitched result is not repeat-history eligible",
            scrollEnv.LastSelection is { RecordForRepeat: false });

        // Exercise the exact F3 pin export path in the packaged executable. The source bitmap
        // is frozen and the save service encodes it on a worker; decoding the resulting PNG
        // proves the self-contained WPF codec, naming, atomic export, and original dimensions.
        string pinRoot = Path.Combine(outputDirectory, "pin-save");
        AppPaths pinPaths = AppPaths.CreateForRoot(pinRoot);
        var pinSettings = new AppSettings();
        var pinSave = new PinImageSaveService(
            () => pinSettings,
            () => pinPaths,
            NullLogger<PinImageSaveService>.Instance);
        BitmapSource pinSource = Solid(23, 17, 0x70);
        PinSaveResult pinResult = pinSave.QuickSaveAsync(pinSource).GetAwaiter().GetResult();
        BitmapSource? pinRoundTrip = pinResult.Path is null ? null : ImageCodec.TryLoad(pinResult.Path);
        Check(report, "F3 pin quick-save writes original PNG dimensions",
            pinResult.Status == PinSaveStatus.Saved
            && pinRoundTrip is { PixelWidth: 23, PixelHeight: 17 });
        Check(report, "F3 pin export leaves no internal recovery backup",
            pinResult.Path is not null && !File.Exists(pinResult.Path + AtomicFile.BackupSuffix));

        report.AppendLine().AppendLine("RESULT: PASS");
        return 0;
    }

    private static AdvancedCaptureService NewService(
        IAdvancedCaptureEnvironment environment,
        LastRegionStore store,
        IScrollInputSink sink) =>
        new(
            environment,
            store,
            sink,
            NullLogger.Instance,
            static (_, token) => token.IsCancellationRequested
                ? Task.FromCanceled(token)
                : Task.CompletedTask);

    private static void Check(StringBuilder report, string name, bool passed)
    {
        report.AppendLine($"{(passed ? "PASS" : "FAIL")}: {name}");
        if (!passed)
        {
            throw new InvalidOperationException($"Self-test assertion failed: {name}");
        }
    }

    private static ScrollFrame RowCodedFrame(int width, int height, int baseValue)
    {
        byte[] pixels = new byte[width * height * ScrollFrame.BytesPerPixel];
        for (int row = 0; row < height; row++)
        {
            Array.Fill(
                pixels,
                (byte)((baseValue + row) & 0xFF),
                row * width * ScrollFrame.BytesPerPixel,
                width * ScrollFrame.BytesPerPixel);
        }

        return new ScrollFrame(width, height, pixels);
    }

    private static ScrollFrame HeaderFrame(int width, int height, int header, int contentBase)
    {
        byte[] pixels = new byte[width * height * ScrollFrame.BytesPerPixel];
        for (int row = 0; row < height; row++)
        {
            byte value = row < header ? (byte)(200 + row) : (byte)(contentBase + row - header);
            Array.Fill(
                pixels,
                value,
                row * width * ScrollFrame.BytesPerPixel,
                width * ScrollFrame.BytesPerPixel);
        }

        return new ScrollFrame(width, height, pixels);
    }

    private static BitmapSource Solid(int width, int height, byte value)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, value);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class FakeEnvironment : IAdvancedCaptureEnvironment
    {
        public int OpenCount { get; private set; }
        public AdvancedSelection? LastSelection { get; private set; }
        public WindowUnderCursor? Window { get; set; }
        public RectD LastScreenCaptureBounds { get; private set; }
        public PointD CursorPosition => new(150, 120);

        public FrozenFrame CaptureMonitorUnderCursor() =>
            new(Solid(300, 200, 0x40), new RectD(0, 0, 300, 200), null, 0);

        public FrozenFrame CaptureScreenRegion(RectD screenBounds)
        {
            LastScreenCaptureBounds = screenBounds;
            return new FrozenFrame(
                Solid((int)screenBounds.Width, (int)screenBounds.Height, 0x40),
                screenBounds,
                null,
                0);
        }

        public BitmapSource CaptureRegion(RectD screenBounds) =>
            Solid((int)screenBounds.Width, (int)screenBounds.Height, 0x40);

        public WindowUnderCursor? WindowAt(PointD screenPoint) => Window;
        public RectD? ResolveRepeatRegion(RegionHistoryEntry history) => history.ScreenRegion;

        public bool OpenEditor(AdvancedSelection selection)
        {
            OpenCount++;
            LastSelection = selection;
            return true;
        }
    }

    private sealed class ScrollingEnvironment : IAdvancedCaptureEnvironment
    {
        private readonly int _total;
        private readonly int _regionHeight;
        private readonly int _width;
        private int _scrollOffset;

        public ScrollingEnvironment(int totalHeight, int regionHeight, int width)
        {
            _total = totalHeight;
            _regionHeight = regionHeight;
            _width = width;
        }

        public int LastOpenedHeight { get; private set; }
        public AdvancedSelection? LastSelection { get; private set; }
        public PointD CursorPosition => new(_width / 2.0, _regionHeight / 2.0);

        public FrozenFrame CaptureMonitorUnderCursor() =>
            new(Solid(_width, _regionHeight, 0), new RectD(0, 0, _width, _regionHeight), null, 0);
        public FrozenFrame CaptureScreenRegion(RectD bounds) =>
            new(CaptureRegion(bounds), bounds, null, 0);

        public BitmapSource CaptureRegion(RectD screenBounds)
        {
            var bitmap = new WriteableBitmap(_width, _regionHeight, 96, 96, PixelFormats.Bgra32, null);
            byte[] pixels = new byte[_width * _regionHeight * 4];
            for (int row = 0; row < _regionHeight; row++)
            {
                byte value = (byte)(Math.Min(_total - 1, _scrollOffset + row) & 0xFF);
                Array.Fill(pixels, value, row * _width * 4, _width * 4);
            }

            bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _width, _regionHeight), pixels, _width * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }

        public WindowUnderCursor? WindowAt(PointD point) =>
            new(new IntPtr(1), new RectD(0, 0, _width, _regionHeight), "scroll");
        public RectD? ResolveRepeatRegion(RegionHistoryEntry history) => history.ScreenRegion;

        public bool OpenEditor(AdvancedSelection selection)
        {
            LastSelection = selection;
            LastOpenedHeight = (int)selection.Region.Height;
            return true;
        }

        internal void Advance(int rows) => _scrollOffset = Math.Min(_total - 1, _scrollOffset + rows);
    }

    private sealed class FakeScrollSink : IScrollInputSink
    {
        public int Calls { get; private set; }
        public ScrollingEnvironment? Target { get; set; }

        public bool ScrollDown(IntPtr targetWindow, PointD screenPoint, int notches)
        {
            Calls++;
            Target?.Advance(notches * 4);
            return true;
        }
    }
}
