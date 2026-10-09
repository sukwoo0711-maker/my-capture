using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Window auto-detect inside the real region selector, driven through its input handlers.</summary>
public sealed class WindowDetectOverlayTests
{
    private static readonly WindowCandidate Editor = new(new IntPtr(11), new RectD(40, 30, 300, 200), "Editor");
    private static readonly WindowCandidate Desktop = new(new IntPtr(12), new RectD(0, 0, 640, 360), "Desktop");

    [Fact]
    public void HoverHighlightsTheFrontmostWindowAndClickSelectsIt() => StaTestHost.Run(() =>
    {
        PointD pointer = new(100, 100);
        var view = DetectingView(() => pointer);
        RegionSelectionEventArgs? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e;
        try
        {
            view.InitializePointer();
            Assert.Equal(new RectD(40, 30, 300, 200), view.HoverRegionForTest);
            Render(view);
            Assert.False(Visual(view, "_selectionVisual").ContentBounds.IsEmpty);

            pointer = new PointD(500, 300);
            view.UpdatePointer(false, false);
            Assert.Equal(new RectD(0, 0, 640, 360), view.HoverRegionForTest);

            pointer = new PointD(100, 100);
            view.UpdatePointer(false, false);
            Invoke(view, "OnMouseLeftButtonDown", Button(UIElement.MouseLeftButtonDownEvent));
            pointer = new PointD(102, 101); // Hand jitter inside the system drag threshold.
            Invoke(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            Assert.Equal(new RectD(40, 30, 300, 200), view.HoverRegionForTest);
            Assert.Null(view.Selection);
            Invoke(view, "OnMouseLeftButtonUp", Button(UIElement.MouseLeftButtonUpEvent));
            PumpUntil(() => confirmed is not null);

            Assert.Equal(new RectD(40, 30, 300, 200), confirmed!.BitmapRegion);
            Assert.Same(Editor, confirmed.DetectedWindow);
            Assert.False(confirmed.TextCapture);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void DragBeyondTheThresholdKeepsFreeRegionSelection() => StaTestHost.Run(() =>
    {
        PointD pointer = new(100, 100);
        var view = DetectingView(() => pointer);
        RegionSelectionEventArgs? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e;
        try
        {
            view.InitializePointer();
            Invoke(view, "OnMouseLeftButtonDown", Button(UIElement.MouseLeftButtonDownEvent));
            pointer = new PointD(180, 150);
            Invoke(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            Assert.Null(view.HoverRegionForTest);
            Assert.Equal(new RectD(100, 100, 80, 50), view.Selection);
            Invoke(view, "OnMouseLeftButtonUp", Button(UIElement.MouseLeftButtonUpEvent));
            PumpUntil(() => confirmed is not null);

            Assert.Equal(new RectD(100, 100, 80, 50), confirmed!.BitmapRegion);
            Assert.Null(confirmed.DetectedWindow);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void DisabledDetectionKeepsThePreviousClickAndRenderingBehaviour() => StaTestHost.Run(() =>
    {
        PointD pointer = new(100, 100);
        var view = new CaptureOverlayView(Frame(), showMagnifier: false)
        {
            ReadCursor = () => pointer,
            WindowCandidates = [Editor, Desktop],
        };
        bool confirmed = false;
        view.SelectionConfirmed += (_, _) => confirmed = true;
        try
        {
            view.InitializePointer();
            Assert.Null(view.HoverRegionForTest);
            Assert.Null(view.OverlayHintText);
            Render(view);
            Assert.True(Visual(view, "_selectionVisual").ContentBounds.IsEmpty);

            Invoke(view, "OnMouseLeftButtonDown", Button(UIElement.MouseLeftButtonDownEvent));
            Assert.Equal(new RectD(100, 100, 0, 0), view.Selection);
            Invoke(view, "OnMouseLeftButtonUp", Button(UIElement.MouseLeftButtonUpEvent));
            Pump();
            Assert.Null(view.Selection);
            Assert.False(confirmed);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ExistingSelectionSuppressesHoverAndEscapeStillCancels() => StaTestHost.Run(() =>
    {
        var view = DetectingView(() => new PointD(100, 100));
        int cancelled = 0;
        view.CancelRequested += (_, _) => cancelled++;
        try
        {
            view.InitializePointer();
            Assert.NotNull(view.HoverRegionForTest);
            // What Ctrl+A or a finished drag leaves behind: the region wins over the hover.
            typeof(CaptureOverlayView).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(view, (RectD?)new RectD(0, 0, 640, 360));
            view.UpdatePointer(false, false);
            Assert.Null(view.HoverRegionForTest);
            view.RaiseEvent(KeyArgs(System.Windows.Input.Key.Escape));
            Assert.Equal(1, cancelled);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ClickedWindowNamesTheSourceEvenUnderAnOverlappingWindow() => StaTestHost.Run(() =>
    {
        var overlapping = new WindowCandidate(new IntPtr(21), new RectD(150, 100, 100, 100), "Overlapping");
        var clicked = new WindowCandidate(new IntPtr(22), new RectD(100, 50, 200, 200), "Clicked", "https://example.com/doc");
        var window = new CaptureOverlayWindow(Frame(), abortOnFocusLoss: false)
        {
            SourceWindows = [overlapping, clicked],
        };
        var selections = new List<CaptureSelectionCompletedEventArgs>();
        window.SelectionCompleted += (_, e) => selections.Add(e);
        window.CompleteSelection(new RectD(100, 50, 200, 200), clicked, textCapture: false);
        var second = new CaptureOverlayWindow(Frame(), abortOnFocusLoss: false) { SourceWindows = [overlapping, clicked] };
        second.SelectionCompleted += (_, e) => selections.Add(e);
        second.CompleteSelection(new RectD(100, 50, 200, 200));

        Assert.Equal("Clicked", selections[0].SourceTitle);
        Assert.Equal("https://example.com/doc", selections[0].SourcePageUrl);
        Assert.Equal("Overlapping", selections[1].SourceTitle);
        Assert.False(selections[0].TextCapture);
    });

    internal static CaptureOverlayView DetectingView(Func<PointD> pointer) => new(Frame(), showMagnifier: false)
    {
        ReadCursor = pointer,
        DetectWindows = true,
        WindowCandidates = [Editor, Desktop],
    };

    internal static KeyEventArgs KeyArgs(Key key) =>
        new(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, key) { RoutedEvent = Keyboard.KeyDownEvent };

    internal static MouseButtonEventArgs Button(RoutedEvent routed) =>
        new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routed };

    internal static void Invoke(CaptureOverlayView view, string method, params object[] arguments) =>
        typeof(CaptureOverlayView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, arguments);

    internal static DrawingVisual Visual(CaptureOverlayView view, string field) =>
        (DrawingVisual)typeof(CaptureOverlayView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    internal static void Render(CaptureOverlayView view)
    {
        view.Measure(new Size(640, 360));
        view.Arrange(new Rect(0, 0, 640, 360));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
    }

    internal static void Pump() => PumpUntil(() => true, requireTrue: false);

    internal static void PumpUntil(Func<bool> ready, bool requireTrue = true)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        while (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(5));
        if (requireTrue) Assert.True(ready());
    }

    internal static FrozenFrame Frame()
    {
        byte[] pixels = new byte[640 * 360 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;
            pixels[i + 1] = 0x40;
            pixels[i + 2] = 0x60;
            pixels[i + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 640, 360), null, 0);
    }

    internal sealed class TestPresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
