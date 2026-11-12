using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Chrome that follows the pointer across a seam between monitors of different DPI. Windows sends
/// WM_DPICHANGED inside SetWindowPos and WPF moves the HWND to the suggested rectangle, so the
/// read-back can differ from the request on every attempt. <see cref="SuggestedRectangle"/> replays that
/// resize on the real chrome HWNDs; the cursor is injected and the real one never moves.
/// </summary>
public sealed class NativeSelectorChromeDpiSeamTests
{
    // Inside the single 1024x768 screen of a CI runner, where a larger selector never reports itself ready.
    private static readonly RectD Desktop = new(0, 0, 1000, 700);
    private static readonly MonitorInfo[] Monitors =
    [
        new("left", new RectD(0, 0, 500, 700), new RectD(0, 0, 500, 652), 96, true),
        new("right", new RectD(500, 0, 500, 700), new RectD(500, 0, 500, 652), 96, false),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChromeThatKeepsMissingItsBoundsNeverEndsTheSelection(bool recording) => StaTestHost.Run(() =>
    {
        NativeSelectorChromeCache.Trim();
        PointD cursor = new(300, 300);
        using NativeCaptureOverlay overlay = Create(() => cursor, recording);
        var failures = new List<Exception>();
        overlay.Failed += failures.Add;
        var misses = new List<Exception>();
        overlay.ChromePlacementMissed = misses.Add;
        var suggested = new SuggestedRectangle();
        try
        {
            overlay.Show();
            PumpUntil(() => overlay.IsReady && overlay.IsChromeReady);
            PointD[] path = [new(150, 200), new(480, 300), new(520, 320), new(800, 400), new(510, 640), new(150, 200)];
            // The first pass only brings up the hover chrome; the recorded pass starts from the same state as the last.
            MoveAlong(overlay, path, p => cursor = p);
            Dictionary<IntPtr, PhysicalWindowBounds> before = MoveAlong(overlay, path, p => cursor = p);
            Assert.Equal(0, overlay.Chrome!.PlacementRepairsForTest);
            Assert.Empty(misses);
            suggested.Attach(overlay.Chrome!.Windows);
            suggested.Active = true;
            // New positions: an unchanged one is skipped without a SetWindowPos.
            MoveAlong(overlay, path.Select(p => new PointD(p.X + 37, p.Y + 23)).ToArray(), p => cursor = p);
            Assert.True(suggested.Resizes > 0, "No chrome HWND was resized.");
            Assert.False(overlay.IsClosed, string.Join(" | ", failures));
            Assert.Empty(failures);
            // The misses were contained where they happened, and the owner heard of them once, for its log.
            Assert.True(overlay.Chrome!.PlacementRepairsForTest > 0, "No missed placement was contained.");
            Assert.Contains("after 3 attempts", Assert.Single(misses).Message);

            // Once the DPI change settles, no chrome keeps the suggested size and placement is stable again.
            suggested.Active = false;
            Dictionary<IntPtr, PhysicalWindowBounds> after = MoveAlong(overlay, path, p => cursor = p);
            Dictionary<IntPtr, PhysicalWindowBounds> again = MoveAlong(overlay, path, p => cursor = p);
            Assert.False(overlay.IsClosed);
            Assert.Equal(after, again);
            foreach ((IntPtr hwnd, PhysicalWindowBounds bounds) in before)
                if (after.TryGetValue(hwnd, out PhysicalWindowBounds placed))
                    Assert.False(placed.Width == bounds.Width * 5 / 4 && placed.Height == bounds.Height * 5 / 4, $"{hwnd:X} kept the suggested size {placed}.");
        }
        finally { suggested.Detach(); overlay.Close(); NativeSelectorChromeCache.Trim(); }
    });

    [Fact]
    public void StrictPlacementThrowsWhenEveryReadBackHasTheSuggestedSize()
    {
        // The exception the chrome must contain: strict placement stays strict for main windows.
        var native = new ResizingNativeApi();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PhysicalWindowPositioner.MoveAndResize(new IntPtr(42), new RectD(10, 20, 200, 100), native));
        Assert.Contains("after 3 attempts", error.Message);
        native.Ratio = 1;
        PhysicalWindowPositioner.MoveAndResize(new IntPtr(42), new RectD(10, 20, 200, 100), native);
    }

    /// <summary>SetWindowPos lands; WPF then applies a suggested rectangle scaled by the DPI ratio.</summary>
    private sealed class ResizingNativeApi : IPhysicalWindowNativeApi
    {
        private PhysicalWindowBounds _bounds;
        internal double Ratio { get; set; } = 1.5;
        public bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags)
        {
            _bounds = new PhysicalWindowBounds(x, y, (int)Math.Round(width * Ratio), (int)Math.Round(height * Ratio));
            return true;
        }
        public bool GetWindowRect(IntPtr hwnd, out PhysicalWindowBounds bounds) { bounds = _bounds; return true; }
        public int GetLastError() => 0;
    }

    private static Dictionary<IntPtr, PhysicalWindowBounds> MoveAlong(NativeCaptureOverlay overlay, PointD[] path, Action<PointD> setCursor)
    {
        var bounds = new Dictionary<IntPtr, PhysicalWindowBounds>();
        foreach (PointD point in path)
        {
            // A failed selector closes itself and drops its chrome; the caller's assertions report it.
            if (overlay.IsClosed) return bounds;
            setCursor(point);
            _ = SendMessage(overlay.Handle, 0x0200, IntPtr.Zero, IntPtr.Zero);
            // The hover tab (1 ms) and DPI repair work run between hand-moved pointer samples.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            do Pump(); while (elapsed.ElapsedMilliseconds < 20);
        }
        if (overlay.IsClosed || overlay.Chrome is null) return bounds;
        foreach (System.Windows.Window window in overlay.Chrome.Windows)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (window.IsVisible && GetWindowRect(hwnd, out NativeRect r)) bounds[hwnd] = PhysicalWindowBounds.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
        }
        return bounds;
    }

    private static NativeCaptureOverlay Create(Func<PointD> cursor, bool recording)
    {
        int width = (int)Desktop.Width, height = (int)Desktop.Height;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        var overlay = new NativeCaptureOverlay(Desktop, false, true)
        {
            PlaceCursor = _ => false,
            ReadCursor = cursor,
            ReadMonitors = () => Monitors,
            SourceWindows =
            [
                new WindowCandidate(new IntPtr(90), new RectD(300, 150, 400, 300), "Straddling"),
                new WindowCandidate(new IntPtr(91), new RectD(650, 250, 250, 200), "Right"),
                new WindowCandidate(new IntPtr(99), Desktop, "Desktop", ClassName: "Progman"),
            ],
            DetectWindows = true,
            ShowKeyPanel = !recording,
            OffersOtherActions = true,
            RecordingSelection = recording,
        };
        overlay.AttachFrame(new FrozenFrame(bitmap, Desktop, null, 0));
        return overlay;
    }

    /// <summary>
    /// Replays a suggested rectangle that keeps replacing the request on real chrome HWNDs: every resize the
    /// selector requests lands at 5/4 of its size, so the strict read-back misses on all three attempts.
    /// </summary>
    private sealed class SuggestedRectangle
    {
        private readonly List<HwndSource> _sources = [];
        internal bool Active { get; set; }
        internal int Resizes { get; private set; }

        internal void Attach(IEnumerable<System.Windows.Window> windows)
        {
            foreach (System.Windows.Window window in windows)
                if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is { } source)
                {
                    source.AddHook(Hook);
                    _sources.Add(source);
                }
        }

        internal void Detach()
        {
            foreach (HwndSource source in _sources) if (!source.IsDisposed) source.RemoveHook(Hook);
            _sources.Clear();
        }

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WM_WINDOWPOSCHANGING: once a move lands Windows reports an unchanged size as SWP_NOSIZE, so the
            // requested size is rewritten before it lands, as WPF's suggested rectangle replaces it.
            if (message != 0x0046 || !Active) return IntPtr.Zero;
            WindowPosition position = Marshal.PtrToStructure<WindowPosition>(lParam);
            if ((position.Flags & 0x0001) != 0 || position.Width < 1) return IntPtr.Zero; // SWP_NOSIZE
            Resizes++;
            position.Width = position.Width * 5 / 4;
            position.Height = position.Height * 5 / 4;
            Marshal.StructureToPtr(position, lParam, false);
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public IntPtr Window, After; public int X, Y, Width, Height; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
