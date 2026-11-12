using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App;

public partial class App
{
    private async Task ProbePinMemoryAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        string scenario, ResidentProbePinDiagnostics? pinDiagnostics)
    {
        if (_pins is null || pinDiagnostics is null) throw new InvalidOperationException("Pin diagnostics unavailable.");
        if (options.Has("pin-native") && scenario != "pin-text")
        {
            await ProbeNativePinMemoryAsync(options, recorder, scenario);
            return;
        }
        if (options.Has("pin-switch") || options.Has("pin-light") || options.Has("pin-zoom"))
        {
            // The product setting, on for this diagnostic process only and never saved.
            bool lightweight = _settings!.Pin.LightweightImagePins;
            _settings.Pin.LightweightImagePins = true;
            try
            {
                if (options.Has("pin-switch")) await ProbeLightPinSwitchAsync(options, recorder, scenario);
                else if (options.Has("pin-zoom")) await ProbeLightPinZoomAsync(options, recorder, scenario, pinDiagnostics);
                else if (scenario == "pin-text") await ProbePinMemoryCoreAsync(options, recorder, scenario, pinDiagnostics);
                else await ProbeLightPinMemoryAsync(options, recorder, scenario, pinDiagnostics);
            }
            finally { _settings.Pin.LightweightImagePins = lightweight; }
            return;
        }
        await ProbePinMemoryCoreAsync(options, recorder, scenario, pinDiagnostics);
    }

    private async Task ProbePinMemoryCoreAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        string scenario, ResidentProbePinDiagnostics pinDiagnostics)
    {
        if (_pins is null) throw new InvalidOperationException("Pin diagnostics unavailable.");
        (int width, int height, int count) = scenario switch
        {
            "pin-1600" => (1600, 1000, 10),
            "pin-1080" => (1920, 1080, 10),
            "pin-4k" => (3840, 2160, 4),
            "pin-text" => (0, 0, 10),
            _ => throw new ArgumentException("Unknown pin memory scenario", nameof(scenario)),
        };
        double zoom = options.Has("pin-150") ? 1.5 : 1;
        pinDiagnostics.Scenario = scenario;
        recorder.ObservePin(scenario, 0, "protocol", new
        {
            Version = 2, Count = count, Zoom = zoom, PerPinWaitMs = 300, DwellMs = options.Quick ? 2000 : 5000,
            SurfaceBytes = (long)width * height * 4,
            Text = "Alternating 23-line code/plain text and 17-row tab-separated grid, production ClipboardTextRenderer at monitor DPI. Exact raster/layout dimensions recorded per pin.",
            InitialZoom = "100% before Show; no manager fit-to-work-area. 4K/150% can exceed the monitor; record visible area when comparing.",
        });
        ResidentProbePinMemory.Map previous = recorder.SamplePin(scenario, "baseline", 0, width, height,
            zoom, null, _pins.ClosedRetainedBytes);
        try
        {
            for (int index = 1; index <= count; index++)
            {
                await ProbeOpenMemoryPinAsync(options, recorder, pinDiagnostics, scenario, index, width, height);
                previous = recorder.SamplePin(scenario, "open", index, width, height, zoom, previous, _pins.ClosedRetainedBytes);
            }
            await Task.Delay(options.Quick ? 2000 : 5000);
            previous = recorder.SamplePin(scenario, "dwell", count, width, height, zoom, previous, _pins.ClosedRetainedBytes);
            await ProbePinInteractionsAsync(options, recorder, pinDiagnostics, scenario);
            await pinDiagnostics.DrainAsync();
            previous = recorder.SamplePin(scenario, "after-interactions", count, width, height, zoom, previous, _pins.ClosedRetainedBytes);
        }
        finally
        {
            _pins.BeforeShowForTest = null;
            _pins.CloseAll();
            await ProbeWaitAsync(() => !Windows.OfType<PinWindow>().Any(), 15_000, "all diagnostic pin HWNDs closed");
            await _pins.LastClosedEncodingForTest;
            await ProbeWaitAsync(() => !_pins.IsInteractionActive, 15_000, "all closed pin encodes completed");
        }
        await Task.Delay(options.Quick ? 2000 : 5000);
        previous = recorder.SamplePin(scenario, "closed", 0, width, height, zoom, previous, _pins.ClosedRetainedBytes);
        // Always audit new scenarios, even when the legacy runner does not pass --resident-audit.
        await recorder.AuditAsync(scenario + " closed", force: true);
        recorder.SamplePin(scenario, "audit", 0, width, height, zoom, previous, _pins.ClosedRetainedBytes);
    }

    private async Task ProbeOpenMemoryPinAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        ResidentProbePinDiagnostics diagnostics, string scenario, int index, int width, int height)
    {
        if (_pins is null) throw new InvalidOperationException("Pins unavailable.");
        PinWindow? pin = null;
        diagnostics.NextIndex = index;
        var intervals = new List<double>();
        TimeSpan? lastFrame = null;
        EventHandler rendering = (_, args) =>
        {
            if (pin is not { ResidentProbeLandingActive: true } || args is not RenderingEventArgs frame) return;
            if (lastFrame is { } previous && frame.RenderingTime > previous)
                intervals.Add((frame.RenderingTime - previous).TotalMilliseconds);
            lastFrame = frame.RenderingTime;
        };
        _pins.BeforeShowForTest = created =>
        {
            pin = created;
            created.ResidentProbeInitialSize();
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            var first = await ResidentProbePresentation.MeasureAsync(Dispatcher, () =>
            {
                if (scenario == "pin-text")
                {
                    bool table = index % 2 == 0;
                    string text = PinProbeText(index, table);
                    double scale = MonitorEnumerator.GetFromCursor().ScaleFactor;
                    _pins.PinRenderedText(ClipboardTextRenderer.Render(text, table, scale), text, table);
                }
                else
                {
                    // Separate pixel data and BitmapSource for every pin; no duplicated-picture cache.
                    MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
                    BitmapSource source = ResidentProbeImages.CachedPattern(width, height, index * 17);
                    _pins.PinImageAtRegion(source,
                        CaptureProvenance.None, new RectD(monitor.WorkArea.Left + 24, monitor.WorkArea.Top + 24, width, height));
                }
            }, () => pin is { IsVisible: true });
            if (pin is null) throw new InvalidOperationException("No pin was created.");
            recorder.PinFrame(scenario, index, "create-first-frame", first);
            recorder.Track(scenario, "pin " + index, pin);
            recorder.Track(scenario, "source " + index, pin.PinnedContent.Image);
            await ProbeWaitAsync(() => !pin.ResidentProbeLandingActive, 10_000, "pin landing finished");
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
            _pins.BeforeShowForTest = null;
        }
        recorder.PinIntervals(scenario, index, intervals);
        if (options.Has("pin-150"))
        {
            diagnostics.Interact(pin);
            var scaled = await ResidentProbePresentation.MeasureAsync(Dispatcher, () => pin.ResidentProbeSetZoom(1.5),
                () => !pin.IsLiveZoomingForTest);
            recorder.PinFrame(scenario, index, "set-150-settled-frame", scaled);
        }
        await Task.Delay(300);
        BitmapSource image = pin.PinnedContent.Image;
        var bounds = pin.PhysicalBounds;
        recorder.ObservePin(scenario, index, "geometry", new
        {
            image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
            pin.PinnedContent.LayoutWidthDip, pin.PinnedContent.LayoutHeightDip,
            pin.State.Zoom, PhysicalWidth = bounds.Right - bounds.Left, PhysicalHeight = bounds.Bottom - bounds.Top,
            SourceBytes = (long)image.PixelWidth * image.PixelHeight * 4,
            pin.AllowsTransparency, NativeLayered = ResidentProbePinDiagnostics.IsLayered(pin),
            Scaling = RenderOptions.GetBitmapScalingMode(pin.ResidentProbeImage).ToString(),
            pin.IsHoverBarShown, Animations = Themes.FluidMotion.AnimationsEnabled,
        });
    }

    internal static string PinProbeText(int index, bool table) => table
        ? string.Join('\n', Enumerable.Range(0, 17).Select(row =>
            $"{index:00}-{row:00}\tCapture {row:00}\t2026-11-10\tReview pending\t1920 x 1080\t{row * 12.75:0.00}\tLocal reference"))
        : string.Join('\n', Enumerable.Range(0, 23).Select(row =>
            $"var capture{row:00} = queue.Read({index * 100 + row}); // Keep the original pixels and source metadata for this item."));

    private async Task ProbePinInteractionsAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        ResidentProbePinDiagnostics diagnostics, string scenario)
    {
        if (_pins is null) return;
        PinWindow pin = _pins.SnapshotOpenPinWindows().Last();
        int index = _pins.Count;
        if (options.Has("sw-pin-idle") && !options.Has("sw-pin") && !options.Has("sw-pin-aux"))
        {
            // Do not fake idleness or move the real cursor. A real hover can prevent this switch.
            await Task.Delay(2400);
            recorder.ObservePin(scenario, index, "idle-before-interaction", diagnostics.IsSoftware(pin));
            if (!diagnostics.IsSoftware(pin)) recorder.Note(scenario + ": idle switch blocked by current interaction/hover; move the pointer outside the diagnostic windows for a desktop rerun.");
        }
        for (int notch = 1; notch <= 20; notch++)
        {
            int delta = notch <= 10 ? 120 : -120;
            long notchStarted = Stopwatch.GetTimestamp();
            var frame = await ResidentProbePresentation.MeasureAsync(Dispatcher, () =>
            {
                diagnostics.Interact(pin);
                // Same wheel handler as real input; no SendInput, clipboard or OS cursor movement.
                pin.HandleMouseWheel(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.MouseWheelEvent },
                    ModifierKeys.None);
            }, () => pin.IsVisible);
            recorder.PinFrame(scenario, index, $"wheel-{notch:00}", frame);
            if (notch == 20)
            {
                var settle = await ResidentProbePresentation.MeasureAsync(Dispatcher, () => { }, () => !pin.IsLiveZoomingForTest);
                recorder.PinFrame(scenario, index, "wheel-last-frame-to-settled-frame", settle);
                if (settle.CompleteMs is not null)
                    recorder.PinFrame(scenario, index, "last-notch-to-settled-frame",
                        new(0, Stopwatch.GetElapsedTime(notchStarted).TotalMilliseconds, null));
            }
        }
        recorder.ObservePin(scenario, index, "zoom-cache-after-settle", pin.ResidentProbeHasRasterCache);
        BitmapSource sampledImage = pin.PinnedContent.Image;
        // Isolate the one changed pin from the preceding wheel/re-rasterization work. Both
        // counters are whole-process samples; their difference applies to one opacity step.
        var beforeOpacity = recorder.SamplePin(scenario, "before-opacity", index,
            sampledImage.PixelWidth, sampledImage.PixelHeight, pin.State.Zoom, null, _pins.ClosedRetainedBytes);
        var opacity = await ResidentProbePresentation.MeasureAsync(Dispatcher, () =>
        {
            diagnostics.Interact(pin);
            pin.ResidentProbeAdjustOpacity(-0.1);
        }, () => pin.IsVisible);
        recorder.PinFrame(scenario, index, "opacity-step-first-frame", opacity);
        recorder.ObservePin(scenario, index, "native-layered-after-opacity", ResidentProbePinDiagnostics.IsLayered(pin));
        await Task.Delay(400);
        var settledOpacity = await ResidentProbePresentation.MeasureAsync(Dispatcher, () => { },
            () => Math.Abs(pin.Opacity - pin.State.Opacity) < 0.0001,
            timeoutMs: 5000);
        recorder.PinFrame(scenario, index, "opacity-post-wait-frame", settledOpacity);
        recorder.SamplePin(scenario, "after-opacity", index, sampledImage.PixelWidth,
            sampledImage.PixelHeight, pin.State.Zoom, beforeOpacity, _pins.ClosedRetainedBytes);
        recorder.ObservePin(scenario, index, "opacity-memory", new
        {
            AffectedPins = 1, OpenPins = index, pin.State.Opacity, pin.AllowsTransparency,
            NativeLayered = ResidentProbePinDiagnostics.IsLayered(pin),
            CounterScope = "whole process; after-opacity minus before-opacity for one changed pin",
        });
        bool hoverAlreadyShown = pin.HoverBarForTest is not null;
        var hover = await ResidentProbePresentation.MeasureAsync(Dispatcher, () =>
        {
            diagnostics.Interact(pin);
            pin.ShowHoverBar();
        }, () => pin.IsHoverBarShown);
        recorder.PinFrame(scenario, index, hoverAlreadyShown ? "hover-bar-reshow-frame" : "hover-bar-first-frame", hover);
        recorder.ObservePin(scenario, index, "hover-first-show-available", !hoverAlreadyShown);
        pin.HideHoverBar();
        // Wait for zoom feedback and opacity/chrome animation to stop before measuring a pure move.
        await Task.Delay(4000);
        diagnostics.Interact(pin);
        await diagnostics.DrainAsync();
        ImageSource? source = pin.ImageSourceForTest;
        int paints = diagnostics.PaintCount(pin);
        var before = pin.PhysicalBounds;
        long started = Stopwatch.GetTimestamp();
        pin.MovePhysicalForTest(before.Left + 200, before.Top);
        recorder.Time($"{scenario}: pin {index} move-200-native-call", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        await Task.Delay(300);
        var after = pin.PhysicalBounds;
        recorder.ObservePin(scenario, index, "move-200", new
        {
            DeltaX = after.Left - before.Left, DeltaY = after.Top - before.Top,
            ContentRasterReplaced = !ReferenceEquals(source, pin.ImageSourceForTest),
            PaintMessages = diagnostics.PaintCount(pin) - paints,
            GpuSurfaceRedraw = (bool?)null,
            Evidence = "HWND origin, image source identity and WM_PAINT only; these cannot prove absence of a retained-mode/GPU surface redraw. Use desktop WPF/DWM ETW for that conclusion.",
        });
    }
}
