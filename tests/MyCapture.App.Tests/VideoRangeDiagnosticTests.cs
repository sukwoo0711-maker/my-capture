using System.IO;
using System.Windows.Controls;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoRangeDiagnosticTests
{
    [Fact]
    public void MatrixHasAllSizesRangesHostsSequencesAndThreeFreshRepeats()
    {
        var cells = VideoRangeDiagnosticPlan.Cells;
        Assert.Equal(36, cells.Count);
        Assert.Equal(42, cells.Sum(c => c.FullRangeSteps.Length));
        Assert.Equal(cells.Count, cells.Select(c => c.Id).Distinct().Count());
        Assert.Equal(24, cells.Count(c => c.Kind == "matrix"));
        foreach (string host in new[] { "probe", "gallery", "editor" })
        foreach (int width in new[] { 320, 640, 1280, 1920 })
        foreach (bool full in new[] { false, true })
            Assert.Single(cells, c => c.Kind == "matrix" && c.Host == host && c.Width == width
                && c.Height == width * 9 / 16 && c.FullRangeSteps.SequenceEqual(new[] { full }));
        foreach (string host in new[] { "probe", "gallery" })
        {
            Assert.Single(cells, c => c.Host == host && c.FullRangeSteps.SequenceEqual(new[] { false, true, false }));
            Assert.Single(cells, c => c.Host == host && c.FullRangeSteps.SequenceEqual(new[] { true, false }));
        }
        Assert.Equal(3, cells.Count(c => c.Kind == "repeat" && c.Host == "gallery" && c.Width == 1280 && !c.FullRangeSteps[0]));
        foreach (int width in new[] { 1280, 1920 })
        foreach (string host in new[] { "gallery", "editor" })
        foreach (string mode in new[] { "in-process" })
            Assert.Single(cells, c => c.Kind == "concurrent" && c.Host == host && c.Width == width
                && c.CalibrationMode == mode && c.FullRangeSteps.SequenceEqual(new[] { false }));
        Assert.Single(cells, c => c.Kind == "content" && c.Pattern == "colour-patches" && c.Width == 1280 && c.Host == "gallery");
        Assert.Equal(TimeSpan.FromSeconds(20), VideoRangeDiagnosticPlan.CellTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), VideoRangeDiagnosticPlan.RunTimeout);
    }

    [Theory]
    [InlineData(0, 64, 192, 255, "Correct")]
    [InlineData(0, 56, 205, 255, "ExpandedAsStudio")]
    [InlineData(16, 71, 181, 235, "StudioRetained")]
    [InlineData(0, 0, 0, 0, "unknown")]
    public void ObservationUsesTheProductClassifierAndKeepsRgba(int a, int b, int c, int d, string expected)
    {
        byte[] bgra = new[] { a, b, c, d }.SelectMany(v => new[] { (byte)v, (byte)v, (byte)v, (byte)255 }).ToArray();
        VideoRangeObservation observation = VideoRangeDiagnosticPlan.Observe(bgra);
        Assert.Equal(expected, observation.Classification);
        Assert.Equal(new VideoRangePatch((byte)c, (byte)c, (byte)c, 255), observation.Patches[2]);
        bgra[1] = 9; bgra[2] = 17; bgra[3] = 128;
        observation = VideoRangeDiagnosticPlan.Observe(bgra);
        Assert.Equal(new VideoRangePatch(17, 9, (byte)a, 128), observation.Patches[0]);
        Assert.Equal("unknown", observation.Classification);
    }

    [Fact]
    public void SummarySeparatesSizeHostOrderRunAndCalibrationDifferences()
    {
        VideoRangeCellResult[] results =
        [
            Result("matrix-studio-320x180-gallery", "Correct"),
            Result("matrix-studio-1280x720-gallery", "StudioRetained"),
            Result("matrix-studio-1280x720-probe", "Correct"),
            Result("sequence-studio-full-studio-gallery", "Correct", "ExpandedAsStudio", "StudioRetained"),
            Result("sequence-full-studio-probe", "Correct", "StudioRetained"),
            Result("repeat-studio-1280x720-gallery-1", "Correct"),
        ];
        string[] facts = VideoRangeDiagnosticPlan.Summarize(results);
        Assert.Contains(facts, f => f.StartsWith("H1:") && f.Contains("differs by size"));
        Assert.Contains(facts, f => f.StartsWith("H2:") && f.Contains("differs by host"));
        Assert.Contains(facts, f => f.StartsWith("H3:") && f.Contains("within sequence-studio-full-studio-gallery"));
        Assert.Contains(facts, f => f.StartsWith("H3:") && f.Contains("different processes"));
        Assert.Contains(facts, f => f.StartsWith("H4:") && f.Contains("identical fresh-process"));
        Assert.Contains(facts, f => f.StartsWith("Calibration differs:"));
        Assert.Contains(facts, f => f.Contains("Collected 6/36"));
    }

    [Fact]
    public void SummaryDoesNotInventDifferencesAndAccountsForUnknownOrMissingCells()
    {
        var results = new[]
        {
            Result("matrix-studio-1280x720-gallery", "Correct"),
            Result("repeat-studio-1280x720-gallery-1", "Correct"),
            Result("matrix-full-320x180-probe", "unknown"),
            Result("matrix-studio-1280x720-editor", "StudioRetained") with { Failure = "timeout" },
        };
        string[] facts = VideoRangeDiagnosticPlan.Summarize(results);
        Assert.DoesNotContain(facts, f => f.StartsWith('H'));
        Assert.Contains(facts, f => f.Contains("Collected 3/36") && f.Contains("unknown samples=1"));
    }

    [Fact]
    public void OnlySequenceSwitchFailuresAreObservations()
    {
        VideoRangeCell matrix = VideoRangeDiagnosticPlan.Cells.Single(c => c.Id == "matrix-studio-1280x720-gallery");
        VideoRangeCell sequence = VideoRangeDiagnosticPlan.Cells.Single(c => c.Id == "sequence-studio-full-studio-gallery");
        VideoRangeCell repeat = VideoRangeDiagnosticPlan.Cells.Single(c => c.Id == "repeat-studio-1280x720-gallery-1");
        Assert.False(VideoRangeDiagnosticPlan.RecordsSwitchFailureAsObservation(matrix, 0));
        Assert.False(VideoRangeDiagnosticPlan.RecordsSwitchFailureAsObservation(repeat, 0));
        Assert.False(VideoRangeDiagnosticPlan.RecordsSwitchFailureAsObservation(sequence, 0));
        Assert.True(VideoRangeDiagnosticPlan.RecordsSwitchFailureAsObservation(sequence, 1));
    }

    [Fact]
    public void PostCalibrationResampleIsReportedSeparatelyFromTheMatrixComparisons()
    {
        VideoRangeCellResult gallery = Result("matrix-studio-1280x720-gallery", "Correct");
        VideoRangeSample before = gallery.Samples[0];
        gallery = gallery with
        {
            Samples = [before, before with { Classification = "StudioRetained", CalibrationStartedBeforeSample = true }],
        };
        var results = new[] { gallery, Result("matrix-studio-1280x720-editor", "Correct") };
        string[] facts = VideoRangeDiagnosticPlan.Summarize(results);
        Assert.Contains(facts, f => f.StartsWith("H5:") && f.Contains("matrix-studio-1280x720-gallery")
            && f.Contains("Correct -> StudioRetained"));
        Assert.DoesNotContain(facts, f => f.StartsWith("H2:"));
        Assert.DoesNotContain(facts, f => f.StartsWith("No classification difference"));
    }

    [Fact]
    public void ArgumentsAndChildStartInfoRoundTripWithoutShellQuoting()
    {
        string root = OwnedTestDirectory.Create("mc-range-args-");
        try
        {
            string output = Path.Combine(root, "output with spaces");
            var request = VideoRangeDiagnosticPlan.ParseArguments(["--SELFTEST-VIDEO-RANGE", output]);
            Assert.Null(request.Cell);
            Assert.Equal(output, request.OutputDirectory);
            var cell = VideoRangeDiagnosticPlan.Cells[0];
            var start = VideoRangeDiagnosticPlan.BuildChildStartInfo(Path.Combine(root, "My Capture.exe"), output, Path.Combine(root, "data"), cell);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.Empty(start.Arguments);
            var child = VideoRangeDiagnosticPlan.ParseArguments(start.ArgumentList.ToArray());
            Assert.Same(cell, child.Cell);
            Assert.Equal(output, child.OutputDirectory);
            Assert.Equal(Path.Combine(root, "data"), start.Environment["MYCAPTURE_DATA_ROOT"]);
            Assert.Equal(start.Environment["TEMP"], start.Environment["TMP"]);
            Assert.Equal(start.Environment["MYCAPTURE_DATA_ROOT"], start.Environment["TEMP"]);
            Assert.False(Directory.Exists(output)); // Pure construction must not create artifacts.
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Theory]
    [InlineData()]
    [InlineData("--selftest-video-range")]
    [InlineData("--selftest-video-range", @"C:\Temp\range", "extra")]
    [InlineData("--internal-selftest-video-range-cell", @"C:\Temp\range")]
    [InlineData("--internal-selftest-video-range-cell", @"C:\Temp\range", "../unknown")]
    [InlineData("--unknown", @"C:\Temp\range")]
    public void InvalidArgumentsAreRejected(params string[] args) =>
        Assert.Throws<ArgumentException>(() => VideoRangeDiagnosticPlan.ParseArguments(args));

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:relative")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\range")]
    [InlineData(@"\\?\C:\range")]
    [InlineData(@"C:\Temp\range.")]
    [InlineData(@"C:\Temp\..\range")]
    [InlineData(@"C:\Temp\range:stream")]
    public void OutputRejectsAmbiguousRoots(string output) =>
        Assert.Throws<IOException>(() => VideoRangeDiagnosticPlan.ValidateOutputDirectory(output));

    [Fact]
    public void OutputRejectsLinksBeforeWriting()
    {
        string owner = OwnedTestDirectory.Create("mc-range-paths-");
        string target = OwnedTestDirectory.Create("mc-range-path-target-");
        string link = Path.Combine(owner, "link");
        try
        {
            UpdatePathsTests.CreateJunction(link, target);
            Assert.Throws<IOException>(() => VideoRangeDiagnosticPlan.ValidateOutputDirectory(Path.Combine(link, "range")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            if (Directory.Exists(link)) TestRecycleBin.DeleteDirectory(link);
            OwnedTestDirectory.Delete(owner);
            OwnedTestDirectory.Delete(target);
        }
    }

    [Fact]
    public void DiagnosticSuppressionPreventsEffectsAndCalibrationBeforeObservations() => StaTestHost.Run(() =>
    {
        bool previousSuppression = StudioRangeVideoEffect.SuppressForDiagnostic;
        var previous = StudioRangeVideoEffect.Calibration;
        try
        {
            var calibration = new StudioRangePresentationCalibration(_ => throw new InvalidOperationException("Must not run"));
            StudioRangeVideoEffect.Calibration = calibration;
            StudioRangeVideoEffect.SuppressForDiagnostic = true;
            var media = new MediaElement { Effect = new FullRangeVideoEffect() };
            StudioRangeVideoEffect.Apply(media, studioRange: true);
            Assert.Null(media.Effect);
            StudioRangeVideoEffect.Apply(media, studioRange: false);
            Assert.Null(media.Effect);
            Assert.False(calibration.IsStarted);
        }
        finally
        {
            StudioRangeVideoEffect.SuppressForDiagnostic = previousSuppression;
            StudioRangeVideoEffect.Calibration = previous;
        }
    });

    [Fact]
    public void ConcurrentAndContentComparisonsDoNotContaminateH1ThroughH5()
    {
        var concurrent = Result("concurrent-in-process-studio-1280x720-gallery", "StudioRetained");
        concurrent = concurrent with
        {
            Samples = [concurrent.Samples[0] with { Phase = "opening-with-calibration", CalibrationStartedBeforeSample = true },
                concurrent.Samples[0] with { Phase = "after-calibration", CalibrationStartedBeforeSample = true }],
        };
        string[] summary = VideoRangeDiagnosticPlan.Summarize([
            Result("matrix-studio-1280x720-gallery", "Correct"), concurrent,
            Result("colour-studio-1280x720-gallery", "ExpandedAsStudio")]);
        Assert.Contains(summary, f => f.StartsWith("H6:") && f.Contains("fresh=Correct, concurrent opening=StudioRetained, completed=StudioRetained"));
        Assert.Contains(summary, f => f.StartsWith("H7:") && f.Contains("grey bars=Correct, colour patches=ExpandedAsStudio"));
        Assert.DoesNotContain(summary, f => f.StartsWith("H4:") || f.StartsWith("H5:"));
    }

    [Fact]
    public void ColourPatternPreservesAllTestPatchesAndClassifiesTheComparableGreys()
    {
        Assert.Equal(16, VideoRangeColourPattern.Patches.Length);
        byte[] bgra = RangePatternClip.Patches.SelectMany(p => new byte[] { p.B, p.G, p.R, 255 }).ToArray();
        var observation = VideoRangeColourPattern.Observe(bgra);
        Assert.Equal("Correct", observation.Classification);
        Assert.Equal(new VideoRangePatch(0, 0, 255, 255), observation.Patches[9]);
        Assert.Equal(new VideoRangePatch(0xF2, 0xB1, 0x34, 255), observation.Patches[13]);
        Assert.Equal(new VideoRangePatch(0x1D, 0x2A, 0x3C, 255), observation.Patches[14]);
        Assert.Equal(new VideoRangePatch(0x2E, 0x6F, 0xD8, 255), observation.Patches[15]);
        bgra[4 * 4 + 1] = 150;
        Assert.Equal("unknown", VideoRangeColourPattern.Observe(bgra).Classification);
    }

    [Theory]
    [InlineData(null, "default")]
    [InlineData("none", "none")]
    [InlineData("default", "default")]
    public void ResponsivenessCalibrationModesAreExplicitAndDefaultToProduct(string? value, string expected)
    {
        Assert.Equal(expected, VideoEditorResponsivenessSelfTest.ReadCalibrationMode(value));
        Assert.Throws<ArgumentException>(() => VideoEditorResponsivenessSelfTest.ReadCalibrationMode("disabled-typo"));
    }

    [Fact]
    public void PlayerVerificationRoundTripsBesideRawSamples()
    {
        var result = Result("matrix-studio-1280x720-gallery", "StudioRetained");
        var decision = new RangeVerificationResult(VideoRangePresentation.StudioRetained,
            new(12, 1, 20), [new(12, 1, 20), new(13, 2, 21), new(11, 1, 19)], 1200, 255, 2, 123, "verified");
        result = result with { Samples = [result.Samples[0] with { Verification = decision }] };
        string json = System.Text.Json.JsonSerializer.Serialize(result);
        var restored = System.Text.Json.JsonSerializer.Deserialize<VideoRangeCellResult>(json)!;
        var actual = Assert.IsType<RangeVerificationResult>(restored.Samples[0].Verification);
        Assert.Equal(decision.Model, actual.Model); Assert.Equal(decision.Errors, actual.Errors);
        Assert.Equal(decision.ChannelErrors, actual.ChannelErrors); Assert.Equal(1200, actual.Points);
        Assert.Equal(255, actual.DynamicRange); Assert.Equal(2, actual.Attempts); Assert.Equal(123, actual.PositionMs);
        Assert.Equal("StudioRetained", restored.Samples[0].Classification);
    }

    private static VideoRangeCellResult Result(string id, params string[] classifications)
    {
        VideoRangeCell cell = VideoRangeDiagnosticPlan.Cells.Single(c => c.Id == id);
        return new(cell, 1, classifications.Select((value, step) => new VideoRangeSample(step,
            cell.FullRangeSteps[step] ? "full" : "studio", value, [], 320, 180, 320, 180, 320, 180,
            1, 1, 96, 96, 2, true, false)).ToArray(),
            new(VideoRangePresentation.Correct, VideoRangePresentation.Correct), null, [], null, null);
    }
}
