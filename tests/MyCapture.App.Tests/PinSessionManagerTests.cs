using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinSessionManagerTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void FromSession_PreservesEveryContentField(int kindValue, bool usesDip) => StaTestHost.Run(() =>
    {
        PinContentKind kind = (PinContentKind)kindValue;
        BitmapSource image = Image();
        string? text = kind == PinContentKind.Image ? null : "제품\t수량  \r\n연필\t3\r\n";
        const string url = "https://example.test/specification?q=%ED%95%9C%EA%B8%80";

        PinContent content = PinContent.FromSession(image, kind, text, url, usesDip);

        Assert.Same(image, content.Image);
        Assert.Equal(kind, content.Kind);
        Assert.Equal(text, content.OriginalText);
        Assert.Equal(url, content.SourcePageUrl);
        Assert.Equal(usesDip, content.UsesDeviceIndependentPixels);
    });

    [Fact]
    public void SnapshotOpenPins_PreservesOpeningOrderAndLivePresentation() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager();
        manager.ReadPinOrderForTest = handles => handles;
        try
        {
            PinWindow first = manager.PinImage(Image());
            PinWindow middle = manager.PinRenderedText(Image(), "text\t  \r\n한글", isTable: false);
            PinWindow last = manager.PinRenderedText(Image(), "셀\t2\r\n", isTable: true);
            middle.Close();
            first.State.SetZoom(1.7);
            first.State.SetOpacity(0.4);
            first.Left += 40;
            first.Top += 30;
            first.ApplyClickThrough(enabled: true);
            manager.HideOrShowAll();

            IReadOnlyList<PinManager.ClosedPin> snapshots = manager.SnapshotOpenPins();

            Assert.Equal(2, snapshots.Count);
            Assert.Same(first.PinnedContent, snapshots[0].Content);
            Assert.Same(last.PinnedContent, snapshots[1].Content);
            Assert.Equal(first.State.ImageWidthDip, snapshots[0].ImageWidthDip);
            Assert.Equal(first.State.ImageHeightDip, snapshots[0].ImageHeightDip);
            Assert.Equal(1.7, snapshots[0].Zoom);
            Assert.Equal(0.4, snapshots[0].Opacity);
            Assert.Equal(first.GetCurrentPlacement(), snapshots[0].Placement);
            Assert.NotNull(snapshots[0].Placement);
            Assert.Null(first.ClosedPlacement);
            Assert.Equal(1, manager.ClosedCount);
            Assert.Equal(2, manager.Count);
            Assert.True(manager.AreHidden);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void SnapshotOpenPins_KeepsStackingOrderIndependentOfOpeningAge() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager();
        try
        {
            PinWindow first = manager.PinImage(Image());
            PinWindow second = manager.PinRenderedText(Image(), "newer", isTable: false);
            manager.ReadPinOrderForTest = _ => [second.Handle, first.Handle];

            IReadOnlyList<PinManager.ClosedPin> snapshots = manager.SnapshotOpenPins();

            Assert.Same(second.PinnedContent, snapshots[0].Content);
            Assert.Same(first.PinnedContent, snapshots[1].Content);
            Assert.Equal(2, snapshots[0].OpenedOrder);
            Assert.Equal(1, snapshots[1].OpenedOrder);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void SnapshotOpenPins_RestoredOpeningAgePredatesPinsOpenedDuringDecode() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager();
        manager.ReadPinOrderForTest = handles => handles;
        try
        {
            PinWindow live = manager.PinRenderedText(Image(), "opened during decode", false);
            PinWindow newerSaved = manager.RestoreSessionPin(new PinManager.ClosedPin(
                PinContent.FromText(Image(), "newer saved", false), 80, 60, 1, 1, null, OpenedOrder: 12));
            PinWindow olderSaved = manager.RestoreSessionPin(new PinManager.ClosedPin(
                PinContent.FromText(Image(), "older saved", false), 80, 60, 1, 1, null, OpenedOrder: 4));

            IReadOnlyList<PinManager.ClosedPin> snapshots = manager.SnapshotOpenPins();

            Assert.Equal(3, snapshots.Single(pin => ReferenceEquals(pin.Content, live.PinnedContent)).OpenedOrder);
            Assert.Equal(2, snapshots.Single(pin => ReferenceEquals(pin.Content, newerSaved.PinnedContent)).OpenedOrder);
            Assert.Equal(1, snapshots.Single(pin => ReferenceEquals(pin.Content, olderSaved.PinnedContent)).OpenedOrder);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void WindowOrder_FiltersOwnedHandlesAndTerminatesOnChangingWindowCycles()
    {
        IntPtr[] owned = [new(1), new(2)];
        IReadOnlyList<IntPtr> ordered = PinWindowOrder.BottomToTop(owned, new IntPtr(100),
            handle => handle.ToInt64() switch
            {
                100 => new IntPtr(1),
                1 => new IntPtr(101),
                101 => new IntPtr(2),
                _ => IntPtr.Zero,
            });
        Assert.Equal(new[] { new IntPtr(2), new IntPtr(1) }, ordered);

        int calls = 0;
        IReadOnlyList<IntPtr> changed = PinWindowOrder.BottomToTop(owned, new IntPtr(100), handle =>
        {
            calls++;
            return handle == new IntPtr(100) ? new IntPtr(1) : new IntPtr(100);
        });
        Assert.Equal(2, calls);
        Assert.Equal(owned.OrderBy(handle => handle.ToInt64()), changed.OrderBy(handle => handle.ToInt64()));
    }

    [Fact]
    public void GetCurrentPlacement_ReadsWithoutClosingAndMatchesTheCloseSnapshot() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager();
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.Left += 25;
            PinClosedPlacement placement = Assert.IsType<PinClosedPlacement>(pin.GetCurrentPlacement());

            Assert.False(pin.IsClosed);
            Assert.Null(pin.ClosedPlacement);
            Assert.False(placement.Bounds.IsEmpty);
            Assert.NotEmpty(placement.DeviceName);

            pin.Close();

            Assert.Equal(placement, pin.ClosedPlacement);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void SessionRestore_KeepsContentAndStateWithoutActivationGuidanceOrHistory() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager();
        manager.ReadPinOrderForTest = handles => handles;
        int created = 0;
        int activationCalls = 0;
        manager.ActivatePinForTest = _ => activationCalls++;
        manager.PinCreated += () => created++;
        try
        {
            foreach (PinContentKind kind in Enum.GetValues<PinContentKind>())
            {
                string? text = kind == PinContentKind.Image ? null : "제품\t수량  \r\n연필\t3\r\n";
                PinContent content = PinContent.FromSession(Image(), kind, text, "https://example.test/source", usesDeviceIndependentPixels: true);
                var snapshot = new PinManager.ClosedPin(content, 84, 56, 1.5, 0.6, PlacementOutsideDesktop());

                PinWindow restored = manager.RestoreSessionPin(snapshot);

                Assert.Same(content, restored.PinnedContent);
                Assert.Equal(kind, restored.ContentKind);
                Assert.Equal(text, restored.OriginalText);
                Assert.Equal(content.SourcePageUrl, restored.SourcePageUrl);
                Assert.Equal(84, restored.State.ImageWidthDip);
                Assert.Equal(56, restored.State.ImageHeightDip);
                Assert.Equal(1.5, restored.State.Zoom);
                Assert.Equal(0.6, restored.State.Opacity);
                Assert.Equal(0.6, restored.Opacity);
                Assert.False(restored.ShowActivated);
                Assert.True(restored.IsVisible);
                Assert.False(restored.State.IsHidden);
                Assert.False(restored.State.IsClickThrough);
                Assert.False(WindowStyleFacade.IsClickThrough(restored.Handle));
            }

            Assert.Equal(3, manager.Count);
            Assert.Equal(0, manager.ClosedCount);
            Assert.Equal(0, created);
            Assert.Equal(0, activationCalls);

            PinContent last = manager.SnapshotOpenPins()[2].Content;
            manager.CloseAll();
            Assert.Equal(3, manager.ClosedCount);
            PinWindow closedRestore = manager.RestoreLastClosed()!;
            Assert.Same(last, closedRestore.PinnedContent);
            Assert.True(closedRestore.ShowActivated);
            Assert.Equal(2, manager.ClosedCount);
            Assert.Equal(0, created);
            Assert.Equal(1, activationCalls);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void SessionRestore_UsesTheDisplayAwareClosedPinPlacement() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager();
        try
        {
            PinClosedPlacement savedPlacement = PlacementOutsideDesktop();
            var snapshot = new PinManager.ClosedPin(PinContent.FromImage(Image()), 80, 60, 1.3, 0.8, savedPlacement);
            PinWindow restored = manager.RestoreSessionPin(snapshot);
            PinDisplayArea[] displays = MonitorEnumerator.GetAll()
                .Select(m => new PinDisplayArea(m.DeviceName, m.Bounds, m.WorkArea, m.ScaleFactor))
                .ToArray();
            PinRestorePlacement.Result expected = PinRestorePlacement.Resolve(
                savedPlacement, restored.State.WidthDip, restored.State.HeightDip, displays);
            (int left, int top, int right, int bottom) = restored.PhysicalBounds;

            Assert.InRange(left, expected.Left - 1, expected.Left + 1);
            Assert.InRange(top, expected.Top - 1, expected.Top + 1);
            Assert.True(right > left && bottom > top);
            Assert.False(restored.ShowActivated);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void OpenPinsChanged_FollowsOpenCloseAndSettledPresentation_DespiteObserverFailure() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager();
        manager.BeforeShowForTest = pin => pin.ShowActivated = false;
        manager.ActivatePinForTest = _ => { };
        var counts = new List<int>();
        manager.OpenPinsChanged += () => throw new InvalidOperationException("Failed session observer");
        manager.OpenPinsChanged += () => counts.Add(manager.Count);
        try
        {
            PinWindow pin = manager.PinImage(Image());
            Assert.Equal(new[] { 1 }, counts);

            Invoke(pin, "ZoomCentered", 1);
            Assert.Single(counts);
            pin.ForceZoomSettleForTest();
            Assert.Equal(new[] { 1, 1 }, counts);
            pin.ForceZoomSettleForTest();
            Assert.Equal(2, counts.Count);

            Invoke(pin, "AdjustOpacity", -0.1);
            Assert.Equal(new[] { 1, 1, 1 }, counts);

            pin.Close();
            Assert.Equal(new[] { 1, 1, 1, 0 }, counts);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData(Key.Left, ModifierKeys.None, -1, 0)]
    [InlineData(Key.Right, ModifierKeys.Shift, 10, 0)]
    [InlineData(Key.Up, ModifierKeys.None, 0, -1)]
    [InlineData(Key.Down, ModifierKeys.Shift, 0, 10)]
    public void ViewStateChanged_ArrowNudgesNotifyAfterMoving(
        Key key, ModifierKeys modifiers, double dx, double dy) => StaTestHost.Run(() =>
    {
        var pin = new PinWindow(PinContent.FromImage(Image()), new PinViewState(80, 60, 1, 1, 0.1), 200, 150,
            () => new PinSettings()) { ShowActivated = false };
        int changes = 0;
        pin.ViewStateChanged += () =>
        {
            changes++;
            Assert.Equal(200 + dx, pin.Left);
            Assert.Equal(150 + dy, pin.Top);
        };
        try
        {
            // Exercise the real arrow command and its modifier selection without native focus.
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new PinKeySource(), 0, key)
                { RoutedEvent = Keyboard.KeyDownEvent };
            pin.HandleKeyDown(args, modifiers);

            Assert.True(args.Handled);
            Assert.Equal(1, changes);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ViewStateChanged_ActivationNotifiesTheSessionObserver() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager();
        int changes = 0;
        manager.OpenPinsChanged += () => changes++;
        try
        {
            PinWindow pin = manager.RestoreSessionPin(new PinManager.ClosedPin(
                PinContent.FromImage(Image()), 80, 60, 1, 1, null));
            Assert.Equal(1, changes);
            int viewChanges = 0;
            pin.ViewStateChanged += () => viewChanges++;

            // Raise Window's actual Activated event, without changing the desktop foreground.
            typeof(Window).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pin, [EventArgs.Empty]);

            Assert.Equal(1, viewChanges);
            Assert.Equal(2, changes);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ViewStateChanged_DragNotifiesOnceWhenTheGestureEnds() => StaTestHost.Run(() =>
    {
        var input = new SyntheticPinDragInput();
        var pin = new PinWindow(PinContent.FromImage(Image()), new PinViewState(80, 60, 1, 1, 0.1), 200, 150,
            () => new PinSettings { CloseOnDoubleClick = false }, input) { ShowActivated = false };
        int changes = 0;
        try
        {
            pin.Show();
            input.Down(pin);
            // Observe the drag after its initial activation, which is covered separately.
            pin.ViewStateChanged += () => changes++;
            input.CursorPosition = (450, 300);
            input.Move(pin);
            Assert.Equal(0, changes);

            input.Up(pin);
            Assert.Equal(1, changes);
            input.Up(pin);
            Assert.Equal(1, changes);
        }
        finally { pin.Close(); }
    });

    private static PinManager Manager() => new(() => new PinSettings(), NullLogger.Instance);

    private sealed class PinKeySource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    private static BitmapSource Image()
    {
        var bitmap = new WriteableBitmap(80, 60, 96, 96, PixelFormats.Bgr32, null);
        bitmap.Freeze();
        return bitmap;
    }

    private static PinClosedPlacement PlacementOutsideDesktop() => new(
        new RectD(-100000, -100000, 120, 90), "removed-display", new RectD(-101000, -101000, 1920, 1080));

    private static void Invoke(PinWindow pin, string method, object value) =>
        typeof(PinWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pin, [value]);
}
