using System.Diagnostics;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;

namespace MyCapture.App;

public partial class App
{
    private bool _probeFreeformUsed;

    /// <summary>Opt-in synthetic input against the real selector. Never moves the system cursor.</summary>
    private async Task<bool> ProbeFreeformSelectionAsync(ResidentProbeRecorder recorder, string name,
        NativeCaptureOverlay? native, CaptureOverlayView? view, Action beforeRelease)
    {
        if (s_residentProbe is not { } options || (!options.Has("selector-freeform")
            && !(options.Has("selector-freeform-once") && !_probeFreeformUsed))) return false;
        _probeFreeformUsed = true;
        await ProbeWaitAsync(() => native?.IsReady ?? view?.FirstFramePresentedForTest == true, 10_000, "freeform input ready");
        var frame = native?.Frame ?? view?.Frame ?? throw new InvalidOperationException("The freeform probe needs a frozen frame.");
        PointD[] points =
        [
            new(frame.PixelWidth * 0.2, frame.PixelHeight * 0.2),
            new(frame.PixelWidth * 0.45, frame.PixelHeight * 0.16),
            new(frame.PixelWidth * 0.7, frame.PixelHeight * 0.3),
            new(frame.PixelWidth * 0.68, frame.PixelHeight * 0.65),
            new(frame.PixelWidth * 0.4, frame.PixelHeight * 0.75),
            new(frame.PixelWidth * 0.18, frame.PixelHeight * 0.5),
        ];
        PointD pointer = points[0].Offset(frame.ScreenBounds.Left, frame.ScreenBounds.Top);
        Func<PointD> read = native?.ReadCursor ?? view!.ReadCursor;
        Func<PointD, bool> place = native?.PlaceCursor ?? view!.PlaceCursor;
        bool? simulatedInside = view?.SimulatedPointerInside;
        if (native is not null) { native.ReadCursor = () => pointer; native.PlaceCursor = _ => false; }
        else { view!.ReadCursor = () => pointer; view.PlaceCursor = _ => false; view.SimulatedPointerInside = true; }
        try
        {
            if (native is not null)
            {
                native.SelectMode(CaptureSelectorMode.Freeform);
                native.UpdatePointer(precision: false, active: false);
                native.BeginSelection(points[0]);
            }
            else { view!.SelectMode(CaptureSelectorMode.Freeform); view.SimulatePointerDown(); }
            for (int index = 1; index < points.Length; index++)
            {
                pointer = points[index].Offset(frame.ScreenBounds.Left, frame.ScreenBounds.Top);
                long began = Stopwatch.GetTimestamp();
                if (native is not null)
                {
                    native.UpdatePointer(precision: false, active: false);
                    native.MoveSelection(points[index]);
                    _ = NativeSelectorInterop.UpdateWindow(native.Handle);
                }
                else
                {
                    view!.SimulatePointerMove();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (ProbeCompleteRender is not { } completeRender || ProbeMediaContextFrom?.Invoke(null, [Dispatcher]) is not { } context)
                        throw new InvalidOperationException("WPF render-thread completion is unavailable.");
                    completeRender.Invoke(context, null);
                }
                recorder.Time($"{name}: freeform point {index} → render submission", Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                int result = NativeSelectorInterop.DwmFlush();
                if (result < 0) throw new InvalidOperationException("DwmFlush failed for the freeform probe.");
                recorder.Time($"{name}: freeform point {index} → DWM completion", Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            }
            recorder.Note("Freeform point timings start at synthetic pointer-handler invocation. Render submission and DWM completion are diagnostic synchronization endpoints, not physical display/scan-out latency. The probe uses an isolated profile and synthetic frozen pixels.");
            beforeRelease();
            if (native is not null) native.EndSelection(points[^1]);
            else view!.SimulatePointerUp();
            return true;
        }
        finally
        {
            if (native is not null) { native.ReadCursor = read; native.PlaceCursor = place; }
            else { view!.ReadCursor = read; view.PlaceCursor = place; view.SimulatedPointerInside = simulatedInside; }
        }
    }
}
