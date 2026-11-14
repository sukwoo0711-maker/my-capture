using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;

namespace MyCapture.App;

/// <summary>
/// Opt-in resident probe scenario <c>selector-drag</c>: opens the real native selector on the synthetic
/// desktop frame and drags a selection through its own window procedure (WM_MOUSEMOVE sent to the
/// selector HWND), timing each pointer sample. The system cursor is never read or moved: the selector's
/// pointer reader is substituted, and nothing is injected into the input queue.
/// </summary>
public partial class App
{
    internal const string SelectorDragScenario = "selector-drag";
    private const int SelectorDragRounds = 4;

    private static class SelectorDragNative
    {
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool UpdateWindow(IntPtr hwnd);
    }

    private readonly record struct SelectorDragStep(string Phase, double X, double Y);

    private static List<SelectorDragStep> SelectorDragPath(RectD desktop)
    {
        // Screen coordinates: grow on the first monitor, wiggle at the corner, cross to the right edge
        // of the virtual desktop (a second monitor when there is one), then shrink back.
        var steps = new List<SelectorDragStep>(360);
        double x = desktop.Left + 300, y = desktop.Top + 200;
        double right = desktop.Right - 40, bottom = desktop.Bottom - 40;
        for (int i = 0; i < 150; i++) { x = Math.Min(right, x + 14); y = Math.Min(bottom, y + 6); steps.Add(new("grow", x, y)); }
        for (int i = 0; i < 60; i++) steps.Add(new("wiggle", x + (i % 4 < 2 ? 2 : -1), y + (i % 3 == 0 ? 1 : -1)));
        double step = Math.Max(4, (right - x) / 60);
        for (int i = 0; i < 60; i++) { x = Math.Min(right, x + step); steps.Add(new("wide", x, y)); }
        for (int i = 0; i < 60; i++) { x = Math.Max(desktop.Left + 400, x - 50); y = Math.Max(desktop.Top + 260, y - 12); steps.Add(new("shrink", x, y)); }
        return steps;
    }

    private async Task ProbeSelectorDragAsync(ResidentProbeRecorder recorder, ResidentProbeOptions probe)
    {
        var csv = new StringBuilder("round,phase,index,handler_ms,paint_ms,ui_ms,render_ms,total_ms,alloc_bytes\n");
        for (int round = 1; round <= SelectorDragRounds; round++)
        {
            _ = await ProbeShortcutToSelectorAsync(recorder, SelectorDragScenario);
            NativeCaptureOverlay native = _overlay?.NativeOverlayForProbe
                ?? throw new InvalidOperationException("The drag probe needs the native selector.");
            await ProbeWaitAsync(() => native.IsReady && native.IsChromeReady, 10_000, "drag probe selector ready");
            await Task.Delay(300);
            RectD desktop = native.ScreenBounds;
            List<SelectorDragStep> path = SelectorDragPath(desktop);
            PointD pointer = new(desktop.Left + 300, desktop.Top + 200);
            Func<PointD> read = native.ReadCursor;
            Func<PointD, bool> place = native.PlaceCursor;
            native.ReadCursor = () => pointer;
            native.PlaceCursor = _ => false;
            var totals = new List<double>(path.Count);
            var handlers = new List<double>(path.Count);
            long allocated = 0;
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            try
            {
                // Pointer down through the same entry point as WM_LBUTTONDOWN, without SetCapture.
                native.BeginSelection(new PointD(pointer.X - desktop.Left, pointer.Y - desktop.Top));
                _ = SelectorDragNative.UpdateWindow(native.Handle);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                for (int index = 0; index < path.Count; index++)
                {
                    SelectorDragStep sample = path[index];
                    pointer = new PointD(sample.X, sample.Y);
                    long bytes = GC.GetAllocatedBytesForCurrentThread();
                    long began = Stopwatch.GetTimestamp();
                    _ = SelectorDragNative.SendMessage(native.Handle, 0x0200, new IntPtr(1), PackClient(pointer, desktop));
                    long handled = Stopwatch.GetTimestamp();
                    _ = SelectorDragNative.UpdateWindow(native.Handle);
                    long painted = Stopwatch.GetTimestamp();
                    long stepBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                    allocated += stepBytes;
                    // Layout and render submission of the WPF chrome (size readout, magnifier) queued by the move.
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    long ui = Stopwatch.GetTimestamp();
                    if (ProbeCompleteRender is { } completeRender && ProbeMediaContextFrom?.Invoke(null, [Dispatcher]) is { } context)
                        completeRender.Invoke(context, null);
                    long rendered = Stopwatch.GetTimestamp();
                    double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
                    double total = Ms(began, rendered);
                    totals.Add(total);
                    handlers.Add(Ms(began, handled));
                    csv.Append(CultureInfo.InvariantCulture, $"{round},{sample.Phase},{index},{Ms(began, handled):0.000},{Ms(handled, painted):0.000},{Ms(painted, ui):0.000},{Ms(ui, rendered):0.000},{total:0.000},{stepBytes}\n");
                }
            }
            finally
            {
                native.ReadCursor = read;
                native.PlaceCursor = place;
            }
            string label = $"{SelectorDragScenario} r{round}";
            recorder.Time($"{label}: move total p50", Percentile(totals, 0.5));
            recorder.Time($"{label}: move total p95", Percentile(totals, 0.95));
            recorder.Time($"{label}: move total max", totals.Max());
            recorder.Time($"{label}: move handler p50", Percentile(handlers, 0.5));
            recorder.Time($"{label}: move handler p95", Percentile(handlers, 0.95));
            recorder.Note($"{label}: {path.Count} moves, {allocated / 1024.0:0.0} KB allocated on the UI thread by handler+paint, "
                + $"GC {GC.CollectionCount(0) - gen0}/{GC.CollectionCount(1) - gen1}/{GC.CollectionCount(2) - gen2}");
            _overlay?.Cancel();
            await ProbeWaitAsync(() => _overlay?.IsActive != true, 10_000, "drag probe selector closed");
            await Task.Delay(500);
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(probe.Output, "selector-drag-steps.csv"), csv.ToString());
        recorder.Note("selector-drag: synthetic WM_MOUSEMOVE sent to the selector HWND with a substituted pointer reader; "
            + "total = handler + WM_PAINT + WPF chrome layout + render-thread completion, not DWM present or scan-out.");
    }

    private static IntPtr PackClient(PointD screen, RectD desktop)
    {
        int x = (int)(screen.X - desktop.Left) & 0xffff, y = (int)(screen.Y - desktop.Top) & 0xffff;
        return new IntPtr((y << 16) | x);
    }

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0) return double.NaN;
        var ordered = values.OrderBy(value => value).ToList();
        return ordered[Math.Max(0, (int)Math.Ceiling(fraction * ordered.Count) - 1)];
    }
}
