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

public sealed class AdvancedCaptureScrollHeaderDeferralTests
{
    private const int Width = 16;
    private const int FrameHeight = 400;
    private const int ScrollRows = 300;
    private const int Stride = Width * ScrollFrame.BytesPerPixel;

    [Fact]
    public void Scrolling_DuplicateBeforeRepaint_RetryKeepsAllSevenHundredRows() =>
        RunSta(() => AssertCapture(duplicateBeforeMove: true));

    [Fact]
    public void Scrolling_DirectMove_ControlKeepsAllSevenHundredRows() =>
        RunSta(() => AssertCapture(duplicateBeforeMove: false));

    private static void AssertCapture(bool duplicateBeforeMove)
    {
        byte[] document = DocumentPixels(FrameHeight + ScrollRows);
        BitmapSource seed = Frame(document, top: 0);
        BitmapSource shifted = Frame(document, top: ScrollRows);
        BitmapSource[] frames = duplicateBeforeMove
            ? [seed, seed, shifted, shifted, shifted]
            : [seed, shifted, shifted, shifted];
        var environment = new SequenceEnvironment(frames);
        var sink = new FakeScrollSink();
        int delayCalls = 0;
        Task Delay(TimeSpan _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            delayCalls++;
            return Task.CompletedTask;
        }

        CaptureOutcome outcome = NewService(environment, sink, Delay)
            .CaptureScrollingAsync(new IntPtr(1), new RectD(0, 0, Width, FrameHeight),
                ScrollStitchOptions.Default)
            .GetAwaiter().GetResult();

        Assert.Equal(CaptureOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(1, environment.OpenCount);
        AdvancedSelection selection = Assert.IsType<AdvancedSelection>(environment.LastSelection);
        Assert.Equal(FrameHeight + ScrollRows, selection.Region.Height);
        Assert.Equal(Width, selection.Frame.Bitmap.PixelWidth);
        Assert.Equal(FrameHeight + ScrollRows, selection.Frame.Bitmap.PixelHeight);
        Assert.Equal(PixelFormats.Bgra32, selection.Frame.Bitmap.Format);
        Assert.True(selection.Frame.Bitmap.IsFrozen);
        byte[] actual = new byte[document.Length];
        selection.Frame.Bitmap.CopyPixels(actual, Stride, 0);
        Assert.Equal(document, actual);

        // Both paths finish after two scroll inputs. Only the regression path needs
        // an additional capture and completed-task delay for the first slow repaint.
        Assert.Equal(2, sink.Calls);
        Assert.Equal(frames.Length, environment.CaptureCalls);
        Assert.Equal(duplicateBeforeMove ? 4 : 3, delayCalls);
    }

    private static byte[] DocumentPixels(int height)
    {
        byte[] pixels = new byte[Stride * height];
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < Width; column++)
            {
                int offset = row * Stride + column * ScrollFrame.BytesPerPixel;
                pixels[offset] = (byte)(row & 0xff);
                pixels[offset + 1] = (byte)(row >> 8);
                pixels[offset + 2] = (byte)((row * 37) & 0xff);
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static BitmapSource Frame(byte[] document, int top)
    {
        byte[] pixels = document.AsSpan(top * Stride, FrameHeight * Stride).ToArray();
        BitmapSource bitmap = BitmapSource.Create(Width, FrameHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static AdvancedCaptureService NewService(IAdvancedCaptureEnvironment environment,
        IScrollInputSink sink, Func<TimeSpan, CancellationToken, Task> delay) =>
        new(environment, new LastRegionStore(() => 10), sink, NullLogger.Instance, delay);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
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

    private sealed class SequenceEnvironment(IReadOnlyList<BitmapSource> frames) : IAdvancedCaptureEnvironment
    {
        public int CaptureCalls { get; private set; }
        public int OpenCount { get; private set; }
        public AdvancedSelection? LastSelection { get; private set; }
        public PointD CursorPosition => new(Width / 2d, FrameHeight / 2d);
        public bool CanOpenEditor => true;

        public FrozenFrame CaptureMonitorUnderCursor() =>
            new(frames[0], new RectD(0, 0, Width, FrameHeight), null, 0);

        public FrozenFrame CaptureScreenRegion(RectD bounds) =>
            new(CaptureRegion(bounds), bounds, null, 0);

        public BitmapSource CaptureRegion(RectD bounds)
        {
            int index = Math.Min(CaptureCalls, frames.Count - 1);
            CaptureCalls++;
            return frames[index];
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

    private sealed class FakeScrollSink : IScrollInputSink
    {
        public int Calls { get; private set; }

        public bool ScrollDown(IntPtr targetWindow, PointD screenPoint, int notches)
        {
            Calls++;
            return true;
        }
    }
}
