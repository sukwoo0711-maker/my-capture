using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The window-detect highlight glides from window to window over the fast motion token while the
/// hover, what a click captures and the tab's words change at once.
/// </summary>
public sealed class WindowDetectGlideTests
{
    private static readonly RectD EditorRegion = new(40, 30, 300, 200);
    private static readonly RectD DesktopRegion = new(0, 0, 640, 360);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(83);

    [Fact]
    public void TheHighlightGlidesWhileAClickStillTakesTheWindowUnderThePointer() => Run(motion: true, () =>
    {
        PointD pointer = new(100, 100);
        TimeSpan now = TimeSpan.FromSeconds(10);
        CaptureOverlayView view = DetectingView(() => pointer);
        view.ReadClock = () => now;
        RegionSelectionEventArgs? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e;
        try
        {
            // The first highlight after opening snaps.
            view.InitializePointer();
            Draw(view);
            Assert.Equal(EditorRegion, view.ShownHoverForTest);
            Assert.False(view.HoverGlidingForTest);

            // Another window: the hover and the words move at once, the picture starts where it is.
            pointer = new PointD(500, 300);
            view.UpdatePointer(false, false);
            Assert.Equal(DesktopRegion, view.HoverRegionForTest);
            Draw(view);
            Assert.True(view.HoverGlidingForTest);
            Assert.Equal(EditorRegion, view.ShownHoverForTest);
            Assert.EndsWith("640 × 360", view.TargetLabelForTest, StringComparison.Ordinal);
            Assert.Equal(new Rect(40, 30, 300, 200), RevealClip(view));

            // Halfway through the time it is well past halfway (ease-out), on whole pixels, and the
            // reveal clip and the band are drawn there.
            now += Fast / 2;
            Draw(view);
            RectD shown = view.ShownHoverForTest!.Value;
            Assert.InRange(shown.Left, 0.5, 19.5);
            Assert.InRange(shown.Right, 491, 639);
            Assert.Equal(shown, shown.ToPixelBounds());
            Assert.Equal(new Rect(shown.Left, shown.Top, shown.Width, shown.Height), RevealClip(view));
            Assert.True(Visual(view, "_selectionVisual").ContentBounds.Left < 20);
            // The tab's words are laid out once per target, not on every glide frame.
            object label = Field(view, "_tabLabelText");
            now += TimeSpan.FromMilliseconds(8);
            Draw(view);
            Assert.Same(label, Field(view, "_tabLabelText"));

            // A click during the glide captures the window under the pointer, exactly as before.
            view.SimulatePointerDown();
            view.SimulatePointerUp();
            PumpUntil(() => confirmed is not null);
            Assert.Equal(DesktopRegion, confirmed!.BitmapRegion);
            Assert.Equal("Desktop", confirmed.DetectedWindow!.Title);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void AGlideLandsOnTheTargetAfterTheFastTokenAndARetargetStartsFromTheScreen() => Run(motion: true, () =>
    {
        PointD pointer = new(100, 100);
        TimeSpan now = TimeSpan.FromSeconds(10);
        CaptureOverlayView view = DetectingView(() => pointer);
        view.ReadClock = () => now;
        CaptureOverlayView snapped = DetectingView(() => new PointD(500, 300));
        try
        {
            view.InitializePointer();
            Draw(view);
            pointer = new PointD(500, 300);
            view.UpdatePointer(false, false);
            Draw(view);

            // Back to the editor a quarter of the way through: the new glide starts where the
            // highlight is on screen, not from either window.
            now += Fast / 4;
            Draw(view);
            RectD onScreen = view.ShownHoverForTest!.Value;
            Assert.NotEqual(EditorRegion, onScreen);
            Assert.NotEqual(DesktopRegion, onScreen);
            pointer = new PointD(100, 100);
            view.UpdatePointer(false, false);
            Draw(view);
            Assert.Equal(onScreen, view.ShownHoverForTest);
            Assert.EndsWith("300 × 200", view.TargetLabelForTest, StringComparison.Ordinal);

            now += Fast;
            Draw(view);
            Assert.False(view.HoverGlidingForTest);
            Assert.Equal(EditorRegion, view.ShownHoverForTest);

            // Landed, the highlight is exactly what a snap draws.
            pointer = new PointD(500, 300);
            view.UpdatePointer(false, false);
            Draw(view);
            now += Fast;
            Draw(view);
            snapped.InitializePointer();
            Draw(snapped);
            Assert.Equal(DesktopRegion, view.ShownHoverForTest);
            Assert.Equal(snapped.TargetTabForTest, view.TargetTabForTest);
            Assert.Equal(snapped.TargetLabelForTest, view.TargetLabelForTest);
            Assert.Equal(RevealClip(snapped), RevealClip(view));
        }
        finally
        {
            view.ReleaseResources();
            snapped.ReleaseResources();
        }
    });

    [Fact]
    public void WithoutMotionEveryHighlightSnaps() => Run(motion: false, () =>
    {
        PointD pointer = new(100, 100);
        CaptureOverlayView view = DetectingView(() => pointer);
        try
        {
            view.InitializePointer();
            Draw(view);
            pointer = new PointD(500, 300);
            view.UpdatePointer(false, false);
            Draw(view);
            Assert.False(view.HoverGlidingForTest);
            Assert.Equal(DesktopRegion, view.ShownHoverForTest);
            Assert.Equal(new Rect(0, 0, 640, 360), RevealClip(view));
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void AGlideDrivesFramesUntilItLandsWithoutRedrawingTheDesktop() => Run(motion: true, () =>
    {
        PointD pointer = new(100, 100);
        TimeSpan now = TimeSpan.FromSeconds(10);
        CaptureOverlayView view = DetectingView(() => pointer);
        view.ReadClock = () => now;
        // Off screen and never activated: other sessions may be using this desktop.
        var host = new Window
        {
            Content = view, Width = 640, Height = 360, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
        };
        try
        {
            host.Show();
            WaitUntil(() => view.IsLoaded && view.StaticRenderCount > 0);
            view.UpdatePointer(false, false);
            view.FlushFeedbackForTest();
            Assert.Equal(EditorRegion, view.ShownHoverForTest);
            int desktop = view.StaticRenderCount;
            int dimmer = view.DimmerRenderCount;

            pointer = new PointD(500, 300);
            view.UpdatePointer(false, false);
            view.FlushFeedbackForTest();
            // While it glides the next composition frame is already asked for.
            Assert.True(view.HoverGlidingForTest);
            Assert.True(view.HasPendingFeedback);
            now += Fast / 2;
            view.FlushFeedbackForTest();
            Assert.True(view.HasPendingFeedback);

            now += Fast;
            view.FlushFeedbackForTest();
            Assert.False(view.HoverGlidingForTest);
            Assert.False(view.HasPendingFeedback);
            Assert.Equal(DesktopRegion, view.ShownHoverForTest);
            Assert.Equal(desktop, view.StaticRenderCount);
            Assert.Equal(dimmer, view.DimmerRenderCount);
        }
        finally
        {
            view.ReleaseResources();
            host.Close();
        }
    });

    private static void Run(bool motion, Action body) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = motion;
        try { body(); }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    private static void Draw(CaptureOverlayView view)
    {
        view.InvalidateVisual();
        view.Measure(new Size(640, 360));
        view.Arrange(new Rect(0, 0, 640, 360));
        view.UpdateLayout();
    }

    private static Rect RevealClip(CaptureOverlayView view) =>
        ((RectangleGeometry)Visual(view, "_revealVisual").Clip).Rect;

    private static object Field(CaptureOverlayView view, string name) =>
        typeof(CaptureOverlayView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static void WaitUntil(Func<bool> ready)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(ready());
    }
}
