using System.Diagnostics;
using System.IO;
using MyCapture.App.Recording;
using MyCapture.Core.Storage;

namespace MyCapture.App.Diagnostics;

internal sealed record VideoRangeCell(string Id, string Host, int Width, int Height, bool[] FullRangeSteps,
    string Kind = "matrix");
internal sealed record VideoRangeArguments(string OutputDirectory, VideoRangeCell? Cell);
internal sealed record VideoRangePatch(byte R, byte G, byte B, byte A);
internal sealed record VideoRangeObservation(VideoRangePatch[] Patches, string Classification);
internal sealed record VideoRangeSample(int Step, string InputRange, string Classification,
    VideoRangeObservation[] Observations, double WindowWidthDip, double WindowHeightDip,
    double ContentWidthDip, double ContentHeightDip, double PresentedWidthPixels, double PresentedHeightPixels,
    double ScaleX, double ScaleY, double DpiX, double DpiY, int RenderTier, bool PixelShader20,
    bool CalibrationStartedBeforeSample);
internal sealed record VideoRangeCellResult(VideoRangeCell Cell, int ProcessId, VideoRangeSample[] Samples,
    VideoRangeCalibrationResult Calibration, string? CalibrationFailure, string[] Adapters,
    string? AdapterQueryFailure, string? Failure);

/// <summary>Pure matrix, command-line and observation-summary contracts for the device diagnostic.</summary>
internal static class VideoRangeDiagnosticPlan
{
    internal const string CommandLineSwitch = "--selftest-video-range";
    internal const string ChildSwitch = "--internal-selftest-video-range-cell";
    internal const string ReportName = "video-range-report.txt";
    internal const string JsonName = "video-range.json";
    internal static readonly TimeSpan CellTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    internal static IReadOnlyList<VideoRangeCell> Cells { get; } = BuildCells();

    private static IReadOnlyList<VideoRangeCell> BuildCells()
    {
        var cells = new List<VideoRangeCell>();
        foreach (bool full in new[] { false, true })
        foreach ((int width, int height) in new[] { (320, 180), (640, 360), (1280, 720), (1920, 1080) })
        foreach (string host in new[] { "probe", "gallery", "editor" })
            cells.Add(new($"matrix-{(full ? "full" : "studio")}-{width}x{height}-{host}", host, width, height, [full]));
        foreach (string host in new[] { "gallery", "probe" })
        {
            cells.Add(new($"sequence-studio-full-studio-{host}", host, 1280, 720, [false, true, false], "sequence"));
            cells.Add(new($"sequence-full-studio-{host}", host, 1280, 720, [true, false], "sequence"));
        }
        for (int repeat = 1; repeat <= 3; repeat++)
            cells.Add(new($"repeat-studio-1280x720-gallery-{repeat}", "gallery", 1280, 720, [false], "repeat"));
        return cells.AsReadOnly();
    }

    internal static VideoRangeArguments ParseArguments(string[] args)
    {
        if (args.Length is < 2 or > 3)
            throw new ArgumentException("Expected --selftest-video-range <absolute-output-directory>.");
        bool child = string.Equals(args[0], ChildSwitch, StringComparison.OrdinalIgnoreCase);
        if ((!child && !string.Equals(args[0], CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
            || args.Length != (child ? 3 : 2))
            throw new ArgumentException("Invalid video range diagnostic arguments.");
        string root = ValidateOutputDirectory(args[1]);
        VideoRangeCell? cell = child ? Cells.SingleOrDefault(c => c.Id == args[2])
            ?? throw new ArgumentException("Unknown video range diagnostic cell.") : null;
        return new(root, cell);
    }

    internal static string ValidateOutputDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)
            || directory.StartsWith(@"\\", StringComparison.Ordinal)
            || directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "." or ".."))
            throw new IOException("Video range output requires an absolute local directory.");
        string root = ValidatedFilePaths.DirectoryRoot(directory);
        if (string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Video range output cannot be a drive root.");
        // Validate ancestors and fixed output names before any directory or file is created.
        _ = DiagnosticOutputPaths.Child(root, ReportName);
        _ = DiagnosticOutputPaths.Child(root, JsonName);
        return root;
    }

    internal static ProcessStartInfo BuildChildStartInfo(string executable, string outputDirectory,
        string dataRoot, VideoRangeCell cell)
    {
        if (!Path.IsPathFullyQualified(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The diagnostic must launch the same absolute executable.");
        if (!Cells.Any(c => c.Id == cell.Id)) throw new ArgumentException("Unknown diagnostic cell.", nameof(cell));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = ValidateOutputDirectory(outputDirectory),
        };
        // ArgumentList preserves spaces and quotes without shell interpolation.
        start.ArgumentList.Add(ChildSwitch);
        start.ArgumentList.Add(start.WorkingDirectory);
        start.ArgumentList.Add(cell.Id);
        string isolatedData = ValidateOutputDirectory(dataRoot);
        start.Environment["MYCAPTURE_DATA_ROOT"] = isolatedData;
        // Product calibration's existing temporary-clip deletion/retry path stays intact,
        // but all of its scratch files are confined to this child's data directory.
        start.Environment["TEMP"] = isolatedData;
        start.Environment["TMP"] = isolatedData;
        return start;
    }

    internal static VideoRangeObservation Observe(byte[] bgra)
    {
        if (bgra.Length != 16) throw new ArgumentException("Four BGRA patches are required.", nameof(bgra));
        var patches = new VideoRangePatch[4];
        for (int i = 0; i < patches.Length; i++)
            patches[i] = new(bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4], bgra[i * 4 + 3]);
        return new(patches, StudioRangeCalibrationProbe.Classify(bgra)?.ToString() ?? "unknown");
    }

    internal static string[] Summarize(IReadOnlyList<VideoRangeCellResult> results)
    {
        var facts = new List<string>();
        // H1-H4 compare only samples taken before the product calibration presented its clips;
        // a re-sample of the same player after calibration is reported separately as H5.
        var beforeCalibration = new List<VideoRangeCellResult>();
        foreach (VideoRangeCellResult result in results)
        {
            VideoRangeSample[] before = result.Samples.Where(s => !s.CalibrationStartedBeforeSample).ToArray();
            foreach (VideoRangeSample after in result.Samples.Where(s => s.CalibrationStartedBeforeSample))
            {
                VideoRangeSample? earlier = before.LastOrDefault(s => s.Step == after.Step);
                if (earlier is not null && earlier.Classification != after.Classification)
                    facts.Add($"H5: {result.Cell.Id} presentation changed after the product calibration: "
                        + $"{earlier.Classification} -> {after.Classification}.");
            }
            beforeCalibration.Add(result with { Samples = before });
        }
        results = beforeCalibration;
        var matrix = results.Where(r => r.Cell.Kind == "matrix" && r.Samples.Length == 1 && r.Failure is null).ToArray();
        foreach (var group in matrix.GroupBy(r => (r.Samples[0].InputRange, r.Cell.Width, r.Cell.Height)))
            if (group.Select(r => r.Samples[0].Classification).Distinct().Count() > 1)
                facts.Add($"H2: {group.Key.InputRange} result differs by host at {group.Key.Width}x{group.Key.Height}: "
                    + string.Join(", ", group.Select(r => $"{r.Cell.Host}={r.Samples[0].Classification}")));
        foreach (var group in matrix.GroupBy(r => (r.Samples[0].InputRange, r.Cell.Host)))
            if (group.Select(r => r.Samples[0].Classification).Distinct().Count() > 1)
                facts.Add($"H1: {group.Key.InputRange} result differs by size in {group.Key.Host}: "
                    + string.Join(", ", group.Select(r => $"{r.Cell.Width}x{r.Cell.Height}={r.Samples[0].Classification}")));
        foreach (var result in results.Where(r => r.Cell.Kind == "sequence" && r.Failure is null))
        {
            var studio = result.Samples.Where(s => s.InputRange == "studio").ToArray();
            if (studio.Length == 2 && studio[0].Classification != studio[1].Classification)
                facts.Add($"H3: studio result differs after a full-range clip within {result.Cell.Id}: "
                    + $"{studio[0].Classification} -> {studio[1].Classification}.");
            if (result.Cell.FullRangeSteps[0] && studio.Length == 1)
            {
                var baseline = matrix.SingleOrDefault(r => r.Cell.Host == result.Cell.Host && r.Cell.Width == 1280
                    && r.Samples[0].InputRange == "studio");
                if (baseline is not null && baseline.Samples[0].Classification != studio[0].Classification)
                    facts.Add($"H3: studio after full differs from fresh-process studio in {result.Cell.Host}: "
                        + $"{baseline.Samples[0].Classification} -> {studio[0].Classification} (different processes).");
            }
        }
        var repeats = results.Where(r => r.Failure is null && r.Cell.Host == "gallery" && r.Cell.Width == 1280
            && r.Cell.Kind != "sequence" && r.Samples.Length == 1 && r.Samples[0].InputRange == "studio").ToArray();
        if (repeats.Select(r => r.Samples[0].Classification).Distinct().Count() > 1)
            facts.Add("H4: 1280x720 studio gallery result varies between identical fresh-process runs: "
                + string.Join(", ", repeats.Select(r => $"{r.Cell.Id}={r.Samples[0].Classification}")));
        foreach (var result in results.Where(r => r.Failure is null))
        foreach (var sample in result.Samples)
        {
            var calibrated = sample.InputRange == "studio" ? result.Calibration.Studio : result.Calibration.Full;
            if (calibrated.HasValue && sample.Classification != calibrated.Value.ToString())
                facts.Add($"Calibration differs: {result.Cell.Id} step {sample.Step}={sample.Classification}, "
                    + $"subsequent product {sample.InputRange} calibration={calibrated.Value}.");
        }
        if (!facts.Any(f => f.StartsWith('H')))
            facts.Add("No classification difference observed in the collected H1-H5 comparisons.");
        facts.Add($"Collected {results.Count(r => r.Failure is null)}/{Cells.Count} cells; "
            + $"unknown samples={results.Sum(r => r.Samples.Count(s => s.Classification == "unknown"))}. "
            + "Unknown is an observation; consult raw RGBA. Missing cells are infrastructure failures.");
        return facts.ToArray();
    }
}
