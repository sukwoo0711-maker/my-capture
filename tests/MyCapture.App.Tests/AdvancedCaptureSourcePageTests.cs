using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AdvancedCaptureSourcePageTests
{
    private const string Url = "https://example.com/document?page=2";
    private static readonly BrowserDocumentSnapshot Document = new(42, "A document", "document-1", Url);

    [Theory]
    [InlineData("full")]
    [InlineData("window")]
    [InlineData("fixed")]
    [InlineData("repeat")]
    public void StableSource_IsReadAroundFrame_AndCarriedBeforeEditor(string mode) => StaTestHost.Run(() =>
    {
        var environment = new Environment();
        CaptureOutcome result = Capture(mode, environment);
        Assert.Equal(CaptureOutcomeKind.Completed, result.Kind);
        Assert.Equal(Url, environment.Selection!.SourcePageUrl);
        Assert.Equal("A document", environment.Selection.SourceTitle);
        Assert.Equal(new[] { "read", "capture", "read", "editor" }, environment.Events);
    });

    [Theory]
    [InlineData("full")]
    [InlineData("window")]
    [InlineData("fixed")]
    [InlineData("repeat")]
    public void DocumentChangesDuringFrame_OmitsUrlWithoutBlockingCapture(string mode) => StaTestHost.Run(() =>
    {
        var environment = new Environment();
        environment.OnCapture = _ => environment.Document = Document with { DocumentId = "document-2" };
        CaptureOutcome result = Capture(mode, environment);
        Assert.Equal(CaptureOutcomeKind.Completed, result.Kind);
        Assert.Equal(string.Empty, environment.Selection!.SourcePageUrl);
    });

    [Theory]
    [InlineData("full")]
    [InlineData("window")]
    [InlineData("fixed")]
    [InlineData("repeat")]
    public void WindowReusedByOtherProcess_OmitsUrl(string mode) => StaTestHost.Run(() =>
    {
        var environment = new Environment();
        environment.OnCapture = _ => environment.Window = environment.Window with { ProcessId = 99 };
        Assert.True(Capture(mode, environment).IsCompleted);
        Assert.Equal(string.Empty, environment.Selection!.SourcePageUrl);
        Assert.Equal(1, environment.Events.Count(item => item == "read"));
    });

    [Theory]
    [InlineData("null")]
    [InlineData("timeout")]
    [InlineData("unsafe-url")]
    [InlineData("missing-document-id")]
    [InlineData("wrong-title")]
    [InlineData("wrong-process")]
    public void UnavailableOrInvalidMetadata_DoesNotPreventStillCapture(string invalid) => StaTestHost.Run(() =>
    {
        var environment = new Environment
        {
            Document = invalid switch
            {
                "null" => null,
                "unsafe-url" => Document with { Url = "javascript:alert(1)" },
                "missing-document-id" => Document with { DocumentId = "" },
                "wrong-title" => Document with { WindowTitle = "Other document" },
                "wrong-process" => Document with { ProcessId = 43 },
                _ => Document,
            },
            ReadFailure = invalid == "timeout" ? new TimeoutException() : null,
        };
        Assert.True(Capture("full", environment).IsCompleted);
        Assert.Equal(string.Empty, environment.Selection!.SourcePageUrl);
        Assert.Equal(1, environment.CaptureCount);
    });

    [Theory]
    [InlineData("fixed")]
    [InlineData("repeat")]
    public void RegionExtendingOutsideSourceWindow_OmitsUrl(string mode) => StaTestHost.Run(() =>
    {
        var environment = new Environment
        {
            Window = new(new IntPtr(7), new RectD(130, 90, 50, 50), "A document", ProcessId: 42),
        };
        Assert.True(Capture(mode, environment).IsCompleted);
        Assert.Equal(string.Empty, environment.Selection!.SourcePageUrl);
    });

    [Fact]
    public void BusySession_DoesNotProbeOrCapture()
    {
        var environment = new Environment { CanOpenEditor = false };
        Assert.Equal(CaptureOutcomeKind.Cancelled, Capture("full", environment).Kind);
        Assert.Empty(environment.Events);
    }

    [Fact]
    public void Fullscreen_WhenCaptureMovesToAnotherMonitor_DoesNotReuseOldCursorSource() => StaTestHost.Run(() =>
    {
        var environment = new Environment { MonitorBounds = new RectD(400, 0, 300, 200) };
        Assert.True(Capture("full", environment).IsCompleted);
        Assert.Equal(string.Empty, environment.Selection!.SourcePageUrl);
    });

    [Fact]
    public void Scrolling_StableTarget_ValidatesEveryFrameAndCarriesUrl() => StaTestHost.Run(() =>
    {
        var environment = ScrollingEnvironment();
        CaptureOutcome result = Scroll(environment, maxFrames: 3);
        Assert.True(result.IsCompleted);
        Assert.Equal(Url, environment.Selection!.SourcePageUrl);
        Assert.Equal(3, environment.CaptureCount);
        Assert.Equal(6, environment.Events.Count(item => item == "read"));
        Assert.Equal("editor", environment.Events.Last());
    });

    [Fact]
    public void Scrolling_ChangedDocumentStaysUnattributedAfterItReturns() => StaTestHost.Run(() =>
    {
        var environment = ScrollingEnvironment();
        environment.OnCapture = count => environment.Document = count == 2
            ? Document with { DocumentId = "other-document" } : Document;
        Assert.True(Scroll(environment, maxFrames: 3).IsCompleted);
        Assert.Equal(3, environment.CaptureCount);
        Assert.Equal(string.Empty, environment.Selection!.SourcePageUrl);
        Assert.Equal(4, environment.Events.Count(item => item == "read"));
    });

    [Fact]
    public void Scrolling_DifferentTargetHandle_DoesNotBorrowNearbyBrowserUrl() => StaTestHost.Run(() =>
    {
        var environment = ScrollingEnvironment();
        Assert.True(Scroll(environment, target: new IntPtr(8)).IsCompleted);
        Assert.Equal(string.Empty, environment.Selection!.SourcePageUrl);
        Assert.DoesNotContain("read", environment.Events);
    });

    [Fact]
    public void Scrolling_CancellationDuringMetadata_PreventsFrameAndEditor() => StaTestHost.Run(() =>
    {
        using var cancellation = new CancellationTokenSource();
        var environment = ScrollingEnvironment();
        environment.OnRead = cancellation.Cancel;
        CaptureOutcome result = Scroll(environment, cancellation: cancellation.Token);
        Assert.Equal(CaptureOutcomeKind.Cancelled, result.Kind);
        Assert.Equal(0, environment.CaptureCount);
        Assert.Null(environment.Selection);
    });

    private static CaptureOutcome Capture(string mode, Environment environment)
    {
        var history = new LastRegionStore(() => 10);
        history.Record(new RegionHistoryEntry(new RectD(100, 80, 120, 80), string.Empty,
            new RectD(0, 0, 300, 200), 96));
        AdvancedCaptureService service = Service(environment, history);
        return mode switch
        {
            "full" => service.CaptureFullScreen(),
            "window" => service.CaptureWindow(),
            "fixed" => service.CaptureFixedSize(120, 80),
            "repeat" => service.RepeatLastRegion(),
            _ => throw new ArgumentException(mode),
        };
    }

    private static CaptureOutcome Scroll(Environment environment, int maxFrames = 2, IntPtr? target = null,
        CancellationToken cancellation = default) =>
        Service(environment, new LastRegionStore(() => 10))
            .CaptureScrollingAsync(target ?? environment.Window.Handle, new RectD(0, 0, 8, 20),
                ScrollStitchOptions.Default, maxFrames, cancellation).GetAwaiter().GetResult();

    private static AdvancedCaptureService Service(Environment environment, LastRegionStore history) =>
        new(environment, history, new ScrollSink(), NullLogger.Instance,
            (_, cancellation) => cancellation.IsCancellationRequested ? Task.FromCanceled(cancellation) : Task.CompletedTask);

    private static Environment ScrollingEnvironment() => new()
    {
        Window = new(new IntPtr(7), new RectD(0, 0, 8, 20), "A document", ProcessId: 42),
        RowCodedFrames = true,
    };

    private sealed class ScrollSink : IScrollInputSink
    {
        public bool ScrollDown(IntPtr targetWindow, PointD screenPoint, int notches) => true;
    }

    private sealed class Environment : IAdvancedCaptureEnvironment
    {
        public WindowUnderCursor Window { get; set; } = new(new IntPtr(7), new RectD(0, 0, 300, 200),
            "A document", ProcessId: 42);
        public BrowserDocumentSnapshot? Document { get; set; } = AdvancedCaptureSourcePageTests.Document;
        public bool CanOpenEditor { get; set; } = true;
        public PointD CursorPosition => new(150, 120);
        public int CaptureCount { get; private set; }
        public List<string> Events { get; } = [];
        public AdvancedSelection? Selection { get; private set; }
        public Action<int>? OnCapture { get; set; }
        public Action? OnRead { get; set; }
        public Exception? ReadFailure { get; set; }
        public bool RowCodedFrames { get; set; }
        public RectD MonitorBounds { get; set; } = new(0, 0, 300, 200);

        public WindowUnderCursor? WindowAt(PointD screenPoint) => Window;
        public RectD? ResolveRepeatRegion(RegionHistoryEntry entry) => entry.ScreenRegion;
        public BrowserDocumentSnapshot? ReadSourceDocument(WindowUnderCursor? window)
        {
            Assert.Null(Selection); // Never query source metadata after opening the editor.
            Events.Add("read");
            OnRead?.Invoke();
            if (ReadFailure is { } failure) throw failure;
            return Document;
        }
        public FrozenFrame CaptureMonitorUnderCursor() => CaptureScreenRegion(MonitorBounds);
        public FrozenFrame CaptureScreenRegion(RectD bounds) => new(CaptureRegion(bounds), bounds, null, 0);
        public BitmapSource CaptureRegion(RectD bounds)
        {
            Events.Add("capture");
            CaptureCount++;
            OnCapture?.Invoke(CaptureCount);
            int width = (int)bounds.Width, height = (int)bounds.Height;
            byte[] pixels = new byte[width * height * 4];
            for (int row = 0; row < height; row++)
                Array.Fill(pixels, (byte)(RowCodedFrames ? row + (CaptureCount - 1) * 5 : 64),
                    row * width * 4, width * 4);
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                pixels, width * 4);
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
