using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Window, fixed-size, previous-region and scrolling captures never wait for their page and application
/// reads: the pixels are taken, saved and opened at once, and what the reads agree on for the same window
/// reaches the saved capture afterwards, exactly as the region selector's late details do.
/// </summary>
public sealed class AdvancedCaptureLateSourceTests
{
    private const string Url = "https://example.com/document?page=2";
    private static readonly BrowserDocumentSnapshot Document = new(42, "A document", "document-1", Url);
    private static readonly WindowSourceSnapshot EdgeSource = new(42, "Microsoft Edge", @"C:\Edge\msedge.exe", "");
    private static readonly CaptureSourceDetails Edge = new("Microsoft Edge", @"C:\Edge\msedge.exe");

    [Theory]
    [InlineData("window")]
    [InlineData("fixed")]
    [InlineData("repeat")]
    public void StillCaptureOpensBeforeItsReadsFinishAndTheAgreedSourceFollows(string mode) => StaTestHost.Run(() =>
    {
        var environment = new LateEnvironment();
        Assert.Equal(CaptureOutcomeKind.Completed, Capture(mode, environment).Kind);

        // Both first reads started before the pixels and are still running when the capture opens.
        Assert.Equal(new[] { "page", "source", "capture", "editor" }, environment.Events);
        AdvancedSelection selection = environment.Selection!;
        Assert.Equal("A document", selection.SourceTitle);
        Assert.Empty(selection.SourcePageUrl);
        Assert.True(selection.SourceDetails is null || selection.SourceDetails.IsEmpty);
        Task<CaptureSelectionSource> pending = Assert.IsAssignableFrom<Task<CaptureSelectionSource>>(selection.PendingSource);
        Assert.False(pending.IsCompleted);

        // The confirming reads start only after both first reads.
        environment.CompletePage(0, Document);
        Thread.Sleep(100);
        Assert.Equal(1, environment.PageReads);
        environment.CompleteSource(0, EdgeSource);
        WaitUntil(() => environment.PageReads == 2 && environment.SourceReads == 2);
        environment.CompletePage(1, Document);
        environment.CompleteSource(1, EdgeSource);

        Assert.Equal(new CaptureSelectionSource("A document", Url, Edge), Result(pending));
        Assert.Equal(1, environment.CaptureCount);
    });

    [Theory]
    [InlineData("window")]
    [InlineData("fixed")]
    [InlineData("repeat")]
    public void WindowThatChangedBesideThePixelsKeepsNoPageOrApplication(string mode) => StaTestHost.Run(() =>
    {
        var environment = new LateEnvironment();
        environment.OnCapture = _ => environment.Window = environment.Window with { ScreenBounds = new RectD(10, 0, 300, 200) };
        Assert.True(Capture(mode, environment).IsCompleted);
        Task<CaptureSelectionSource> pending = environment.Selection!.PendingSource!;

        environment.CompletePage(0, Document);
        environment.CompleteSource(0, EdgeSource);
        Assert.Equal(new CaptureSelectionSource("A document", "", CaptureSourceDetails.Empty), Result(pending));
        Assert.Equal(1, environment.PageReads);
        Assert.Equal(1, environment.SourceReads);
    });

    [Theory]
    [InlineData("window")]
    [InlineData("fixed")]
    [InlineData("repeat")]
    public void PageThatChangedBeforeTheConfirmingReadIsDroppedButTheApplicationKept(string mode) => StaTestHost.Run(() =>
    {
        var environment = new LateEnvironment();
        Assert.True(Capture(mode, environment).IsCompleted);
        Task<CaptureSelectionSource> pending = environment.Selection!.PendingSource!;

        environment.CompletePage(0, Document);
        environment.CompleteSource(0, EdgeSource);
        WaitUntil(() => environment.PageReads == 2 && environment.SourceReads == 2);
        environment.CompletePage(1, Document with { DocumentId = "document-2" });
        environment.CompleteSource(1, EdgeSource);
        Assert.Equal(new CaptureSelectionSource("A document", "", Edge), Result(pending));
    });

    [Fact]
    public void ScrollingFramesNeverWaitForTheReads() => StaTestHost.Run(() =>
    {
        LateEnvironment environment = ScrollingEnvironment();
        Assert.True(Scroll(environment, maxFrames: 4).IsCompleted);

        // Every frame was taken while the first page and application reads were still running.
        Assert.Equal(4, environment.CaptureCount);
        Assert.Equal(new[] { "source", "page", "capture", "capture", "capture", "capture", "editor" }, environment.Events);
        Task<CaptureSelectionSource> pending = Assert.IsAssignableFrom<Task<CaptureSelectionSource>>(
            environment.Selection!.PendingSource);
        Assert.False(pending.IsCompleted);

        // That page read started before the first frame; the one confirming it starts after the last.
        environment.CompletePage(0, Document);
        environment.CompleteSource(0, EdgeSource);
        WaitUntil(() => environment.PageReads == 2 && environment.SourceReads == 2);
        environment.CompletePage(1, Document);
        environment.CompleteSource(1, EdgeSource);
        Assert.Equal(new CaptureSelectionSource("", Url, Edge), Result(pending));
    });

    [Fact]
    public void ScrollingPageThatChangedByTheLastReadIsDroppedButTheApplicationKept() => StaTestHost.Run(() =>
    {
        LateEnvironment environment = ScrollingEnvironment();
        Assert.True(Scroll(environment, maxFrames: 3).IsCompleted);
        Task<CaptureSelectionSource> pending = environment.Selection!.PendingSource!;

        environment.CompletePage(0, Document);
        environment.CompleteSource(0, EdgeSource);
        WaitUntil(() => environment.PageReads == 2 && environment.SourceReads == 2);
        environment.CompletePage(1, Document with { DocumentId = "other-document" });
        environment.CompleteSource(1, EdgeSource);
        Assert.Equal(new CaptureSelectionSource("", "", Edge), Result(pending));
    });

    [Fact]
    public void ScrollingWindowThatMovedDuringTheFramesKeepsNoPage() => StaTestHost.Run(() =>
    {
        LateEnvironment environment = ScrollingEnvironment();
        environment.OnCapture = count =>
        {
            if (count == 2) environment.Window = environment.Window with { ScreenBounds = new RectD(4, 0, 8, 20) };
        };
        Assert.True(Scroll(environment, maxFrames: 3).IsCompleted);
        Task<CaptureSelectionSource> pending = environment.Selection!.PendingSource!;
        environment.CompletePage(0, Document);
        environment.CompleteSource(0, EdgeSource);
        // Neither the page nor the application is confirmed for a window that changed.
        Assert.Equal(new CaptureSelectionSource("", "", CaptureSourceDetails.Empty), Result(pending));
        Assert.Equal(1, environment.PageReads);
        Assert.Equal(1, environment.SourceReads);
    });

    [Theory]
    [InlineData("window", false)]
    [InlineData("fixed", false)]
    [InlineData("repeat", false)]
    [InlineData("scroll", false)]
    [InlineData("window", true)]
    [InlineData("fixed", true)]
    [InlineData("repeat", true)]
    [InlineData("scroll", true)]
    public void SavedBeforeTheReadsAndBackfilledUnlessDeleted(string mode, bool deleteFirst) => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        LateEnvironment environment = mode == "scroll" ? ScrollingEnvironment() : new LateEnvironment();
        environment.Opened = shell.Open;
        Assert.True((mode == "scroll" ? Scroll(environment, maxFrames: 3) : Capture(mode, environment)).IsCompleted);

        // The App's own selection handler saves the capture without waiting for any read.
        shell.Wait(shell.Coordinator.LastTransitionForTest);
        CaptureRecord record = Assert.Single(shell.Queue.Records);
        Assert.Empty(record.SourcePageUrl);
        Assert.True(record.SourceDetails.IsEmpty);
        Assert.Equal(1, environment.PageReads);
        string directory = shell.Queue.GetDirectory(record);
        Assert.True(File.Exists(Path.Combine(directory, CaptureFileNames.Original)));
        if (deleteFirst)
        {
            shell.ReleaseEditSession();
            Assert.True(shell.Queue.Remove(record.Id));
            shell.Queue.Save();
            Assert.False(Directory.Exists(directory));
        }

        environment.CompletePage(0, Document);
        environment.CompleteSource(0, EdgeSource);
        WaitUntil(() => environment.PageReads == 2 && environment.SourceReads == 2);
        environment.CompletePage(1, Document);
        environment.CompleteSource(1, EdgeSource);
        shell.Wait(shell.SourceBackfill);

        if (deleteFirst)
        {
            Assert.False(Directory.Exists(directory));
            Assert.Empty(shell.Queue.Records);
            Assert.Empty(shell.Reload().Records);
            return;
        }

        Assert.Equal(Url, record.SourcePageUrl);
        Assert.Equal(Edge, record.SourceDetails);
        Assert.Equal(MyCapture.Core.Localization.UiText.Get("Capture.WebpageTag"), record.Tags);
        Assert.Equal(Url, PngSourceUrlMetadata.TryReadSourceUrl(
            File.ReadAllBytes(Path.Combine(directory, CaptureFileNames.Original))));
        CaptureRecord reloaded = Assert.Single(shell.Reload().Records);
        Assert.Equal(Url, reloaded.SourcePageUrl);
        Assert.Equal(Edge, reloaded.SourceDetails);
    });

    private static CaptureOutcome Capture(string mode, LateEnvironment environment)
    {
        var history = new LastRegionStore(() => 10);
        history.Record(new RegionHistoryEntry(new RectD(100, 80, 120, 80), string.Empty, new RectD(0, 0, 300, 200), 96));
        AdvancedCaptureService service = Service(environment, history);
        return mode switch
        {
            "window" => service.CaptureWindow(),
            "fixed" => service.CaptureFixedSize(120, 80),
            "repeat" => service.RepeatLastRegion(),
            _ => throw new ArgumentException(mode),
        };
    }

    private static CaptureOutcome Scroll(LateEnvironment environment, int maxFrames) =>
        Service(environment, new LastRegionStore(() => 10))
            .CaptureScrollingAsync(environment.Window.Handle, new RectD(0, 0, 8, 20), ScrollStitchOptions.Default, maxFrames)
            .GetAwaiter().GetResult();

    private static AdvancedCaptureService Service(LateEnvironment environment, LastRegionStore history) =>
        new(environment, history, new ScrollSink(), NullLogger.Instance,
            (_, token) => token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask);

    private static LateEnvironment ScrollingEnvironment() => new()
    {
        Window = new WindowUnderCursor(new IntPtr(7), new RectD(0, 0, 8, 20), "A document", ProcessId: 42),
        RowCodedFrames = true,
    };

    private static CaptureSelectionSource Result(Task<CaptureSelectionSource> pending)
    {
        Assert.True(pending.Wait(TimeSpan.FromSeconds(5)), "The late source did not resolve.");
        return pending.Result;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The reads did not start.");
            Thread.Sleep(5);
        }
    }

    private sealed class ScrollSink : IScrollInputSink
    {
        public bool ScrollDown(IntPtr targetWindow, PointD screenPoint, int notches) => true;
    }

    /// <summary>Reads that stay pending until the test completes them, in the order they were asked.</summary>
    private sealed class LateEnvironment : IAdvancedCaptureEnvironment
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];
        private readonly List<TaskCompletionSource<BrowserDocumentSnapshot?>> _pages = [];
        private readonly List<TaskCompletionSource<WindowSourceSnapshot?>> _sources = [];
        private WindowUnderCursor _window = new(new IntPtr(7), new RectD(0, 0, 300, 200), "A document", ProcessId: 42);

        public WindowUnderCursor Window
        {
            get { lock (_gate) return _window; }
            set { lock (_gate) _window = value; }
        }

        public Action<int>? OnCapture { get; set; }
        public Func<AdvancedSelection, bool>? Opened { get; set; }
        public bool RowCodedFrames { get; set; }
        public int CaptureCount { get; private set; }
        public AdvancedSelection? Selection { get; private set; }
        public bool CanOpenEditor => true;
        public PointD CursorPosition => new(150, 120);

        public string[] Events { get { lock (_gate) return _events.ToArray(); } }
        public int PageReads { get { lock (_gate) return _pages.Count; } }
        public int SourceReads { get { lock (_gate) return _sources.Count; } }

        public void CompletePage(int index, BrowserDocumentSnapshot? document)
        {
            TaskCompletionSource<BrowserDocumentSnapshot?> read;
            lock (_gate) read = _pages[index];
            read.SetResult(document);
        }

        public void CompleteSource(int index, WindowSourceSnapshot? source)
        {
            TaskCompletionSource<WindowSourceSnapshot?> read;
            lock (_gate) read = _sources[index];
            read.SetResult(source);
        }

        public WindowUnderCursor? WindowAt(PointD screenPoint) => Window;
        public RectD? ResolveRepeatRegion(RegionHistoryEntry entry) => entry.ScreenRegion;

        public Task<BrowserDocumentSnapshot?> ReadSourceDocumentAsync(WindowUnderCursor? window)
        {
            var read = new TaskCompletionSource<BrowserDocumentSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _events.Add("page");
                _pages.Add(read);
            }

            return read.Task;
        }

        public Task<WindowSourceSnapshot?> ReadWindowSourceAsync(WindowUnderCursor? window)
        {
            var read = new TaskCompletionSource<WindowSourceSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _events.Add("source");
                _sources.Add(read);
            }

            return read.Task;
        }

        public FrozenFrame CaptureMonitorUnderCursor() => CaptureScreenRegion(new RectD(0, 0, 300, 200));
        public FrozenFrame CaptureScreenRegion(RectD bounds) => new(CaptureRegion(bounds), bounds, null, 0);

        public BitmapSource CaptureRegion(RectD bounds)
        {
            lock (_gate) _events.Add("capture");
            CaptureCount++;
            OnCapture?.Invoke(CaptureCount);
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
            lock (_gate) _events.Add("editor");
            Selection = selection;
            return Opened?.Invoke(selection) ?? true;
        }
    }

    /// <summary>
    /// The App's own capture handler over a temporary library, behind the real coordinator, opened the
    /// way OverlayAdvancedCaptureEnvironment opens an advanced capture. No editor window is shown.
    /// </summary>
    private sealed class Shell : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly App _app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mycapture-late-source-").FullName;
        private readonly AppPaths _paths;
        private readonly AppSettings _settings = new();

        internal Shell()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            // The App hands late details back to its own dispatcher.
            (typeof(DispatcherObject).GetField("_dispatcher", Flags)
                ?? throw new InvalidOperationException("DispatcherObject._dispatcher is unavailable."))
                .SetValue(_app, Dispatcher.CurrentDispatcher);
            _paths = AppPaths.CreateForRoot(_root);
            Queue = new CaptureQueue(_paths, _settings.Queue, NullLogger<CaptureQueue>.Instance);
            Queue.Evicted += (_, e) =>
            {
                string directory = Queue.GetDirectory(e.Record);
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            };
            var persistence = new CapturePersistenceService(Queue, _paths, () => _settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(persistence, () => _settings, () => _paths,
                NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
            Set("_queue", Queue);
            Set("_settings", _settings);
            Set("_persistence", persistence);
            Set("_commit", commit);
            Coordinator = new CaptureOverlayCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
                new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
                NullLogger<CaptureOverlayCoordinator>.Instance)
            {
                SelectionPersistRequested = selection =>
                    (Task)typeof(App).GetMethod("OnCaptureSelectionCompletedAsync", Flags)!.Invoke(_app, [selection])!,
                OpenEditorAfterCapture = () => false,
            };
        }

        internal CaptureQueue Queue { get; }

        internal CaptureOverlayCoordinator Coordinator { get; }

        internal Task SourceBackfill => (Task)typeof(App).GetField("_sourceBackfillTask", Flags)!.GetValue(_app)!;

        internal bool Open(AdvancedSelection selection) => Coordinator.StartWithSelection(selection.Frame,
            selection.Region, selection.SourceTitle, selection.RecordForRepeat, selection.SourcePageUrl,
            selection.SourceDetails, selection.PendingSource);

        internal void Wait(Task task)
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The capture did not settle.");
                if (!task.IsCompleted) Thread.Sleep(2);
            }

            task.GetAwaiter().GetResult();
        }

        internal void ReleaseEditSession()
        {
            ((IDisposable?)typeof(App).GetField("_currentEditSession", Flags)!.GetValue(_app))?.Dispose();
            Set("_currentEditSession", null);
        }

        internal CaptureQueue Reload()
        {
            var reloaded = new CaptureQueue(_paths, _settings.Queue, NullLogger<CaptureQueue>.Instance);
            reloaded.Load();
            return reloaded;
        }

        private void Set(string name, object? value) => typeof(App).GetField(name, Flags)!.SetValue(_app, value);

        public void Dispose()
        {
            ReleaseEditSession();
            Coordinator.Dispose();
            SynchronizationContext.SetSynchronizationContext(_previous);
            if (Directory.Exists(_root)) TestRecycleBin.DeleteDirectory(_root, recursive: true);
        }
    }
}
