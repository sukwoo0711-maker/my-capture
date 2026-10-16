using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>Diagnostic-only player configuration and H8/H9 comparisons; no product callers.</summary>
internal static class VideoRangePathDiagnostic
{
    internal static VideoRangePathOptions OptionsFor(VideoRangeCell cell, double dpiScale = 1)
    {
        if (cell.Kind != "path" || cell.PathVariant is null) throw new ArgumentException("Not a path cell.", nameof(cell));
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) throw new ArgumentOutOfRangeException(nameof(dpiScale));
        var product = new GalleryPlayerDiagnosticOptions();
        // 0.95 leaves room for pixel rounding while guaranteeing at least 0.9 physical scale.
        double largeHeight = cell.Height * 0.95 / dpiScale;
        GalleryPlayerDiagnosticOptions? gallery = cell.PathVariant switch
        {
            "gallery-product" => product,
            "gallery-pause-immediately" => product with { OpenMode = GalleryPlayerOpenMode.PauseImmediately },
            "gallery-playing" => product with { OpenMode = GalleryPlayerOpenMode.Playing },
            "gallery-large" => product with { FrameHeightDip = largeHeight },
            "gallery-no-exclusion" => product with { CaptureExclusion = false },
            "gallery-no-clip" => product with { RoundedClip = false },
            "gallery-plain-window" => product with { ProductWindowStyle = false },
            "gallery-all-editor-like" => new(GalleryPlayerOpenMode.PauseImmediately, largeHeight, false, false, false),
            "editor-product" or "editor-small" => null,
            _ => throw new ArgumentException("Unknown path variant.", nameof(cell)),
        };
        return new(cell.PathVariant, gallery);
    }

    internal static double PhysicalScale(MediaElement media, VideoRangeCell cell)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(media);
        return Math.Min(media.ActualWidth * dpi.DpiScaleX / cell.Width,
            media.ActualHeight * dpi.DpiScaleY / cell.Height);
    }

    internal static void FitSmallEditor(Window editor, MediaElement media, VideoRangeCell cell, double targetScale)
    {
        // Resize only the diagnostic host. The product's 555-DIP minimum is too tall to match
        // the small gallery at 100% DPI; relax that window minimum only in this diagnostic cell.
        // Retain the editor's content, preview and controls, including their own layout minima.
        // A height adjustment follows the actual laid-out video, including all chrome/timeline rows.
        editor.MinHeight = 0;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            editor.UpdateLayout();
            double scale = PhysicalScale(media, cell);
            if (Math.Abs(scale - targetScale) <= 0.01) return;
            double dy = (targetScale - scale) * cell.Height / VisualTreeHelper.GetDpi(media).DpiScaleY;
            double height = Math.Max(editor.MinHeight, editor.Height + dy);
            if (Math.Abs(height - editor.Height) < 0.5) break;
            editor.Height = height;
        }
        editor.UpdateLayout();
        if (Math.Abs(PhysicalScale(media, cell) - targetScale) > 0.05)
            throw new InvalidOperationException($"editor-small scale {PhysicalScale(media, cell):F3} is outside target {targetScale:F3} +/-0.05.");
    }

    internal static string FormatOptions(VideoRangePathOptions? options) => options is null ? "unavailable"
        : options.Gallery is { } g
            ? $"open={g.OpenMode}, frame={g.FrameHeightDip?.ToString("F2", CultureInfo.InvariantCulture) ?? "220(product)"}DIP, "
                + $"exclusion={g.CaptureExclusion}, rounded-clip={g.RoundedClip}, product-window={g.ProductWindowStyle}"
            : $"{options.Variant}, target-scale={options.TargetScale?.ToString("F3", CultureInfo.InvariantCulture) ?? "product(1100x760)"}";

    internal static string FormatVerification(RangeVerificationResult? verification) => verification is null ? "unavailable"
        : $"kind={verification.Kind}, model={verification.Model?.ToString() ?? "none"}, status={verification.Status}, "
            + $"errors(identity/retained/expanded)={verification.Errors.Identity:F3}/{verification.Errors.StudioRetained:F3}/{verification.Errors.ExpandedAsStudio:F3}";

    private static string Rgb(VideoRangeObservation observation, int index) => index >= observation.Patches.Length ? "missing"
        : $"{observation.Patches[index].R}/{observation.Patches[index].G}/{observation.Patches[index].B}";

    internal static string FormatGreys(VideoRangeObservation observation, string pattern) => pattern == "colour-patches"
        ? $"16={Rgb(observation, 1)},64={Rgb(observation, 2)},192={Rgb(observation, 4)},235={Rgb(observation, 5)}"
        : $"16=n/a,64={Rgb(observation, 2)},192={Rgb(observation, 4)},235=n/a";

    private static string FormatZeroChannels(VideoRangeObservation observation, string pattern)
    {
        if (pattern != "colour-patches") return "black=" + Rgb(observation, 0);
        var channels = new List<string>();
        for (int i = 0; i < Math.Min(observation.Patches.Length, VideoRangeColourPattern.Patches.Length); i++)
        {
            var source = VideoRangeColourPattern.Patches[i];
            var actual = observation.Patches[i];
            if (source.R == 0) channels.Add($"{source.Name}.R={actual.R}");
            if (source.G == 0) channels.Add($"{source.Name}.G={actual.G}");
            if (source.B == 0) channels.Add($"{source.Name}.B={actual.B}");
        }
        return string.Join(",", channels);
    }

    private static VideoRangeTimePoint[] Scheduled(VideoRangeCellResult result) => result.Samples.FirstOrDefault()?.TimeSeries
        ?.Where(p => p.Phase == "ready").ToArray() ?? [];

    private static string Endpoint(VideoRangeCellResult result, bool last)
    {
        VideoRangeTimePoint[] points = Scheduled(result);
        if (points.Length == 0) return "missing";
        VideoRangeTimePoint point = last ? points[^1] : points[0];
        return $"{point.ElapsedSinceReadyMs:F0}ms [{FormatGreys(point.Observation, result.Cell.Pattern)}; "
            + $"channel-0={FormatZeroChannels(point.Observation, result.Cell.Pattern)}]";
    }

    // Compare both endpoints, using the largest RGB difference at any shared grey patch.
    // Missing/transparent observations and failed cells can never count as a match.
    private static double? GreyDifference(VideoRangeCellResult left, VideoRangeCellResult right, bool sharedGreys)
    {
        var a = Scheduled(left); var b = Scheduled(right);
        if (left.Failure is not null || right.Failure is not null || a.Length < 2 || b.Length < 2) return null;
        int[] indices = sharedGreys ? [0, 2, 4, 6] : [1, 2, 4, 5];
        int maximum = 0;
        foreach (var (x, y) in new[] { (a[0], b[0]), (a[^1], b[^1]) })
        foreach (int index in indices)
        {
            if (index >= x.Observation.Patches.Length || index >= y.Observation.Patches.Length) return null;
            var p = x.Observation.Patches[index]; var q = y.Observation.Patches[index];
            if (p.A < 250 || q.A < 250) return null;
            maximum = Math.Max(maximum, Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B))));
        }
        return maximum;
    }

    private static string Compare(VideoRangeCellResult result, VideoRangeCellResult? baseline, string name, bool sharedGreys = false)
    {
        if (baseline is null || baseline.Failure is not null || result.Failure is not null) return $"comparison with {name}=unavailable";
        var a = result.Samples.FirstOrDefault()?.Verification;
        var b = baseline.Samples.FirstOrDefault()?.Verification;
        string model = a is null || b is null ? "unavailable"
            : $"{(a.Kind == b.Kind && a.Model == b.Model ? "matches" : "differs from")} {name}";
        double? difference = GreyDifference(result, baseline, sharedGreys);
        string greys = difference is null ? "unavailable"
            : $"{(difference > 4 ? "differs from" : "matches")} {name} (max delta={difference:F1}, threshold>4)";
        return $"kind/model: {model}; grey first/last: {greys}";
    }

    internal static IEnumerable<string> Summarize(IReadOnlyList<VideoRangeCellResult> results)
    {
        foreach (var range in results.Where(r => r.Cell.Kind == "path").GroupBy(r => r.Cell.FullRangeSteps[0]))
        {
            string input = range.Key ? "full" : "studio";
            VideoRangeCellResult? Find(string variant, string pattern = "colour-patches") =>
                range.SingleOrDefault(r => r.Cell.PathVariant == variant && r.Cell.Pattern == pattern);
            foreach (var result in range)
            {
                var sample = result.Samples.FirstOrDefault();
                var cycle = sample?.TimeSeries?.SingleOrDefault(p => p.Phase == "after-play-pause");
                yield return $"H8: {input} {result.Cell.PathVariant} {result.Cell.Pattern}: {FormatVerification(sample?.Verification)}; "
                    + $"first={Endpoint(result, false)}; last={Endpoint(result, true)}; "
                    + (cycle is null ? string.Empty : $"after-play-pause={cycle.ElapsedSinceReadyMs:F0}ms "
                        + $"[{FormatGreys(cycle.Observation, result.Cell.Pattern)}; channel-0={FormatZeroChannels(cycle.Observation, result.Cell.Pattern)}]; ")
                    + $"cell failure={result.Failure ?? "none"}.";
                if (result.Cell.Pattern == "colour-patches" && result.Cell.PathVariant is not ("gallery-product" or "editor-product" or "gallery-all-editor-like"))
                    yield return $"H8: {input} single-variable {result.Cell.PathVariant}: "
                        + Compare(result, Find("gallery-product"), "gallery-product") + "; "
                        + Compare(result, Find("editor-product"), "editor-product") + ".";
            }
            foreach (string variant in new[] { "gallery-product", "editor-small" })
            {
                var colour = Find(variant); var bars = Find(variant, "grey-bars");
                var a = colour?.Samples.FirstOrDefault(); var b = bars?.Samples.FirstOrDefault();
                bool sameScale = a is not null && b is not null && a.ScaleX > 0 && b.ScaleX > 0
                    && Math.Abs(a.ScaleX - b.ScaleX) <= 0.01 && Math.Abs(a.ScaleY - b.ScaleY) <= 0.01;
                yield return $"H9: {input} {variant} colour-patches vs grey-bars: "
                    + (colour is not null && bars is not null && sameScale
                        ? $"same host/scale ({a!.ScaleX:F3}/{b!.ScaleX:F3}); " + Compare(colour, bars, "grey-bars", sharedGreys: true)
                        : "comparison unavailable (missing samples or scale mismatch)")
                    + "; shared greys=0/64/192/255; grey-bars has no grey 16/235. Content dependence is an observation, not a driver-cause attribution.";
            }
        }
    }

    internal static IEnumerable<string> ReportTable(IReadOnlyList<VideoRangeCellResult> results)
    {
        yield return "PATH CELLS: cell | range | options | scale / presented pixels | verification kind/model/status/errors | target / actual since ready | position / playing | grey 16/64/192/235 (R/G/B)";
        foreach (var result in results.Where(r => r.Cell.Kind == "path"))
        {
            var sample = result.Samples.FirstOrDefault();
            string prefix = $"{result.Cell.Id} | {(result.Cell.FullRangeSteps[0] ? "full" : "studio")} | {FormatOptions(result.PathOptions)} | "
                + (sample is null ? "unavailable" : $"{sample.ScaleX:F3}/{sample.ScaleY:F3} / {sample.PresentedWidthPixels:F1}x{sample.PresentedHeightPixels:F1}")
                + $" | {FormatVerification(sample?.Verification)}";
            if (sample?.TimeSeries is not { Length: > 0 } series) yield return prefix + $" | missing | {result.Failure ?? "no time series"}";
            else foreach (var point in series)
                yield return prefix + $" | {point.Phase} {point.TargetMs:F0}/{point.ElapsedSinceReadyMs:F1}ms | "
                    + $"{point.PositionMs:F1}ms / {point.Playing} | {FormatGreys(point.Observation, result.Cell.Pattern)}";
        }
    }
}
