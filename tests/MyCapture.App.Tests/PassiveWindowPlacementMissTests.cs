using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A notification and the recording watermark are placed on exact pixels, and the bounds are read
/// back. When Windows puts the window somewhere else on every attempt, the window stays and the miss
/// is reported once: thrown from a layout event, it would reach the application's error window.
/// </summary>
public sealed class PassiveWindowPlacementMissTests
{
    [Fact]
    public void ANotificationThatMissesItsBoundsStaysOpenAndReportsItOnce() => StaTestHost.Run(() =>
    {
        using TestDpiContext dpi = TestDpiContext.At96Dpi();
        using var mode = new ExclusionMode(true);
        using var language = UiText.UseLanguage("ko-KR");
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var monitor = new MonitorInfo("TEST", new RectD(-20000, -20000, 1080, 1920), new RectD(-20000, -20000, 1080, 1880), 96, false);
        var reported = new List<Exception>();
        var presenter = new ThemedShellPresenter(() => false, () => false)
        {
            NotificationMonitor = () => monitor,
            NotificationPlacementMissed = reported.Add,
        };
        try
        {
            presenter.ShowNotification("MyCapture", "준비됨", TrayBalloonKind.Information);
            Window card = Assert.IsType<Window>(presenter.NotificationWindowForTest);
            ControlStateMotionTests.Drain(card);
            Assert.True(card.IsVisible);
            Assert.Empty(reported);

            int displacedLeft;
            using (var displaced = new DisplacedMoves(card))
            {
                // A taller card is placed again from SizeChanged, and lands beside its place every time.
                Grow(card);
                Assert.True(displaced.Moves >= 3, $"The placement moved the card {displaced.Moves} times.");
                Assert.Contains("after 3 attempts", Assert.IsType<InvalidOperationException>(Assert.Single(reported)).Message);
                Assert.True(card.IsVisible);
                Assert.True(presenter.HasNotification);

                // It misses again; the notification's first miss was the one to report.
                Grow(card);
                Assert.Single(reported);
                displacedLeft = Bounds(card).Left;
            }

            // Without the displacement the next placement reaches its bounds.
            Grow(card);
            Assert.Equal(displacedLeft - DisplacedMoves.Offset, Bounds(card).Left);
            Assert.Single(reported);
            Assert.True(card.IsVisible);
        }
        finally
        {
            presenter.Dispose();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    [Fact]
    public void AWatermarkThatMissesItsBoundsStaysAndReportsItOnce() => StaTestHost.Run(() =>
    {
        using TestDpiContext dpi = TestDpiContext.At96Dpi();
        RecordingWatermarkSnapshot snapshot = RecordingWatermarkSnapshot.Create(
            new RecordingWatermarkSettings { Enabled = true, Anchor = StampAnchor.BottomRight },
            RecordingWatermarkTests.Image(Colors.Blue, 500, 250));
        var region = new RectD(-28000, -28000, 800, 450);
        var reported = new List<Exception>();
        var watermark = new RecordingWatermarkWindow(region, 1, snapshot) { PlacementMissed = reported.Add };
        try
        {
            TestDpiContext.Initialize96DpiWindow(watermark);
            watermark.Show();
            Assert.Null(watermark.PlacementMiss);
            Assert.Equal(Pixels(watermark.PhysicalBounds), Bounds(watermark));

            using (var displaced = new DisplacedMoves(watermark))
            {
                // The recorded region moves, and the watermark lands beside its place every time.
                watermark.UpdateRecordingRegion(region with { X = region.X + 40 }, 1);
                Assert.True(displaced.Moves >= 3, $"The placement moved the watermark {displaced.Moves} times.");
                var miss = Assert.IsType<InvalidOperationException>(watermark.PlacementMiss);
                Assert.Contains("after 3 attempts", miss.Message);
                Assert.Same(miss, Assert.Single(reported));
                Assert.True(watermark.IsVisible);

                // It misses again; the first miss stays the one that was reported.
                watermark.UpdateRecordingRegion(region with { X = region.X + 80 }, 1);
                Assert.Same(miss, watermark.PlacementMiss);
                Assert.Single(reported);
            }

            // Without the displacement the next placement reaches its bounds.
            watermark.UpdateRecordingRegion(region, 1);
            Assert.Equal(Pixels(watermark.PhysicalBounds), Bounds(watermark));
            Assert.Single(reported);
        }
        finally { watermark.Close(); }
    });

    // The card sizes its height to its content, so a taller minimum changes its size.
    private static void Grow(Window card)
    {
        double before = card.ActualHeight;
        card.MinHeight = before + 24;
        card.UpdateLayout();
        ControlStateMotionTests.Drain(card);
        Assert.True(card.ActualHeight > before, "The taller minimum did not change the card's size.");
    }

    private static NativeRect Bounds(Window window)
    {
        Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out NativeRect bounds));
        return bounds;
    }

    private static NativeRect Pixels(RectD bounds) =>
        new() { Left = (int)bounds.Left, Top = (int)bounds.Top, Right = (int)bounds.Right, Bottom = (int)bounds.Bottom };

    /// <summary>
    /// Lands every move of a window a few pixels beside where it was asked to go, as Windows does when
    /// it replaces a requested rectangle with one of its own, so a strict read-back misses each time.
    /// </summary>
    private sealed class DisplacedMoves : IDisposable
    {
        internal const int Offset = 7;
        private readonly HwndSource _source;

        internal DisplacedMoves(Window window)
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
                ?? throw new InvalidOperationException("The window has no handle yet.");
            _source.AddHook(Hook);
        }

        internal int Moves { get; private set; }

        public void Dispose()
        {
            if (!_source.IsDisposed) _source.RemoveHook(Hook);
        }

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x0046) return IntPtr.Zero; // WM_WINDOWPOSCHANGING
            WindowPosition position = Marshal.PtrToStructure<WindowPosition>(lParam);
            if ((position.Flags & 0x0002) != 0) return IntPtr.Zero; // SWP_NOMOVE
            Moves++;
            position.X += Offset;
            Marshal.StructureToPtr(position, lParam, false);
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public IntPtr Window, After; public int X, Y, Width, Height; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
