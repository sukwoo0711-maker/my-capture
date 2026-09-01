using System.ComponentModel;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PhysicalWindowPositionerTests
{
    [Fact]
    public void PhysicalWindowBounds_FromEdgesPreservesNegativeOriginSize()
    {
        PhysicalWindowBounds bounds = PhysicalWindowBounds.FromEdges(-1921, -201, 640, 1001);

        Assert.Equal(new PhysicalWindowBounds(-1921, -201, 2561, 1202), bounds);
    }

    [Fact]
    public void PlaceTopmost_RoundsOutwardAndVerifiesExactNegativeOriginBounds()
    {
        var native = new FakePhysicalWindowNativeApi();
        native.Readbacks.Enqueue(new PhysicalWindowBounds(-1921, -201, 2561, 1202));

        PhysicalWindowPositioner.PlaceTopmost(
            new IntPtr(42),
            new RectD(-1920.75, -200.25, 2560.5, 1200.5),
            native);

        PlacementCall call = Assert.Single(native.Placements);
        Assert.Equal(new IntPtr(42), call.Hwnd);
        Assert.Equal(new IntPtr(-1), call.InsertAfter);
        Assert.Equal(-1921, call.X);
        Assert.Equal(-201, call.Y);
        Assert.Equal(2561, call.Width);
        Assert.Equal(1202, call.Height);
        Assert.Equal(0x0040u, call.Flags);
        Assert.Equal(1, native.ReadCount);
    }

    [Fact]
    public void PlaceTopmost_RetriesWhenReadbackDoesNotExactlyMatch()
    {
        var native = new FakePhysicalWindowNativeApi();
        native.Readbacks.Enqueue(new PhysicalWindowBounds(-1919, 100, 200, 300));
        native.Readbacks.Enqueue(new PhysicalWindowBounds(-1920, 100, 200, 300));

        PhysicalWindowPositioner.PlaceTopmost(
            new IntPtr(42),
            new RectD(-1920, 100, 200, 300),
            native);

        Assert.Equal(2, native.Placements.Count);
        Assert.Equal(2, native.ReadCount);
    }

    [Fact]
    public void PlaceTopmost_StopsAfterBoundedNumberOfMismatches()
    {
        var native = new FakePhysicalWindowNativeApi();
        for (int i = 0; i < PhysicalWindowPositioner.PlacementAttemptLimit; i++)
        {
            native.Readbacks.Enqueue(new PhysicalWindowBounds(-1919, 100, 200, 300));
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PhysicalWindowPositioner.PlaceTopmost(
                new IntPtr(42),
                new RectD(-1920, 100, 200, 300),
                native));

        Assert.Equal(PhysicalWindowPositioner.PlacementAttemptLimit, native.Placements.Count);
        Assert.Equal(PhysicalWindowPositioner.PlacementAttemptLimit, native.ReadCount);
        Assert.Contains("Expected [-1920,100 200x300]", error.Message, StringComparison.Ordinal);
        Assert.Contains("actual [-1919,100 200x300]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaceTopmost_ReportsSetWindowPosFailureWithoutReadback()
    {
        var native = new FakePhysicalWindowNativeApi
        {
            SetWindowPosResult = false,
            LastError = 5,
        };

        Win32Exception error = Assert.Throws<Win32Exception>(() =>
            PhysicalWindowPositioner.PlaceTopmost(
                new IntPtr(42),
                new RectD(-100, -50, 200, 100),
                native));

        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(0, native.ReadCount);
    }

    [Fact]
    public void PlaceTopmost_ReportsGetWindowRectFailure()
    {
        var native = new FakePhysicalWindowNativeApi
        {
            GetWindowRectResult = false,
            LastError = 1400,
        };

        Win32Exception error = Assert.Throws<Win32Exception>(() =>
            PhysicalWindowPositioner.PlaceTopmost(
                new IntPtr(42),
                new RectD(-100, -50, 200, 100),
                native));

        Assert.Equal(1400, error.NativeErrorCode);
        Assert.Single(native.Placements);
        Assert.Equal(1, native.ReadCount);
    }

    [Fact]
    public void PlaceTopmost_RejectsMissingWindowHandleBeforeNativeCalls()
    {
        var native = new FakePhysicalWindowNativeApi();

        Assert.Throws<ArgumentException>(() =>
            PhysicalWindowPositioner.PlaceTopmost(
                IntPtr.Zero,
                new RectD(-100, -50, 200, 100),
                native));

        Assert.Empty(native.Placements);
        Assert.Equal(0, native.ReadCount);
    }

    private sealed class FakePhysicalWindowNativeApi : IPhysicalWindowNativeApi
    {
        internal Queue<PhysicalWindowBounds> Readbacks { get; } = new();

        internal List<PlacementCall> Placements { get; } = [];

        internal bool SetWindowPosResult { get; init; } = true;

        internal bool GetWindowRectResult { get; init; } = true;

        internal int LastError { get; init; }

        internal int ReadCount { get; private set; }

        public bool SetWindowPos(
            IntPtr hwnd,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags)
        {
            Placements.Add(new PlacementCall(hwnd, insertAfter, x, y, width, height, flags));
            return SetWindowPosResult;
        }

        public bool GetWindowRect(IntPtr hwnd, out PhysicalWindowBounds bounds)
        {
            _ = hwnd;
            ReadCount++;
            bounds = Readbacks.Count > 0 ? Readbacks.Dequeue() : default;
            return GetWindowRectResult;
        }

        public int GetLastError() => LastError;
    }

    private readonly record struct PlacementCall(
        IntPtr Hwnd,
        IntPtr InsertAfter,
        int X,
        int Y,
        int Width,
        int Height,
        uint Flags);
}
