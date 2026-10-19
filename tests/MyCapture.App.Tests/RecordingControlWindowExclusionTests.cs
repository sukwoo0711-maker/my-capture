using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingControlWindowExclusionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpaqueRegionMatchesFrameAndPaletteBeforeAndDuringRecording(bool fullScreen) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(false);
        MonitorInfo monitor = MonitorEnumerator.GetFromPoint(new PointD(100, 100));
        RectD work = monitor.WorkArea;
        RectD region = fullScreen ? monitor.Bounds
            : new RectD(work.Center.X - 80, work.Center.Y - 50, 160, 100).ToPixelBounds();
        using var received = new ManualResetEventSlim();
        using var recorder = new RegionRecorder(
            new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), false,
                backend: RecordingCaptureBackend.Gdi),
            options => new FrameSink(options.Width, options.Height, received), NullLogger.Instance);
        var window = CreateWindow(region, () => recorder);
        bool finished = false;
        Exception? failure = null;
        window.RecordingFinished += (_, _) => finished = true;
        window.Failed += (_, args) => failure = args.Exception;
        try
        {
            window.Show();
            window.UpdateLayout();
            NativeExclusion.AssertWindow(window, layered: false);
            Assert.True(window.IsCaptureExcluded);
            Assert.Equal(fullScreen, NativeExclusion.Field<bool>(window, "_paletteOverlapsRegion"));
            AssertRegionMatchesVisuals(window);
            Rect before = ScreenBounds(NativeExclusion.Field<Border>(window, "_regionFrame"));
            Assert.True(before.Width > region.Width);

            window.PrimaryButtonForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsRecording, "Start did not reach the capture worker: " + NativeExclusion.Field<Exception?>(recorder, "_failure"));
            PumpUntil(() => received.IsSet || failure is not null || !window.IsRecording);
            Assert.Null(failure);
            Assert.True(received.IsSet, "The capture worker did not deliver a frame: " + NativeExclusion.Field<Exception?>(recorder, "_failure"));
            Assert.True(window.IsRecording);
            Assert.Null(failure);
            window.UpdateLayout();
            AssertRegionMatchesVisuals(window);
            Rect during = ScreenBounds(NativeExclusion.Field<Border>(window, "_regionFrame"));
            Assert.Equal(new Rect(region.X, region.Y, region.Width, region.Height), during);
            Assert.True(window.IsCaptureExcluded);
            window.RequestStop();
            PumpUntil(() => finished || failure is not null);
            Assert.Null(failure);
        }
        finally
        {
            if (window.IsRecording)
            {
                window.RequestStop();
                PumpUntil(() => finished || failure is not null);
            }
            window.CompleteAndClose();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcludedStartGateAndRecordingRegionUpdateBeforeDesktopReadback(bool fullScreen) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(false);
        RectD work = MonitorEnumerator.GetFromPoint(new PointD(100, 100)).WorkArea;
        RectD region = fullScreen ? MonitorEnumerator.GetFromPoint(work.Center).Bounds
            : new RectD(work.Center.X - 80, work.Center.Y - 50, 160, 100).ToPixelBounds();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var expectedFailure = new InvalidOperationException("Test stops at the encoder boundary before desktop readback.");
        using var recorder = new RegionRecorder(
            new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), false,
                backend: RecordingCaptureBackend.Gdi),
            _ =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Encoder boundary was not released.");
                throw expectedFailure;
            }, NullLogger.Instance);
        var window = CreateWindow(region, () => recorder);
        Exception? failure = null;
        window.Failed += (_, args) => failure = args.Exception;
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(window.IsCaptureExcluded);
            Assert.Equal(fullScreen, NativeExclusion.Field<bool>(window, "_paletteOverlapsRegion"));
            AssertRegionMatchesVisuals(window);
            Assert.True(ScreenBounds(NativeExclusion.Field<Border>(window, "_regionFrame")).Width > region.Width);
            window.PrimaryButtonForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "The start gate did not reach the recorder's encoder factory.");
            Assert.True(window.IsRecording);
            window.UpdateLayout();
            NativeExclusion.AssertWindow(window, layered: false);
            AssertRegionMatchesVisuals(window);
            Assert.Equal(new Rect(region.X, region.Y, region.Width, region.Height),
                ScreenBounds(NativeExclusion.Field<Border>(window, "_regionFrame")));
            window.RequestStop();
            release.Set();
            PumpUntil(() => failure is not null);
            Assert.Same(expectedFailure, failure!.InnerException);
        }
        finally
        {
            release.Set();
            if (window.IsRecording)
            {
                window.RequestStop();
                PumpUntil(() => failure is not null);
            }
            window.CompleteAndClose();
        }
    });

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    [InlineData(1.5, false)]
    [InlineData(2.0, false)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(1.5, true)]
    [InlineData(2.0, true)]
    public void OpaquePixelBoundsPreservePaletteSideAtEveryDpi(double scale, bool above)
    {
        var work = new RectD(0, 0, 1920, 1040);
        var capture = new RectD(880, above ? work.Bottom - 120 : 100, 160, 100);
        RecordingControlLayout planned = RecordingControlLayoutPlanner.Plan(capture, work, scale, 6, 56, 560);
        Assert.False(planned.PaletteOverlapsRegion);
        Assert.True(above ? planned.PaletteBounds.Bottom <= planned.FrameBounds.Top
            : planned.PaletteBounds.Top >= planned.FrameBounds.Bottom);
        RecordingControlLayout rounded = RecordingControlLayoutPlanner.RoundOpaqueBounds(planned);
        double gap = above ? rounded.FrameBounds.Top - rounded.PaletteBounds.Bottom
            : rounded.PaletteBounds.Top - rounded.FrameBounds.Bottom;
        Assert.True(gap >= 0, $"DPI {scale}, above={above}, frame={rounded.FrameBounds}, palette={rounded.PaletteBounds}, gap={gap}px");
        Assert.Equal(planned.FrameBounds.ToPixelBounds(), rounded.FrameBounds);
        Assert.Equal(rounded.WindowBounds, rounded.WindowBounds.ToPixelBounds());
        Assert.Equal(Math.Min(rounded.FrameBounds.Left, rounded.PaletteBounds.Left), rounded.WindowBounds.Left);
        Assert.Equal(Math.Min(rounded.FrameBounds.Top, rounded.PaletteBounds.Top), rounded.WindowBounds.Top);
        Assert.Equal(Math.Max(rounded.FrameBounds.Right, rounded.PaletteBounds.Right), rounded.WindowBounds.Right);
        Assert.Equal(Math.Max(rounded.FrameBounds.Bottom, rounded.PaletteBounds.Bottom), rounded.WindowBounds.Bottom);
    }

    [Fact]
    public void RegionIsRecomputedWhenThePaletteFlipsAndTheRegionMoves() => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(false);
        RectD work = MonitorEnumerator.GetFromPoint(new PointD(100, 100)).WorkArea;
        var window = CreateWindow(new RectD(work.Center.X - 80, work.Top + 100, 160, 100));
        IntPtr initial = CreateRectRgn(0, 0, 0, 0);
        IntPtr moved = CreateRectRgn(0, 0, 0, 0);
        try
        {
            window.Show();
            window.UpdateLayout();
            AssertRegionMatchesVisuals(window);
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            Assert.NotEqual(0, GetWindowRgn(hwnd, initial));
            Border strip = NativeExclusion.Field<Border>(window, "_controlStrip");
            Border frame = NativeExclusion.Field<Border>(window, "_regionFrame");
            Assert.True(ScreenBounds(strip).Top >= ScreenBounds(frame).Bottom);
            var next = new RectD(work.Center.X + 40, work.Bottom - 120, 160, 100);
            typeof(RecordingControlWindow).GetField("_screenRegion", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, next);
            typeof(RecordingControlWindow).GetMethod("ApplyPhysicalLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true]);
            window.UpdateLayout();
            AssertRegionMatchesVisuals(window);
            Assert.True(ScreenBounds(strip).Bottom <= ScreenBounds(frame).Top);
            Assert.NotEqual(0, GetWindowRgn(hwnd, moved));
            Assert.False(EqualRgn(initial, moved));
        }
        finally { window.Close(); DeleteObject(initial); DeleteObject(moved); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DpiChangesDuringLayoutCoalesceIntoOneLoadedRegionRefresh(bool positionHwnd) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(false);
        RectD work = MonitorEnumerator.GetFromPoint(new PointD(100, 100)).WorkArea;
        var initial = new RectD(work.Center.X - 80, work.Center.Y - 50, 160, 100);
        var final = new RectD(initial.X + 40, initial.Y + 20, initial.Width, initial.Height);
        var window = CreateWindow(initial);
        MethodInfo apply = typeof(RecordingControlWindow).GetMethod("ApplyPhysicalLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo dpiChanged = typeof(RecordingControlWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        FieldInfo screenRegion = typeof(RecordingControlWindow).GetField("_screenRegion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var leftChanged = DependencyPropertyDescriptor.FromProperty(Window.LeftProperty, typeof(RecordingControlWindow));
        bool notifiedDuringLayout = false;
        EventHandler onLeftChanged = (_, _) =>
        {
            if (notifiedDuringLayout) return;
            Assert.True(NativeExclusion.Field<bool>(window, "_applyingPhysicalLayout"));
            notifiedDuringLayout = true;
            // Reenter at the same boundary as a monitor DPI change caused by Left/Top.
            // Change the final geometry too, so a dropped notification leaves a stale region.
            screenRegion.SetValue(window, final);
            dpiChanged.Invoke(window, [window, null]);
            dpiChanged.Invoke(window, [window, null]);
            Assert.True(NativeExclusion.Field<bool>(window, "_dpiChangedDuringPhysicalLayout"));
        };
        try
        {
            window.Show();
            NativeExclusion.Drain();
            window.Left += 10;
            leftChanged.AddValueChanged(window, onLeftChanged);
            apply.Invoke(window, [positionHwnd]);
            Assert.True(notifiedDuringLayout);
            var pending = NativeExclusion.Field<DispatcherOperation>(window, "_dpiLayoutOperation");
            Assert.Equal(DispatcherPriority.Loaded, pending.Priority);
            Assert.Equal(DispatcherOperationStatus.Pending, pending.Status);
            Assert.False(NativeExclusion.Field<bool>(window, "_dpiChangedDuringPhysicalLayout"));
            // More DPI messages before Loaded must reuse the one pending refresh.
            dpiChanged.Invoke(window, [window, null]);
            dpiChanged.Invoke(window, [window, null]);
            Assert.Same(pending, NativeExclusion.Field<DispatcherOperation>(window, "_dpiLayoutOperation"));
            NativeExclusion.Drain();
            Assert.Equal(DispatcherOperationStatus.Completed, pending.Status);
            Assert.Same(pending, NativeExclusion.Field<DispatcherOperation>(window, "_dpiLayoutOperation"));
            window.UpdateLayout();
            AssertRegionMatchesVisuals(window);
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            RectD expectedFrame = RecordingControlLayoutPlanner.Plan(final, work, scale, 6, 56, 560).FrameBounds.ToPixelBounds();
            Assert.Equal(new Rect(expectedFrame.X, expectedFrame.Y, expectedFrame.Width, expectedFrame.Height),
                ScreenBounds(NativeExclusion.Field<Border>(window, "_regionFrame")));
        }
        finally
        {
            leftChanged.RemoveValueChanged(window, onLeftChanged);
            window.Close();
        }
    });

    [Fact]
    public void FullScreenStartRemainsBlockedIfAffinityWasNotApplied() => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(false);
        bool factoryReached = false;
        var window = CreateWindow(MonitorEnumerator.GetFromPoint(new PointD(100, 100)).Bounds,
            () => { factoryReached = true; throw new InvalidOperationException(); });
        try
        {
            window.Show();
            Assert.True(window.IsCaptureExcluded);
            typeof(RecordingControlWindow).GetField("_captureExclusionApplied", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            window.PrimaryButtonForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(factoryReached);
            Assert.False(window.IsRecording);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModePreservesPaletteContentAndLayeredModeHasNoWindowRegion(bool supported) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(supported);
        using var language = UiText.UseLanguage("ko-KR");
        var window = CreateWindow(new RectD(100, 100, 320, 180));
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            LoadTheme(window);
            window.Show();
            window.PrimaryButtonForTest.Style = (Style)window.FindResource("Button.Primary");
            window.CancelButtonForTest.Style = (Style)window.FindResource("Button.Ghost");
            window.MoreButtonForTest.Style = (Style)window.FindResource("Button.Ghost");
            window.UpdateLayout();
            NativeExclusion.AssertWindow(window, supported);
            Assert.Equal(NativeExclusion.ExpectsSuccess(supported), window.IsCaptureExcluded);
            Border strip = NativeExclusion.Field<Border>(window, "_controlStrip");
            Border frame = NativeExclusion.Field<Border>(window, "_regionFrame");
            Assert.Equal(new CornerRadius(supported ? 28 : 0), strip.CornerRadius);
            Assert.Equal(new CornerRadius(supported ? 2 : 0), frame.CornerRadius);
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            RectD capture = NativeExclusion.Field<RectD>(window, "_screenRegion");
            RectD work = MonitorEnumerator.GetFromPoint(capture.Center).WorkArea;
            RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(capture, work, scale, 6, 56, 560);
            RectD palette = (supported ? layout : RecordingControlLayoutPlanner.RoundOpaqueBounds(layout)).PaletteBounds;
            Assert.Equal(palette.Width / scale, strip.Width, 3);
            Assert.Equal(palette.Height / scale, strip.Height, 3);
            var content = Assert.IsType<Border>(Assert.IsType<Viewbox>(strip.Child).Child);
            Assert.True(ScreenBounds(strip).Contains(ScreenBounds(content)), "The palette content must fit inside its visible bounds.");
            Assert.Equal("녹화 시작", RecordingControlWindow.ButtonLabel(window.PrimaryButtonForTest));
            if (supported) Assert.Equal(0, GetWindowRgn(new WindowInteropHelper(window).Handle, region));
            else { Assert.Null(strip.Effect); AssertRegionMatchesVisuals(window); }
            SavePalette(strip, supported);
        }
        finally { window.Close(); DeleteObject(region); }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void NativeRegionUsesWindowRelativePhysicalPixelsAtDifferentDpi(double scale) => StaTestHost.Run(() =>
    {
        var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            int border = (int)Math.Round(6 * scale);
            var bounds = new RectD(-1600, -900, 900, 700);
            var frame = new RectD(-1500, -850, 400, 300);
            var palette = new RectD(-1560, -550, 560, 56);
            RecordingControlWindowRegion.Apply(hwnd, bounds, frame, palette, border);
            Assert.NotEqual(0, GetWindowRgn(hwnd, region));
            Assert.True(PtInRegion(region, 100, 50));
            Assert.True(PtInRegion(region, 100 + border - 1, 150));
            Assert.False(PtInRegion(region, 100 + border, 150));
            Assert.True(PtInRegion(region, 499, 150));
            Assert.False(PtInRegion(region, 500, 150));
            Assert.False(PtInRegion(region, 300, 200));
            Assert.True(PtInRegion(region, 40, 350));
            Assert.True(PtInRegion(region, 599, 405));
            Assert.False(PtInRegion(region, 600, 405));
            Assert.False(PtInRegion(region, 10, 10));
        }
        finally { window.Close(); DeleteObject(region); }
    });

    [Fact]
    public void FailedSetWindowRegionReleasesAllAllocatedGdiRegions()
    {
        using Process process = Process.GetCurrentProcess();
        // Warm the native path before taking the process-wide GDI baseline.
        Assert.Throws<Win32Exception>(() => RecordingControlWindowRegion.Apply(IntPtr.Zero,
            new RectD(0, 0, 800, 600), new RectD(50, 50, 400, 300), new RectD(0, 400, 560, 56), 6));
        uint before = GetGuiResources(process.Handle, 0);
        for (int i = 0; i < 100; i++)
            Assert.Throws<Win32Exception>(() => RecordingControlWindowRegion.Apply(IntPtr.Zero,
                new RectD(0, 0, 800, 600), new RectD(50, 50, 400, 300), new RectD(0, 400, 560, 56), 6));
        Assert.Equal(before, GetGuiResources(process.Handle, 0));
    }

    private static RecordingControlWindow CreateWindow(RectD region, Func<RegionRecorder>? factory = null) => new(
        region, new RecordingSettings { UseStartDelay = false, FrameRate = RecordingFrameRate.Fps10 },
        factory ?? (() => throw new InvalidOperationException("No recording expected.")),
        () => Path.Combine(Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT") ?? Path.GetTempPath(), "no-file-encoder.mp4"),
        NullLogger.Instance) { ShowActivated = false };

    private static void AssertRegionMatchesVisuals(RecordingControlWindow window)
    {
        Border frame = NativeExclusion.Field<Border>(window, "_regionFrame");
        Border palette = NativeExclusion.Field<Border>(window, "_controlStrip");
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        Assert.True(GetWindowRect(hwnd, out NativeRect bounds));
        Rect outer = ScreenBounds(frame);
        double thickness = Math.Round(frame.BorderThickness.Left * VisualTreeHelper.GetDpi(frame).DpiScaleX);
        Rect inner = new(outer.Left + thickness, outer.Top + thickness,
            Math.Max(0, outer.Width - 2 * thickness), Math.Max(0, outer.Height - 2 * thickness));
        IntPtr actual = CreateRectRgn(0, 0, 0, 0);
        IntPtr expected = CreateRectRgn(0, 0, 0, 0);
        try
        {
            Assert.NotEqual(0, GetWindowRgn(hwnd, actual));
            // Build the expectation from the four rendered border strips (independently of
            // production's outer-minus-inner construction) and the rendered palette bounds.
            Rect[] pieces =
            [
                new(outer.Left, outer.Top, outer.Width, thickness),
                new(outer.Left, outer.Bottom - thickness, outer.Width, thickness),
                new(outer.Left, inner.Top, thickness, inner.Height),
                new(outer.Right - thickness, inner.Top, thickness, inner.Height),
                ScreenBounds(palette),
            ];
            foreach (Rect piece in pieces)
            {
                IntPtr part = CreateRectRgn((int)Math.Round(piece.Left - bounds.Left), (int)Math.Round(piece.Top - bounds.Top),
                    (int)Math.Round(piece.Right - bounds.Left), (int)Math.Round(piece.Bottom - bounds.Top));
                try { Assert.NotEqual(0, CombineRgn(expected, expected, part, 2)); }
                finally { DeleteObject(part); }
            }
            Assert.True(EqualRgn(expected, actual), "The HWND region differs from the visible frame ring plus palette.");
            Assert.False(PtInRegion(actual, (int)(inner.Left + inner.Width / 2 - bounds.Left),
                (int)(inner.Top + inner.Height / 2 - bounds.Top)), "The recording interior must be a click-through hole.");
        }
        finally { DeleteObject(actual); DeleteObject(expected); }
    }

    private static Rect ScreenBounds(FrameworkElement element) => new(
        element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));

    private static void PumpUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            NativeExclusion.Drain();
            Thread.Sleep(10);
        }
        Assert.True(ready());
    }

    private static void LoadTheme(Window window)
    {
        XElement? combined = null;
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(RecordingControlWindowExclusionTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            combined ??= new XElement(dictionary.Name);
            foreach (XAttribute declaration in dictionary.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
                    && !value.Contains(";assembly=", StringComparison.Ordinal)) value += ";assembly=MyCapture";
                combined.SetAttributeValue(declaration.Name, value);
            }
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(combined!.ToString(SaveOptions.DisableFormatting)));
    }

    private static void SavePalette(Border strip, bool supported)
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_LAYERED_EVIDENCE");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        DpiScale dpi = VisualTreeHelper.GetDpi(strip);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(strip.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(strip.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(strip) { Stretch = Stretch.Fill }, null,
                new Rect(0, 0, strip.ActualWidth, strip.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(directory, supported ? "palette-as-is.png" : "palette-to-be.png"));
        encoder.Save(file);
    }

    private sealed class FrameSink(int width, int height, ManualResetEventSlim received) : IVideoEncoder
    {
        public int Width => width;
        public int Height => height;
        public void WriteFrame(in EncoderFrame frame) => received.Set();
        public void Complete() { }
        public void Dispose() { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualRgn(IntPtr first, IntPtr second);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr region);
}
