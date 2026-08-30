using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AdvancedCaptureServiceTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private static AdvancedCaptureService NewService(
        IAdvancedCaptureEnvironment environment,
        LastRegionStore store,
        IScrollInputSink? sink = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            environment,
            store,
            sink ?? new FakeScrollSink(),
            NullLogger.Instance,
            delay ?? (static (_, token) => token.IsCancellationRequested
                ? Task.FromCanceled(token)
                : Task.CompletedTask));

    private static BitmapSource Solid(int width, int height, byte value = 0x40)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, value);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource RowCodedBitmap(int width, int height, int baseValue)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            Array.Fill(pixels, (byte)(baseValue + row), row * width * 4, width * 4);
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class FakeEnvironment : IAdvancedCaptureEnvironment
    {
        public int OpenCount { get; private set; }
        public int ScreenCaptureCount { get; private set; }
        public AdvancedSelection? LastSelection { get; private set; }
        public RectD LastScreenCaptureBounds { get; private set; }
        public WindowUnderCursor? Window { get; set; }
        public bool RejectEditor { get; set; }
        public bool ResolveMissing { get; set; }
        public RectD? ResolvedRegion { get; set; }
        public PointD Cursor { get; set; } = new(150, 120);

        public FrozenFrame CaptureMonitorUnderCursor() =>
            new(Solid(300, 200), new RectD(0, 0, 300, 200), null, 0);

        public FrozenFrame CaptureScreenRegion(RectD screenBounds)
        {
            ScreenCaptureCount++;
            LastScreenCaptureBounds = screenBounds;
            return new FrozenFrame(
                Solid((int)screenBounds.Width, (int)screenBounds.Height),
                screenBounds,
                null,
                0);
        }

        public BitmapSource CaptureRegion(RectD screenBounds) =>
            Solid((int)screenBounds.Width, (int)screenBounds.Height);

        public PointD CursorPosition => Cursor;
        public WindowUnderCursor? WindowAt(PointD screenPoint) => Window;
        public RectD? ResolveRepeatRegion(RegionHistoryEntry entry) =>
            ResolveMissing ? null : ResolvedRegion ?? entry.ScreenRegion;

        public bool OpenEditor(AdvancedSelection selection)
        {
            LastSelection = selection;
            if (RejectEditor) return false;
            OpenCount++;
            return true;
        }
    }

    private sealed class FakeScrollSink : IScrollInputSink
    {
        public int Calls { get; private set; }
        public bool Accept { get; set; } = true;
        public Action<int>? OnScroll { get; set; }

        public bool ScrollDown(IntPtr targetWindow, PointD screenPoint, int notches)
        {
            Calls++;
            OnScroll?.Invoke(notches);
            return Accept;
        }
    }

    [Fact]
    public void FullScreen_OpensWholeMonitorWithoutChangingHistory() => RunSta(() =>
    {
        var env = new FakeEnvironment();
        var store = new LastRegionStore(() => 10);
        CaptureOutcome result = NewService(env, store).CaptureFullScreen();
        Assert.Equal(CaptureOutcomeKind.Completed, result.Kind);
        Assert.Equal(new RectD(0, 0, 300, 200), env.LastSelection!.Region);
        Assert.False(env.LastSelection.RecordForRepeat);
        Assert.Equal(0, store.Count);
    });

    [Fact]
    public void Window_FreezesCompleteCrossMonitorFrameAndCarriesTitle() => RunSta(() =>
    {
        var bounds = new RectD(250, 40, 240, 120); // deliberately beyond a 300px cursor monitor
        var env = new FakeEnvironment
        {
            Window = new WindowUnderCursor(new IntPtr(7), bounds, "메모장", new RectD(258, 70, 224, 82)),
        };
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10)).CaptureWindow();
        Assert.Equal(CaptureOutcomeKind.Completed, result.Kind);
        Assert.Equal(bounds, env.LastScreenCaptureBounds);
        Assert.Equal(new RectD(0, 0, 240, 120), env.LastSelection!.Region);
        Assert.Equal("메모장", env.LastSelection.SourceTitle);
        Assert.False(env.LastSelection.RecordForRepeat);
    });

    [Fact]
    public void Window_WithNoCandidate_IsExplicit() => RunSta(() =>
    {
        var env = new FakeEnvironment();
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10)).CaptureWindow();
        Assert.Equal(CaptureOutcomeKind.NothingToCapture, result.Kind);
        Assert.Equal(0, env.OpenCount);
    });

    [Fact]
    public void Repeat_UsesTopologyResolvedRegionWithoutReRecording() => RunSta(() =>
    {
        var store = new LastRegionStore(() => 10);
        store.Record(new RegionHistoryEntry(
            new RectD(10, 20, 100, 80), "DISPLAY-A", new RectD(0, 0, 300, 200), 96));
        var env = new FakeEnvironment { ResolvedRegion = new RectD(520, 40, 200, 160) };
        CaptureOutcome result = NewService(env, store).RepeatLastRegion();
        Assert.Equal(CaptureOutcomeKind.Completed, result.Kind);
        Assert.Equal(new RectD(520, 40, 200, 160), env.LastScreenCaptureBounds);
        Assert.False(env.LastSelection!.RecordForRepeat);
        Assert.Single(store.SnapshotEntries());
    });

    [Fact]
    public void Repeat_WhenSourceMonitorDisappeared_DoesNotCapture() => RunSta(() =>
    {
        var store = new LastRegionStore(() => 10);
        store.Record(new RegionHistoryEntry(
            new RectD(10, 20, 100, 80), "DISPLAY-GONE", new RectD(0, 0, 300, 200), 96));
        var env = new FakeEnvironment { ResolveMissing = true };
        CaptureOutcome result = NewService(env, store).RepeatLastRegion();
        Assert.Equal(CaptureOutcomeKind.NothingToCapture, result.Kind);
        Assert.Equal(0, env.ScreenCaptureCount);
        Assert.Equal(0, env.OpenCount);
    });

    [Fact]
    public void Repeat_WithNoHistory_IsExplicit() => RunSta(() =>
    {
        var env = new FakeEnvironment();
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10)).RepeatLastRegion();
        Assert.Equal(CaptureOutcomeKind.NothingToCapture, result.Kind);
    });

    [Fact]
    public void EditorRejection_DoesNotMutateHistory() => RunSta(() =>
    {
        var env = new FakeEnvironment { RejectEditor = true };
        var store = new LastRegionStore(() => 10);
        CaptureOutcome result = NewService(env, store).CaptureFullScreen();
        Assert.Equal(CaptureOutcomeKind.Cancelled, result.Kind);
        Assert.Equal(0, store.Count);
    });

    [Fact]
    public void FixedSize_CentresAndClamps() => RunSta(() =>
    {
        var env = new FakeEnvironment { Cursor = new PointD(290, 190) };
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10)).CaptureFixedSize(100, 80);
        Assert.True(result.IsCompleted);
        Assert.Equal(new RectD(200, 120, 100, 80), env.LastSelection!.Region);
        Assert.False(env.LastSelection.RecordForRepeat);
    });

    [Theory]
    [InlineData(0, 80)]
    [InlineData(100, 0)]
    [InlineData(-1, 80)]
    public void FixedSize_NonPositive_IsRejected(int width, int height) => RunSta(() =>
    {
        var env = new FakeEnvironment();
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10)).CaptureFixedSize(width, height);
        Assert.Equal(CaptureOutcomeKind.NothingToCapture, result.Kind);
        Assert.Equal(0, env.OpenCount);
    });

    [Fact]
    public void Scrolling_EmptyRegion_IsRejected() => RunSta(() =>
    {
        var env = new FakeEnvironment();
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10))
            .CaptureScrollingAsync(new IntPtr(1), RectD.Empty, ScrollStitchOptions.Default)
            .GetAwaiter().GetResult();
        Assert.Equal(CaptureOutcomeKind.NothingToCapture, result.Kind);
    });

    [Fact]
    public void Scrolling_StitchesVerifiedTallImageAndNeverRecordsRepeat() => RunSta(() =>
    {
        var env = new ScrollingEnvironment(totalHeight: 80, regionHeight: 20, width: 8);
        var sink = new FakeScrollSink { OnScroll = n => env.Advance(n * 4) };
        var store = new LastRegionStore(() => 10);
        CaptureOutcome result = NewService(env, store, sink)
            .CaptureScrollingAsync(new IntPtr(1), new RectD(0, 0, 8, 20), ScrollStitchOptions.Default, 20)
            .GetAwaiter().GetResult();
        Assert.True(result.IsCompleted);
        Assert.True(env.LastOpenedHeight > 20);
        Assert.False(env.LastSelection!.RecordForRepeat);
        Assert.Equal(0, store.Count);
    });

    [Fact]
    public void Scrolling_RejectedInput_FailsWithoutEditor() => RunSta(() =>
    {
        var env = new SequenceEnvironment([RowCodedBitmap(8, 20, 0)]);
        var sink = new FakeScrollSink { Accept = false };
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10), sink)
            .CaptureScrollingAsync(new IntPtr(1), new RectD(0, 0, 8, 20), ScrollStitchOptions.Default)
            .GetAwaiter().GetResult();
        Assert.Equal(CaptureOutcomeKind.Failed, result.Kind);
        Assert.Equal(0, env.OpenCount);
    });

    [Fact]
    public void Scrolling_NonScrollableDuplicate_IsNothingWithoutEditor() => RunSta(() =>
    {
        BitmapSource same = RowCodedBitmap(8, 20, 0);
        var env = new SequenceEnvironment([same, same, same]);
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10), new FakeScrollSink())
            .CaptureScrollingAsync(new IntPtr(1), new RectD(0, 0, 8, 20), ScrollStitchOptions.Default)
            .GetAwaiter().GetResult();
        Assert.Equal(CaptureOutcomeKind.NothingToCapture, result.Kind);
        Assert.Equal(0, env.OpenCount);
    });

    [Fact]
    public void Scrolling_FirstNoOverlap_FailsWithoutEditor() => RunSta(() =>
    {
        var env = new SequenceEnvironment([
            RowCodedBitmap(8, 20, 0),
            RowCodedBitmap(8, 20, 100),
        ]);
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10), new FakeScrollSink())
            .CaptureScrollingAsync(new IntPtr(1), new RectD(0, 0, 8, 20), ScrollStitchOptions.Default)
            .GetAwaiter().GetResult();
        Assert.Equal(CaptureOutcomeKind.Failed, result.Kind);
        Assert.Equal(0, env.OpenCount);
    });

    [Fact]
    public void Scrolling_LaterNoOverlap_OpensOnlyVerifiedPartialWithMessage() => RunSta(() =>
    {
        var env = new SequenceEnvironment([
            RowCodedBitmap(8, 20, 0),
            RowCodedBitmap(8, 20, 5),
            RowCodedBitmap(8, 20, 100),
        ]);
        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10), new FakeScrollSink())
            .CaptureScrollingAsync(new IntPtr(1), new RectD(0, 0, 8, 20), ScrollStitchOptions.Default)
            .GetAwaiter().GetResult();
        Assert.Equal(CaptureOutcomeKind.Completed, result.Kind);
        Assert.NotEmpty(result.Message);
        Assert.Equal(1, env.OpenCount);
        Assert.Equal(25, env.LastSelection!.Region.Height);
    });

    [Fact]
    public void Scrolling_CancellationAfterInput_PreventsRecaptureAndEditor() => RunSta(() =>
    {
        using var cts = new CancellationTokenSource();
        var env = new SequenceEnvironment([RowCodedBitmap(8, 20, 0)]);
        int delayCalls = 0;
        Task Delay(TimeSpan _, CancellationToken token)
        {
            delayCalls++;
            cts.Cancel();
            return Task.FromCanceled(token);
        }

        CaptureOutcome result = NewService(env, new LastRegionStore(() => 10), new FakeScrollSink(), Delay)
            .CaptureScrollingAsync(
                new IntPtr(1),
                new RectD(0, 0, 8, 20),
                ScrollStitchOptions.Default,
                cancellation: cts.Token)
            .GetAwaiter().GetResult();
        Assert.Equal(CaptureOutcomeKind.Cancelled, result.Kind);
        Assert.Equal(1, delayCalls);
        Assert.Equal(1, env.CaptureCalls);
        Assert.Equal(0, env.OpenCount);
    });

    [Fact]
    public void Scrolling_SlowDuplicate_RetriesSettleBeforeEnding() => RunSta(() =>
    {
        BitmapSource first = RowCodedBitmap(8, 20, 0);
        var env = new SequenceEnvironment([first, first, RowCodedBitmap(8, 20, 5), first, first]);
        int delayCalls = 0;
        CaptureOutcome result = NewService(
                env,
                new LastRegionStore(() => 10),
                new FakeScrollSink(),
                (_, _) => { delayCalls++; return Task.CompletedTask; })
            .CaptureScrollingAsync(new IntPtr(1), new RectD(0, 0, 8, 20), ScrollStitchOptions.Default, 3)
            .GetAwaiter().GetResult();
        Assert.Equal(CaptureOutcomeKind.Completed, result.Kind);
        Assert.True(delayCalls >= 3); // normal settle + slow retry + next settle/retry
        Assert.Equal(1, env.OpenCount);
    });

    private sealed class SequenceEnvironment : IAdvancedCaptureEnvironment
    {
        private readonly IReadOnlyList<BitmapSource> _frames;
        public SequenceEnvironment(IReadOnlyList<BitmapSource> frames) => _frames = frames;
        public int CaptureCalls { get; private set; }
        public int OpenCount { get; private set; }
        public AdvancedSelection? LastSelection { get; private set; }
        public PointD CursorPosition => new(4, 10);
        public FrozenFrame CaptureMonitorUnderCursor() =>
            new(_frames[0], new RectD(0, 0, _frames[0].PixelWidth, _frames[0].PixelHeight), null, 0);
        public FrozenFrame CaptureScreenRegion(RectD bounds) => new(CaptureRegion(bounds), bounds, null, 0);
        public BitmapSource CaptureRegion(RectD bounds)
        {
            int index = Math.Min(CaptureCalls, _frames.Count - 1);
            CaptureCalls++;
            return _frames[index];
        }
        public WindowUnderCursor? WindowAt(PointD point) => null;
        public RectD? ResolveRepeatRegion(RegionHistoryEntry entry) => entry.ScreenRegion;
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
        private int _offset;
        public ScrollingEnvironment(int totalHeight, int regionHeight, int width)
        {
            _total = totalHeight; _regionHeight = regionHeight; _width = width;
        }
        public int LastOpenedHeight { get; private set; }
        public AdvancedSelection? LastSelection { get; private set; }
        public PointD CursorPosition => new(_width / 2d, _regionHeight / 2d);
        public FrozenFrame CaptureMonitorUnderCursor() =>
            new(CaptureRegion(new RectD(0, 0, _width, _regionHeight)), new RectD(0, 0, _width, _regionHeight), null, 0);
        public FrozenFrame CaptureScreenRegion(RectD bounds) => new(CaptureRegion(bounds), bounds, null, 0);
        public BitmapSource CaptureRegion(RectD bounds)
        {
            var bitmap = new WriteableBitmap(_width, _regionHeight, 96, 96, PixelFormats.Bgra32, null);
            byte[] pixels = new byte[_width * _regionHeight * 4];
            for (int row = 0; row < _regionHeight; row++)
            {
                byte value = (byte)(Math.Min(_total - 1, _offset + row) & 0xFF);
                Array.Fill(pixels, value, row * _width * 4, _width * 4);
            }
            bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _width, _regionHeight), pixels, _width * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }
        public WindowUnderCursor? WindowAt(PointD point) =>
            new(new IntPtr(1), new RectD(0, 0, _width, _regionHeight), "scroll");
        public RectD? ResolveRepeatRegion(RegionHistoryEntry entry) => entry.ScreenRegion;
        public bool OpenEditor(AdvancedSelection selection)
        {
            LastSelection = selection;
            LastOpenedHeight = (int)selection.Region.Height;
            return true;
        }
        public void Advance(int rows) => _offset = Math.Min(_total - 1, _offset + rows);
    }
}
