using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The window-detect highlight glides from window to window over
/// <see cref="CaptureOverlayView.HoverGlideLength"/> while the hover, what a click captures and the
/// tab's words change at once. A new window during a glide, a move of a few pixels and a window
/// larger than a quarter of the screen snap.
/// </summary>
public sealed class WindowDetectGlideTests
{
    // Notes and Chat are each under a quarter of the 640 × 360 frame, Panel sits 4 pixels inside
    // Notes and the desktop is the whole frame.
    private static readonly WindowCandidate Panel = new(new IntPtr(31), new RectD(44, 34, 192, 142), "Panel");
    private static readonly WindowCandidate Notes = new(new IntPtr(32), new RectD(40, 30, 200, 150), "Notes");
    private static readonly WindowCandidate Chat = new(new IntPtr(33), new RectD(400, 40, 180, 140), "Chat");
    private static readonly WindowCandidate Desktop = new(new IntPtr(34), new RectD(0, 0, 640, 360), "Desktop");
    private static readonly PointD OnPanel = new(100, 100);
    private static readonly PointD OnNotes = new(42, 100);
    private static readonly PointD OnChat = new(500, 100);
    private static readonly PointD OnDesktop = new(300, 300);
    private static readonly TimeSpan Glide = CaptureOverlayView.HoverGlideLength;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheHighlightGlidesWhileAClickStillTakesTheWindowUnderThePointer(bool native) => Run(motion: true, () =>
    {
        PointD pointer = OnNotes;
        TimeSpan now = TimeSpan.FromSeconds(10);
        GlideSelector view = GlidingView(native, () => pointer);
        view.ReadClock = () => now;
        RegionSelectionEventArgs? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e;
        try
        {
            // The first highlight after opening snaps.
            view.InitializePointer();
            Draw(view);
            Assert.Equal(Notes.ScreenBounds, view.ShownHoverForTest);
            Assert.False(view.HoverGlidingForTest);

            // Another window: the hover and the words move at once, the picture starts where it is.
            pointer = OnChat;
            view.UpdatePointer(false, false);
            Assert.Equal(Chat.ScreenBounds, view.HoverRegionForTest);
            Draw(view);
            Assert.True(view.HoverGlidingForTest);
            Assert.Equal(Notes.ScreenBounds, view.ShownHoverForTest);
            Assert.EndsWith("180 × 140", view.TargetLabelForTest, StringComparison.Ordinal);
            Assert.Equal(new Rect(40, 30, 200, 150), RevealClip(view));

            // Halfway through the time it is well past halfway (ease-out), on whole pixels, and the
            // reveal clip and the band are drawn there.
            now += Glide / 2;
            Draw(view);
            RectD shown = view.ShownHoverForTest!.Value;
            Assert.InRange(shown.Left, 221, 399);
            Assert.InRange(shown.Right, 411, 579);
            Assert.Equal(shown, shown.ToPixelBounds());
            Assert.Equal(new Rect(shown.Left, shown.Top, shown.Width, shown.Height), RevealClip(view));
            Assert.True(Visual(view, "_selectionVisual").ContentBounds.Left < 390);
            // The tab's words are laid out once per target, not on every glide frame.
            object label = Field(view, "_tabLabelText");
            now += TimeSpan.FromMilliseconds(8);
            Draw(view);
            Assert.Same(label, Field(view, "_tabLabelText"));

            // A click during the glide captures the window under the pointer, exactly as before.
            Assert.True(view.HoverGlidingForTest);
            view.SimulatePointerDown();
            view.SimulatePointerUp();
            PumpUntil(() => confirmed is not null);
            Assert.Equal(Chat.ScreenBounds, confirmed!.BitmapRegion);
            Assert.Equal("Chat", confirmed.DetectedWindow!.Title);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewWindowDuringAGlideSnapsAndALandedGlideMatchesASnap(bool native) => Run(motion: true, () =>
    {
        PointD pointer = OnNotes;
        TimeSpan now = TimeSpan.FromSeconds(10);
        GlideSelector view = GlidingView(native, () => pointer);
        view.ReadClock = () => now;
        GlideSelector snapped = GlidingView(native, () => OnChat);
        try
        {
            view.InitializePointer();
            Draw(view);
            pointer = OnChat;
            view.UpdatePointer(false, false);
            Draw(view);

            // Back to Notes a quarter of the way through: the highlight jumps there instead of
            // starting another glide, so a pointer sweeping across windows never trails it.
            now += Glide / 4;
            Draw(view);
            RectD onScreen = view.ShownHoverForTest!.Value;
            Assert.NotEqual(Notes.ScreenBounds, onScreen);
            Assert.NotEqual(Chat.ScreenBounds, onScreen);
            pointer = OnNotes;
            view.UpdatePointer(false, false);
            Draw(view);
            Assert.False(view.HoverGlidingForTest);
            Assert.Equal(Notes.ScreenBounds, view.ShownHoverForTest);
            Assert.Equal(new Rect(40, 30, 200, 150), RevealClip(view));
            Assert.EndsWith("200 × 150", view.TargetLabelForTest, StringComparison.Ordinal);

            // From rest the next window glides again and lands after the glide length, exactly
            // where a snap draws it.
            pointer = OnChat;
            view.UpdatePointer(false, false);
            Draw(view);
            Assert.True(view.HoverGlidingForTest);
            now += Glide;
            Draw(view);
            Assert.False(view.HoverGlidingForTest);
            snapped.InitializePointer();
            Draw(snapped);
            Assert.Equal(Chat.ScreenBounds, view.ShownHoverForTest);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMoveOfAFewPixelsAndAWindowLargerThanAQuarterOfTheScreenSnap(bool native) => Run(motion: true, () =>
    {
        PointD pointer = OnPanel;
        TimeSpan now = TimeSpan.FromSeconds(10);
        GlideSelector view = GlidingView(native, () => pointer, Panel);
        view.ReadClock = () => now;
        try
        {
            view.InitializePointer();
            Draw(view);
            Assert.Equal(Panel.ScreenBounds, view.ShownHoverForTest);

            // From Panel to the window around it every edge moves 4 pixels: no glide.
            pointer = OnNotes;
            view.UpdatePointer(false, false);
            Draw(view);
            Assert.False(view.HoverGlidingForTest);
            Assert.Equal(Notes.ScreenBounds, view.ShownHoverForTest);

            // The desktop fills the screen: to it and back from it the highlight snaps.
            pointer = OnDesktop;
            view.UpdatePointer(false, false);
            Draw(view);
            Assert.False(view.HoverGlidingForTest);
            Assert.Equal(Desktop.ScreenBounds, view.ShownHoverForTest);
            Assert.Equal(new Rect(0, 0, 640, 360), RevealClip(view));
            pointer = OnChat;
            view.UpdatePointer(false, false);
            Draw(view);
            Assert.False(view.HoverGlidingForTest);
            Assert.Equal(Chat.ScreenBounds, view.ShownHoverForTest);
            Assert.Equal(new Rect(400, 40, 180, 140), RevealClip(view));
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutMotionEveryHighlightSnaps(bool native) => Run(motion: false, () =>
    {
        PointD pointer = OnNotes;
        GlideSelector view = GlidingView(native, () => pointer);
        try
        {
            view.InitializePointer();
            Draw(view);
            pointer = OnChat;
            view.UpdatePointer(false, false);
            Draw(view);
            Assert.False(view.HoverGlidingForTest);
            Assert.Equal(Chat.ScreenBounds, view.ShownHoverForTest);
            Assert.Equal(new Rect(400, 40, 180, 140), RevealClip(view));
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGlideDrivesFramesUntilItLandsWithoutRedrawingTheDesktop(bool native) => Run(motion: true, () =>
    {
        PointD pointer = OnNotes;
        TimeSpan now = TimeSpan.FromSeconds(10);
        GlideSelector view = GlidingView(native, () => pointer);
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
            Assert.Equal(Notes.ScreenBounds, view.ShownHoverForTest);
            int desktop = view.StaticRenderCount;
            int dimmer = view.DimmerRenderCount;

            pointer = OnChat;
            view.UpdatePointer(false, false);
            view.FlushFeedbackForTest();
            // While it glides the next composition frame is already asked for.
            Assert.True(view.HoverGlidingForTest);
            Assert.True(view.HasPendingFeedback);
            now += Glide / 2;
            view.FlushFeedbackForTest();
            Assert.True(view.HasPendingFeedback);

            now += Glide;
            view.FlushFeedbackForTest();
            Assert.False(view.HoverGlidingForTest);
            Assert.False(view.HasPendingFeedback);
            Assert.Equal(Chat.ScreenBounds, view.ShownHoverForTest);
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

    private static GlideSelector GlidingView(bool native, Func<PointD> pointer, params WindowCandidate[] above) =>
        new(native, pointer, [.. above, Notes, Chat, Desktop]);

    private static void Draw(GlideSelector view) => view.Draw();

    private static Rect RevealClip(GlideSelector view) => view.RevealClip;

    private static object Field(GlideSelector view, string name) => view.Field(name);

    private static SelectionVisualBounds Visual(GlideSelector view, string name) => name == "_selectionVisual"
        ? new(view.SelectionContentBounds) : throw new ArgumentOutOfRangeException(nameof(name));

    private readonly record struct SelectionVisualBounds(Rect ContentBounds);

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
