using System.Diagnostics;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App;

public partial class App
{
    // pin-light: the product's own lightweight picture pin, opened through PinManager with the
    // setting on. The measurements mirror pin-native so the two tables compare row by row.
    private async Task ProbeLightPinMemoryAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        string scenario, ResidentProbePinDiagnostics diagnostics)
    {
        if (_pins is null) throw new InvalidOperationException("Pins unavailable.");
        (int width, int height, int count) = scenario switch
        {
            "pin-1600" => (1600, 1000, 10), "pin-1080" => (1920, 1080, 10),
            "pin-4k" => (3840, 2160, 4), _ => throw new ArgumentException("Unknown lightweight pin scenario"),
        };
        double zoom = options.Has("pin-150") ? 1.5 : 1;
        diagnostics.Scenario = scenario;
        var windows = new List<NativePinWindow>();
        int initialWindows = NativePinWindow.LiveWindowCount, initialDibs = NativePinWindow.LiveDibCount,
            initialDcs = NativePinWindow.LiveDcCount;
        int initialSources = NativePinPixelSource.LiveAllocationCount,
            initialWorkers = NativePinWindow.LiveWorkerBufferCount,
            initialSections = NativePinWindow.LiveDisplaySectionCount,
            initialClocks = NativePinWindow.LiveMotionClockCount;
        recorder.ObservePin(scenario, 0, "protocol", new
        {
            Version = 5, Count = count, Zoom = zoom, NativePicture = true, Product = true, PerPinWaitMs = 300,
            DwellMs = options.Quick ? 2000 : 5000, SurfaceBytes = (long)width * height * 4,
            MemoryDesign = "PinWindow is the logical pin; PinManager chose the native host (setting LightweightImagePins on for this process only). The PinContent BitmapSource stays for history, session and a later switch to the WPF host.",
            MemoryAccounting = "As pin-native, plus the PinContent BitmapSource that every product pin keeps.",
            FrameEndpoint = "Successful UpdateLayeredWindow submission then DwmFlush return; composition completion bound, not physical scan-out.",
            LiveCandidate = NativePinWindow.LiveMode.ToString(),
            Missing = new[] { "snap while dragging" },
            InitialZoom = "100% source pixels at initial monitor DPI; 150% applied after landing.",
        });
        ResidentProbePinMemory.Map Sample(string phase, int open, ResidentProbePinMemory.Map? before, int pixelWidth,
            int pixelHeight, double sampleZoom)
        {
            var map = recorder.SamplePin(scenario, phase, open, pixelWidth, pixelHeight, sampleZoom, before, _pins.ClosedRetainedBytes);
            ObserveNativePinMemory(recorder, scenario, phase, open, windows);
            return map;
        }
        var previous = Sample("baseline", 0, null, width, height, zoom);
        try
        {
            for (int index = 1; index <= count; index++)
            {
                windows.Add(await ProbeOpenLightPinAsync(options, recorder, diagnostics, scenario, index, width, height));
                previous = Sample("open", index, previous, width, height, zoom);
            }
            await Task.Delay(options.Quick ? 2000 : 5000);
            previous = Sample("dwell", count, previous, width, height, zoom);
            await ProbeLightPinInteractionsAsync(recorder, scenario, _pins.SnapshotOpenPinWindows().Last(), count, Sample);
            previous = Sample("after-interactions", count, previous, width, height, zoom);
            if (options.Has("pin-reopen"))
                previous = await ProbeReopenLightPinsAsync(options, recorder, scenario, windows, previous, Sample);
        }
        finally
        {
            _pins.BeforeShowForTest = null;
            _pins.CloseAll();
            await ProbeWaitAsync(() => !Windows.OfType<PinWindow>().Any(), 15_000, "all diagnostic pin HWNDs closed");
            await _pins.LastClosedEncodingForTest;
            await ProbeWaitAsync(() => !_pins.IsInteractionActive, 15_000, "all closed pin encodes completed");
            List<Exception>? drainErrors = null;
            foreach (NativePinWindow window in windows)
            {
                try { await window.DrainAsync(); }
                catch (Exception error) { (drainErrors ??= []).Add(error); }
            }
            windows.Clear();
            if (drainErrors is not null) throw new AggregateException("Lightweight pin drain failed", drainErrors);
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
        if (!released) throw new InvalidOperationException("Lightweight pin resources did not return to their baseline.");
        await recorder.AuditAsync(scenario + " closed", force: true);
        Sample("audit", 0, previous, width, height, zoom);
    }

    // pin-reopen: every pin closed into the closed history and reopened from it. The reopened picture is a PNG
    // decoded on a pool thread, as a session restore after a restart decodes it.
    private async Task<ResidentProbePinMemory.Map> ProbeReopenLightPinsAsync(ResidentProbeOptions options,
        ResidentProbeRecorder recorder, string scenario, List<NativePinWindow> windows, ResidentProbePinMemory.Map previous,
        Func<string, int, ResidentProbePinMemory.Map?, int, int, double, ResidentProbePinMemory.Map> sample)
    {
        if (_pins is null) throw new InvalidOperationException("Pins unavailable.");
        PinPicture first = _pins.SnapshotOpenPinWindows()[0].PinnedContent.Picture;
        (int width, int height) = (first.PixelWidth, first.PixelHeight);
        double zoom = options.Has("pin-150") ? 1.5 : 1;
        foreach (PinWindow open in _pins.SnapshotOpenPinWindows()) open.CloseWithExit();
        await ProbeWaitAsync(() => !Windows.OfType<PinWindow>().Any(), 15_000, "all pins closed for the reopen");
        await _pins.LastClosedEncodingForTest;
        await ProbeWaitAsync(() => !_pins.IsInteractionActive, 15_000, "closed pin encodes completed before the reopen");
        foreach (NativePinWindow window in windows) await window.DrainAsync();
        windows.Clear();
        int remembered = _pins.ClosedCount;
        await Task.Delay(options.Quick ? 2000 : 5000);
        previous = sample("closed-for-reopen", 0, previous, width, height, zoom);
        int reopened = 0;
        while (_pins.CanRestoreClosed)
        {
            PinWindow? pin = await _pins.RestoreLastClosedAsync();
            if (pin is null) break;
            reopened++;
            NativePinWindow? native = pin.NativeWindowForTest;
            if (native is not null)
            {
                windows.Add(native);
                await ProbeWaitAsync(() => !native.IsLanding, 10_000, "reopened lightweight pin landing finished");
                ThrowNativePinError(native);
            }
            await Task.Delay(300);
            PinPicture picture = pin.PinnedContent.Picture;
            recorder.ObservePin(scenario, reopened, "reopened", new
            {
                pin.IsNativeHosted, PictureBitmapDropped = pin.IsPictureBitmapDropped, Format = picture.Format.ToString(),
                picture.PixelWidth, picture.PixelHeight, PictureRebuilds = PinPicture.RebuildCount,
                SourceBytes = native?.Source?.ByteLength ?? 0,
            });
            recorder.Track(scenario, "reopened pin " + reopened, pin);
        }
        await Task.Delay(options.Quick ? 2000 : 5000);
        previous = sample("reopened", reopened, previous, width, height, zoom);
        recorder.ObservePin(scenario, 0, "reopen", new
        {
            Remembered = remembered, Reopened = reopened,
            Native = _pins.SnapshotOpenPinWindows().Count(pin => pin.IsNativeHosted),
            Dropped = _pins.SnapshotOpenPinWindows().Count(pin => pin.IsPictureBitmapDropped),
        });
        return previous;
    }

    private async Task<NativePinWindow> ProbeOpenLightPinAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        ResidentProbePinDiagnostics diagnostics, string scenario, int index, int width, int height)
    {
        if (_pins is null) throw new InvalidOperationException("Pins unavailable.");
        diagnostics.NextIndex = index;
        PinWindow? pin = null;
        NativePinWindow? native = null;
        var intervals = new List<double>();
        long? lastSubmission = null;
        long created = 0, returned = 0, ornamentCreated = 0, ornamentRendered = 0, firstLanding = 0, secondLanding = 0;
        var firstVisible = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Submitted()
        {
            if (native is null) return;
            long now = Stopwatch.GetTimestamp();
            if (native.LastPresentedAlpha > 0) firstVisible.TrySetResult(now);
            if (!native.IsLanding) return;
            if (firstLanding == 0) firstLanding = now;
            else if (secondLanding == 0) secondLanding = now;
            if (lastSubmission is { } before) intervals.Add(Stopwatch.GetElapsedTime(before, now).TotalMilliseconds);
            lastSubmission = now;
        }
        // Where the opening turn's time goes after the first visible frame: the landing ornament's window.
        Action<System.Windows.Window>? priorHook = ResidentProbeWindowHook.SourceInitialized;
        ResidentProbeWindowHook.SourceInitialized = window =>
        {
            priorHook?.Invoke(window);
            if (window is not PinLandingDecoration decoration || ornamentCreated != 0) return;
            ornamentCreated = Stopwatch.GetTimestamp();
            decoration.ContentRendered += (_, _) => { if (ornamentRendered == 0) ornamentRendered = Stopwatch.GetTimestamp(); };
        };
        _pins.BeforeShowForTest = shown =>
        {
            pin = shown;
            created = Stopwatch.GetTimestamp();
            native = shown.NativeWindowForTest;
            if (native is null) return;
            native.Presented += Submitted;
            shown.ResidentProbeInitialSize();
        };
        var steps = new List<(string Step, long At)>();
        _pins.OpeningStepForProbe = step => steps.Add((step, Stopwatch.GetTimestamp()));
        long started = Stopwatch.GetTimestamp();
        try
        {
            MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
            BitmapSource source = ResidentProbeImages.CachedPattern(width, height, index * 17);
            double sourceMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _pins.PinImageAtRegion(source, CaptureProvenance.None,
                new RectD(monitor.WorkArea.Left + 24, monitor.WorkArea.Top + 24, width, height));
            returned = Stopwatch.GetTimestamp();
            if (pin is null) throw new InvalidOperationException("No pin was created.");
            if (native is null) throw new InvalidOperationException("The pin did not use the lightweight host.");
            long firstAt = await ProbeNativePinWaitAsync(native, firstVisible.Task, 5000, "first nonzero lightweight pin alpha submission");
            double submitted = Stopwatch.GetElapsedTime(started, firstAt).TotalMilliseconds;
            native.FlushPresentation();
            double complete = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            double constructedMilliseconds = Stopwatch.GetElapsedTime(started, created).TotalMilliseconds;
            // The records wait for the landing: writing them on the dispatcher during it delayed its frames.
            await ProbeWaitAsync(() => !native.IsLanding, 10_000, "lightweight pin landing finished");
            ThrowNativePinError(native);
            recorder.PinFrame(scenario, index, "create-first-frame", new(submitted, complete, null));
            recorder.ObservePin(scenario, index, "native-first-frame", new
            {
                SourceMilliseconds = sourceMilliseconds,
                WindowAndFirstRasterMilliseconds = constructedMilliseconds - sourceMilliseconds,
                ShowUntilVisibleSubmissionMilliseconds = submitted - constructedMilliseconds,
                FlushMilliseconds = complete - submitted,
            });
            double? After(long at) => at == 0 ? null : Stopwatch.GetElapsedTime(firstAt, at).TotalMilliseconds;
            recorder.ObservePin(scenario, index, "native-opening-turn", new
            {
                // Milliseconds after the first visible submission.
                OrnamentWindowCreated = After(ornamentCreated),
                OpeningTurnReturned = After(returned),
                FlushReturned = complete - submitted,
                OrnamentFirstFrameRendered = After(ornamentRendered),
                FirstLandingFrame = After(firstLanding),
                SecondLandingFrame = After(secondLanding),
                // Each step of the manager's opening turn, as its end after the first visible submission.
                Steps = steps.Select(step => new { step.Step, End = After(step.At) }).ToArray(),
            });
            recorder.Track(scenario, "pin " + index, pin);
            recorder.Track(scenario, "native pin " + index, native);
            // The bitmap the pin was opened with: a lightweight opaque picture lets it go (design 9, increment 5).
            recorder.Track(scenario, "source " + index, source);
        }
        finally
        {
            if (native is not null) native.Presented -= Submitted;
            _pins.BeforeShowForTest = null;
            _pins.OpeningStepForProbe = null;
            ResidentProbeWindowHook.SourceInitialized = priorHook;
        }
        recorder.PinIntervals(scenario, index, intervals);
        if (options.Has("pin-150"))
        {
            long zoomStarted = Stopwatch.GetTimestamp();
            pin.ResidentProbeSetZoom(1.5);
            await native.WaitForSettledAsync();
            ThrowNativePinError(native);
            double submitted = Stopwatch.GetElapsedTime(zoomStarted).TotalMilliseconds;
            native.FlushPresentation();
            recorder.PinFrame(scenario, index, "set-150-settled-frame",
                new(submitted, Stopwatch.GetElapsedTime(zoomStarted).TotalMilliseconds, null));
        }
        await Task.Delay(300);
        // From the picture's size: reading its bitmap would build one when only the native source is kept.
        PinPicture picture = pin.PinnedContent.Picture;
        recorder.ObservePin(scenario, index, "geometry", new
        {
            picture.PixelWidth, picture.PixelHeight, picture.DpiX, picture.DpiY,
            PictureBitmapDropped = pin.IsPictureBitmapDropped, PictureRebuilds = PinPicture.RebuildCount,
            pin.PinnedContent.LayoutWidthDip, pin.PinnedContent.LayoutHeightDip,
            native.Zoom, PhysicalWidth = native.PixelWidth, PhysicalHeight = native.PixelHeight,
            SourceBytes = native.Source?.ByteLength ?? 0, native.DisplayBytes, native.DisplaySectionBytes,
            native.DisplayPrivateBytes, native.EdgeCacheBytes,
            NativeLayered = true, WpfPictureTarget = false, pin.IsNativeHosted, native.DpiScale,
        });
        ObserveNativePin(recorder, scenario, index, "native-after-landing", native);
        return native;
    }

    private async Task ProbeLightPinInteractionsAsync(ResidentProbeRecorder recorder, string scenario,
        PinWindow pin, int index,
        Func<string, int, ResidentProbePinMemory.Map?, int, int, double, ResidentProbePinMemory.Map> sample)
    {
        NativePinWindow native = pin.NativeWindowForTest
            ?? throw new InvalidOperationException("The measured pin is no longer on the lightweight host.");
        for (int notch = 1; notch <= 20; notch++)
        {
            int delta = notch <= 10 ? 120 : -120;
            long started = Stopwatch.GetTimestamp();
            recorder.PinFrame(scenario, index, $"wheel-{notch:00}", MeasureNativePin(native, () => pin.ResidentProbeWheel(delta)));
            recorder.PinDuration(scenario, index, $"wheel-{notch:00}-cpu-raster", native.LastRasterMilliseconds);
            recorder.ObservePin(scenario, index, $"native-wheel-{notch:00}", new
            {
                native.LastLiveUiMilliseconds, native.LastLiveSubmitMilliseconds,
                native.RequestedGeneration, native.WorkerBufferCount, native.PeakWorkerBufferCount,
                native.LastLiveAllocateMilliseconds, native.LastLiveStretchMilliseconds, native.LastLiveEdgeMilliseconds,
                native.LastPresentMilliseconds, native.Zoom, LiveFrameBytes = native.DisplayBytes,
                SurfaceBytes = (long)native.PixelWidth * native.PixelHeight * 4, native.IsDisplayClipped,
                native.WorkerSectionBytes,
            });
            await Task.Yield();
            if (notch != 20) continue;
            long lastFrame = Stopwatch.GetTimestamp();
            await ProbeNativePinWaitAsync(native, native.WaitForSettledAsync(), 30_000, "lightweight pin quality resample settled");
            ThrowNativePinError(native);
            native.FlushPresentation();
            recorder.PinFrame(scenario, index, "wheel-last-frame-to-settled-frame",
                new(0, Stopwatch.GetElapsedTime(lastFrame).TotalMilliseconds, null));
            recorder.PinFrame(scenario, index, "last-notch-to-settled-frame",
                new(0, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null));
            recorder.PinDuration(scenario, index, "settled-cpu-raster", native.LastRasterMilliseconds);
            recorder.ObservePin(scenario, index, "native-settled-work", new
            {
                native.LastSettleMilliseconds, native.LastSettleUiMilliseconds, native.LongestSettleUiMilliseconds,
                native.LongestSettleAdoptionMilliseconds, native.LongestSettleDispatcherOperationMilliseconds,
                native.SettleDispatcherOperationCount,
                native.RequestedGeneration, native.SettledGeneration, native.WorkerBufferCount,
                native.PeakWorkerBufferCount, NativePinWindow.LiveWorkerBufferCount, native.IsDisplayClipped,
            });
        }
        var beforeOpacity = sample("before-opacity", index, null, native.Source!.Width, native.Source.Height, native.Zoom);
        int rasters = native.RasterCount, presents = native.PresentCount;
        // The product fades opacity: the step's first frame is the fade's first submission.
        var firstFade = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Faded() => firstFade.TrySetResult();
        native.Presented += Faded;
        long opacityStarted = Stopwatch.GetTimestamp();
        try
        {
            pin.ResidentProbeAdjustOpacity(-.1);
            await ProbeNativePinWaitAsync(native, firstFade.Task, 5000, "lightweight pin opacity first submission");
        }
        finally { native.Presented -= Faded; }
        double opacitySubmitted = Stopwatch.GetElapsedTime(opacityStarted).TotalMilliseconds;
        native.FlushPresentation();
        recorder.PinFrame(scenario, index, "opacity-step-first-frame",
            new(opacitySubmitted, Stopwatch.GetElapsedTime(opacityStarted).TotalMilliseconds, null));
        await ProbeWaitAsync(() => !native.IsOpacityFading, 5000, "lightweight pin opacity fade finished");
        recorder.ObservePin(scenario, index, "native-opacity-work", new
        {
            RasterDelta = native.RasterCount - rasters, PresentDelta = native.PresentCount - presents,
            native.Opacity, NativeLayered = true, WpfPictureTarget = false,
        });
        await Task.Delay(400);
        recorder.PinFrame(scenario, index, "opacity-post-wait-frame", MeasureNativePin(native, () => { }));
        sample("after-opacity", index, beforeOpacity, native.Source!.Width, native.Source.Height, native.Zoom);
        ObserveNativePin(recorder, scenario, index, "native-after-opacity", native);
        // The bar native pins share: the first show creates it, as a WPF pin's first show creates its own.
        bool hoverAlreadyShown = PinWindow.SharedNativeHoverBarForTest is not null;
        var hover = await ResidentProbePresentation.MeasureAsync(Dispatcher, pin.ResidentProbeHover, () => pin.IsHoverBarShown);
        recorder.PinFrame(scenario, index, hoverAlreadyShown ? "hover-bar-reshow-frame" : "hover-bar-first-frame", hover);
        recorder.ObservePin(scenario, index, "hover-first-show-available", !hoverAlreadyShown);
        pin.HideHoverBar();
        await Task.Delay(4000);
        var before = native.Bounds;
        rasters = native.RasterCount;
        presents = native.PresentCount;
        long moving = Stopwatch.GetTimestamp();
        pin.MovePhysicalForTest(before.Left + 200, before.Top);
        recorder.Time($"{scenario}: pin {index} move-200-native-call", Stopwatch.GetElapsedTime(moving).TotalMilliseconds);
        await Task.Delay(300);
        ThrowNativePinError(native);
        recorder.ObservePin(scenario, index, "move-200", new
        {
            DeltaX = native.Bounds.Left - before.Left, DeltaY = native.Bounds.Top - before.Top,
            RasterDelta = native.RasterCount - rasters, PresentDelta = native.PresentCount - presents,
            pin.IsNativeHosted,
            Evidence = "Native origin move and actual CPU raster/UpdateLayeredWindow counters.",
        });
        if (!pin.IsNativeHosted) throw new InvalidOperationException("The measured pin switched to the WPF host during the probe.");
    }
}
