using System.Globalization;
using System.IO;
using System.Text;

namespace MyCapture.App.Tests;

internal static class PinOpenReport
{
    internal static string F(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    internal static double? Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return null;
        // Nearest rank; a single cold/burst sample intentionally has identical median/p95/max.
        if (percentile == 0.5 && sorted.Length % 2 == 0) return (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }
    private static string Q(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string Time(long start, long timestamp) => timestamp == 0 ? "" : F(PinOpenProbe.Ms(start, timestamp));

    internal static void Write(string output, string kind, IReadOnlyList<PinOpenSample> samples,
        IReadOnlyList<PinRendererSample> renderers, IReadOnlyList<PinBurstSample> bursts,
        IReadOnlyList<string> environment, IReadOnlyList<string> notes, Exception? failure)
    {
        var csv = new StringBuilder("kind,phase,variant,repeat,status,completed,start_ticks,start_ms,rendered_ms,rendered_upper_bound_ms,show_enter_ms,shown_ms,loaded_ms,first_frame_ms,reveal_end_estimate_ms,overlay_first_frame_ms,delta_vs_a_ms,frame_window_ms,frame_count,gap_count,gap_max_ms,gap_p95_ms,gaps_gt25,show_to_first_callback_ms,tail_without_callback_ms,foreground_before,foreground_after,foreground_unchanged,foreground_observable,overlay_activated_count,monitor_dpi_scale,wpf_dpi_scale,window_dpi,refresh_hz\n");
        var rawFrames = new StringBuilder("kind,phase,variant,repeat,callback_ms,rendering_time_ms,gap_ms\n");
        foreach (PinOpenSample sample in samples)
        {
            PinFrames frames = sample.Frames;
            csv.AppendLine(string.Join(",", sample.Kind, sample.Phase, sample.Variant, sample.Repeat, Q(sample.Status), sample.Completed,
                sample.Start, "0", sample.RenderedAtStart ? "0" : "", Time(sample.Start, sample.RenderedUpperBound),
                Time(sample.Start, sample.ShowEnter), Time(sample.Start, sample.Shown), Time(sample.Start, sample.Loaded),
                Time(sample.Start, sample.FirstFrame), sample.Loaded == 0 ? "" : F(PinOpenProbe.Ms(sample.Start, sample.Loaded) + (sample.AnimationsEnabled ? 167 : 0)),
                sample.OverlayFirstFrame is long overlay ? Time(sample.Start, overlay) : "", F(sample.DeltaVsA), sample.WindowMs,
                frames.Samples.Count, frames.Gaps.Length, F(frames.MaxGap), F(frames.P95Gap), frames.Over25,
                Time(sample.ShowEnter, sample.FirstFrame), Tail(frames, sample.Shown, sample.WindowMs),
                sample.ForegroundBefore.ToString("X"), sample.ForegroundAfter.ToString("X"),
                ForegroundEqual(sample), sample.ForegroundBefore != IntPtr.Zero && sample.ForegroundAfter != IntPtr.Zero,
                sample.ActivationCount, F(sample.MonitorScale), F(sample.WpfScale), sample.WindowDpi, F(sample.RefreshHz)));
            AppendFrames(rawFrames, sample.Kind, sample.Phase, sample.Variant, sample.Repeat, sample.Start, frames);
        }
        WriteCsv(output, "pin-open.csv", csv);
        var rendererCsv = new StringBuilder("kind,phase,repeat,parse_ms,raster_ms,total_ms,pixel_width,pixel_height,dpi_x,sta_thread_id\n");
        foreach (PinRendererSample row in renderers)
            rendererCsv.AppendLine(string.Join(",", row.Kind, row.Repeat == 0 ? "first" : "warm", row.Repeat,
                F(row.ParseMs), F(row.RasterMs), F(row.TotalMs), row.PixelWidth, row.PixelHeight, F(row.DpiX), row.ThreadId));
        WriteCsv(output, "renderer.csv", rendererCsv);
        var burstCsv = new StringBuilder("count,shown_count,start_ticks,first_show_enter_ms,first_shown_ms,all_shown_ms,loop_end_ms,first_callback_ms,frame_window_ms,frame_count,gap_count,gap_max_ms,gap_p95_ms,gaps_gt25,tail_without_callback_ms\n");
        foreach (PinBurstSample sample in bursts)
        {
            PinFrames frames = sample.Frames;
            burstCsv.AppendLine(string.Join(",", sample.Count, sample.ShownCount, sample.Start,
                Time(sample.Start, sample.FirstShowEnter), Time(sample.Start, sample.FirstShown), Time(sample.Start, sample.AllShown),
                Time(sample.Start, sample.LoopEnd), Time(sample.FirstShowEnter, sample.FirstFrame), BurstWindow(sample), frames.Samples.Count,
                frames.Gaps.Length, F(frames.MaxGap), F(frames.P95Gap), frames.Over25, Tail(frames, sample.AllShown, PinOpenProbe.BurstTailMs)));
            AppendFrames(rawFrames, "mixed", "restore", "burst", sample.Count, sample.Start, frames);
        }
        WriteCsv(output, "restore-burst.csv", burstCsv);
        WriteCsv(output, "frame-samples.csv", rawFrames);

        var summary = new StringBuilder("Pin open probe: " + kind + Environment.NewLine);
        foreach (string line in environment.Concat(notes)) summary.AppendLine(line);
        summary.AppendLine("Percentiles use nearest rank (median averages the middle pair). Units: ms. Blank = unavailable.");
        foreach (var group in samples.GroupBy(row => (row.Kind, row.Phase, row.Variant)))
        {
            PinOpenSample[] valid = group.Where(row => row.Completed).ToArray();
            summary.AppendLine($"{group.Key}: attempted={group.Count()}; complete={valid.Length}; incomplete/failed/skipped={group.Count() - valid.Length}; {Stats("shown", valid.Select(r => PinOpenProbe.Ms(r.Start, r.Shown)))}; {Stats("firstFrame", valid.Select(r => PinOpenProbe.Ms(r.Start, r.FirstFrame)))}; {Stats("rendered upper bound", valid.Select(r => PinOpenProbe.Ms(r.Start, r.RenderedUpperBound)))}");
            bool foregroundKnown = valid.Length > 0 && valid.All(r => r.ForegroundBefore != IntPtr.Zero && r.ForegroundAfter != IntPtr.Zero);
            string unchanged = foregroundKnown ? valid.All(r => r.ForegroundBefore == r.ForegroundAfter).ToString() : "unknown";
            summary.AppendLine($"  {Stats("delta vs A", valid.Where(r => r.DeltaVsA.HasValue).Select(r => r.DeltaVsA!.Value))}; {Stats("overlay firstFrame", valid.Where(r => r.OverlayFirstFrame.HasValue).Select(r => PinOpenProbe.Ms(r.Start, r.OverlayFirstFrame!.Value)))}; {Stats("frame gaps", valid.SelectMany(r => r.Frames.Gaps))}; >25ms={valid.Sum(r => r.Frames.Over25)}; callbacks={valid.Sum(r => r.Frames.Samples.Count)}; foreground unchanged={unchanged}; foreground observable={foregroundKnown}; overlay Activated (all attempts)={group.Sum(r => r.ActivationCount)}");
            foreach (PinOpenSample invalid in group.Where(row => !row.Completed))
                summary.AppendLine($"  repetition {invalid.Repeat}: {invalid.Status}; raw partial observations retained in CSV.");
        }
        foreach (var group in renderers.GroupBy(row => (row.Kind, Phase: row.Repeat == 0 ? "first" : "warm")))
            summary.AppendLine($"Renderer {group.Key}: {Stats("parse", group.Select(row => row.ParseMs))}; {Stats("raster", group.Select(row => row.RasterMs))}; {Stats("total", group.Select(row => row.TotalMs))}");
        foreach (PinBurstSample burst in bursts)
            summary.AppendLine($"Restore {burst.Count}: shown={burst.ShownCount}; firstShowEnter={Time(burst.Start, burst.FirstShowEnter)}; allShown={Time(burst.Start, burst.AllShown)}; restore task done={Time(burst.Start, burst.LoopEnd)}; first Show to first callback={Time(burst.FirstShowEnter, burst.FirstFrame)}; frame window={BurstWindow(burst)}; {Stats("frame gaps", burst.Frames.Gaps)}; >25ms={burst.Frames.Over25}; callbacks={burst.Frames.Samples.Count}; tail without callback={Tail(burst.Frames, burst.AllShown, PinOpenProbe.BurstTailMs)}");
        if (failure is not null) summary.AppendLine("FAILURE: " + failure);
        File.WriteAllText(Path.Combine(output, "summary.txt"), summary.ToString(), new UTF8Encoding(false));

        // Each sub-measurement has a compact self-contained folder, while the section-level tables join all rows.
        WritePart(output, "single", "pin-open.csv", csv, samples.Where(row => row.Phase is "cold" or "warm"), summary);
        WritePart(output, "overlay", "pin-open.csv", csv, samples.Where(row => row.Phase.StartsWith("overlay", StringComparison.Ordinal)), summary);
        WriteSimplePart(output, "renderer", "renderer.csv", rendererCsv, summary);
        WriteSimplePart(output, "restore", "restore-burst.csv", burstCsv, summary);
    }

    private static void WritePart(string output, string folder, string filename, StringBuilder all,
        IEnumerable<PinOpenSample> selected, StringBuilder summary)
    {
        var keys = selected.Select(row => string.Join(",", row.Kind, row.Phase, row.Variant, row.Repeat) + ",").ToArray();
        if (keys.Length == 0) return;
        string[] lines = all.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var csv = new StringBuilder(lines[0] + Environment.NewLine);
        foreach (string line in lines.Skip(1)) if (keys.Any(key => line.StartsWith(key, StringComparison.Ordinal))) csv.AppendLine(line);
        WriteSimplePart(output, folder, filename, csv, summary);
    }
    private static void WriteSimplePart(string output, string folder, string filename, StringBuilder csv, StringBuilder summary)
    {
        string path = Path.Combine(output, folder);
        Directory.CreateDirectory(path);
        WriteCsv(path, filename, csv);
        File.WriteAllText(Path.Combine(path, "summary.txt"), summary.ToString(), new UTF8Encoding(false));
    }
    private static string BurstWindow(PinBurstSample sample) => sample.FirstShowEnter == 0 || sample.AllShown == 0
        ? "" : F(PinOpenProbe.Ms(sample.FirstShowEnter, sample.AllShown) + PinOpenProbe.BurstTailMs);
    private static string Tail(PinFrames frames, long shown, int duration) => frames.Samples.Count == 0
        ? F(duration) : F(Math.Max(0, duration - PinOpenProbe.Ms(shown, frames.Samples[^1].Timestamp)));
    private static string ForegroundEqual(PinOpenSample sample) => sample.ForegroundBefore == IntPtr.Zero || sample.ForegroundAfter == IntPtr.Zero
        ? "" : (sample.ForegroundBefore == sample.ForegroundAfter).ToString();
    private static string Stats(string name, IEnumerable<double> values)
    {
        double[] data = values.ToArray();
        return $"{name} n={data.Length} median/p95/max={F(Percentile(data, 0.5))}/{F(Percentile(data, 0.95))}/{F(data.Length == 0 ? null : data.Max())}";
    }
    private static void WriteCsv(string output, string name, StringBuilder csv) =>
        File.WriteAllText(Path.Combine(output, name), csv.ToString(), new UTF8Encoding(true));
    private static void AppendFrames(StringBuilder csv, string kind, string phase, string variant, int repeat, long start, PinFrames frames)
    {
        for (int i = 0; i < frames.Samples.Count; i++)
        {
            PinFrame frame = frames.Samples[i];
            csv.AppendLine(string.Join(",", kind, phase, variant, repeat, Time(start, frame.Timestamp), F(frame.RenderingTime.TotalMilliseconds),
                i == 0 ? "" : F((frame.RenderingTime - frames.Samples[i - 1].RenderingTime).TotalMilliseconds)));
        }
    }
}
