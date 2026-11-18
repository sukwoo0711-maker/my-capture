using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App;

public partial class App
{
    // pin-switch: the automatic check of a lightweight pin's move to its WPF window. Three pins, each
    // switched once: at 100%, at 259% in the middle of a wheel zoom (live frame), and at 259% settled.
    // The screen around each pin is captured before, during and after the hand-over, and every frame
    // is compared with the one before it started.
    private const int SwitchFramesAfter = 12;
    private const int SwitchMargin = 16;
    private const int SwitchChangedThreshold = 8;

    private async Task ProbeLightPinSwitchAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder, string scenario)
    {
        if (_pins is null) throw new InvalidOperationException("Pins unavailable.");
        var cases = new (string Name, int Notches, bool Settle)[]
        {
            ("switch-100", 0, true), ("switch-259-mid-wheel", 10, false), ("switch-259-settled", 10, true),
            // Larger than the screen at rest: the native window holds only the visible part (design section 7),
            // and the WPF window must still take the whole surface.
            ("switch-800-settled", 22, true),
        };
        var results = new List<object>();
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        try
        {
            for (int index = 0; index < cases.Length; index++)
            {
                results.Add(await ProbeOneLightPinSwitchAsync(options, recorder, engine, scenario, index + 1,
                    cases[index].Name, cases[index].Notches, cases[index].Settle));
            }
        }
        finally
        {
            _pins.CloseAll();
            await ProbeWaitAsync(() => !Windows.OfType<PinWindow>().Any(), 15_000, "switch-check pins closed");
            await _pins.LastClosedEncodingForTest;
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(options.Output, "pin-switch-check.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Scenario = scenario,
            MarginPx = SwitchMargin,
            ChangedThreshold = SwitchChangedThreshold,
            Reading = "Each frame is compared with 'before' over the pin rectangle plus the margin (clipped to the screen). "
                + "A jump shows as a changed box that moves or grows at the hand-over; a flash to the desktop as a large "
                + "mean difference in one frame that returns afterwards; an opacity or colour step as a small, uniform "
                + "difference over the pin that stays. The readout of the wheel zoom is part of the pin and shows in the frames.",
            Cases = results,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<object> ProbeOneLightPinSwitchAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        ScreenCaptureEngine engine, string scenario, int index, string name, int notches, bool settle)
    {
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
        const int width = 640, height = 400;
        PinWindow? pin = null;
        _pins!.BeforeShowForTest = shown => pin = shown;
        try
        {
            BitmapSource source = ResidentProbeImages.CachedPattern(width, height, index * 29);
            RectD work = monitor.WorkArea;
            _pins.PinImageAtRegion(source, CaptureProvenance.None, new RectD(
                Math.Round(work.Left + ((work.Width - width) / 2)), Math.Round(work.Top + ((work.Height - height) / 2)), width, height));
        }
        finally { _pins.BeforeShowForTest = null; }
        if (pin?.NativeWindowForTest is not { } native) throw new InvalidOperationException("The switch-check pin did not use the lightweight host.");
        await ProbeWaitAsync(() => !native.IsLanding && !native.IsRevealing, 10_000, "switch-check landing finished");
        pin.ResidentProbeInitialSize();
        if (settle) await ProbeNativePinWaitAsync(native, native.WaitForSettledAsync(), 30_000, "switch-check settled before");
        for (int notch = 0; notch < notches; notch++)
        {
            pin.ResidentProbeWheel(120);
            if (settle || notch < notches - 1) await Task.Yield();
        }
        if (settle && notches > 0)
        {
            await ProbeNativePinWaitAsync(native, native.WaitForSettledAsync(), 30_000, "switch-check settled after wheel");
            await Task.Delay(300);
        }
        ThrowNativePinError(native);
        native.FlushPresentation();

        IntPtr nativeHandle = native.Handle;
        RectD nativeRect = WindowRect(nativeHandle);
        RectD nativeWhole = native.Bounds;
        RectD region = ClipToScreen(new RectD(nativeRect.Left - SwitchMargin, nativeRect.Top - SwitchMargin,
            nativeRect.Width + (2 * SwitchMargin), nativeRect.Height + (2 * SwitchMargin)));
        using ScreenCaptureEngine.CaptureSession session = engine.CreateSession(region, includeCursor: false);
        int stride = session.Width * 4;
        var frames = new List<(string Label, double Ms, byte[] Pixels)>();
        long started = Stopwatch.GetTimestamp();
        void Grab(string label)
        {
            var pixels = new byte[stride * session.Height];
            session.CaptureInto(pixels, stride);
            frames.Add((label, Stopwatch.GetElapsedTime(started).TotalMilliseconds, pixels));
        }

        Grab("before");
        bool liveAtSwitch = native.IsLiveZooming, clippedAtSwitch = native.IsDisplayClipped;
        RectD wpfAtRetire = default, nativeAtRetire = default;
        IntPtr below = IntPtr.Zero, wpfHandle = IntPtr.Zero;
        bool switched = false;
        // The pin activates its WPF window when the native one was active, as on the desktop: the outline
        // of an active pin is emphasized, so a suppressed activation would show as an outline colour step.
        bool nativeWasActive = native.IsActive || NativePinWindowInterop.GetActiveWindow() == nativeHandle;
        pin.BeforeNativeRetiredForTest = (nativeWindow, wpf) =>
        {
            wpfHandle = wpf;
            below = nativeWindow == IntPtr.Zero ? IntPtr.Zero : NativePinWindowInterop.GetWindow(nativeWindow, 2); // GW_HWNDNEXT
            nativeAtRetire = nativeWindow == IntPtr.Zero ? default : WindowRect(nativeWindow);
            wpfAtRetire = WindowRect(wpf);
            Grab("at-retire");
        };
        void Switched(PinSwitchReason _)
        {
            switched = true;
            Grab("after-retire");
        }
        pin.HostSwitched += Switched;
        try
        {
            pin.SwitchToWpf(PinSwitchReason.Command);
            Grab("at-call");
            await ProbeWaitAsync(() => switched, 5000, "switch-check hand-over");
            for (int frame = 1; frame <= SwitchFramesAfter; frame++)
            {
                // Below Render priority, so WPF draws its pending frame first (Task.Yield resumes above it
                // and would show the first WPF frame twelve times).
                await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.Background);
                _ = NativePinWindowInterop.DwmFlush();
                Grab($"after-{frame:00}");
            }
        }
        finally
        {
            pin.HostSwitched -= Switched;
            pin.BeforeNativeRetiredForTest = null;
        }
        RectD wpfAfter = WindowRect(new System.Windows.Interop.WindowInteropHelper(pin).Handle);

        byte[] baseline = frames[0].Pixels;
        var rows = new List<object>();
        for (int i = 0; i < frames.Count; i++)
        {
            (string label, double ms, byte[] pixels) = frames[i];
            string file = $"pin-switch-{index}-{i:00}-{label}.png";
            SaveSwitchFrame(DiagnosticOutputPaths.Child(options.Output, file), pixels, session.Width, session.Height, stride);
            SwitchDifference diff = CompareSwitchFrames(baseline, pixels, session.Width, session.Height, stride);
            rows.Add(new
            {
                Label = label, Milliseconds = ms, File = file, diff.MeanDifference, diff.MaxDifference, diff.ChangedPixels,
                ChangedFraction = diff.ChangedPixels / (double)(session.Width * session.Height), diff.ChangedBox,
            });
        }
        var summary = new
        {
            Case = name,
            Index = index,
            Zoom = pin.State.Zoom,
            LiveFrameAtSwitch = liveAtSwitch,
            ClippedLiveFrameAtSwitch = clippedAtSwitch,
            NativeWasActive = nativeWasActive,
            Region = region,
            NativeBefore = nativeRect,
            NativeAtRetire = nativeAtRetire,
            WpfAtRetire = wpfAtRetire,
            WpfAfter = wpfAfter,
            SameRectangleAtRetire = nativeAtRetire == wpfAtRetire,
            SameRectangleAfter = nativeRect == wpfAfter,
            // The surface the native window showed part of, and whether the WPF window took all of it.
            NativeWholeSurface = nativeWhole,
            WholeSurfaceMatchesWpfAfter = nativeWhole == wpfAfter,
            WpfDirectlyBelowNativeAtRetire = wpfHandle != IntPtr.Zero && below == wpfHandle,
            Frames = rows,
        };
        recorder.ObservePin(scenario, index, "pin-switch-" + name, summary);
        return summary;
    }

    private static RectD WindowRect(IntPtr handle) =>
        handle != IntPtr.Zero && NativePinWindowInterop.GetWindowRect(handle, out var rect)
            ? new RectD(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : default;

    private static RectD ClipToScreen(RectD region)
    {
        RectD screen = MonitorEnumerator.GetVirtualDesktopBounds();
        double left = Math.Max(region.Left, screen.Left), top = Math.Max(region.Top, screen.Top);
        double right = Math.Min(region.Left + region.Width, screen.Left + screen.Width);
        double bottom = Math.Min(region.Top + region.Height, screen.Top + screen.Height);
        return new RectD(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static void SaveSwitchFrame(string path, byte[] pixels, int width, int height, int stride)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    internal readonly record struct SwitchDifference(double MeanDifference, int MaxDifference, long ChangedPixels, RectD? ChangedBox);

    /// <summary>Per-channel absolute difference of two BGRX frames: mean over every channel, maximum, changed pixels and their box.</summary>
    internal static SwitchDifference CompareSwitchFrames(byte[] before, byte[] after, int width, int height, int stride)
    {
        long sum = 0, changed = 0;
        int max = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int offset = row + (x * 4), pixel = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int difference = Math.Abs(before[offset + channel] - after[offset + channel]);
                    sum += difference;
                    pixel = Math.Max(pixel, difference);
                }
                max = Math.Max(max, pixel);
                if (pixel <= SwitchChangedThreshold) continue;
                changed++;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        }
        double mean = width * height == 0 ? 0 : sum / (3d * width * height);
        RectD? box = changed == 0 ? null : new RectD(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return new SwitchDifference(mean, max, changed, box);
    }
}
