using System.Diagnostics;
using System.Windows;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Platform.Display;

namespace MyCapture.App;

public partial class App
{
    // This branch exists only inside the explicitly requested diagnostic scenarios. It does not
    // replace PinManager or allocate a native picture in a normal product run.
    private async Task ProbeNativePinMemoryAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        string scenario)
    {
        bool viewport = NativePinWindow.LiveViewportEnabled;
        // The losing live-frame candidate stays measurable beside the chosen one, in the same build.
        NativePinWindow.LiveViewportEnabled = !options.Has("pin-native-full");
        try { await ProbeNativePinScenarioAsync(options, recorder, scenario); }
        finally { NativePinWindow.LiveViewportEnabled = viewport; }
    }

    private async Task ProbeNativePinScenarioAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        string scenario)
    {
        (int width, int height, int count) = scenario switch
        {
            "pin-1600" => (1600, 1000, 10), "pin-1080" => (1920, 1080, 10),
            "pin-4k" => (3840, 2160, 4), _ => throw new ArgumentException("Unknown native pin scenario"),
        };
        double zoom = options.Has("pin-150") ? 1.5 : 1;
        var pins = new List<NativePinWindow>();
        int initialWindows = NativePinWindow.LiveWindowCount, initialDibs = NativePinWindow.LiveDibCount,
            initialDcs = NativePinWindow.LiveDcCount;
        int initialSources = NativePinPixelSource.LiveAllocationCount,
            initialWorkers = NativePinWindow.LiveWorkerBufferCount,
            initialSections = NativePinWindow.LiveDisplaySectionCount,
            initialClocks = NativePinWindow.LiveMotionClockCount;
        recorder.ObservePin(scenario, 0, "protocol", new
        {
            Version = 5, Count = count, Zoom = zoom, NativePicture = true, PerPinWaitMs = 300,
            DwellMs = options.Quick ? 2000 : 5000, SurfaceBytes = (long)width * height * 4,
            MemoryDesign = "Deterministically owned PBGRA source plus one display DIB and a bounded perimeter cache. One temporary worker section during settle, none at rest. No PinManager history PNG is produced.",
            MemoryAccounting = "PrivateBytes counts the source (process heap) and a display DIB that Windows allocated. It does not count a display adopted from the worker's page-file section, nor the worker's section. The native-memory observations give each of these, in total and per pin, at every sample; Map.MappedBytes is every mapped view in the process, for this host and for the WPF one alike.",
            FrameEndpoint = "Successful UpdateLayeredWindow submission then DwmFlush return; composition completion bound, not physical scan-out. Motion uses a background DwmFlush-paced clock with one acknowledged STA submission per tick, without a WPF rendering target. The first visible submission and the settled submission are timed where they happen, not by polling.",
            LiveCandidate = NativePinWindow.LiveMode.ToString(),
            LiveFrame = NativePinWindow.LiveViewportEnabled
                ? "The part of the surface on the virtual screen; the settled picture is the whole surface."
                : "The whole zoomed surface (pin-native-full).",
            Missing = new[] { "hover bar", "bottom message", "context menu", "text selection", "translation", "session/history restore", "keyboard commands other than plus/minus zoom", "separate landing ornament" },
            InitialZoom = "100% source pixels at initial monitor DPI; 150% applied after landing. May extend outside work area, as WPF scenarios do.",
        });
        recorder.Note("pin-native uses GDI live scaling, worker Lanczos3 and a native layered window. Native flush endpoints differ from the WPF render-thread fence. Source pixels are generated directly with the same probe pattern, without a temporary WIC bitmap. Text keeps its WPF fallback. Hover bar/history remain absent.");
        ResidentProbePinMemory.Map Sample(string phase, int open, ResidentProbePinMemory.Map? before, int pixelWidth,
            int pixelHeight, double sampleZoom)
        {
            var map = recorder.SamplePin(scenario, phase, open, pixelWidth, pixelHeight, sampleZoom, before, 0);
            ObserveNativePinMemory(recorder, scenario, phase, open, pins);
            return map;
        }
        var previous = Sample("baseline", 0, null, width, height, zoom);
        try
        {
            for (int index = 1; index <= count; index++)
            {
                await ProbeOpenNativePinAsync(options, recorder, scenario, index, width, height, pins);
                previous = Sample("open", index, previous, width, height, zoom);
            }
            await Task.Delay(options.Quick ? 2000 : 5000);
            previous = Sample("dwell", count, previous, width, height, zoom);
            await ProbeNativePinInteractionsAsync(recorder, scenario, pins[^1], count, Sample);
            previous = Sample("after-interactions", count, previous, width, height, zoom);
        }
        finally
        {
            // Dispose every owned HWND even if another pin failed a draw or close.
            List<Exception>? closeErrors = null;
            foreach (NativePinWindow pin in pins)
            {
                try { pin.Dispose(); }
                catch (Exception error) { (closeErrors ??= []).Add(error); }
            }
            // Closing never joins a worker on the STA. Drain asynchronously before the
            // unchanged closed dwell/sample, without forcing GC or trimming working set.
            foreach (NativePinWindow pin in pins)
            {
                try { await pin.DrainAsync(); }
                catch (Exception error) { (closeErrors ??= []).Add(error); }
            }
            pins.Clear();
            if (closeErrors is not null) throw new AggregateException("Native pin close failed", closeErrors);
        }
        await Task.Delay(options.Quick ? 2000 : 5000);
        previous = Sample("closed", 0, previous, width, height, zoom);
        bool released = NativePinWindow.LiveWindowCount == initialWindows
            && NativePinWindow.LiveDibCount == initialDibs && NativePinWindow.LiveDcCount == initialDcs
            && NativePinPixelSource.LiveAllocationCount == initialSources
            && NativePinWindow.LiveWorkerBufferCount == initialWorkers
            && NativePinWindow.LiveDisplaySectionCount == initialSections
            && NativePinWindow.LiveMotionClockCount == initialClocks;
        recorder.ObservePin(scenario, 0, "native-closed-resources", new
        {
            Released = released, NativePinWindow.LiveWindowCount, NativePinWindow.LiveDibCount,
            NativePinWindow.LiveDcCount, InitialWindows = initialWindows, InitialDibs = initialDibs, InitialDcs = initialDcs,
            NativePinPixelSource.LiveAllocationCount, NativePinPixelSource.LiveBytes,
            NativePinWindow.LiveWorkerBufferCount, NativePinWindow.LiveWorkerBufferBytes,
            NativePinWindow.LiveDisplaySectionCount, NativePinWindow.LiveDisplaySectionBytes,
            NativePinWindow.LiveDisplayPrivateBytes,
            InitialSources = initialSources, InitialWorkers = initialWorkers, InitialSections = initialSections,
            NativePinWindow.LiveMotionClockCount, InitialClocks = initialClocks,
        });
        if (!released) throw new InvalidOperationException("Native pin resources did not return to their baseline.");
        await recorder.AuditAsync(scenario + " closed", force: true);
        Sample("audit", 0, previous, width, height, zoom);
    }

    private async Task ProbeOpenNativePinAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        string scenario, int index, int width, int height, List<NativePinWindow> pins)
    {
        NativePinWindow? pin = null;
        var intervals = new List<double>();
        long? lastSubmission = null;
        var firstVisible = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Submitted()
        {
            if (pin is null) return;
            long now = Stopwatch.GetTimestamp();
            if (pin.LastPresentedAlpha > 0) firstVisible.TrySetResult(now);
            if (!pin.IsLanding) return;
            if (lastSubmission is { } previous) intervals.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
            lastSubmission = now;
        }
        long started = Stopwatch.GetTimestamp();
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
        var source = CreateNativeProbeSource(width, height, index * 17);
        double sourceMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        pin = new NativePinWindow(source, monitor.WorkArea.Left + 24, monitor.WorkArea.Top + 24,
            dpiScale: monitor.ScaleFactor);
        pins.Add(pin);
        double constructedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        pin.Presented += Submitted;
        try
        {
            pin.Show();
            pin.StartLanding();
            long firstAt = await ProbeNativePinWaitAsync(pin, firstVisible.Task, 5000, "first nonzero native pin alpha submission");
            double submitted = Stopwatch.GetElapsedTime(started, firstAt).TotalMilliseconds;
            pin.FlushPresentation();
            double complete = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            recorder.PinFrame(scenario, index, "create-first-frame", new(submitted, complete, null));
            // Where the first frame's time goes; the four parts add up to it. The source is the
            // probe's own picture generator. Showing ends at the first submission with any alpha,
            // which the landing's first update usually makes at once.
            recorder.ObservePin(scenario, index, "native-first-frame", new
            {
                SourceMilliseconds = sourceMilliseconds,
                WindowAndFirstRasterMilliseconds = constructedMilliseconds - sourceMilliseconds,
                ShowUntilVisibleSubmissionMilliseconds = submitted - constructedMilliseconds,
                FlushMilliseconds = complete - submitted,
            });
            recorder.Track(scenario, "native pin " + index, pin);
            recorder.Track(scenario, "native source " + index, source);
            await ProbeWaitAsync(() => !pin.IsLanding, 10_000, "native pin landing finished");
            ThrowNativePinError(pin);
        }
        finally { pin.Presented -= Submitted; }
        recorder.PinIntervals(scenario, index, intervals);
        if (options.Has("pin-150"))
        {
            long zoomStarted = Stopwatch.GetTimestamp();
            pin.SetZoom(1.5);
            await pin.WaitForSettledAsync();
            ThrowNativePinError(pin);
            double submitted = Stopwatch.GetElapsedTime(zoomStarted).TotalMilliseconds;
            pin.FlushPresentation();
            recorder.PinFrame(scenario, index, "set-150-settled-frame",
                new(submitted, Stopwatch.GetElapsedTime(zoomStarted).TotalMilliseconds, null));
        }
        await Task.Delay(300);
        recorder.ObservePin(scenario, index, "geometry", new
        {
            PixelWidth = source.Width, PixelHeight = source.Height, DpiX = 96, DpiY = 96,
            LayoutWidthDip = pin.State.ImageWidthDip, LayoutHeightDip = pin.State.ImageHeightDip,
            pin.Zoom, PhysicalWidth = pin.PixelWidth, PhysicalHeight = pin.PixelHeight,
            SourceBytes = source.ByteLength, pin.DisplayBytes, pin.DisplaySectionBytes, pin.DisplayPrivateBytes,
            pin.EdgeCacheBytes,
            NativeLayered = true, WpfPictureTarget = false, Scaling = "Lanczos3 worker settled / GDI nearest live",
            InitialMonitorScale = monitor.ScaleFactor, pin.DpiScale,
        });
        ObserveNativePin(recorder, scenario, index, "native-after-landing", pin);
    }

    private async Task ProbeNativePinInteractionsAsync(ResidentProbeRecorder recorder, string scenario,
        NativePinWindow pin, int index,
        Func<string, int, ResidentProbePinMemory.Map?, int, int, double, ResidentProbePinMemory.Map> sample)
    {
        for (int notch = 1; notch <= 20; notch++)
        {
            int delta = notch <= 10 ? 120 : -120;
            Point anchor = new(pin.Bounds.Left + pin.Bounds.Width / 2, pin.Bounds.Top + pin.Bounds.Height / 2);
            long started = Stopwatch.GetTimestamp();
            recorder.PinFrame(scenario, index, $"wheel-{notch:00}", MeasureNativePin(pin, () => pin.Wheel(delta, anchor)));
            recorder.PinDuration(scenario, index, $"wheel-{notch:00}-cpu-raster", pin.LastRasterMilliseconds);
            recorder.ObservePin(scenario, index, $"native-wheel-{notch:00}", new
            {
                pin.LastLiveUiMilliseconds, pin.LastLiveSubmitMilliseconds,
                pin.RequestedGeneration, pin.WorkerBufferCount, pin.PeakWorkerBufferCount,
                // The steps of this notch on the UI thread, and how much of the surface it drew.
                pin.LastLiveAllocateMilliseconds, pin.LastLiveStretchMilliseconds, pin.LastLiveEdgeMilliseconds,
                pin.LastPresentMilliseconds, pin.Zoom, LiveFrameBytes = pin.DisplayBytes,
                SurfaceBytes = (long)pin.PixelWidth * pin.PixelHeight * 4, pin.IsDisplayClipped,
                pin.WorkerSectionBytes,
            });
            await Task.Yield();
            if (notch != 20) continue;
            long lastFrame = Stopwatch.GetTimestamp();
            await ProbeNativePinWaitAsync(pin, pin.WaitForSettledAsync(), 30_000, "native pin quality resample settled");
            ThrowNativePinError(pin);
            pin.FlushPresentation();
            recorder.PinFrame(scenario, index, "wheel-last-frame-to-settled-frame",
                new(0, Stopwatch.GetElapsedTime(lastFrame).TotalMilliseconds, null));
            recorder.PinFrame(scenario, index, "last-notch-to-settled-frame",
                new(0, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null));
            recorder.PinDuration(scenario, index, "settled-cpu-raster", pin.LastRasterMilliseconds);
            recorder.ObservePin(scenario, index, "native-settled-work", new
            {
                pin.LastSettleMilliseconds, pin.LastSettleUiMilliseconds, pin.LongestSettleUiMilliseconds,
                pin.LongestSettleAdoptionMilliseconds, pin.LongestSettleDispatcherOperationMilliseconds,
                pin.SettleDispatcherOperationCount,
                pin.RequestedGeneration, pin.SettledGeneration, pin.WorkerBufferCount,
                pin.PeakWorkerBufferCount, NativePinWindow.LiveWorkerBufferCount, pin.IsDisplayClipped,
            });
        }
        var beforeOpacity = sample("before-opacity", index, null, pin.Source!.Width, pin.Source.Height, pin.Zoom);
        int rasters = pin.RasterCount, presents = pin.PresentCount;
        recorder.PinFrame(scenario, index, "opacity-step-first-frame", MeasureNativePin(pin, () => pin.AdjustOpacity(-.1)));
        recorder.ObservePin(scenario, index, "native-opacity-work", new
        {
            RasterDelta = pin.RasterCount - rasters, PresentDelta = pin.PresentCount - presents,
            pin.Opacity, NativeLayered = true, WpfPictureTarget = false,
        });
        await Task.Delay(400);
        recorder.PinFrame(scenario, index, "opacity-post-wait-frame", MeasureNativePin(pin, () => { }));
        sample("after-opacity", index, beforeOpacity, pin.Source!.Width, pin.Source.Height, pin.Zoom);
        ObserveNativePin(recorder, scenario, index, "native-after-opacity", pin);
        recorder.ObservePin(scenario, index, "hover-first-show-available", false);
        recorder.ObservePin(scenario, index, "prototype-missing-hover-bar", "Not implemented; no hover timing was synthesized.");
        await Task.Delay(4000);
        var before = pin.Bounds;
        rasters = pin.RasterCount;
        presents = pin.PresentCount;
        long moving = Stopwatch.GetTimestamp();
        pin.MovePhysical(before.Left + 200, before.Top);
        recorder.Time($"{scenario}: pin {index} move-200-native-call", Stopwatch.GetElapsedTime(moving).TotalMilliseconds);
        await Task.Delay(300);
        ThrowNativePinError(pin);
        recorder.ObservePin(scenario, index, "move-200", new
        {
            DeltaX = pin.Bounds.Left - before.Left, DeltaY = pin.Bounds.Top - before.Top,
            RasterDelta = pin.RasterCount - rasters, PresentDelta = pin.PresentCount - presents,
            Evidence = "Native origin move and actual CPU raster/UpdateLayeredWindow counters. A DPI transition can require a new-sized DIB; no statement about compositor-internal copies.",
        });
    }

    // Completes with the task, or fails with the pin's own error or a timeout. A failed motion
    // or settle callback raises no event, so the timeout is what surfaces it.
    private static async Task ProbeNativePinWaitAsync(NativePinWindow pin, Task awaited, int timeoutMs, string what)
    {
        using var watchdog = new CancellationTokenSource();
        if (await Task.WhenAny(awaited, Task.Delay(timeoutMs, watchdog.Token)) != awaited)
        {
            ThrowNativePinError(pin);
            throw new TimeoutException("Resident probe timed out: " + what);
        }
        watchdog.Cancel();
        await awaited;
    }

    private static async Task<T> ProbeNativePinWaitAsync<T>(NativePinWindow pin, Task<T> awaited, int timeoutMs, string what)
    {
        await ProbeNativePinWaitAsync(pin, (Task)awaited, timeoutMs, what);
        return await awaited;
    }

    private static ResidentProbePresentation.Frame MeasureNativePin(NativePinWindow pin, Action action)
    {
        long started = Stopwatch.GetTimestamp();
        action();
        ThrowNativePinError(pin);
        double submitted = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        pin.FlushPresentation();
        return new(submitted, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
    }

    private static void ThrowNativePinError(NativePinWindow pin)
    {
        if (pin.LastPresentError is { } error) throw new InvalidOperationException(error);
    }

    // The picture buffers the prototype holds, by where Windows accounts for them. Private commit
    // (PrivateBytes) contains the first two and neither of the sections.
    private static void ObserveNativePinMemory(ResidentProbeRecorder recorder, string scenario, string phase,
        int open, IReadOnlyList<NativePinWindow> pins) => recorder.ObservePin(scenario, open, "native-memory", new
        {
            Phase = phase, Pins = open,
            SourcePrivateBytes = NativePinPixelSource.LiveBytes,
            DisplayPrivateBytes = NativePinWindow.LiveDisplayPrivateBytes,
            DisplaySectionBytes = NativePinWindow.LiveDisplaySectionBytes,
            WorkerSectionBytes = NativePinWindow.LiveWorkerBufferBytes,
            SectionBytes = NativePinWindow.LiveDisplaySectionBytes + NativePinWindow.LiveWorkerBufferBytes,
            PerPin = pins.Where(pin => !pin.IsClosed).Select(pin => new
            {
                SourcePrivateBytes = pin.Source?.ByteLength ?? 0, pin.DisplayPrivateBytes,
                pin.DisplaySectionBytes, pin.WorkerSectionBytes, pin.EdgeCacheBytes,
            }).ToArray(),
        });

    private static void ObserveNativePin(ResidentProbeRecorder recorder, string scenario, int index,
        string phase, NativePinWindow pin) => recorder.ObservePin(scenario, index, phase, new
        {
            pin.Zoom, pin.Opacity, pin.DpiScale, pin.DisplayBytes, pin.RasterCount, pin.PresentCount,
            pin.LastRasterMilliseconds, pin.LastPresentError, pin.IsLanding, pin.IsLiveZooming,
            pin.LastPresentedAlpha,
            SourceBytes = pin.Source?.ByteLength ?? 0, pin.EdgeCacheBytes, pin.DisplaySectionBytes,
            pin.DisplayPrivateBytes, pin.WorkerSectionBytes, pin.IsDisplayClipped,
            pin.WorkerBufferCount, pin.PeakWorkerBufferCount, NativePinWindow.LiveWorkerBufferCount,
            NativePinWindow.LiveWorkerBufferBytes, NativePinWindow.LiveDisplaySectionCount,
            NativePinWindow.LiveDisplaySectionBytes, NativePinPixelSource.LiveAllocationCount,
            NativePinPixelSource.LiveBytes, pin.LastLiveUiMilliseconds, pin.LastLiveSubmitMilliseconds,
            pin.LastSettleMilliseconds, pin.LastSettleUiMilliseconds, pin.LongestSettleUiMilliseconds,
            pin.LongestSettleAdoptionMilliseconds, pin.LongestSettleDispatcherOperationMilliseconds,
            pin.SettleDispatcherOperationCount,
            pin.RequestedGeneration, pin.SettledGeneration, pin.MotionTickCount,
            pin.LastMotionClockHResult, NativePinWindow.LiveMotionClockCount,
            NativeLayered = true, WpfPictureTarget = false,
        });

    // Same bytes as ResidentProbeImages.CachedPattern, including its cached 8-row
    // bands, without a GC/finalizer-owned WIC copy between generation and the pin.
    internal static NativePinPixelSource CreateNativeProbeSource(int width, int height, int seed)
    {
        byte[] cachedRow = new byte[checked(width * 4)];
        int cachedBand = -1;
        return NativePinPixelSource.CreateFilled(width, height, (row, y) =>
        {
            int band = y / 8;
            if (band != cachedBand)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = x * 4;
                    cachedRow[offset] = (byte)(70 + ((x / 24 + y / 24 + seed) % 150));
                    cachedRow[offset + 1] = (byte)(90 + ((x / 48 + seed) % 120));
                    cachedRow[offset + 2] = (byte)(120 + ((y / 40) % 110));
                    cachedRow[offset + 3] = 255;
                }
                cachedBand = band;
            }
            cachedRow.AsSpan().CopyTo(row);
        });
    }
}
