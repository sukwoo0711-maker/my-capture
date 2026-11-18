using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ElementSelectorInputTests
{
    private static readonly RectD Root = new(40, 30, 300, 200), Element = new(80, 60, 120, 100);
    private static readonly WindowCandidate Window = new(new IntPtr(88), Root, "Fixture", ProcessId: uint.MaxValue);
    private static readonly PointD Pointer = new(100, 100);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoLookupBeforeReadyOrWithoutStep(bool native) => StaTestHost.Run(() =>
    {
        var source = new ElementSelectionControllerTests.FakeSource();
        if (native)
        {
            using var overlay = Native(source);
            overlay.RefreshHover(); Assert.False(overlay.StepElement(1)); Assert.Equal(0, source.Calls);
            Ready(overlay); overlay.RefreshHover(); Assert.Equal(0, source.Calls);
            Assert.True(overlay.StepElement(1)); Assert.Equal(1, source.Calls);
        }
        else
        {
            var view = Wpf(source);
            try
            {
                view.InitializePointer(); Assert.False(view.StepElement(1)); Assert.Equal(0, source.Calls);
                view.NotifyFirstFramePresented(); view.UpdatePointer(false, false); Assert.Equal(0, source.Calls);
                Assert.True(view.StepElement(1)); Assert.Equal(1, source.Calls);
            }
            finally { view.ReleaseResources(); }
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClickAndEnterCaptureExactHighlightedElement(bool native, bool enter) => StaTestHost.Run(() =>
    {
        var source = new ElementSelectionControllerTests.FakeSource();
        RectD? captured = null;
        if (native)
        {
            using var overlay = Native(source); Ready(overlay);
            overlay.GeometrySelectionCompleted += rect => captured = rect;
            Assert.True(overlay.HandleKey(Key.PageDown, ModifierKeys.None)); Complete(source, () => !overlay.ElementPending);
            Assert.Equal(Element, overlay.HoverRegion); Assert.Equal(1, overlay.ElementDepth);
            if (enter) overlay.HandleKey(Key.Enter, ModifierKeys.None);
            else { overlay.BeginSelection(Pointer); overlay.EndSelection(Pointer); }
            Assert.True(source.Disposed);
        }
        else
        {
            var view = Wpf(source);
            try
            {
                view.SelectionConfirmed += (_, e) => captured = e.BitmapRegion;
                view.NotifyFirstFramePresented(); view.HandleKeyDown(WindowDetectOverlayTests.KeyArgs(Key.PageDown), ModifierKeys.None);
                Complete(source, () => !view.ElementPending);
                Assert.Equal(Element, view.HoverRegionForTest); Assert.Equal(1, view.ElementDepth);
                if (enter) view.HandleKeyDown(WindowDetectOverlayTests.KeyArgs(Key.Enter), ModifierKeys.None);
                else { view.SimulatePointerDown(); view.SimulatePointerUp(); WindowDetectOverlayTests.PumpUntil(() => captured is not null); }
                Assert.True(source.Disposed);
            }
            finally { view.ReleaseResources(); }
        }
        Assert.Equal(Element, captured);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FractionalWheelMessageAndPageUpPreserveDefaultWindowClick(bool native) => StaTestHost.Run(() =>
    {
        var source = new ElementSelectionControllerTests.FakeSource();
        if (native)
        {
            using var overlay = Native(source); Ready(overlay);
            SendMessage(overlay.Handle, 0x20A, new IntPtr(60 << 16), IntPtr.Zero); Assert.Equal(0, source.Calls);
            SendMessage(overlay.Handle, 0x20A, new IntPtr(60 << 16), IntPtr.Zero); Assert.Equal(1, source.Calls);
            Complete(source, () => !overlay.ElementPending);
            overlay.HandleKey(Key.PageUp, ModifierKeys.None); Assert.Equal(Root, overlay.HoverRegion);
            Assert.False(overlay.HandleKey(Key.PageDown, ModifierKeys.Control)); Assert.Equal(1, source.Calls);
        }
        else
        {
            var view = Wpf(source);
            try
            {
                view.NotifyFirstFramePresented();
                Wheel(view, 60); Assert.Equal(0, source.Calls); Wheel(view, 60); Assert.Equal(1, source.Calls);
                Complete(source, () => !view.ElementPending);
                view.HandleKeyDown(WindowDetectOverlayTests.KeyArgs(Key.PageUp), ModifierKeys.None);
                Assert.Equal(Root, view.HoverRegionForTest);
                var key = WindowDetectOverlayTests.KeyArgs(Key.PageDown); view.HandleKeyDown(key, ModifierKeys.Control);
                Assert.False(key.Handled); Assert.Equal(1, source.Calls);
            }
            finally { view.ReleaseResources(); }
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingResponseCannotAlterAPressedWindow(bool native) => StaTestHost.Run(() =>
    {
        var source = new ElementSelectionControllerTests.FakeSource();
        RectD? captured = null;
        if (native)
        {
            using var overlay = Native(source); Ready(overlay);
            overlay.GeometrySelectionCompleted += rect => captured = rect;
            overlay.StepElement(1); overlay.BeginSelection(Pointer);
            Complete(source, () => !overlay.ElementPending); Assert.Equal(Root, overlay.HoverRegion);
            overlay.EndSelection(Pointer);
        }
        else
        {
            var view = Wpf(source);
            try
            {
                view.SelectionConfirmed += (_, e) => captured = e.BitmapRegion;
                view.NotifyFirstFramePresented(); view.StepElement(1); view.SimulatePointerDown();
                Complete(source, () => !view.ElementPending); Assert.Equal(Root, view.HoverRegionForTest);
                view.SimulatePointerUp(); WindowDetectOverlayTests.PumpUntil(() => captured is not null);
            }
            finally { view.ReleaseResources(); }
        }
        Assert.Equal(Root, captured);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingSelectorDisposesPendingSourceAndDropsLateResult(bool native) => StaTestHost.Run(() =>
    {
        var source = new ElementSelectionControllerTests.FakeSource();
        if (native)
        {
            using var overlay = Native(source); Ready(overlay); overlay.StepElement(1); overlay.Close();
            source.Complete(new(ElementGeometryStatus.Success, [new(Root, 1), new(Element, 1)]));
            Assert.True(overlay.IsClosed); Assert.Equal(0, overlay.ElementDepth); Assert.False(overlay.HoverElement);
        }
        else
        {
            var view = Wpf(source); view.NotifyFirstFramePresented(); view.StepElement(1); view.ReleaseResources();
            source.Complete(new(ElementGeometryStatus.Success, [new(Root, 1), new(Element, 1)]));
            Assert.Equal(0, view.ElementDepth); Assert.False(view.HoverElement);
        }
        Assert.True(source.Disposed); Assert.True(source.Cancellation.IsCancellationRequested);
    });

    [Fact]
    public void NativeSelectionTimelineIncludesBeginEndAndClosedWithElementStep() => StaTestHost.Run(() =>
    {
        var source = new ElementSelectionControllerTests.FakeSource(); using var overlay = Native(source); Ready(overlay);
        var previous = SelectorTimeline.Mark; var marks = new List<string>(); SelectorTimeline.Mark = marks.Add;
        try
        {
            overlay.StepElement(1); Complete(source, () => !overlay.ElementPending);
            overlay.BeginSelection(Pointer); overlay.EndSelection(Pointer);
            Assert.Contains("selector-begin-selection", marks); Assert.Contains("selector-end-selection", marks);
            Assert.Contains("selector-closed", marks);
            Assert.True(marks.IndexOf("selector-begin-selection") < marks.IndexOf("selector-closed"));
            Assert.True(marks.IndexOf("selector-closed") < marks.IndexOf("selector-end-selection"));
            Assert.Equal(0, overlay.LiveOwnedNativeHandles);
        }
        finally { SelectorTimeline.Mark = previous; }
    });

    internal static NativeCaptureOverlay Native(IElementGeometrySource source)
    {
        FrozenFrame frame = WindowDetectOverlayTests.Frame();
        var overlay = new NativeCaptureOverlay(frame.ScreenBounds, false, false)
        {
            DetectWindows = true, SourceWindows = [Window], ReadCursor = () => Pointer, PlaceCursor = _ => false,
            ReadMonitors = () => [new MonitorInfo("fixture", frame.ScreenBounds, frame.ScreenBounds, 96, true)],
            CreateElementSource = () => source,
        };
        overlay.AttachFrame(frame); return overlay;
    }
    internal static void Ready(NativeCaptureOverlay overlay) => typeof(NativeCaptureOverlay)
        .GetProperty("IsReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(overlay, true);
    private static CaptureOverlayView Wpf(IElementGeometrySource source) => new(WindowDetectOverlayTests.Frame(), false)
    { DetectWindows = true, WindowCandidates = [Window], ReadCursor = () => Pointer, PlaceCursor = _ => false, CreateElementSource = () => source };
    private static void Complete(ElementSelectionControllerTests.FakeSource source, Func<bool> done)
    {
        source.Complete(new(ElementGeometryStatus.Success, [new(Root, 50032), new(Element, 50000)]));
        WindowDetectOverlayTests.PumpUntil(done);
    }
    private static void Wheel(CaptureOverlayView view, int delta) => view.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
    { RoutedEvent = Mouse.MouseWheelEvent });
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
