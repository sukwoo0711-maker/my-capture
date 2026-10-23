using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Real-device responsiveness diagnostic: records a real MP4, opens the real editor window,
/// drives a five-second scrub, performs exact release seeks, and writes measured pass/fail
/// evidence plus a rendered-window PNG. Reachable through --selftest-video-editor.
/// </summary>
internal static class VideoEditorResponsivenessSelfTest
{
    internal const string CommandLineSwitch = "--selftest-video-editor";

    internal const string CalibrationEnvironmentVariable = "MYCAPTURE_VIDEO_EDITOR_SELFTEST_CALIBRATION";
    private static string _verificationMode = "default";

    internal static string ReadCalibrationMode(string? value) => value switch
    {
        null or "" or "default" => "default",
        "none" => value,
        _ => throw new ArgumentException("Verification mode must be none or default."),
    };

    private static void ConfigureCalibration()
    {
        _verificationMode = ReadCalibrationMode(Environment.GetEnvironmentVariable(CalibrationEnvironmentVariable));
        StudioRangeVideoEffect.VerificationEnabled = _verificationMode == "default";
    }

    private const int DragSamples = 300;
    private const int AllocationSamples = 6000;
    private const int ExactSamples = 20;
    private const string SoakIterationsEnvironmentVariable =
        "MYCAPTURE_VIDEO_EDITOR_SELFTEST_ITERATIONS";
    private const long MaxSoakManagedGrowthBytes = 16L * 1024 * 1024;
    private const long MaxSoakPrivateGrowthBytes = 20L * 1024 * 1024;
    private const string ReportFileName = "video-editor-selftest-report.txt";

    // Every wait inside a phase is bounded well below its budget; the slack covers a cold first
    // media open on a busy hosted runner (up to about 25 s). A phase that outlives its budget has
    // hung, and the watchdog ends the process with a stall report instead of letting the
    // packaged runner wait out its whole-test limit.
    private static readonly TimeSpan ShortPhaseBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MediaPhaseBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExactSeekPhaseBudget = TimeSpan.FromSeconds(90);

    private static SelfTestWatchdog? _watchdog;
    private static ProgressReport? _report;
    private static Func<string>? _editorState;
    private static int _uiThreadId;
    private static long _started;

    internal static int Run(string outputDirectory)
    {
        ConfigureCalibration();
        _started = Stopwatch.GetTimestamp();
        _uiThreadId = SelfTestStallEvidence.CurrentNativeThreadId();
        _report = new ProgressReport(Path.Combine(outputDirectory, ReportFileName));
        _watchdog = new SelfTestWatchdog("start", ShortPhaseBudget, OnStalled);
        try
        {
            int iterations = ReadSoakIterations();
            return iterations == 1
                ? RunSingle(outputDirectory)
                : RunSoak(outputDirectory, iterations);
        }
        finally
        {
            // The watchdog outlives the test on purpose: a process that cannot exit after
            // writing its report would otherwise hold the runner until its whole-test limit.
            Enter("process exit", ShortPhaseBudget);
        }
    }

    private static int RunSoak(string outputDirectory, int iterations)
    {
        Directory.CreateDirectory(outputDirectory);
        var report = new ProgressReport(Path.Combine(outputDirectory, ReportFileName));
        _report = report;
        string sharedClipPath = Path.Combine(outputDirectory, "responsiveness-soak-source.mp4");
        RecordingResult recording = RecordClip(sharedClipPath);
        var failures = new List<string>();
        var managedBytes = new long[iterations];
        var privateBytes = new long[iterations];
        var workingSetBytes = new long[iterations];
        using Process process = Process.GetCurrentProcess();

        report.Line("MyCapture video-editor same-process soak");
        report.Line($"UTC: {DateTimeOffset.UtcNow:O}");
        report.Line($"OS: {Environment.OSVersion}");
        report.Line($"Iterations: {iterations}");
        report.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"Shared real clip: {recording.Width}x{recording.Height} @ {recording.Fps}fps, " +
            $"{recording.EmittedFrames} frames, {recording.DurationMs:0}ms, " +
            $"{new FileInfo(sharedClipPath).Length} bytes"));
        report.Line(
            "The real MP4 is recorded once so retained-memory measurements isolate editor " +
            "open/scrub/exact-seek/close lifecycle rather than recorder/encoder caches.");
        report.Line();

        for (int i = 0; i < iterations; i++)
        {
            string iterationDirectory = Path.Combine(
                outputDirectory,
                string.Create(CultureInfo.InvariantCulture, $"iteration-{i + 1:000}"));
            long started = Stopwatch.GetTimestamp();
            int result = RunSingle(iterationDirectory, recording);
            _report = report;

            // Let MediaElement/Dispatcher teardown complete before measuring retained state.
            Enter("soak teardown", ShortPhaseBudget);
            PumpFor(TimeSpan.FromMilliseconds(100));
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            PumpFor(TimeSpan.FromMilliseconds(30));

            managedBytes[i] = GC.GetTotalMemory(forceFullCollection: false);
            process.Refresh();
            privateBytes[i] = process.PrivateMemorySize64;
            workingSetBytes[i] = process.WorkingSet64;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            string status = result == 0 ? "PASS" : "FAIL";
            report.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"Iteration {i + 1:000}: {status}, elapsed={elapsed.TotalSeconds:0.000}s, " +
                $"managed={managedBytes[i]}B, private={privateBytes[i]}B, " +
                $"working-set={workingSetBytes[i]}B"));
            if (result != 0)
            {
                failures.Add($"iteration {i + 1:000} failed its full responsiveness gate");
            }
        }

        long managedGrowth = managedBytes[^1] - managedBytes[0];
        long privateGrowth = privateBytes[^1] - privateBytes[0];
        long peakWorkingSet = workingSetBytes.Max();
        report.Line();
        report.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"Retained managed growth: {managedGrowth}B " +
            $"(limit {MaxSoakManagedGrowthBytes}B)"));
        report.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"Retained private growth: {privateGrowth}B " +
            $"(limit {MaxSoakPrivateGrowthBytes}B)"));
        report.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"Peak working set: {peakWorkingSet}B"));

        if (managedGrowth > MaxSoakManagedGrowthBytes)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"retained managed growth {managedGrowth}B exceeded " +
                $"{MaxSoakManagedGrowthBytes}B"));
        }
        if (privateGrowth > MaxSoakPrivateGrowthBytes)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"retained private growth {privateGrowth}B exceeded " +
                $"{MaxSoakPrivateGrowthBytes}B"));
        }

        return report.Finish(failures);
    }

    private static int ReadSoakIterations()
    {
        string? raw = Environment.GetEnvironmentVariable(SoakIterationsEnvironmentVariable);
        return int.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int iterations)
            ? Math.Clamp(iterations, 1, 100)
            : 1;
    }

    private static int RunSingle(
        string outputDirectory,
        RecordingResult? sharedRecording = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string clipPath = sharedRecording?.OutputPath
            ?? Path.Combine(outputDirectory, "responsiveness-source.mp4");
        string screenshotPath = Path.Combine(outputDirectory, "video-editor-window.png");
        var report = new ProgressReport(Path.Combine(outputDirectory, ReportFileName));
        _report = report;
        var failures = new List<string>();
        VideoEditorWindow? editor = null;

        void Line(string value = "") => report.Line(value);
        void Gate(bool condition, string pass, string failure)
        {
            if (condition)
            {
                Line("PASS: " + pass);
            }
            else
            {
                failures.Add(failure);
                Line("FAIL: " + failure);
            }
        }

        try
        {
            Line("MyCapture video-editor responsiveness self-test");
            Line($"UTC: {DateTimeOffset.UtcNow:O}");
            Line($"OS: {Environment.OSVersion}");
            Line($"Process: {Environment.ProcessPath}");
            Line($"Verification mode: {_verificationMode}");
            Line();

            RecordingResult recording = sharedRecording ?? RecordClip(clipPath);
            Enter("read container duration", ShortPhaseBudget);
            double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clipPath);
            ValidateSourceClip(recording, containerMs);
            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"Real clip: {recording.Width}x{recording.Height} @ {recording.Fps}fps, " +
                $"{recording.EmittedFrames} frames, {recording.DurationMs:0}ms, " +
                $"{new FileInfo(clipPath).Length} bytes"));

            Enter("open editor window", MediaPhaseBudget);
            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
                builder.SetMinimumLevel(LogLevel.Warning));
            editor = new VideoEditorWindow(
                recording,
                AppPaths.CreateForRoot(outputDirectory),
                loggerFactory)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Width = 760,
                Height = 620,
                Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Left + 20),
                Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Top + 20),
                ShowActivated = false,
            };
            VideoEditorWindow shown = editor;
            _editorState = () => DescribeEditor(shown);
            editor.Show();

            Enter("wait for media ready", MediaPhaseBudget);
            bool ready = PumpUntil(
                () => editor.IsMediaReadyForTest || editor.HasMediaFailedForTest,
                TimeSpan.FromSeconds(10));
            Gate(ready && editor.IsMediaReadyForTest && !editor.HasMediaFailedForTest,
                "real MP4 opened in the real WPF editor",
                "real MP4 did not reach ready state: " + editor.MediaFailureForTest);
            if (!editor.IsMediaReadyForTest)
            {
                return report.Finish(failures);
            }

            Enter("layout gates", ShortPhaseBudget);
            TwoLineTimeline timeline = editor.TimelineForTest;
            PreviewSeekCoordinator coordinator = editor.PreviewSeekCoordinatorForTest;
            editor.UpdateLayout();
            timeline.FlushRenderForTest();

            // WPF's NaturalDuration is Windows Media Player's whole-second value; the editor must
            // use the container duration so the final fraction can be trimmed and exported.
            double clipMs = editor.DurationMsForTest;
            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"Durations: container {containerMs:0.###}ms, editor {clipMs:0.###}ms, " +
                $"trim out {timeline.OutMs:0.###}ms, recorded {recording.DurationMs:0.###}ms"));
            Gate(containerMs > 0
                    && Math.Abs(clipMs - containerMs) <= 0.01
                    && Math.Abs(timeline.OutMs - containerMs) <= 0.01,
                "editor duration and initial trim end equal the exact container duration",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"editor duration {clipMs:0.###}ms / trim out {timeline.OutMs:0.###}ms differ from container {containerMs:0.###}ms"));

            Gate(timeline.FixedVisualCountForTest == 9,
                "three surfaces retain exactly nine fixed DrawingVisual layers",
                $"fixed visual count was {timeline.FixedVisualCountForTest}, expected 9");
            Gate(editor.ControlRowCountForTest == 2,
                "editor controls remain in two fixed rows",
                $"control row count was {editor.ControlRowCountForTest}, expected 2");
            Gate(editor.WidestControlRowContentWidthForTest <= editor.ControlAreaWidthForTest + 0.5,
                "two-row controls fit the 760px minimum-width window",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"controls overflowed at 760px: content={editor.WidestControlRowContentWidthForTest:0.0}, " +
                    $"available={editor.ControlAreaWidthForTest:0.0}"));
            Gate(timeline.VisibleSpanMs <= timeline.CoarseIntervalMs + 0.001,
                "initial detail view remains one coarse overview interval",
                "initial detail range no longer matches the 0.7.0 hierarchy");

            // Allocation-only hot loop: no dispatcher pump, so this isolates pointer-to-intent
            // state mutation from decoder/composition work. Arrays are allocated before counting.
            Enter("intent burst", ShortPhaseBudget);
            var intentLatencies = new double[AllocationSamples];
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            long gen2Before = GC.CollectionCount(2);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < AllocationSamples; i++)
            {
                double target = (i % 1000) / 1000.0 * Math.Min(clipMs, timeline.VisibleSpanMs);
                long started = Stopwatch.GetTimestamp();
                timeline.SetPlayhead(target, ensureVisible: false);
                intentLatencies[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            long gen2Collections = GC.CollectionCount(2) - gen2Before;
            timeline.FlushRenderForTest();
            double intentP95 = Percentile(intentLatencies, 0.95);
            double intentP99 = Percentile(intentLatencies, 0.99);
            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"Intent-only burst: samples={AllocationSamples}, p95={intentP95:0.000}ms, " +
                $"p99={intentP99:0.000}ms, allocated={allocatedBytes}B, Gen2={gen2Collections}"));
            Gate(intentP95 <= 16.7,
                $"pointer-to-intent p95 {intentP95:0.000}ms <= 16.7ms",
                $"pointer-to-intent p95 {intentP95:0.000}ms exceeded 16.7ms");
            Gate(intentP99 <= 33.0,
                $"pointer-to-intent p99 {intentP99:0.000}ms <= 33ms",
                $"pointer-to-intent p99 {intentP99:0.000}ms exceeded 33ms");
            Gate(allocatedBytes <= 1_500_000,
                $"intent loop allocation {allocatedBytes}B <= 1,500,000B",
                $"intent loop allocated {allocatedBytes}B, above the zero-Shape budget");
            Gate(gen2Collections == 0,
                "intent loop caused zero Gen2 collections",
                $"intent loop caused {gen2Collections} Gen2 collection(s)");

            // Filmstrip publications request legitimate static redraws independently of pointer input:
            // each decoded batch must appear, so a batch landing between two scrub frames costs one
            // extra composition frame. Measure and gate that phase on its own, then count the scrub.
            Enter("filmstrip preparation", MediaPhaseBudget);
            long preparationFramesBefore = timeline.RenderFrameCountForTest;
            editor.UpdateLayout();
            bool filmstripCompleted = PumpUntil(
                () => timeline.FilmstripForTest?.IsCompleted == true, TimeSpan.FromSeconds(10));
            bool publicationsDrained = false;
            _ = editor.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => publicationsDrained = true));
            bool publicationDrainCompleted = PumpUntil(() => publicationsDrained, TimeSpan.FromSeconds(1));
            timeline.FlushRenderForTest();
            Gate(filmstripCompleted && publicationDrainCompleted && timeline.FilmstripForTest?.IsCompleted == true,
                "filmstrip decoder and queued publications completed before scrub measurement",
                "filmstrip preparation did not complete within its bounded readiness budget");
            int tiles = timeline.FilmstripForTest?.Layout.TileCount ?? 0;
            int publicationBatches = (tiles + TimelineFilmstrip.PublishBatchSize - 1) / TimelineFilmstrip.PublishBatchSize;
            long preparationFrames = timeline.RenderFrameCountForTest - preparationFramesBefore;
            Line($"Composition preparation: decoder-completed={filmstripCompleted}; publications-drained={publicationDrainCompleted}; " +
                $"published={timeline.FilmstripForTest?.PublishedCount}; tiles={tiles}; batches={publicationBatches}; " +
                $"render-frames={preparationFrames}");
            // At most one coalesced frame per published batch, plus the layout pass that started it.
            Gate(preparationFrames <= publicationBatches + 1,
                $"filmstrip preparation drew {preparationFrames} <= {publicationBatches} batch(es) + 1 frames",
                $"filmstrip preparation drew {preparationFrames} frames for {publicationBatches} publication batch(es)");

            // Five seconds at approximately 60 input samples/sec. SeekFromOverview exercises
            // the real event wiring and worst-case detail-follow path, not a private shortcut.
            Enter("scrub", MediaPhaseBudget);
            var pointerLatencies = new double[DragSamples];
            var cycleTimes = new double[DragSamples - 1];
            long framesBefore = timeline.RenderFrameCountForTest;
            long seeksBefore = coordinator.IssuedSeekCountForTest;
            PresentedFrameVerification? verification = StudioRangeVideoEffect.VerificationFor(editor.MediaForTest);
            int samplesBeforeScrub = verification?.Samples ?? 0;
            int decodesBeforeScrub = verification?.Decodes ?? 0;
            bool verificationOverlappedScrub = verification?.IsRunning == true;
            long previousCycle = Stopwatch.GetTimestamp();
            for (int i = 0; i < DragSamples; i++)
            {
                if (i > 0)
                {
                    cycleTimes[i - 1] = Stopwatch.GetElapsedTime(previousCycle).TotalMilliseconds;
                }
                previousCycle = Stopwatch.GetTimestamp();
                _watchdog?.Step(i);

                double fraction = i / (double)(DragSamples - 1);
                double target = clipMs * fraction;
                long started = Stopwatch.GetTimestamp();
                timeline.SeekFromOverview(target);
                verificationOverlappedScrub |= verification?.IsRunning == true;
                pointerLatencies[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                PumpFor(TimeSpan.FromMilliseconds(16));
            }

            int scrubSamples = (verification?.Samples ?? 0) - samplesBeforeScrub;
            int scrubDecodes = (verification?.Decodes ?? 0) - decodesBeforeScrub;
            long framesAfterInput = timeline.RenderFrameCountForTest;
            Enter("release exact seek", ShortPhaseBudget);
            var releaseWatch = Stopwatch.StartNew();
            long finalGeneration = coordinator.RequestExact(clipMs * 0.75);
            bool exactCompleted = PumpUntil(
                () => coordinator.PresentedGeneration == finalGeneration,
                TimeSpan.FromSeconds(3));
            releaseWatch.Stop();
            double pointerP95 = Percentile(pointerLatencies, 0.95);
            double pointerP99 = Percentile(pointerLatencies, 0.99);
            double cycleP50 = Percentile(cycleTimes, 0.50);
            double cycleP95 = Percentile(cycleTimes, 0.95);
            double cycleP99 = Percentile(cycleTimes, 0.99);
            double maxCycle = cycleTimes.Max();
            long framesAfterRelease = timeline.RenderFrameCountForTest;
            long renderedFrames = framesAfterInput - framesBefore;
            long releaseFrames = framesAfterRelease - framesAfterInput;
            long totalFrames = framesAfterRelease - framesBefore;
            long issuedSeeks = coordinator.IssuedSeekCountForTest - seeksBefore;
            Line(string.Create(CultureInfo.InvariantCulture,
                $"Composition scopes: start={framesBefore}, input-end={framesAfterInput}, release-end={framesAfterRelease}; " +
                $"input-frames={renderedFrames}, release-frames={releaseFrames}, " +
                $"total-frames={totalFrames}; release-exact-wait={releaseWatch.Elapsed.TotalMilliseconds:0.000}ms"));
            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"5s real scrub: samples={DragSamples}, pointer p95={pointerP95:0.000}ms, " +
                $"p99={pointerP99:0.000}ms, cycle p50={cycleP50:0.000}ms, p95={cycleP95:0.000}ms, " +
                $"p99={cycleP99:0.000}ms, max={maxCycle:0.000}ms, " +
                $"render-frames={renderedFrames}, decoder-seeks={issuedSeeks}, " +
                $"dropped={coordinator.DroppedRequestCountForTest}, stale={coordinator.StaleResultCountForTest}"));
            Line($"Verification/scrub: mode={_verificationMode}; samples={scrubSamples}; decodes={scrubDecodes}; "
                + $"overlap={verificationOverlappedScrub}; outcome={verification?.LastResult}; effect={editor.MediaForTest.Effect?.GetType().Name ?? "None"}");
            Gate(scrubSamples == 0 && scrubDecodes == 0 && !verificationOverlappedScrub,
                "no range verification sampling or decoding during scrub",
                $"range verification overlapped scrub: samples={scrubSamples}, decodes={scrubDecodes}, active={verificationOverlappedScrub}");
            Gate(pointerP95 <= 16.7,
                $"real scrub pointer p95 {pointerP95:0.000}ms <= 16.7ms",
                $"real scrub pointer p95 {pointerP95:0.000}ms exceeded 16.7ms");
            Gate(pointerP99 <= 33.0,
                $"real scrub pointer p99 {pointerP99:0.000}ms <= 33ms",
                $"real scrub pointer p99 {pointerP99:0.000}ms exceeded 33ms");
            // A single desktop-compositor or OS scheduling outlier is not evidence that the
            // editor's input/render path is persistently blocked. Gate both tail latency and a
            // separate hard-freeze ceiling, while retaining the absolute maximum in the report.
            Gate(cycleP99 <= 50.0,
                $"UI cycle p99 {cycleP99:0.000}ms <= 50ms",
                $"UI cycle p99 {cycleP99:0.000}ms exceeded 50ms");
            Gate(maxCycle <= 150.0,
                $"maximum observed UI cycle {maxCycle:0.000}ms <= 150ms freeze ceiling",
                $"maximum observed UI cycle {maxCycle:0.000}ms exceeded the 150ms freeze ceiling");
            // Each phase is gated on its own and together, so frames cannot move between scopes
            // unnoticed. Input: at most one coalesced frame per pointer sample, with the original
            // one-frame tolerance. Release: the exact seek presents through the preview, not the
            // timeline, so the only legitimate timeline frame is the last sample's invalidation when
            // it has not reached a composition tick by input end. Total: the original
            // scrub-through-release budget, which the two phase limits alone would exceed by one.
            Gate(renderedFrames <= DragSamples + 1,
                $"input phase draws {renderedFrames} <= {DragSamples} samples + 1",
                $"input phase drew {renderedFrames} frames for {DragSamples} samples");
            Gate(releaseFrames <= 1,
                $"release phase draws {releaseFrames} <= 1 trailing frame",
                $"release phase drew {releaseFrames} frames; the exact seek must not redraw the timeline");
            Gate(totalFrames <= DragSamples + 1,
                $"scrub and release draw {totalFrames} <= {DragSamples} samples + 1",
                $"scrub and release drew {totalFrames} frames for {DragSamples} samples");
            Gate(coordinator.MaxObservedInFlightForTest <= 1,
                "decoder had at most one seek in flight",
                $"decoder max in-flight was {coordinator.MaxObservedInFlightForTest}");
            Gate(coordinator.PendingCountForTest <= 1,
                "decoder retained at most one pending latest seek",
                $"decoder pending count was {coordinator.PendingCountForTest}");
            Gate(exactCompleted && coordinator.PresentedMode == PreviewSeekMode.Exact,
                "release exact seek completed and stale preview was suppressed",
                "release exact seek did not become the final presented generation");

            Enter("exact seeks", ExactSeekPhaseBudget);
            var exactLatencies = new double[ExactSamples];
            for (int i = 0; i < ExactSamples; i++)
            {
                _watchdog?.Step(i);
                double target = clipMs * ((i + 1.0) / (ExactSamples + 1.0));
                long started = Stopwatch.GetTimestamp();
                long generation = coordinator.RequestExact(target);
                bool completed = PumpUntil(
                    () => coordinator.PresentedGeneration == generation,
                    TimeSpan.FromSeconds(2));
                exactLatencies[i] = completed
                    ? Stopwatch.GetElapsedTime(started).TotalMilliseconds
                    : 2000;
            }

            double exactP95 = Percentile(exactLatencies, 0.95);
            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"Exact seeks: samples={ExactSamples}, p95={exactP95:0.000}ms"));
            Gate(exactP95 <= 250.0,
                $"exact seek p95 {exactP95:0.000}ms <= 250ms",
                $"exact seek p95 {exactP95:0.000}ms exceeded 250ms");

            // The final frame lies past Windows Media Player's whole-second length; the preview
            // must still seek there instead of stopping at the truncated end.
            Enter("final-frame seek", ShortPhaseBudget);
            double tailTarget = Math.Max(0, clipMs - (500.0 / Math.Max(1, recording.Fps)));
            long tailGeneration = coordinator.RequestExact(tailTarget);
            bool tailPresented = PumpUntil(
                () => coordinator.PresentedGeneration == tailGeneration,
                TimeSpan.FromSeconds(2));
            double tailPresentedMs = coordinator.PresentedPositionMs;
            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"Final-frame seek: requested {tailTarget:0.###}ms, presented {tailPresentedMs:0.###}ms"));
            Gate(tailPresented && Math.Abs(tailPresentedMs - tailTarget) <= 5.0,
                "preview seeks into the final frame of the exact duration",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"final-frame seek to {tailTarget:0.###}ms presented {tailPresentedMs:0.###}ms (completed={tailPresented})"));

            Enter("trim and zoom", ShortPhaseBudget);
            timeline.SetIn(clipMs * 0.1);
            timeline.SetOut(clipMs * 0.9);
            timeline.ZoomAroundPlayhead(0.5);
            timeline.FitAll();
            Gate(timeline.IsFitAll && timeline.InMs < timeline.OutMs,
                "trim, zoom, and Fit All remain usable after sustained scrub",
                "timeline invariants failed after scrub/trim/zoom flow");

            Enter("window screenshot", ShortPhaseBudget);
            CaptureWindow(editor, screenshotPath);
            Gate(File.Exists(screenshotPath) && new FileInfo(screenshotPath).Length > 1000,
                "real editor window screenshot was captured for visual review",
                "editor screenshot was not produced");
            Line($"Screenshot: {screenshotPath}");
            Line($"Source clip retained: {clipPath}");
        }
        catch (Exception ex)
        {
            failures.Add("Unhandled self-test exception: " + ex.Message);
            Line();
            Line(ex.ToString());
        }
        finally
        {
            Enter("close editor", ShortPhaseBudget);
            editor?.Close();
            PumpFor(TimeSpan.FromMilliseconds(30));
            _editorState = null;
        }

        return report.Finish(failures);
    }

    private static RecordingResult RecordClip(string path)
    {
        Enter("record source clip", MediaPhaseBudget);
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        var grabber = new RegionFrameGrabber(engine, includeCursor: false);
        using var firstFrame = new ManualResetEventSlim();
        using var recorder = new RegionRecorder(
            grabber,
            options => new SourceEncoder(
                new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance), firstFrame),
            NullLogger.Instance);
        recorder.Start(
            new RectD(0, 0, 320, 240),
            path,
            new RecordingSettings { FrameRate = RecordingFrameRate.Fps15, IncludeCursor = false });
        var startup = Stopwatch.StartNew();
        while (!firstFrame.Wait(50) && recorder.IsRecording && startup.Elapsed < TimeSpan.FromSeconds(15)) { }
        if (!firstFrame.IsSet)
        {
            _ = recorder.Stop();
            throw new TimeoutException("Video-editor source produced no encoded frame within 15 seconds.");
        }
        // Sample real recording time after startup, so a slow WGC/codec cannot consume
        // the whole fixture interval and leave a single timestamp-zero frame.
        Thread.Sleep(2500);
        Enter("finish source clip", MediaPhaseBudget);
        RecordingResult recording = recorder.Stop();
        ValidateSourceClip(recording, MediaFoundationVideoFrameReader.ReadContainerDurationMs(path));
        return recording;
    }

    internal static void ValidateSourceClip(RecordingResult recording, double containerDurationMs)
    {
        if (recording.EmittedFrames < 2 || !double.IsFinite(containerDurationMs) || containerDurationMs < 2000)
            throw new InvalidOperationException(
                $"Video-editor source needs multiple frames and at least 2000ms of media; " +
                $"got {recording.EmittedFrames} frame(s), container {containerDurationMs:0.###}ms, " +
                $"recorder {recording.DurationMs:0.###}ms.");
    }

    private sealed class SourceEncoder(IVideoEncoder inner, ManualResetEventSlim firstFrame) : IVideoEncoder
    {
        public int Width => inner.Width;
        public int Height => inner.Height;
        public void WriteFrame(in EncoderFrame frame) { inner.WriteFrame(frame); firstFrame.Set(); }
        public void Complete() => inner.Complete();
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Starts a bounded phase and saves the report so far, so a stall leaves a record.</summary>
    private static void Enter(string phase, TimeSpan budget)
    {
        _watchdog?.Enter(phase, budget);
        _report?.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"Phase: {phase} (+{Stopwatch.GetElapsedTime(_started).TotalSeconds:0.000}s)"));
        _report?.Save();
    }

    /// <summary>
    /// Runs on the watchdog thread when a phase outlives its budget: records where the process
    /// stopped, and how (busy or blocked, which windows are open, a dump for the stacks), then
    /// ends the process so the runner gets a failure in seconds instead of at its own limit.
    /// </summary>
    private static void OnStalled(SelfTestStall stall)
    {
        ProgressReport? report = Volatile.Read(ref _report);
        if (report is null)
        {
            SelfTestStallEvidence.Terminate(SelfTestStallEvidence.StalledExitCode);
            return;
        }

        string failure = string.Create(
            CultureInfo.InvariantCulture,
            $"self-test stalled in phase '{stall.Phase}' (step {stall.Step}) for " +
            $"{stall.Elapsed.TotalSeconds:0.0}s; the phase budget is {stall.Budget.TotalSeconds:0}s");
        var evidence = new StringBuilder();
        evidence.Append(SelfTestStallEvidence.DescribeThreads(_uiThreadId, TimeSpan.FromSeconds(1)));
        evidence.Append(SelfTestStallEvidence.DescribeWindows());
        evidence.AppendLine(ReadEditorState(TimeSpan.FromSeconds(2)));
        // Save before the dump, which can take a while: the runner must find the record even
        // if its own limit ends the process first.
        report.Stalled(failure, evidence.ToString());
        string dumpPath = Path.Combine(
            Path.GetDirectoryName(report.Path) ?? Path.GetTempPath(),
            "video-editor-stall.dmp");
        evidence.AppendLine(SelfTestStallEvidence.WriteDump(dumpPath, TimeSpan.FromSeconds(120)));
        report.Stalled(failure, evidence.ToString());
        SelfTestStallEvidence.Terminate(SelfTestStallEvidence.StalledExitCode);
    }

    /// <summary>Reads the editor's state off the stalled UI thread, giving up if a lock is held.</summary>
    private static string ReadEditorState(TimeSpan timeout)
    {
        Func<string>? state = Volatile.Read(ref _editorState);
        if (state is null)
        {
            return "Editor: not open";
        }

        Task<string> read = Task.Run(() =>
        {
            try
            {
                return state();
            }
            catch (Exception ex)
            {
                return "Editor: state unavailable (" + ex.GetType().Name + ": " + ex.Message + ")";
            }
        });
        return read.Wait(timeout) ? read.Result : "Editor: state read blocked (a lock is held by the stalled thread)";
    }

    // Plain fields and locked counters only: this runs off the UI thread.
    private static string DescribeEditor(VideoEditorWindow editor)
    {
        PreviewSeekCoordinator coordinator = editor.PreviewSeekCoordinatorForTest;
        TwoLineTimeline timeline = editor.TimelineForTest;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Editor: media-ready={editor.IsMediaReadyForTest}, media-opened={editor.HasMediaOpenedForTest}, " +
            $"media-failed={editor.HasMediaFailedForTest}, source-range-known={editor.IsSourceRangeKnownForTest}, " +
            $"filmstrip-completed={timeline.FilmstripForTest?.IsCompleted}, render-frames={timeline.RenderFrameCountForTest}, " +
            $"presented-generation={coordinator.PresentedGeneration}, presented-mode={coordinator.PresentedMode}, " +
            $"in-flight={coordinator.IsInFlightForTest}, pending={coordinator.PendingCountForTest}, " +
            $"issued-seeks={coordinator.IssuedSeekCountForTest}");
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpFor(TimeSpan.FromMilliseconds(10));
        }

        return condition();
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static double Percentile(double[] source, double percentile)
    {
        double[] sorted = (double[])source.Clone();
        Array.Sort(sorted);
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static void CaptureWindow(Window window, string path)
    {
        window.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    /// <summary>
    /// The report of one run, written as it grows: each phase saves it without a RESULT line, so
    /// a process that hangs or is ended leaves the last phase it reached. Lines are added on the
    /// UI thread and read by the watchdog, hence the lock.
    /// </summary>
    private sealed class ProgressReport(string path)
    {
        private static readonly UTF8Encoding Utf8 = new(false);
        private readonly object _gate = new();
        private readonly StringBuilder _body = new();
        private string _result = string.Empty;

        internal string Path => path;

        internal void Line(string value = "")
        {
            lock (_gate)
            {
                _body.AppendLine(value);
            }
        }

        /// <summary>Saves the lines so far, after the result once <see cref="Finish"/> has run.</summary>
        internal void Save()
        {
            string text;
            lock (_gate)
            {
                text = _body + _result;
            }

            try
            {
                File.WriteAllText(path, text, Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A progress save must never fail the run it records; the final write throws.
            }
        }

        internal int Finish(List<string> failures)
        {
            var result = new StringBuilder();
            result.AppendLine();
            result.AppendLine(failures.Count == 0
                ? "RESULT: PASS"
                : $"RESULT: FAIL ({failures.Count} gate(s))");
            foreach (string failure in failures)
            {
                result.AppendLine("  - " + failure);
            }

            string text;
            lock (_gate)
            {
                _result = result.ToString();
                text = _body + _result;
            }

            File.WriteAllText(path, text, Utf8);
            return failures.Count == 0 ? 0 : 1;
        }

        /// <summary>Replaces any earlier result: a stall, even after a pass, fails the run.</summary>
        internal void Stalled(string failure, string evidence)
        {
            var text = new StringBuilder();
            lock (_gate)
            {
                text.Append(_body);
            }

            text.AppendLine();
            text.AppendLine("FAIL: " + failure);
            text.AppendLine("Stall evidence:");
            text.Append(evidence);
            text.AppendLine();
            text.AppendLine("RESULT: FAIL (stalled)");
            text.AppendLine("  - " + failure);
            try
            {
                File.WriteAllText(path, text.ToString(), Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The exit code still reports the stall.
            }
        }
    }
}
