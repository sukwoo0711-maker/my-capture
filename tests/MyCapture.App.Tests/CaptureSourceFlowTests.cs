using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Application and folder metadata from the frozen frame to the stored library record.</summary>
public sealed class CaptureSourceFlowTests
{
    private const string ExplorerPath = @"C:\Windows\explorer.exe";
    private const string Folder = @"C:\Users\test\Documents\계약";
    private static readonly WindowCandidate Explorer = new(new IntPtr(20), new RectD(-400, 0, 400, 300), "계약",
        ProcessId: 7, ClassName: "CabinetWClass");
    private static readonly WindowSourceSnapshot ExplorerSource = new(7, "Windows 탐색기", ExplorerPath, Folder);

    [Theory]
    [InlineData("stable", "Windows 탐색기", Folder)]
    [InlineData("navigated", "Windows 탐색기", "")]
    [InlineData("process", "", "")]
    [InlineData("missing", "", "")]
    public async Task SourcesAreReadBesideThePageProbeAndKeptOnlyWhenStableAroundThePixels(string change,
        string expectedApp, string expectedFolder)
    {
        var events = new List<string>();
        int sourceReads = 0;
        var result = await CapturePageContext.AcquireAsync([Explorer], () =>
        {
            events.Add("pixels");
            return Frame();
        }, _ =>
        {
            events.Add("pages");
            return Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        }, readSources: _ =>
        {
            events.Add("sources");
            sourceReads++;
            WindowSourceSnapshot snapshot = sourceReads == 1 ? ExplorerSource : change switch
            {
                "navigated" => ExplorerSource with { DocumentPath = @"C:\Users\test\Downloads" },
                "process" => ExplorerSource with { ProcessId = 8 },
                _ => ExplorerSource,
            };
            IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> read = sourceReads == 2 && change == "missing"
                ? new Dictionary<IntPtr, WindowSourceSnapshot>()
                : new Dictionary<IntPtr, WindowSourceSnapshot> { [Explorer.Handle] = snapshot };
            return Task.FromResult(read);
        });

        Assert.Equal(new[] { "pages", "sources", "pixels", "sources" }, events);
        CaptureSourceDetails? source = Assert.Single(result.Windows).Source;
        Assert.Equal(expectedApp, source?.AppName ?? "");
        Assert.Equal(expectedFolder, source?.DocumentPath ?? "");
        Assert.Null(Explorer.Source); // The selector's earlier snapshot is never mutated.
    }

    [Fact]
    public async Task SourceReaderFailureOrMovingWindowsLeaveTheCaptureWithoutSource()
    {
        int acquisitions = 0;
        var failed = await CapturePageContext.AcquireAsync([Explorer], () => { acquisitions++; return Frame(); },
            _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>()),
            readSources: _ => throw new InvalidOperationException("shell unavailable"));
        Assert.Equal(1, acquisitions);
        Assert.Null(Assert.Single(failed.Windows).Source);

        int reads = 0;
        var moved = await CapturePageContext.AcquireAsync([Explorer], Frame,
            _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>()),
            () => [Explorer with { ScreenBounds = new RectD(-380, 0, 400, 300) }],
            _ =>
            {
                reads++;
                return Task.FromResult<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>>(
                    new Dictionary<IntPtr, WindowSourceSnapshot> { [Explorer.Handle] = ExplorerSource });
            });
        Assert.Null(Assert.Single(moved.Windows).Source);
        Assert.Equal(1, reads); // No second read once the layout is known to have changed.
    }

    [Fact]
    public void RegionSelectionTakesApplicationAndFolderFromTheTitleWindow() => StaTestHost.Run(() =>
    {
        var explorer = Explorer with
        {
            ScreenBounds = new RectD(0, 0, 400, 300),
            Source = new CaptureSourceDetails("Windows 탐색기", ExplorerPath, Folder),
        };
        var browser = new WindowCandidate(new IntPtr(21), new RectD(300, 0, 340, 360), "Docs", "https://example.com/doc",
            ProcessId: 42, ClassName: "Chrome_WidgetWin_1",
            Source: new CaptureSourceDetails("Microsoft Edge", @"C:\Edge\msedge.exe"));
        var desk = new WindowCandidate(new IntPtr(12), new RectD(0, 0, 640, 360), "Program Manager",
            ProcessId: 3, ClassName: "Progman", Source: new CaptureSourceDetails("Windows 탐색기", ExplorerPath));
        var selections = new List<CaptureSelectionCompletedEventArgs>();
        CaptureSelectionCompletedEventArgs Complete(RectD region, WindowCandidate? clicked = null, bool wholeMonitor = false)
        {
            var overlay = new CaptureOverlayWindow(WindowDetectOverlayTests.Frame(), abortOnFocusLoss: false)
            { SourceWindows = [browser, explorer, desk] };
            overlay.SelectionCompleted += (_, e) => selections.Add(e);
            overlay.CompleteSelection(region, clicked, textCapture: false, wholeMonitor);
            return selections[^1];
        }

        CaptureSelectionCompletedEventArgs drag = Complete(new RectD(20, 20, 200, 200));
        Assert.Equal("계약", drag.SourceTitle);
        Assert.Equal(new CaptureSourceDetails("Windows 탐색기", ExplorerPath, Folder), drag.SourceDetails);
        Assert.Empty(drag.SourcePageUrl);

        CaptureSelectionCompletedEventArgs page = Complete(new RectD(400, 100, 100, 100));
        Assert.Equal("Docs", page.SourceTitle);
        Assert.Equal("https://example.com/doc", page.SourcePageUrl);
        Assert.Equal("Microsoft Edge", page.SourceDetails.AppName);
        Assert.Empty(page.SourceDetails.DocumentPath);

        // A clicked window is the source even when another window covers the region's centre.
        Assert.Equal("Docs", Complete(new RectD(200, 0, 400, 300)).SourceTitle);
        CaptureSelectionCompletedEventArgs clicked = Complete(new RectD(200, 0, 400, 300), explorer);
        Assert.Equal("계약", clicked.SourceTitle);
        Assert.Equal(Folder, clicked.SourceDetails.DocumentPath);

        Assert.True(Complete(new RectD(0, 0, 640, 360), desk).SourceDetails.IsEmpty);
        CaptureSelectionCompletedEventArgs monitor = Complete(new RectD(0, 0, 640, 360), wholeMonitor: true);
        Assert.True(monitor.SourceDetails.IsEmpty);
        Assert.Empty(monitor.SourceTitle);
    });

    [Fact]
    public void SelectionMadeBeforeSourceDetailsAreConfirmedWaitsAndCarriesTheConfirmedDetails() => StaTestHost.Run(() =>
    {
        var page = new WindowCandidate(new IntPtr(21), new RectD(0, 0, 640, 360), "Docs", ProcessId: 42,
            ClassName: "Chrome_WidgetWin_1");
        var overlay = new CaptureOverlayWindow(WindowDetectOverlayTests.Frame(), abortOnFocusLoss: false)
        { SourceWindows = [page] };
        var selections = new List<CaptureSelectionCompletedEventArgs>();
        overlay.SelectionCompleted += (_, e) => selections.Add(e);
        overlay.AwaitSourceDetails();

        overlay.CompleteSelection(new RectD(20, 20, 200, 120), page, textCapture: false);
        Assert.Empty(selections);
        Assert.True(overlay.SourceDetailsPendingForTest);

        overlay.ConfirmSourceDetails([page with
        {
            SourcePageUrl = "https://example.com/doc",
            Source = new CaptureSourceDetails("Microsoft Edge", @"C:\Edge\msedge.exe"),
        }]);
        CaptureSelectionCompletedEventArgs selection = Assert.Single(selections);
        Assert.Equal(new RectD(20, 20, 200, 120), selection.BitmapRegion);
        Assert.Equal("Docs", selection.SourceTitle);
        Assert.Equal("https://example.com/doc", selection.SourcePageUrl);
        Assert.Equal("Microsoft Edge", selection.SourceDetails.AppName);
        Assert.False(overlay.SourceDetailsPendingForTest);
    });

    [Fact]
    public void ClosingWhileSourceDetailsArePendingDropsTheWaitingSelection() => StaTestHost.Run(() =>
    {
        var overlay = new CaptureOverlayWindow(WindowDetectOverlayTests.Frame(), abortOnFocusLoss: false);
        int completed = 0;
        overlay.SelectionCompleted += (_, _) => completed++;
        overlay.AwaitSourceDetails();
        overlay.CompleteSelection(new RectD(4, 4, 32, 24));
        overlay.Close();
        overlay.ConfirmSourceDetails([]);
        Assert.Equal(0, completed);
    });

    [Fact]
    public void ConfirmedSourceDetailsLeaveLaterSelectionsImmediate() => StaTestHost.Run(() =>
    {
        var overlay = new CaptureOverlayWindow(WindowDetectOverlayTests.Frame(), abortOnFocusLoss: false);
        int completed = 0;
        overlay.SelectionCompleted += (_, _) => completed++;
        overlay.AwaitSourceDetails();
        overlay.ConfirmSourceDetails([]);
        overlay.CompleteSelection(new RectD(4, 4, 32, 24));
        Assert.Equal(1, completed);
    });

    [Theory]
    [InlineData("window")]
    [InlineData("fixed")]
    [InlineData("repeat")]
    public void AdvancedCapturesReadTheSourceAroundTheFrame(string mode) => StaTestHost.Run(() =>
    {
        var environment = new SourceEnvironment();
        Assert.True(Capture(mode, environment).IsCompleted);
        Assert.Equal(new CaptureSourceDetails("Windows 탐색기", ExplorerPath, Folder), environment.Selection!.SourceDetails);
        Assert.Equal(new[] { "source", "capture", "source", "editor" }, environment.Events);
    });

    [Theory]
    [InlineData("window")]
    [InlineData("fixed")]
    [InlineData("repeat")]
    public void AdvancedCapturesDropWhatChangedDuringTheFrame(string mode) => StaTestHost.Run(() =>
    {
        var navigated = new SourceEnvironment();
        navigated.OnCapture = () => navigated.Source = ExplorerSource with { DocumentPath = @"C:\Users\test\Downloads" };
        Assert.True(Capture(mode, navigated).IsCompleted);
        Assert.Equal(new CaptureSourceDetails("Windows 탐색기", ExplorerPath), navigated.Selection!.SourceDetails);

        var reused = new SourceEnvironment();
        reused.OnCapture = () => reused.Window = reused.Window with { ProcessId = 99 };
        Assert.True(Capture(mode, reused).IsCompleted);
        Assert.True(reused.Selection!.SourceDetails!.IsEmpty);

        var failing = new SourceEnvironment { Failure = new TimeoutException() };
        Assert.True(Capture(mode, failing).IsCompleted);
        Assert.True(failing.Selection!.SourceDetails!.IsEmpty);
        Assert.Equal(1, failing.CaptureCount);
    });

    [Fact]
    public void FullScreenShortcutNeverReadsASource() => StaTestHost.Run(() =>
    {
        var environment = new SourceEnvironment();
        Assert.True(Capture("full", environment).IsCompleted);
        Assert.True(environment.Selection!.SourceDetails is null || environment.Selection.SourceDetails.IsEmpty);
        Assert.Empty(environment.Selection.SourceTitle);
        Assert.Equal(new[] { "capture", "editor" }, environment.Events);
    });

    [Fact]
    public void ScrollingCaptureKeepsTheSourceSeenBeforeTheFirstAndAfterTheLastFrame() => StaTestHost.Run(() =>
    {
        var environment = new SourceEnvironment
        {
            Window = new WindowUnderCursor(new IntPtr(20), new RectD(0, 0, 8, 20), "계약", ProcessId: 7),
            RowCodedFrames = true,
        };
        CaptureOutcome result = new AdvancedCaptureService(environment, new LastRegionStore(() => 10), new ScrollSink(),
                NullLogger.Instance, (_, token) => token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask)
            .CaptureScrollingAsync(environment.Window.Handle, new RectD(0, 0, 8, 20), ScrollStitchOptions.Default, 3)
            .GetAwaiter().GetResult();
        Assert.True(result.IsCompleted);
        Assert.Equal(new CaptureSourceDetails("Windows 탐색기", ExplorerPath, Folder), environment.Selection!.SourceDetails);
        Assert.Equal(2, environment.Events.Count(item => item == "source"));
        Assert.Equal("source", environment.Events[0]);
    });

    [Fact]
    public void PersistedRecordKeepsSourceLocallyButPngFilesNeverCarryIt() => StaTestHost.Run(() =>
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mycapture-source-").FullName;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var settings = new AppSettings();
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            BitmapSource bitmap = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 6 * 4], 8 * 4);
            bitmap.Freeze();
            var details = new CaptureSourceDetails("Windows 탐색기", ExplorerPath, Folder);
            CaptureRecord record = CapturePersistenceService.CreatePendingRecord(bitmap, 1, "계약", "DISPLAY1",
                sourceDetails: details);
            persistence.PersistPendingOriginalAsync(record, bitmap).GetAwaiter().GetResult();

            var reloaded = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            reloaded.Load();
            Assert.Equal(details, Assert.Single(reloaded.Records).SourceDetails);
            CaptureRecord meta = JsonSerializer.Deserialize<CaptureRecord>(
                File.ReadAllText(queue.GetFilePath(record, CaptureFileNames.Meta)), MyCapture.Core.Serialization.JsonDefaults.Readable)!;
            Assert.Equal(details, meta.SourceDetails);
            foreach (string png in new[] { CaptureFileNames.Original, CaptureFileNames.Rendered })
            {
                byte[] bytes = File.ReadAllBytes(queue.GetFilePath(record, png));
                foreach (string secret in new[] { Folder, ExplorerPath, "Windows 탐색기", "계약" })
                {
                    Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(secret)), $"{png} carries {secret}");
                    Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(secret)), $"{png} carries {secret}");
                }
            }
        }
        finally { TestRecycleBin.DeleteDirectory(root, recursive: true); }
    });

    private static bool Contains(byte[] haystack, byte[] needle) =>
        needle.Length > 0 && haystack.AsSpan().IndexOf(needle) >= 0;

    private static CaptureOutcome Capture(string mode, SourceEnvironment environment)
    {
        var history = new LastRegionStore(() => 10);
        history.Record(new RegionHistoryEntry(new RectD(100, 80, 120, 80), string.Empty, new RectD(0, 0, 300, 200), 96));
        var service = new AdvancedCaptureService(environment, history, new ScrollSink(), NullLogger.Instance,
            (_, token) => token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask);
        return mode switch
        {
            "full" => service.CaptureFullScreen(),
            "window" => service.CaptureWindow(),
            "fixed" => service.CaptureFixedSize(120, 80),
            "repeat" => service.RepeatLastRegion(),
            _ => throw new ArgumentException(mode),
        };
    }

    private static FrozenFrame Frame()
    {
        BitmapSource bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[256], 32);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(-400, 0, 8, 8), null, 0);
    }

    private sealed class ScrollSink : IScrollInputSink
    {
        public bool ScrollDown(IntPtr targetWindow, PointD screenPoint, int notches) => true;
    }

    private sealed class SourceEnvironment : IAdvancedCaptureEnvironment
    {
        public WindowUnderCursor Window { get; set; } = new(new IntPtr(20), new RectD(0, 0, 300, 200), "계약", ProcessId: 7);
        public WindowSourceSnapshot? Source { get; set; } = ExplorerSource;
        public Exception? Failure { get; set; }
        public Action? OnCapture { get; set; }
        public bool RowCodedFrames { get; set; }
        public List<string> Events { get; } = [];
        public int CaptureCount { get; private set; }
        public AdvancedSelection? Selection { get; private set; }
        public bool CanOpenEditor => true;
        public PointD CursorPosition => new(150, 120);

        public WindowUnderCursor? WindowAt(PointD screenPoint) => Window;
        public RectD? ResolveRepeatRegion(RegionHistoryEntry entry) => entry.ScreenRegion;

        public WindowSourceSnapshot? ReadWindowSource(WindowUnderCursor? window)
        {
            Assert.Null(Selection); // Never read source metadata after the editor opened.
            Events.Add("source");
            if (Failure is { } failure) throw failure;
            return Source;
        }

        public FrozenFrame CaptureMonitorUnderCursor() => CaptureScreenRegion(new RectD(0, 0, 300, 200));
        public FrozenFrame CaptureScreenRegion(RectD bounds) => new(CaptureRegion(bounds), bounds, null, 0);

        public BitmapSource CaptureRegion(RectD bounds)
        {
            Events.Add("capture");
            CaptureCount++;
            OnCapture?.Invoke();
            int width = (int)bounds.Width, height = (int)bounds.Height;
            byte[] pixels = new byte[width * height * 4];
            for (int row = 0; row < height; row++)
                Array.Fill(pixels, (byte)(RowCodedFrames ? row + (CaptureCount - 1) * 5 : 64), row * width * 4, width * 4);
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        public bool OpenEditor(AdvancedSelection selection)
        {
            Events.Add("editor");
            Selection = selection;
            return true;
        }
    }
}
