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

    private const int DragSamples = 300;
    private const int AllocationSamples = 6000;
    private const int ExactSamples = 20;
    private const string SoakIterationsEnvironmentVariable =
        "MYCAPTURE_VIDEO_EDITOR_SELFTEST_ITERATIONS";
    private const long MaxSoakManagedGrowthBytes = 16L * 1024 * 1024;
    private const long MaxSoakPrivateGrowthBytes = 20L * 1024 * 1024;

    internal static int Run(string outputDirectory)
    {
        int iterations = ReadSoakIterations();
        return iterations == 1
            ? RunSingle(outputDirectory)
            : RunSoak(outputDirectory, iterations);
    }

    private static int RunSoak(string outputDirectory, int iterations)
    {
        Directory.CreateDirectory(outputDirectory);
        string reportPath = Path.Combine(outputDirectory, "video-editor-selftest-report.txt");
        string sharedClipPath = Path.Combine(outputDirectory, "responsiveness-soak-source.mp4");
        RecordingResult recording = RecordClip(sharedClipPath);
        var report = new StringBuilder();
        var failures = new List<string>();
        var managedBytes = new long[iterations];
        var privateBytes = new long[iterations];
        var workingSetBytes = new long[iterations];
        using Process process = Process.GetCurrentProcess();

        report.AppendLine("MyCapture video-editor same-process soak");
        report.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
        report.AppendLine($"OS: {Environment.OSVersion}");
        report.AppendLine($"Iterations: {iterations}");
        report.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Shared real clip: {recording.Width}x{recording.Height} @ {recording.Fps}fps, " +
            $"{recording.EmittedFrames} frames, {recording.DurationMs:0}ms, " +
            $"{new FileInfo(sharedClipPath).Length} bytes"));
        report.AppendLine(
            "The real MP4 is recorded once so retained-memory measurements isolate editor " +
            "open/scrub/exact-seek/close lifecycle rather than recorder/encoder caches.");
        report.AppendLine();

        for (int i = 0; i < iterations; i++)
        {
            string iterationDirectory = Path.Combine(
                outputDirectory,
                string.Create(CultureInfo.InvariantCulture, $"iteration-{i + 1:000}"));
            long started = Stopwatch.GetTimestamp();
            int result = RunSingle(iterationDirectory, recording);

            // Let MediaElement/Dispatcher teardown complete before measuring retained state.
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
            report.AppendLine(string.Create(
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
        report.AppendLine();
        report.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Retained managed growth: {managedGrowth}B " +
            $"(limit {MaxSoakManagedGrowthBytes}B)"));
        report.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Retained private growth: {privateGrowth}B " +
            $"(limit {MaxSoakPrivateGrowthBytes}B)"));
        report.AppendLine(string.Create(
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

        report.AppendLine();
        report.AppendLine(failures.Count == 0
            ? "RESULT: PASS"
            : $"RESULT: FAIL ({failures.Count} gate(s))");
        foreach (string failure in failures)
        {
            report.AppendLine("  - " + failure);
        }

        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        return failures.Count == 0 ? 0 : 1;
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
        string reportPath = Path.Combine(outputDirectory, "video-editor-selftest-report.txt");
        var report = new StringBuilder();
        var failures = new List<string>();
        VideoEditorWindow? editor = null;

        void Line(string value = "") => report.AppendLine(value);
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
            Line();

            RecordingResult recording = sharedRecording ?? RecordClip(clipPath);
            double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clipPath);
            ValidateSourceClip(recording, containerMs);
            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"Real clip: {recording.Width}x{recording.Height} @ {recording.Fps}fps, " +
                $"{recording.EmittedFrames} frames, {recording.DurationMs:0}ms, " +
                $"{new FileInfo(clipPath).Length} bytes"));

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
            editor.Show();

            bool ready = PumpUntil(
                () => editor.IsMediaReadyForTest || editor.HasMediaFailedForTest,
                TimeSpan.FromSeconds(10));
            Gate(ready && editor.IsMediaReadyForTest && !editor.HasMediaFailedForTest,
                "real MP4 opened in the real WPF editor",
                "real MP4 did not reach ready state: " + editor.MediaFailureForTest);
            if (!editor.IsMediaReadyForTest)
            {
                return Finish(reportPath, report, failures);
            }

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
            var pointerLatencies = new double[DragSamples];
            var cycleTimes = new double[DragSamples - 1];
            long framesBefore = timeline.RenderFrameCountForTest;
            long seeksBefore = coordinator.IssuedSeekCountForTest;
            long previousCycle = Stopwatch.GetTimestamp();
            for (int i = 0; i < DragSamples; i++)
            {
                if (i > 0)
                {
                    cycleTimes[i - 1] = Stopwatch.GetElapsedTime(previousCycle).TotalMilliseconds;
                }
                previousCycle = Stopwatch.GetTimestamp();

                double fraction = i / (double)(DragSamples - 1);
                double target = clipMs * fraction;
                long started = Stopwatch.GetTimestamp();
                timeline.SeekFromOverview(target);
                pointerLatencies[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                PumpFor(TimeSpan.FromMilliseconds(16));
            }

            long framesAfterInput = timeline.RenderFrameCountForTest;
            var releaseWatch = Stopwatch.StartNew();
            long finalGeneration = coordinator.RequestExact(clipMs * 0.75);
            bool exactCompleted = PumpUntil(
                () => coordinator.PresentedGeneration == finalGeneration,
                TimeSpan.FromSeconds(3));
            releaseWatch.Stop();
            double pointerP95 = Percentile(pointerLatencies, 0.95);
            double pointerP99 = Percentile(pointerLatencies, 0.99);
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
                $"p99={pointerP99:0.000}ms, cycle p95={cycleP95:0.000}ms, " +
                $"p99={cycleP99:0.000}ms, max={maxCycle:0.000}ms, " +
                $"render-frames={renderedFrames}, decoder-seeks={issuedSeeks}, " +
                $"dropped={coordinator.DroppedRequestCountForTest}, stale={coordinator.StaleResultCountForTest}"));
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

            var exactLatencies = new double[ExactSamples];
            for (int i = 0; i < ExactSamples; i++)
            {
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

            timeline.SetIn(clipMs * 0.1);
            timeline.SetOut(clipMs * 0.9);
            timeline.ZoomAroundPlayhead(0.5);
            timeline.FitAll();
            Gate(timeline.IsFitAll && timeline.InMs < timeline.OutMs,
                "trim, zoom, and Fit All remain usable after sustained scrub",
                "timeline invariants failed after scrub/trim/zoom flow");

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
            editor?.Close();
            PumpFor(TimeSpan.FromMilliseconds(30));
        }

        return Finish(reportPath, report, failures);
    }

    private static RecordingResult RecordClip(string path)
    {
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

    private static int Finish(string reportPath, StringBuilder report, List<string> failures)
    {
        report.AppendLine();
        report.AppendLine(failures.Count == 0
            ? "RESULT: PASS"
            : $"RESULT: FAIL ({failures.Count} gate(s))");
        foreach (string failure in failures)
        {
            report.AppendLine("  - " + failure);
        }

        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        return failures.Count == 0 ? 0 : 1;
    }
}
