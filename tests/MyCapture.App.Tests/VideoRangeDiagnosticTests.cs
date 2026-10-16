using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoRangeDiagnosticTests
{
    [Fact]
    public void MatrixHasAllSizesRangesHostsSequencesAndThreeFreshRepeats()
    {
        var cells = VideoRangeDiagnosticPlan.Cells;
        Assert.Equal(60, cells.Count);
        Assert.Equal(66, cells.Sum(c => c.FullRangeSteps.Length));
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
        Assert.Equal(TimeSpan.FromMinutes(13), VideoRangeDiagnosticPlan.RunTimeout);
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
        Assert.Contains(facts, f => f.Contains("Collected 6/60"));
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
        Assert.Contains(facts, f => f.Contains("Collected 3/60") && f.Contains("unknown samples=1"));
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

    [Fact]
    public void MeasuredVerificationRoundTripsCurvesResidualAndCoverageBesideRawSamples()
    {
        string curve = Convert.ToBase64String(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var decision = new RangeVerificationResult(null, new(4.494, 6.843, 10.119), [], 3096, 255, 1, 123, "verified")
        {
            Mapping = new(curve, curve, curve),
            MappingDiagnostics = new("verified", [new(12, 12, 255, 7, 50), new(12, 12, 255, 7, 50), new(12, 12, 255, 7, 50)], 0.7, 0.8, 1),
        };
        var result = Result("matrix-studio-1280x720-gallery", "unknown");
        result = result with { Samples = [result.Samples[0] with { Verification = decision }] };
        string json = System.Text.Json.JsonSerializer.Serialize(result);
        var restored = System.Text.Json.JsonSerializer.Deserialize<VideoRangeCellResult>(json)!;
        var actual = Assert.IsType<RangeVerificationResult>(restored.Samples[0].Verification);
        Assert.Equal("measured", actual.Kind); Assert.Equal(VideoRangeCorrection.Measured, actual.Correction);
        Assert.Equal(decision.Mapping, actual.Mapping); Assert.Equal(decision.Errors, actual.Errors);
        Assert.Equal(decision.MappingDiagnostics.Coverage, actual.MappingDiagnostics!.Coverage);
        Assert.Equal(0.7, actual.MappingDiagnostics.Residual); Assert.Equal(0.8, actual.MappingDiagnostics.HeldOutResidual);
        Assert.Equal(1, actual.MappingDiagnostics.SplitDisagreement);
        Assert.Contains("kind=measured", actual.Details); Assert.Contains("residual=0.700", actual.Details);
        Assert.Contains(curve, actual.Details);
        Assert.Equal("fallback", (actual with { Mapping = null, IsFallback = true }).Kind);
    }

    [Fact]
    public void PathCellsAppendAllVariantsWithoutChangingTheOriginalOrder()
    {
        var original = new List<string>();
        foreach (string range in new[] { "studio", "full" })
        foreach (string size in new[] { "320x180", "640x360", "1280x720", "1920x1080" })
        foreach (string host in new[] { "probe", "gallery", "editor" }) original.Add($"matrix-{range}-{size}-{host}");
        original.AddRange(["sequence-studio-full-studio-gallery", "sequence-full-studio-gallery",
            "sequence-studio-full-studio-probe", "sequence-full-studio-probe",
            "repeat-studio-1280x720-gallery-1", "repeat-studio-1280x720-gallery-2", "repeat-studio-1280x720-gallery-3",
            "concurrent-in-process-studio-1280x720-gallery", "concurrent-in-process-studio-1280x720-editor",
            "concurrent-in-process-studio-1920x1080-gallery", "concurrent-in-process-studio-1920x1080-editor",
            "colour-studio-1280x720-gallery"]);
        Assert.Equal(original, VideoRangeDiagnosticPlan.Cells.Take(36).Select(c => c.Id));
        var expected = new List<string>();
        foreach (string range in new[] { "studio", "full" })
        {
            foreach (string variant in new[] { "gallery-product", "gallery-pause-immediately", "gallery-playing",
                "gallery-large", "gallery-no-exclusion", "gallery-no-clip", "gallery-plain-window", "editor-product",
                "editor-small", "gallery-all-editor-like" }) expected.Add($"path-{range}-colour-patches-{variant}");
            foreach (string variant in new[] { "gallery-product", "editor-small" }) expected.Add($"path-{range}-grey-bars-{variant}");
        }
        Assert.Equal(expected, VideoRangeDiagnosticPlan.Cells.Skip(36).Select(c => c.Id));
        Assert.All(VideoRangeDiagnosticPlan.Cells.Skip(36), cell =>
        {
            Assert.Equal("path", cell.Kind); Assert.Equal(1280, cell.Width); Assert.Equal(720, cell.Height);
            Assert.Single(cell.FullRangeSteps); Assert.Equal("none", cell.CalibrationMode);
            Assert.Equal(cell.PathVariant!.StartsWith("editor-", StringComparison.Ordinal) ? "editor" : "gallery", cell.Host);
        });
        Assert.Equal(new double[] { 300, 1000, 2500, 5000 }, VideoRangeDiagnosticPlan.PathSampleTimesMs);
    }

    [Fact]
    public void PathArgumentsRoundTripUsingTheShortChildDirectory()
    {
        string root = OwnedTestDirectory.Create("mc-path-args-");
        try
        {
            var cell = VideoRangeDiagnosticPlan.Cells.Single(c => c.Id == "path-full-colour-patches-gallery-pause-immediately");
            string output = Path.Combine(root, "c-12345678", "49");
            var start = VideoRangeDiagnosticPlan.BuildChildStartInfo(Path.Combine(root, "MyCapture.exe"), output, Path.Combine(output, "data"), cell);
            var parsed = VideoRangeDiagnosticPlan.ParseArguments(start.ArgumentList.ToArray());
            Assert.Same(cell, parsed.Cell); Assert.Equal(output, parsed.OutputDirectory);
            Assert.DoesNotContain(cell.Id, output);
            Assert.Equal(Path.Combine(output, "data"), start.Environment["TEMP"]);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void PathOptionsChangeOnlyTheirNamedConditionAtTheActualDpi()
    {
        var defaults = new GalleryPlayerDiagnosticOptions();
        foreach (var cell in VideoRangeDiagnosticPlan.Cells.Where(c => c.Kind == "path"))
        {
            var options = VideoRangePathDiagnostic.OptionsFor(cell, 1.25);
            Assert.Equal(cell.PathVariant, options.Variant);
            GalleryPlayerDiagnosticOptions? expected = cell.PathVariant switch
            {
                "gallery-product" => defaults,
                "gallery-pause-immediately" => defaults with { OpenMode = GalleryPlayerOpenMode.PauseImmediately },
                "gallery-playing" => defaults with { OpenMode = GalleryPlayerOpenMode.Playing },
                "gallery-large" => defaults with { FrameHeightDip = 547.2 },
                "gallery-no-exclusion" => defaults with { CaptureExclusion = false },
                "gallery-no-clip" => defaults with { RoundedClip = false },
                "gallery-plain-window" => defaults with { ProductWindowStyle = false },
                "gallery-all-editor-like" => new(GalleryPlayerOpenMode.PauseImmediately, 547.2, false, false, false),
                "editor-product" or "editor-small" => null,
                _ => throw new InvalidOperationException(),
            };
            Assert.Equal(expected, options.Gallery);
            Assert.Null(options.TargetScale);
        }
        foreach (double dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var large = VideoRangeDiagnosticPlan.Cells.Single(c => c.Id == "path-studio-colour-patches-gallery-large");
            Assert.InRange(VideoRangePathDiagnostic.OptionsFor(large, dpi).Gallery!.FrameHeightDip!.Value * dpi / 720, 0.9, 1);
        }
    }

    [Fact]
    public void PathSummarySeparatesModelAndGreyDifferencesAndKeepsH1ThroughH7()
    {
        var original = new[] { Result("matrix-studio-1280x720-gallery", "Correct"),
            Result("colour-studio-1280x720-gallery", "StudioRetained") };
        var paths = new[]
        {
            PathResult("studio", "gallery-product", VideoRangePresentation.StudioRetained, 10),
            PathResult("studio", "editor-product", VideoRangePresentation.Correct, 0),
            PathResult("studio", "gallery-pause-immediately", VideoRangePresentation.Correct, 0),
            PathResult("studio", "gallery-no-clip", VideoRangePresentation.StudioRetained, 6), // exactly 4: match
            PathResult("studio", "gallery-playing", VideoRangePresentation.StudioRetained, 5), // 5: differs
            PathResult("studio", "gallery-product", VideoRangePresentation.Correct, 0, "grey-bars"),
            PathResult("studio", "editor-small", VideoRangePresentation.Correct, 0),
            PathResult("studio", "editor-small", VideoRangePresentation.Correct, 0, "grey-bars"),
            PathResult("full", "gallery-product", VideoRangePresentation.ExpandedAsStudio, 12),
            PathResult("full", "editor-product", VideoRangePresentation.Correct, 0),
        };
        string[] facts = VideoRangeDiagnosticPlan.Summarize(original.Concat(paths).ToArray());
        Assert.Equal(VideoRangeDiagnosticPlan.Summarize(original).Where(f => f.StartsWith('H')),
            facts.Where(f => f.StartsWith('H') && !f.StartsWith("H8:") && !f.StartsWith("H9:")));
        Assert.Contains(facts, f => f.StartsWith("H8: studio gallery-product colour-patches:")
            && f.Contains("kind=model, model=StudioRetained, status=verified") && f.Contains("errors(identity/retained/expanded)")
            && f.Contains("first=300ms [16=26/26/26") && f.Contains("last=5000ms") && f.Contains("channel-0=black.R=10"));
        Assert.Contains(facts, f => f.StartsWith("H8: studio single-variable gallery-pause-immediately:")
            && f.Contains("kind/model: differs from gallery-product") && f.Contains("grey first/last: differs from gallery-product")
            && f.Contains("kind/model: matches editor-product") && f.Contains("grey first/last: matches editor-product"));
        Assert.Contains(facts, f => f.StartsWith("H8: studio single-variable gallery-no-clip:") && f.Contains("grey first/last: matches gallery-product"));
        Assert.Contains(facts, f => f.StartsWith("H8: studio single-variable gallery-playing:")
            && f.Contains("kind/model: matches gallery-product") && f.Contains("grey first/last: differs from gallery-product"));
        Assert.Contains(facts, f => f.StartsWith("H8: full gallery-product") && f.Contains("ExpandedAsStudio"));
        Assert.Contains(facts, f => f.StartsWith("H9: studio gallery-product") && f.Contains("same host/scale")
            && f.Contains("differs from grey-bars") && f.Contains("shared greys=0/64/192/255"));
        Assert.Contains(facts, f => f.StartsWith("H9: studio editor-small") && f.Contains("matches grey-bars"));
        string table = string.Join(Environment.NewLine, VideoRangePathDiagnostic.ReportTable(paths));
        Assert.Contains("PATH CELLS:", table); Assert.Contains("open=Product", table);
        Assert.Contains("300/300.0ms", table); Assert.Contains("1000/1000.0ms", table);
        Assert.Contains("2500/2500.0ms", table); Assert.Contains("5000/5000.0ms", table);
        Assert.Contains("16=n/a", table); Assert.Contains("235=n/a", table);
    }

    [Fact]
    public void PathSummaryDoesNotTreatMissingTransparentOrMismatchedScaleEvidenceAsAMatch()
    {
        var gallery = PathResult("studio", "gallery-product", VideoRangePresentation.Correct, 0);
        var editor = PathResult("studio", "editor-product", VideoRangePresentation.Correct, 0);
        var partial = PathResult("studio", "gallery-pause-immediately", VideoRangePresentation.Correct, 0);
        var transparent = partial.Samples[0].TimeSeries!.Select(t => t with
        { Observation = t.Observation with { Patches = t.Observation.Patches.Select(p => p with { A = 0 }).ToArray() } }).ToArray();
        partial = partial with { Samples = [partial.Samples[0] with { TimeSeries = transparent }] };
        var bars = PathResult("studio", "gallery-product", VideoRangePresentation.Correct, 0, "grey-bars");
        bars = bars with { Samples = [bars.Samples[0] with { ScaleX = 0.6 }] };
        string[] facts = VideoRangeDiagnosticPlan.Summarize([gallery, editor, partial, bars]);
        Assert.Contains(facts, f => f.StartsWith("H8: studio single-variable gallery-pause-immediately") && f.Contains("grey first/last: unavailable"));
        Assert.Contains(facts, f => f.StartsWith("H9: studio gallery-product") && f.Contains("scale mismatch"));
        facts = VideoRangeDiagnosticPlan.Summarize([partial with { Failure = "timeout" }, gallery, editor]);
        Assert.Contains(facts, f => f.Contains("comparison with editor-product=unavailable"));
    }

    [Fact]
    public void PathTimeSeriesAndOptionsRoundTripWithTheWholeVerificationResult()
    {
        var result = PathResult("full", "gallery-pause-immediately", VideoRangePresentation.Correct, 0);
        var sample = result.Samples[0];
        result = result with { Samples = [sample with { TimeSeries = [.. sample.TimeSeries!,
            sample.TimeSeries![^1] with { Phase = "after-play-pause", TargetMs = 5800, ElapsedSinceReadyMs = 5820, PositionMs = 500 }] }] };
        var actual = JsonSerializer.Deserialize<VideoRangeCellResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal(result.PathOptions, actual.PathOptions);
        Assert.Equal(result.Samples[0].Verification!.Errors, actual.Samples[0].Verification!.Errors);
        Assert.Equal(5, actual.Samples[0].TimeSeries!.Length);
        Assert.Equal("after-play-pause", actual.Samples[0].TimeSeries![^1].Phase);
        Assert.Equal(5820, actual.Samples[0].TimeSeries![^1].ElapsedSinceReadyMs);
        Assert.Equal(500, actual.Samples[0].TimeSeries![^1].PositionMs);
        Assert.All(actual.Samples[0].TimeSeries!, p => Assert.Equal(16, p.Observation.Patches.Length));
        Assert.Contains(VideoRangePathDiagnostic.ReportTable([actual]), line => line.Contains("after-play-pause 5800/5820.0ms"));
    }

    [Fact]
    public Task GalleryProductConstructorRetainsProductDefaults() =>
        SettingsThemePreviewTests.RunIsolated(nameof(GalleryProductConstructorRetainsProductDefaults), () =>
        {
            CreateApplication();
            var player = new GalleryVideoPlayerWindow();
            try
            {
                Assert.Null(player.DiagnosticOptions); Assert.Equal(420, player.Width);
                Assert.True(player.Topmost); Assert.Equal(WindowStyle.ToolWindow, player.WindowStyle);
                Assert.False(player.ShowInTaskbar); Assert.False(player.ShowActivated);
                Assert.Equal(SizeToContent.Height, player.SizeToContent);
                var frame = Assert.IsType<Border>(player.MediaForDiagnostic.Parent);
                Assert.Equal(220, frame.Height); Assert.Equal(new CornerRadius(8), frame.CornerRadius); Assert.True(frame.ClipToBounds);
                Assert.True(new GalleryPlayerDiagnosticOptions().CaptureExclusion);
                Assert.False(player.IsMediaReadyForDiagnostic); Assert.False(player.IsPlayingForDiagnostic);
                Assert.Null(player.MediaForDiagnostic.Source); Assert.Null(StudioRangeVideoEffect.VerificationFor(player.MediaForDiagnostic));
            }
            finally { player.Close(); }
        }, typeof(VideoRangeDiagnosticTests));

    [Fact]
    public Task GalleryPauseImmediatelyPathSmoke() => PathSmoke(nameof(GalleryPauseImmediatelyPathSmoke), "gallery-pause-immediately");

    [Fact]
    public Task GalleryProductPathSmoke() => PathSmoke(nameof(GalleryProductPathSmoke), "gallery-product");

    [Fact]
    public Task GalleryLargePathSmoke() => PathSmoke(nameof(GalleryLargePathSmoke), "gallery-large");

    [Fact]
    public Task GalleryPlayingPathSmoke() => PathSmoke(nameof(GalleryPlayingPathSmoke), "gallery-playing");

    [Fact]
    public Task EditorSmallPathSmoke() => PathSmoke(nameof(EditorSmallPathSmoke), "editor-small");

    private static Task PathSmoke(string testName, string variant) => SettingsThemePreviewTests.RunIsolated(testName, () =>
    {
        CreateApplication();
        string root = OwnedTestDirectory.Create("mc-path-smoke-");
        string? previousData = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT");
        string? previousTemp = Environment.GetEnvironmentVariable("TEMP"), previousTmp = Environment.GetEnvironmentVariable("TMP");
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        try
        {
            // App.OnStartup supplies this context in the executable; the isolated bare STA does not.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Environment.SetEnvironmentVariable("MYCAPTURE_DATA_ROOT", Path.Combine(root, "data"));
            var cell = VideoRangeDiagnosticPlan.Cells.Single(c => c.Id == $"path-studio-colour-patches-{variant}");
            string output = Path.Combine(root, "out");
            int exitCode = VideoRangeSelfTest.RunChild(output, cell);
            string json = File.ReadAllText(Path.Combine(output, VideoRangeDiagnosticPlan.JsonName));
            Assert.True(exitCode == 0, json);
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            var result = JsonSerializer.Deserialize<VideoRangeCellResult>(json, options)!;
            var sample = Assert.Single(result.Samples);
            Assert.NotNull(sample.Verification); Assert.False(sample.CalibrationStartedBeforeSample);
            Assert.Equal(variant == "gallery-pause-immediately" ? 5 : 4, sample.TimeSeries!.Length);
            Assert.All(sample.TimeSeries!, point =>
            {
                Assert.Equal(16, point.Observation.Patches.Length);
                Assert.Contains(point.Observation.Patches, patch => patch.A >= 250);
                Assert.Equal(variant == "gallery-playing", point.Playing);
                Assert.True(point.ElapsedSinceReadyMs >= point.TargetMs - 30);
            });
            if (variant == "gallery-large") Assert.True(Math.Min(sample.ScaleX, sample.ScaleY) >= 0.9);
            if (variant == "editor-small") Assert.InRange(Math.Abs(sample.ScaleX - result.PathOptions!.TargetScale!.Value), 0, 0.05);
            if (variant == "gallery-pause-immediately") Assert.InRange(sample.TimeSeries![^1].PositionMs, 200, 900);
            if (variant == "gallery-playing") Assert.True(sample.TimeSeries![^1].PositionMs > sample.TimeSeries[0].PositionMs + 4000);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYCAPTURE_DATA_ROOT", previousData);
            Environment.SetEnvironmentVariable("TEMP", previousTemp); Environment.SetEnvironmentVariable("TMP", previousTmp);
            SynchronizationContext.SetSynchronizationContext(previousContext);
            OwnedTestDirectory.Delete(root);
        }
    }, typeof(VideoRangeDiagnosticTests));

    private static void CreateApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
    }

    private static VideoRangeCellResult PathResult(string range, string variant, VideoRangePresentation model, int greyOffset,
        string pattern = "colour-patches")
    {
        var result = Result($"path-{range}-{pattern}-{variant}", model.ToString());
        var patches = VideoRangeColourPattern.Patches.Select(p => new VideoRangePatch(
            (byte)Math.Clamp(p.R + greyOffset, 0, 255), (byte)Math.Clamp(p.G + greyOffset, 0, 255),
            (byte)Math.Clamp(p.B + greyOffset, 0, 255), 255)).ToArray();
        var observation = new VideoRangeObservation(patches, model.ToString());
        return result with
        {
            PathOptions = VideoRangePathDiagnostic.OptionsFor(result.Cell),
            Samples = [result.Samples[0] with
            {
                Verification = new(model, new(1, 2, 3), [], 100, 255, 1, 0, "verified"),
                TimeSeries = VideoRangeDiagnosticPlan.PathSampleTimesMs.Select(ms => new VideoRangeTimePoint("ready", ms, ms, 0, false, observation)).ToArray(),
            }],
        };
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
