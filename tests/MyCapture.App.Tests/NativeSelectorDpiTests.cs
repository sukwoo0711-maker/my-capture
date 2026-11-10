using System.Runtime.InteropServices;
using System.Windows;
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

public sealed class NativeSelectorDpiTests
{
    private static readonly RectD Desktop = new(-800, 0, 2400, 800);
    private static readonly MonitorInfo[] Monitors =
    [
        new("left", new RectD(-800, 0, 800, 800), new RectD(-800, 0, 800, 760), 96, true),
        new("middle", new RectD(0, 0, 800, 800), new RectD(0, 0, 800, 760), 120, false),
        new("right", new RectD(800, 0, 800, 800), new RectD(800, 0, 800, 760), 144, false),
    ];

    [Fact]
    public void PointerChromeCrossesMonitorSeamsAtThePointersDpiWithoutMovingTheBar() => StaTestHost.Run(() =>
    {
        using var dpi = TestDpiContext.AtPerMonitorDpi();
        int enumerations = 0;
        using var owner = Owner(() => { enumerations++; return Monitors; });
        using var chrome = new NativeSelectorChrome(owner, true, false);
        chrome.Show();
        PumpUntil(() => owner.IsChromeReady);
        RectD bar = Bounds(chrome.Windows[0]), guide = Bounds(chrome.Windows[2]);
        int initialEnumerations = enumerations;
        foreach ((PointD pointer, double scale) in new[]
        {
            (new PointD(-500, 200), 1d), (new PointD(300, 200), 1.25),
            (new PointD(1100, 200), 1.5), (new PointD(300, 200), 1.25), (new PointD(-500, 200), 1d),
        })
        {
            Int32Rect selection = SelectionNear(pointer);
            chrome.Update(pointer, selection);
            Pump();
            Assert.Equal(bar, Bounds(chrome.Windows[0]));
            Assert.Equal(guide, Bounds(chrome.Windows[2]));
            AssertReadoutGeometry(chrome, pointer, selection, scale);
            AssertMagnifierGeometry(chrome, pointer, scale);
        }
        Assert.Equal(initialEnumerations, enumerations);
    });

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(799, 1.25)]
    [InlineData(1599, 1.5)]
    public void PointerChromeClampsItsScaledBoundsInsideEachMonitorsWorkArea(double x, double scale) => StaTestHost.Run(() =>
    {
        using var owner = Owner(() => Monitors);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        chrome.Show();
        PumpUntil(() => owner.IsChromeReady);
        var pointer = new PointD(x, 750);
        Int32Rect selection = SelectionNear(pointer);
        chrome.Update(pointer, selection);
        Pump();
        AssertReadoutGeometry(chrome, pointer, selection, scale);
        AssertMagnifierGeometry(chrome, pointer, scale);
        double right = x + 1;
        Assert.True(Bounds(chrome.Windows[1]).Right <= right - 8 * scale + 1);
        Assert.True(Bounds(chrome.Windows[1]).Bottom <= 760 - 8 * scale + 1);
        Assert.True(Bounds(chrome.Windows[3]).Right < right);
        Assert.True(Bounds(chrome.Windows[3]).Bottom < 760);
    });

    [Fact]
    public void DpiTransitionsNeverShowOrRestackPointerWindowsAndAnUnchangedPointerDoesNotMoveThem() => StaTestHost.Run(() =>
    {
        using var owner = Owner(() => Monitors);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        chrome.Show();
        PumpUntil(() => owner.IsChromeReady);
        int changes = 0;
        var flags = new List<uint>();
        HwndSourceHook hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == 0x0046)
            {
                changes++;
                flags.Add(Marshal.PtrToStructure<WindowPosition>(lParam).Flags);
            }
            return IntPtr.Zero;
        };
        HwndSource[] sources = [HwndSource.FromHwnd(new WindowInteropHelper(chrome.Windows[1]).Handle)!,
            HwndSource.FromHwnd(new WindowInteropHelper(chrome.Windows[3]).Handle)!];
        foreach (HwndSource source in sources) source.AddHook(hook);
        try
        {
            chrome.Update(new PointD(1100, 200), SelectionNear(new PointD(1100, 200)));
            Pump();
            Assert.NotEmpty(flags);
            Assert.All(flags, value =>
            {
                Assert.NotEqual(0u, value & 0x0004); // SWP_NOZORDER
                Assert.Equal(0u, value & 0x0040); // No SWP_SHOWWINDOW on the input path.
            });
            int afterTransition = changes;
            for (int index = 0; index < 20; index++) chrome.Update(new PointD(1100, 200), SelectionNear(new PointD(1100, 200)));
            Assert.Equal(afterTransition, changes);
        }
        finally { foreach (HwndSource source in sources) source.RemoveHook(hook); }
    });

    [Theory]
    [InlineData(120)]
    [InlineData(144)]
    public void RetainedBarKeepsItsLogicalSizeWhenMonitorAndHostDpiDiffer(uint monitorDpi) => StaTestHost.Run(() =>
    {
        MonitorInfo[] monitors = [new("injected", Desktop, Desktop, monitorDpi, true)];
        using var first = Owner(() => monitors);
        using var chrome = new NativeSelectorChrome(first, false, false);
        chrome.Show();
        PumpUntil(() => first.IsChromeReady);
        Size originalSize = chrome.LogicalSizeForTest(chrome.Windows[0]);
        RectD originalBounds = chrome.BoundsForTest(chrome.Windows[0]);
        for (int lease = 0; lease < 3; lease++)
        {
            chrome.Detach();
            using var next = Owner(() => monitors);
            try
            {
                chrome.Bind(next, false);
                chrome.Show();
                PumpUntil(() => next.IsChromeReady);
                Assert.Equal(originalSize, chrome.LogicalSizeForTest(chrome.Windows[0]));
                Assert.Equal(originalBounds, chrome.BoundsForTest(chrome.Windows[0]));
                Assert.Equal(originalBounds, Bounds(chrome.Windows[0]));
            }
            finally { chrome.Detach(); }
        }
        using var changed = Owner(() => monitors);
        changed.TextCaptureAvailable = true;
        try
        {
            chrome.Bind(changed, false);
            chrome.Show();
            PumpUntil(() => changed.IsChromeReady);
            Size resized = chrome.LogicalSizeForTest(chrome.Windows[0]);
            Assert.True(resized.Width > originalSize.Width, "The rebound bar must measure the newly available text command.");
            Assert.Equal(originalSize.Height, resized.Height);
            Assert.Equal(chrome.BoundsForTest(chrome.Windows[0]), Bounds(chrome.Windows[0]));
        }
        finally { chrome.Detach(); }
    });

    private static void AssertMagnifierGeometry(NativeSelectorChrome chrome, PointD pointer, double scale)
    {
        NativeSelectorMagnifier magnifier = chrome.MagnifierForTest!;
        Assert.Equal(150, magnifier.ImageSizeForTest.Width * scale, 8);
        Assert.Equal(150, magnifier.ImageSizeForTest.Height * scale, 8);
        MonitorInfo monitor = Monitors.Single(item => pointer.X >= item.Bounds.Left && pointer.X < item.Bounds.Right);
        double height = 150 + magnifier.FooterHeightForTest * scale, margin = 8 * scale;
        double x = pointer.X + 20, y = pointer.Y + 22;
        if (x + 150 > monitor.WorkArea.Right - margin) x = pointer.X - 20 - 150;
        if (y + height > monitor.WorkArea.Bottom - margin) y = pointer.Y - 22 - height;
        x = Math.Clamp(x, monitor.WorkArea.Left + margin, monitor.WorkArea.Right - 150 - margin);
        y = Math.Clamp(y, monitor.WorkArea.Top + margin, monitor.WorkArea.Bottom - height - margin);
        double inset = Math.Ceiling(3 * scale);
        RectD expected = new RectD(Math.Round(x) - inset, Math.Round(y) - inset, 150 + 6 * scale, height + 6 * scale).ToPixelBounds();
        AssertGeometry(chrome, chrome.Windows[3], scale, expected);
        RectD image = chrome.MagnifierImageBoundsForTest;
        Assert.Equal(Math.Round(image.Left), image.Left, 8);
        Assert.Equal(Math.Round(image.Top), image.Top, 8);
    }

    private static Int32Rect SelectionNear(PointD pointer) => new((int)(pointer.X - Desktop.Left - 100), (int)pointer.Y - 80, 100, 80);

    private static void AssertReadoutGeometry(NativeSelectorChrome chrome, PointD pointer, Int32Rect selection, double scale)
    {
        MonitorInfo monitor = Monitors.Single(item => pointer.X >= item.Bounds.Left && pointer.X < item.Bounds.Right);
        Size logical = chrome.LogicalSizeForTest(chrome.Windows[1]);
        double width = logical.Width * scale, height = logical.Height * scale, edge = 8 * scale;
        double x = Math.Clamp(Desktop.Left + selection.X, monitor.WorkArea.Left + edge, monitor.WorkArea.Right - width - edge);
        double y = selection.Y - height - edge;
        if (y < monitor.WorkArea.Top + edge) y = Math.Min(monitor.WorkArea.Bottom - height - edge, selection.Y + selection.Height + edge);
        RectD expected = new RectD(x, y, width, height).ToPixelBounds();
        Assert.True(chrome.IsReadoutVisibleForTest);
        AssertGeometry(chrome, chrome.Windows[1], scale, expected);
    }

    private static void AssertGeometry(NativeSelectorChrome chrome, Window window, double scale, RectD expected)
    {
        Assert.Equal(expected, chrome.BoundsForTest(window));
        Assert.Equal(expected, Bounds(window));
        Assert.Equal(scale, chrome.ScaleForTest(window));
        FrameworkElement content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        ScaleTransform transform = Assert.IsType<ScaleTransform>(content.LayoutTransform);
        Matrix host = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.CompositionTarget.TransformToDevice;
        Assert.Equal(scale, transform.ScaleX * host.M11, 8);
        Assert.Equal(scale, transform.ScaleY * host.M22, 8);
        Assert.Equal(expected.Width, window.Width * host.M11, 8);
        Assert.Equal(expected.Height, window.Height * host.M22, 8);
    }

    private static NativeCaptureOverlay Owner(Func<IReadOnlyList<MonitorInfo>> monitors)
    {
        var bitmap = BitmapSource.Create(2400, 800, 96, 96, PixelFormats.Bgr32, null, new byte[2400 * 800 * 4], 2400 * 4);
        bitmap.Freeze();
        var owner = new NativeCaptureOverlay(Desktop, false, true)
        {
            PlaceCursor = _ => false,
            ReadCursor = () => new PointD(-500, 200), ReadMonitors = monitors,
        };
        owner.AttachFrame(new FrozenFrame(bitmap, Desktop, null, 0));
        return owner;
    }

    private static RectD Bounds(Window window)
    {
        Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out NativeRect rect));
        return new RectD(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public IntPtr Window, After; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
