using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinCollapseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoubleClicksAndMenuToggleWithoutClosing(bool closeSetting) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var input = new SyntheticPinDragInput { Modifiers = ModifierKeys.Shift };
        var pin = NewPin(input, closeSetting);
        try
        {
            pin.Show();
            var original = pin.PhysicalBounds;
            input.DoubleClick(pin);
            Assert.True(pin.State.IsCollapsed);
            Assert.Equal(original.Left, pin.PhysicalBounds.Left);
            Assert.Equal(original.Top, pin.PhysicalBounds.Top);
            Assert.Equal(96, pin.Width);
            Assert.Equal(54, pin.Height);
            var thumbnail = Assert.IsAssignableFrom<BitmapSource>(pin.ImageSourceForTest);
            Assert.True(thumbnail.PixelWidth <= 160 * VisualTreeHelper.GetDpi(pin).DpiScaleX);
            Assert.Equal("Double-click to expand the pin.", pin.ToolTip);
            input.Modifiers = ModifierKeys.None;
            input.DoubleClick(pin);
            Assert.False(pin.IsClosed);
            Assert.False(pin.State.IsCollapsed);
            MenuItem item = CollapseItem(pin);
            Assert.Equal("Collapse", item.Header);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(pin.State.IsCollapsed);
            Assert.Equal("Expand", item.Header);
            input.Modifiers = ModifierKeys.Shift;
            input.DoubleClick(pin);
            Assert.False(pin.State.IsCollapsed);
            Assert.Equal(UiText.Get("Onboard_PinTip"), pin.ToolTip);
            pin.State.SetClickThrough(true);
            input.DoubleClick(pin);
            Assert.False(pin.State.IsCollapsed);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void CollapsedInputAllowsCopySaveMoveOpacityButBlocksZoomSelectionAndChrome() => StaTestHost.Run(() =>
    {
        var input = new SyntheticPinDragInput { Anchor = new Point(12, 12) };
        var pin = NewPin(input);
        int copies = 0, saves = 0;
        pin.CopyRequested += (_, _) => copies++;
        pin.SaveRequested += (_, _) => saves++;
        try
        {
            pin.Show();
            pin.ToggleCollapsed();
            foreach (Key key in new[] { Key.Add, Key.Subtract, Key.D0, Key.F2, Key.O }) Press(pin, key);
            Wheel(pin, 120, ModifierKeys.None);
            Assert.Equal(1.5, pin.State.Zoom);
            Assert.Equal(96, pin.Width);
            Assert.Equal(PinResizeEdge.None, pin.ResizeEdgeAt(new Point(0, 0)));
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(pin.IsHoverBarShown);
            pin.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.All(pin.ContextMenu.Items.OfType<MenuItem>().Where(item => item.InputGestureText is "0" or "+" or "-" or "F2"),
                item => Assert.False(item.IsEnabled));
            Press(pin, Key.C, ModifierKeys.Control);
            Press(pin, Key.S, ModifierKeys.Control);
            Assert.Equal(1, copies);
            Assert.Equal(1, saves);
            Assert.False(pin.IsFeedbackVisible);
            Wheel(pin, -120, ModifierKeys.Control);
            Assert.Equal(.6, pin.State.Opacity, 6);
            var before = pin.PhysicalBounds;
            Press(pin, Key.Right);
            Assert.True(pin.PhysicalBounds.Left > before.Left);
            input.Down(pin);
            input.CursorPosition = (350, 250);
            input.Move(pin);
            input.Up(pin);
            var moved = pin.PhysicalBounds;
            Assert.NotEqual(before.Left, moved.Left);
            pin.ReadWorkArea = _ => new RectD(0, 0, 500, 400);
            pin.ToggleCollapsed();
            Assert.Equal(480, pin.Width);
            Assert.Equal(270, pin.Height);
            Assert.Equal(.7, pin.Opacity);
            Assert.Equal(moved.Left, pin.PhysicalBounds.Left);
            Assert.Equal(moved.Top, pin.PhysicalBounds.Top);
            pin.ToggleCollapsed();
            pin.MovePhysicalForTest(2000, 2000);
            pin.ToggleCollapsed();
            Assert.True(pin.PhysicalBounds.Left < 500);
            Assert.True(pin.PhysicalBounds.Top < 400);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextAndPictureSelectionKeepDoubleClickUntilMenuCollapses(bool picture) => StaTestHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var input = new SyntheticPinDragInput { Modifiers = ModifierKeys.Shift };
        var content = picture ? PinContent.FromImage(Image()) : PinContent.FromText(Image(), "sample", false);
        var pin = new PinWindow(content, new PinViewState(320, 180, 1, 1, .1), 100, 100,
            dragInput: input, recognizeSelection: (_, _) => Task.FromResult(PinImageTextSelectionTests.Layout()))
            { ShowActivated = false, SelectionFocus = new NoFocus() };
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            Wait(pin.SelectionWork);
            Assert.True(pin.IsTextSelectionMode);
            input.DoubleClick(pin);
            Assert.False(pin.State.IsCollapsed);
            Assert.True(pin.IsTextSelectionMode);
            CollapseItem(pin).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(pin.State.IsCollapsed);
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(pin.IsRecognizingSelection);
            pin.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.False(Assert.Single(pin.ContextMenu.Items.OfType<MenuItem>(), item => item.InputGestureText == "F2").IsEnabled);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void TranslationAndHideShowAndTrayActivationKeepCollapse() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            BeforeShowForTest = pin => pin.ShowActivated = false,
            ActivatePinForTest = _ => { },
        };
        try
        {
            PinWindow pin = manager.PinImage(Image());
            var translated = PinContent.FromText(Image(), "translated", false);
            pin.ApplyTranslation(translated, needsReview: false);
            var source = pin.ImageSourceForTest;
            pin.ToggleCollapsed();
            Assert.Equal("Collapsed", TrayMenuLayout.OpenPinStatus(pin));
            manager.HideOrShowAll();
            Assert.Equal("Hidden · Collapsed", TrayMenuLayout.OpenPinStatus(pin));
            manager.HideOrShowAll();
            pin.HidePin();
            manager.ActivateOpenPin(pin);
            Assert.True(pin.State.IsCollapsed);
            Assert.False(pin.State.IsHidden);
            pin.ToggleCollapsed();
            Assert.Same(source, pin.ImageSourceForTest);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void CollapseAndCopyCompletionReleaseFlattenedTranslationCache() => StaTestHost.Run(() =>
    {
        var source = PinContent.FromImage(Image());
        var layer = new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(20, 20, 60, 30)));
        layer.Freeze();
        var composition = new PinOverlayComposition(source.Image, layer, false, 1);
        var pin = NewPin(new SyntheticPinDragInput());
        try
        {
            pin.Show();
            pin.ApplyTranslation(PinContent.FromTranslationOverlay(composition, "translated", source), needsReview: false);
            BitmapSource before = composition.Image;
            pin.ToggleCollapsed();
            BitmapSource copied = composition.Image;
            Assert.NotSame(before, copied);
            pin.ReportCopyResult(true);
            Assert.NotSame(copied, composition.Image);
            Assert.False(pin.IsFeedbackVisible);
            Assert.Equal(96, pin.Width);
            Assert.Same(layer, composition.Overlay);
            Assert.Same(source.Image, composition.Source);
            pin.ToggleCollapsed();
            Assert.False(pin.State.IsCollapsed);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void SessionSavesAndRestoresCollapseWithExpandedPresentation() => StaTestHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = OwnedTestDirectory.Create("pin-collapse-session-");
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
        var manager = new PinManager(() => settings, NullLogger.Instance) { BeforeShowForTest = pin => pin.ShowActivated = false };
        try
        {
            var store = new PinSessionStore(root, NullLogger.Instance);
            using var service = new PinSessionService(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance);
            Wait(service.RestoreAsync());
            PinWindow pin = manager.PinImage(Image());
            pin.State.SetOpacity(.7);
            pin.ToggleCollapsed();
            Wheel(pin, -120, ModifierKeys.Control);
            Assert.True(service.SaveNow().IsComplete);
            PinSessionPin saved = Assert.Single(store.Load()).Pin;
            Assert.True(saved.IsCollapsed);
            Assert.Equal(.7, saved.Opacity);
            Assert.Equal(.6, saved.CollapsedOpacity!.Value, 6);
            manager.CloseAllForExit();
            using var restoredService = new PinSessionService(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance);
            Wait(restoredService.RestoreAsync());
            PinWindow restored = Assert.Single(manager.SnapshotOpenPinWindows());
            Assert.True(restored.State.IsCollapsed);
            Assert.Equal(96, restored.Width);
            restored.ToggleCollapsed();
            Assert.Equal(saved.Zoom, restored.State.Zoom);
            Assert.Equal(.7, restored.Opacity);
        }
        finally { manager.CloseAllForExit(); OwnedTestDirectory.Delete(root); }
    });

    internal static PinWindow NewPin(SyntheticPinDragInput input, bool close = true) =>
        new(PinContent.FromImage(Image()), new PinViewState(320, 180, 1.5, .7, .1), 100, 100,
            () => new PinSettings { CloseOnDoubleClick = close }, input) { ShowActivated = false };

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Delete)]
    public void CollapsedPinStillClosesFromKeyboard(Key key) => StaTestHost.Run(() =>
    {
        var pin = NewPin(new SyntheticPinDragInput());
        try
        {
            pin.Show(); pin.ToggleCollapsed(); Press(pin, key);
            Assert.True(pin.IsClosed);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void CollapsedSessionSnapshotAndShowOnlyKeepState() => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        { BeforeShowForTest = pin => pin.ShowActivated = false, ActivatePinForTest = _ => { } };
        try
        {
            var pin = manager.PinImage(Image());
            var other = manager.PinImage(Image());
            pin.State.SetOpacity(.8);
            pin.ToggleCollapsed();
            other.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.Single(other.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("Pin_ShowOnly")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(pin.State.IsHidden);
            manager.ActivateOpenPin(pin);
            Assert.True(pin.State.IsCollapsed);
            PinManager.ClosedPin snapshot = Assert.Single(manager.SnapshotOpenPins(), saved => saved.IsCollapsed);
            Assert.Equal(.8, snapshot.Opacity);
            manager.CloseAllForExit();
            PinWindow restored = manager.RestoreSessionPin(snapshot);
            Assert.True(restored.State.IsCollapsed);
            restored.ToggleCollapsed();
            Assert.Equal(.8, restored.State.Opacity);
            Assert.Equal(snapshot.ImageWidthDip * snapshot.Zoom, restored.Width);
        }
        finally { manager.CloseAllForExit(); }
    });

    internal static BitmapSource Image()
    {
        var pixels = new byte[320 * 180 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 160; pixels[i + 1] = 100; pixels[i + 2] = 40; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
        image.Freeze();
        return image;
    }
    private static MenuItem CollapseItem(PinWindow pin) => pin.ContextMenu.Items.OfType<MenuItem>()
        .Single(item => item.InputGestureText == UiText.Get("Pin_CollapseGesture"));
    private static void Press(PinWindow pin, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent }, modifiers);
    private static void Wheel(PinWindow pin, int delta, ModifierKeys modifiers) =>
        pin.HandleMouseWheel(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.MouseWheelEvent }, modifiers);
    private static void Wait(Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10)) RangePatternClip.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private sealed class NoFocus : IPinSelectionFocus
    {
        public void Enter(PinWindow owner, PinTextSelectionView selection) { }
        public bool IsActive(PinWindow owner) => false;
        public void Restore(PinWindow owner) { }
    }
}
