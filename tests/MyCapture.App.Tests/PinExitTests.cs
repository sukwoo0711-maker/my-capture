using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A user close of a pin happens at once (history, session, lists) and only its pixels leave: a
/// 120 ms shrink and fade without input, kept out of captures, which bulk closes end at once.
/// </summary>
public sealed class PinExitTests
{
    [Fact]
    public void UserCloseRecordsAtOnceAndLeavesWithoutInput() => Run(motion: true, exclude: true, (manager, excluded) =>
    {
        int changes = 0;
        PinWindow pin = manager.PinImage(Image());
        manager.OpenPinsChanged += () => changes++;
        pin.ShowHoverBar();
        PinHoverBar bar = Assert.IsType<PinHoverBar>(pin.HoverBarForTest);
        Assert.True(bar.IsVisible);
        Assert.True(pin.LandingStarted);
        PinLandingDecoration? decoration = pin.LandingDecoration;
        int closed = 0;
        pin.Closed += (_, _) => closed++;

        Press(pin, Key.Escape);

        // The close itself is done: every list and the restore history already know.
        Assert.True(pin.IsClosed);
        Assert.Equal(0, manager.Count);
        Assert.Empty(manager.SnapshotOpenPinWindows());
        Assert.Empty(manager.SnapshotOpenPins());
        Assert.Equal(1, manager.ClosedCount);
        Assert.NotNull(pin.ClosedPlacement);
        Assert.Equal(1, changes);
        Assert.Equal(0, closed);

        // Only the pixels linger, out of captures and out of reach.
        Assert.True(pin.IsLeaving);
        Assert.True(pin.IsVisible);
        Assert.False(pin.IsHitTestVisible);
        Assert.Equal([pin.Handle], excluded);
        Assert.True(Field<ScaleTransform>(pin, "_pinScale").HasAnimatedProperties);
        Assert.True(((UIElement)pin.Content).HasAnimatedProperties);
        Assert.Null(pin.LandingDecoration);
        if (decoration is not null) Assert.True(decoration.IsClosed);
        Assert.False(bar.IsHitTestVisible);

        // Keys reach nothing while it leaves.
        double zoom = pin.State.Zoom;
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin)!, 0, Key.Add)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        pin.RaiseEvent(key);
        Assert.True(key.Handled);
        Assert.Equal(zoom, pin.State.Zoom);
        Press(pin, Key.Escape);
        Assert.Equal(1, manager.ClosedCount);

        // Close all ends the exit at once.
        manager.CloseAll();
        Assert.False(pin.IsLeaving);
        Assert.Equal(1, closed);
        Assert.Null(PresentationSource.FromVisual(pin));
        Assert.Null(pin.HoverBarForTest);
        Assert.False(bar.IsVisible);

        PinWindow restored = Assert.IsType<PinWindow>(PinHistoryTestSupport.Restore(manager));
        Assert.False(restored.IsClosed);
        Assert.Equal(1, manager.Count);
    });

    [Fact]
    public void ExitEndsOnItsOwnAndClosesTheWindowOnce() => Run(motion: true, exclude: true, (manager, _) =>
    {
        PinWindow pin = manager.PinImage(Image());
        int closed = 0;
        pin.Closed += (_, _) => closed++;

        Press(pin, Key.Delete);
        Assert.True(pin.IsLeaving);

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (closed == 0 && DateTime.UtcNow < deadline) Pump(pin.Dispatcher, 20);

        Assert.Equal(1, closed);
        Assert.False(pin.IsLeaving);
        Assert.Null(PresentationSource.FromVisual(pin));
        Assert.Equal(1, manager.ClosedCount);
        // A direct close after the exit ended changes nothing.
        pin.Close();
        Assert.Equal(1, closed);
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ClosesAtOnceWithoutMotionOrWhenCaptureCannotSkipIt(bool motion, bool exclude) =>
        Run(motion, exclude, (manager, excluded) =>
        {
            PinWindow pin = manager.PinImage(Image());
            int closed = 0;
            pin.Closed += (_, _) => closed++;

            Press(pin, Key.Escape);

            Assert.True(pin.IsClosed);
            Assert.False(pin.IsLeaving);
            Assert.Equal(1, closed);
            Assert.Null(PresentationSource.FromVisual(pin));
            Assert.Equal(1, manager.ClosedCount);
            // Without motion nothing is left to keep out of a capture.
            Assert.Equal(motion ? 1 : 0, excluded.Count);
        });

    [Theory]
    [InlineData("bar")]
    [InlineData("escape")]
    [InlineData("delete")]
    [InlineData("double-click")]
    [InlineData("menu")]
    public void EveryUserClosePathLeaves(string path) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, bool> exclusion = PinWindow.ExcludeLeavingPinFromCapture;
        Func<Window, bool> barExclusion = PinHoverBar.ApplyCaptureExclusion;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        PinWindow.ExcludeLeavingPinFromCapture = _ => true;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        var pin = new PinWindow(PinContent.FromImage(Image()), new PinViewState(160, 120, 1, 1, 0.1), -10000, -10000,
            () => new PinSettings { CloseOnDoubleClick = true }, new StillInput()) { ShowActivated = false };
        int started = 0;
        pin.CloseStarted += (_, _) => started++;
        try
        {
            pin.Show();
            switch (path)
            {
                case "bar":
                    pin.CreateHoverBarForTest().CloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    break;
                case "escape":
                    Press(pin, Key.Escape);
                    break;
                case "delete":
                    Press(pin, Key.Delete);
                    break;
                case "double-click":
                    var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
                    typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, 2);
                    typeof(PinWindow).GetMethod("OnMouseLeftButtonDown", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(pin, [args]);
                    break;
                default:
                    pin.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("Dialog.Close")))
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    break;
            }

            Assert.True(pin.IsClosed);
            Assert.True(pin.IsLeaving);
            Assert.Equal(1, started);
            pin.CompleteExit();
            Assert.False(pin.IsLeaving);
            Assert.Null(PresentationSource.FromVisual(pin));
            Assert.Equal(1, started);
        }
        finally
        {
            pin.Close();
            PinWindow.ExcludeLeavingPinFromCapture = exclusion;
            PinHoverBar.ApplyCaptureExclusion = barExclusion;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public void ADirectCloseDuringTheExitFinishesItOnce() => Run(motion: true, exclude: true, (manager, _) =>
    {
        PinWindow pin = manager.PinImage(Image());
        int started = 0, closed = 0;
        pin.CloseStarted += (_, _) => started++;
        pin.Closed += (_, _) => closed++;

        Press(pin, Key.Escape);
        pin.Close();

        Assert.Equal(1, started);
        Assert.Equal(1, closed);
        Assert.False(pin.IsLeaving);
        Assert.Equal(1, manager.ClosedCount);
        // The exit's own deferred close later finds the window gone.
        Pump(pin.Dispatcher, PinWindow.ExitDuration.Milliseconds + 400);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void ExitForAppShutdownRecordsNothing() => Run(motion: true, exclude: true, (manager, _) =>
    {
        PinWindow first = manager.PinImage(Image());
        PinWindow second = manager.PinImage(Image());
        Press(first, Key.Escape);
        Assert.True(first.IsLeaving);
        Assert.Equal(1, manager.ClosedCount);

        manager.CloseAllForExit();

        Assert.False(first.IsLeaving);
        Assert.True(second.IsClosed);
        Assert.Null(PresentationSource.FromVisual(first));
        Assert.Null(PresentationSource.FromVisual(second));
        Assert.Equal(0, manager.ClosedCount);
    });

    private static void Run(bool motion, bool exclude, Action<PinManager, List<IntPtr>> body) => StaTestHost.Run(() =>
    {
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, bool> previousExclusion = PinWindow.ExcludeLeavingPinFromCapture;
        Func<Window, bool> barExclusion = PinHoverBar.ApplyCaptureExclusion;
        var excluded = new List<IntPtr>();
        FluidMotion.AnimationsEnabledOverrideForTest = motion;
        PinWindow.ExcludeLeavingPinFromCapture = handle =>
        {
            excluded.Add(handle);
            return exclude;
        };
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = () => OffscreenMonitor,
            ReadRestoreMonitorsForTest = () => [OffscreenMonitor],
            BeforeShowForTest = pin => pin.ShowActivated = false,
            ActivatePinForTest = _ => { },
        };
        try
        {
            body(manager, excluded);
        }
        finally
        {
            manager.CloseAllForExit();
            PinWindow.ExcludeLeavingPinFromCapture = previousExclusion;
            PinHoverBar.ApplyCaptureExclusion = barExclusion;
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
        }
    });

    private static MonitorInfo OffscreenMonitor => new("pin-exit-test", new RectD(-12000, -12000, 4000, 4000),
        new RectD(-12000, -12000, 4000, 4000), 96, false);

    private static void Press(PinWindow pin, Key key) =>
        pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin)!, 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent }, ModifierKeys.None);

    private static void Pump(Dispatcher dispatcher, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, dispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static BitmapSource Image()
    {
        var pixels = new byte[160 * 120 * 4];
        Array.Fill(pixels, (byte)0xC0);
        BitmapSource image = BitmapSource.Create(160, 120, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4);
        image.Freeze();
        return image;
    }

    private sealed class StillInput : IPinDragInput
    {
        public ModifierKeys Modifiers => ModifierKeys.None;
        public (int X, int Y) CursorPosition => (0, 0);
        public Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner) => new(0, 0);
        public bool LeftButtonPressed(MouseEventArgs args) => false;
        public bool TryCapture(PinWindow owner) => false;
        public bool HasCapture(PinWindow owner) => false;
        public void Release(PinWindow owner) { }
    }
}
