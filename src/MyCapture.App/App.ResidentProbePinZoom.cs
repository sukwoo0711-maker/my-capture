using System.Diagnostics;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Primitives;

namespace MyCapture.App;

public partial class App
{
    // pin-zoom: one product lightweight pin opened as pin-light does, then zoomed to each stop and left to
    // settle. At every stop the bytes of each part the pin holds and the process totals are recorded; from
    // 259% on, a drag of the overflowing pin and one jump show how long the newly visible part takes to appear.
    // pin-hold-whole measures the same steps with the whole surface held at rest (before design section 7);
    // pin-move-live draws the newly visible part with a live frame and a settle instead of on this thread.
    private static readonly double[] ZoomStops = [1, 1.5, 2.5937424601, 8];
    private const long HoldWholeLimitBytes = 1L << 30;
    private const int DragSteps = 30, DragStepX = -16, DragStepY = -8, JumpX = -600, JumpY = -300;

    private async Task ProbeLightPinZoomAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder,
        string scenario, ResidentProbePinDiagnostics diagnostics)
    {
        if (_pins is null) throw new InvalidOperationException("Pins unavailable.");
        (int width, int height) = scenario switch
        {
            "pin-1600" => (1600, 1000), "pin-1080" => (1920, 1080), "pin-4k" => (3840, 2160),
            _ => throw new ArgumentException("Unknown zoom scenario"),
        };
        bool rest = NativePinWindow.ScreenBoundedRest;
        long limit = NativePinWindow.ReholdPixelLimit;
        NativePinWindow.ScreenBoundedRest = !options.Has("pin-hold-whole");
        if (options.Has("pin-move-live")) NativePinWindow.ReholdPixelLimit = 0;
        diagnostics.Scenario = scenario;
        var windows = new List<NativePinWindow>();
        try
        {
            recorder.ObservePin(scenario, 0, "protocol", new
            {
                Version = 1, Count = 1, Stops = ZoomStops, NativePicture = true, Product = true,
                NativePinWindow.ScreenBoundedRest, NativePinWindow.ReholdPixelLimit,
                HoldWholeLimitBytes, DragSteps, DragStepX, DragStepY, JumpX, JumpY,
                Reading = "At each stop: SetZoom anchored at the top-left, settle, one second, then the bytes of every part "
                    + "the pin holds and the process totals (the memory samples zoom-NNN). From 259% on: thirty drag steps of "
                    + "(-16,-8) px eight milliseconds apart, then one jump of (-600,-300) px; per move the time until "
                    + "UpdateLayeredWindow returned and until DwmFlush returned, the newly drawn pixels and whether a live "
                    + "frame was shown. The pin then goes back to where it was before the next stop.",
            });
            ResidentProbePinMemory.Map Sample(string phase, ResidentProbePinMemory.Map? before, double zoom)
            {
                var map = recorder.SamplePin(scenario, phase, windows.Count, width, height, zoom, before, _pins.ClosedRetainedBytes);
                ObserveNativePinMemory(recorder, scenario, phase, windows.Count, windows);
                return map;
            }
            var previous = Sample("baseline", null, 1);
            windows.Add(await ProbeOpenLightPinAsync(options, recorder, diagnostics, scenario, 1, width, height));
            PinWindow pin = _pins.SnapshotOpenPinWindows().Last();
            NativePinWindow native = windows[0];
            await Task.Delay(1000);
            previous = Sample("open", previous, 1);
            RectD home = native.Bounds;
            foreach (double stop in ZoomStops)
            {
                string label = $"zoom-{stop * 100:0}";
                long surface = (long)Math.Round(native.State.ImageWidthDip * stop * native.DpiScale)
                    * (long)Math.Round(native.State.ImageHeightDip * stop * native.DpiScale) * 4;
                if (!NativePinWindow.ScreenBoundedRest && surface > HoldWholeLimitBytes)
                {
                    recorder.ObservePin(scenario, 1, label, new { Skipped = true, SurfaceBytes = surface, Reason = "The whole surface is over 1 GB." });
                    continue;
                }
                long zoomStarted = Stopwatch.GetTimestamp();
                pin.ResidentProbeSetZoom(stop);
                await ProbeNativePinWaitAsync(native, native.WaitForSettledAsync(), 60_000, label + " settled");
                ThrowNativePinError(native);
                native.FlushPresentation();
                recorder.PinFrame(scenario, 1, label + "-settled-frame",
                    new(0, Stopwatch.GetElapsedTime(zoomStarted).TotalMilliseconds, null));
                await Task.Delay(1000);
                previous = Sample(label, previous, stop);
                ObserveZoomStop(recorder, scenario, label, pin, native);
                if (stop < 2.5) continue;

                await MeasureOverflowMovesAsync(recorder, scenario, label, pin, native);
                pin.MovePhysicalForTest(home.Left, home.Top);
                if (native.IsLiveZooming)
                    await ProbeNativePinWaitAsync(native, native.WaitForSettledAsync(), 60_000, label + " settled at home");
                await Task.Delay(1000);
                previous = Sample(label + "-after-moves", previous, stop);
                ObserveZoomStop(recorder, scenario, label + "-after-moves", pin, native);
            }
            ObserveNativePin(recorder, scenario, 1, "native-after-zoom", native);
        }
        finally
        {
            _pins.BeforeShowForTest = null;
            _pins.CloseAll();
            await ProbeWaitAsync(() => !Windows.OfType<PinWindow>().Any(), 15_000, "zoom pins closed");
            await _pins.LastClosedEncodingForTest;
            foreach (NativePinWindow window in windows) await window.DrainAsync();
            windows.Clear();
            NativePinWindow.ScreenBoundedRest = rest;
            NativePinWindow.ReholdPixelLimit = limit;
        }
        await Task.Delay(2000);
        recorder.SamplePin(scenario, "closed", 0, width, height, 1, null, _pins.ClosedRetainedBytes);
    }

    private static void ObserveZoomStop(ResidentProbeRecorder recorder, string scenario, string label, PinWindow pin,
        NativePinWindow native)
    {
        using Process process = Process.GetCurrentProcess();
        // Read from the picture's size: reading the bitmap of a picture kept as its native source would build one.
        PinContent content = pin.PinnedContent;
        recorder.ObservePin(scenario, 1, label, new
        {
            native.Zoom, native.DpiScale, SurfaceWidth = native.PixelWidth, SurfaceHeight = native.PixelHeight,
            SurfaceBytes = (long)native.PixelWidth * native.PixelHeight * 4,
            Held = native.DisplayedBounds, native.IsDisplayClipped, native.IsLiveZooming,
            // Every part the pin holds, by section.
            SourceBytes = native.Source?.ByteLength ?? 0,
            ContentImageBytes = pin.IsPictureBitmapDropped ? 0 : (long)content.PixelWidth * content.PixelHeight * 4,
            native.DisplayBytes, native.DisplaySectionBytes, native.DisplayPrivateBytes,
            native.EdgeCacheBytes, native.WorkerSectionBytes,
            NativePinWindow.LiveWorkerBufferBytes, NativePinWindow.LiveDisplaySectionBytes,
            NativePinWindow.LiveDisplayPrivateBytes, NativePinPixelSource.LiveBytes,
            // The process totals at the same moment (the memory sample of this label has the map).
            ProcessPrivateBytes = process.PrivateMemorySize64, ProcessWorkingSetBytes = process.WorkingSet64,
            native.LastSettleMilliseconds, native.LastRasterMilliseconds, native.LastSettleUiMilliseconds,
        });
    }

    private async Task MeasureOverflowMovesAsync(ResidentProbeRecorder recorder, string scenario, string label,
        PinWindow pin, NativePinWindow native)
    {
        var steps = new List<object>();
        // A pin's top never goes above the desktop, so the overflow is to the right and below: the pin first
        // goes down (not measured; it only hides rows), and each step up and left brings rows and columns in.
        await SetUpMoveAsync(0, -DragSteps * DragStepY);
        long dragStarted = Stopwatch.GetTimestamp();
        for (int step = 1; step <= DragSteps + 1; step++)
        {
            bool jump = step > DragSteps;
            if (jump)
            {
                if (native.IsLiveZooming)
                    await ProbeNativePinWaitAsync(native, native.WaitForSettledAsync(), 60_000, label + " settled after the drag");
                await SetUpMoveAsync(0, -JumpY);
            }
            RectD before = native.Bounds;
            int reholds = native.ReholdCount, rasters = native.RasterCount;
            long started = Stopwatch.GetTimestamp();
            pin.MovePhysicalForTest(before.Left + (jump ? JumpX : DragStepX), before.Top + (jump ? JumpY : DragStepY));
            ThrowNativePinError(native);
            double submitted = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            native.FlushPresentation();
            double complete = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            string name = jump ? $"{label}-jump" : $"{label}-drag-{step:00}";
            recorder.PinFrame(scenario, 1, name, new(submitted, complete, null));
            steps.Add(new
            {
                Step = name, SubmittedMs = submitted, CompleteMs = complete, native.LastMoveMilliseconds,
                Moved = new { X = native.Bounds.Left - before.Left, Y = native.Bounds.Top - before.Top },
                Rehold = native.ReholdCount - reholds, native.LastMoveFreshPixels, native.LastReholdPixels, native.LastReholdMilliseconds,
                native.LastReholdOutlineMilliseconds,
                RasterDelta = native.RasterCount - rasters, native.LastMoveDrewLive, native.IsLiveZooming,
                native.LastPresentMilliseconds, native.LastRasterMilliseconds, Held = native.DisplayedBounds,
                native.DisplayBytes,
            });
            if (!jump) await Task.Delay(8);
        }
        double dragMs = Stopwatch.GetElapsedTime(dragStarted).TotalMilliseconds;
        long settleStarted = Stopwatch.GetTimestamp();
        bool live = native.IsLiveZooming;
        if (live) await ProbeNativePinWaitAsync(native, native.WaitForSettledAsync(), 60_000, label + " settled after the jump");
        native.FlushPresentation();
        double settledAfterJump = Stopwatch.GetElapsedTime(settleStarted).TotalMilliseconds;
        if (live) recorder.PinFrame(scenario, 1, $"{label}-jump-to-settled-frame", new(0, settledAfterJump, null));
        recorder.ObservePin(scenario, 1, label + "-moves", new
        {
            DragMilliseconds = dragMs, JumpSettledAfterMs = live ? settledAfterJump : (double?)null,
            NativePinWindow.ReholdPixelLimit, Steps = steps,
        });

        async Task SetUpMoveAsync(int dx, int dy)
        {
            pin.MovePhysicalForTest(native.Bounds.Left + dx, native.Bounds.Top + dy);
            if (native.IsLiveZooming)
                await ProbeNativePinWaitAsync(native, native.WaitForSettledAsync(), 60_000, label + " settled after a set-up move");
            await Task.Delay(300);
        }
    }
}
