using System.Windows;
using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CapturePrecisionPointerTests
{
    private static readonly RectD Bounds = new(-100, -100, 400, 300);

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Precision_AccumulatesSmallMotions_AndIgnoresWarpEvents(int direction)
    {
        var pointer = new CapturePrecisionPointer();
        PointD output = pointer.Update(new(-20, -30), true, Bounds);
        for (int i = 1; i <= 5; i++)
        {
            output = pointer.Update(output.Offset(direction, direction), true, Bounds);
            Assert.Equal(new PointD(-20 + (i == 5 ? direction : 0), -30 + (i == 5 ? direction : 0)), output);
            Assert.Equal(output, pointer.Update(output, true, Bounds));
        }
    }

    [Fact]
    public void ModeTransitionsAndReset_DoNotJump_AndNormalMotionIsUnchanged()
    {
        var pointer = new CapturePrecisionPointer();
        Assert.Equal(new PointD(10, 20), pointer.Update(new(10, 20), false, Bounds));
        Assert.Equal(new PointD(70, 90), pointer.Update(new(70, 90), false, Bounds));
        Assert.Equal(new PointD(70, 90), pointer.Update(new(70, 90), true, Bounds));
        Assert.Equal(new PointD(72, 88), pointer.Update(new(80, 80), true, Bounds));
        Assert.Equal(new PointD(72, 88), pointer.Update(new(72, 88), false, Bounds));
        Assert.Equal(new PointD(82, 78), pointer.Update(new(82, 78), false, Bounds));
        pointer.Reset();
        Assert.Equal(new PointD(-80, -90), pointer.Update(new(-80, -90), true, Bounds));
    }

    [Fact]
    public void Precision_ClampsNegativeOriginBoundsWithoutResidualOvershoot()
    {
        var pointer = new CapturePrecisionPointer();
        pointer.Update(new(-99, -99), true, Bounds);
        Assert.Equal(new PointD(-100, -100), pointer.Update(new(-200, -200), true, Bounds));
        Assert.Equal(new PointD(-99, -99), pointer.Update(new(-95, -95), true, Bounds));
    }

    [Fact]
    public void View_UsesEffectivePhysicalPoint_AndStopsWarpingWhenInactiveOrCancelled() => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Bounds, showMagnifier: false);
        PointD actual = new(-20, -30);
        int writes = 0;
        view.ReadCursor = () => actual;
        view.PlaceCursor = target => { writes++; actual = target; return true; };
        Assert.Equal(new PointD(80, 70), view.UpdatePointer(true, true));
        actual = actual.Offset(10, 10);
        Assert.Equal(new PointD(82, 72), view.UpdatePointer(true, true));
        Assert.Equal(new PointD(-18, -28), actual);
        Assert.Equal(new PointD(82, 72), view.UpdatePointer(true, true));
        Assert.Equal(1, writes);
        actual = actual.Offset(10, 10);
        Assert.Equal(new PointD(92, 82), view.UpdatePointer(true, false));
        Assert.Equal(1, writes);
        // Escape marks the selector ended; even queued movement must not move the cursor.
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Escape)
        { RoutedEvent = Keyboard.KeyDownEvent };
        view.RaiseEvent(key);
        actual = actual.Offset(10, 10);
        view.UpdatePointer(true, true);
        Assert.Equal(1, writes);
    });

    [Fact]
    public void GeometryCompletion_WorksWithoutBitmap_AndPreservesDesktopOrigin() => StaTestHost.Run(() =>
    {
        var window = new CaptureOverlayWindow(Bounds, false, false);
        RectD? selected = null;
        window.GeometrySelectionCompleted = region => selected = region;
        window.CompleteSelection(new RectD(12, 18, 123, 87));
        Assert.Equal(new RectD(-88, -82, 123, 87), selected);
    });

    private sealed class TestPresentationSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
