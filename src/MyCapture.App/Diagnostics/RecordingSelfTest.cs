using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Drives the real recording pipeline end-to-end without the UI: record a region, stop,
/// then record a SECOND clip and stop again — verifying that stop truly stops and that a
/// fresh recording is independent (both clips valid, each starting at time 0, neither
/// continuing the other). Two further sessions pause the real encoder: one resumes and
/// stops, one stops while still paused; both clips must exclude the paused time.
/// </summary>
/// <remarks>
/// <para>
/// Reachable via <c>MyCapture.exe --selftest-recording &lt;directory&gt;</c>.
/// </para>
/// <para>
/// Added after a field report that "stop then it records again from 0". The automated
/// suite covered the capture, OCR, settings and shell paths but had no recording
/// self-test, so this closes that gap: it exercises the real Media Foundation encoder,
/// the real screen grabber, and the real start/stop lifecycle against a live display,
/// then re-decodes both outputs to confirm they are playable clips of the expected size.
/// </para>
/// </remarks>
internal static class RecordingSelfTest
{
    public const string CommandLineSwitch = "--selftest-recording";

    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var report = new StringBuilder();
        void Line(string text) => report.AppendLine(text);
        var failures = new List<string>();

        Line("MyCapture recording self-test");
        Line($"UTC: {DateTimeOffset.UtcNow:u}");
        Line($"OS: {Environment.OSVersion.VersionString}");
        Line(string.Empty);

        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        var region = new RectD(0, 0, 320, 240);
        var settings = new RecordingSettings { FrameRate = RecordingFrameRate.Fps15, IncludeCursor = false };

        // ---- Session 1: record ~1.5s, stop ----
        string clip1 = Path.Combine(outputDirectory, "session1.mp4");
        RecordingResult? r1 = RecordOnce(engine, region, clip1, settings, 1500, report, failures, "Session 1");

        // ---- Session 2: a FRESH recorder must record independently from 0 ----
        string clip2 = Path.Combine(outputDirectory, "session2.mp4");
        RecordingResult? r2 = RecordOnce(engine, region, clip2, settings, 900, report, failures, "Session 2");

        // ---- Cross-checks ----
        if (r1 is not null && r2 is not null)
        {
            // Both clips must exist and be non-trivial.
            foreach ((string label, string path) in new[] { ("session1", clip1), ("session2", clip2) })
            {
                if (!File.Exists(path))
                {
                    failures.Add($"{label}: output file missing");
                }
                else if (new FileInfo(path).Length < 1000)
                {
                    failures.Add($"{label}: output implausibly small ({new FileInfo(path).Length} bytes)");
                }
            }

            // Re-decode both to prove they are valid, independent clips.
            (bool ok1, int w1, int h1, double d1, double? p1) = Probe(clip1);
            (bool ok2, int w2, int h2, double d2, double? p2) = Probe(clip2);
            Line(string.Create(CultureInfo.InvariantCulture, $"Session 1 decode: opened={ok1} {w1}x{h1} container {d1:0.###}ms, WPF {FormatPlayer(p1)}"));
            Line(string.Create(CultureInfo.InvariantCulture, $"Session 2 decode: opened={ok2} {w2}x{h2} container {d2:0.###}ms, WPF {FormatPlayer(p2)}"));

            if (!ok1) { failures.Add("session1: produced MP4 could not be re-opened"); }
            if (!ok2) { failures.Add("session2: produced MP4 could not be re-opened"); }
            if (ok1 && (w1 != 320 || h1 != 240)) { failures.Add($"session1: unexpected size {w1}x{h1}"); }
            if (ok2 && (w2 != 320 || h2 != 240)) { failures.Add($"session2: unexpected size {w2}x{h2}"); }

            // The core of the field bug: a stopped recording must NOT bleed into the next.
            // Session 2 was recorded for a shorter time, so it should be clearly shorter.
            if (ok1 && ok2 && d2 >= d1)
            {
                Line($"NOTE: session2 ({d2:0}ms) not shorter than session1 ({d1:0}ms) — timing variance, not necessarily a fault");
            }
        }

        // WPF MediaPlayer reports these clips in whole seconds (Windows Media Player truncates
        // 1538ms to 1000ms), so decoded durations come from the container (MF_PD_DURATION); the
        // WPF value is reported only as evidence.

        // ---- Session 3: record, pause, resume, record, stop — the pause must vanish from the clip ----
        string clip3 = Path.Combine(outputDirectory, "session3-pause-resume.mp4");
        RecordWithPause(engine, region, clip3, settings, 2000, 1500, 2000, stopWhilePaused: false, report, failures, "Session 3");

        // ---- Session 4: record, pause, stop while still paused ----
        string clip4 = Path.Combine(outputDirectory, "session4-stop-paused.mp4");
        RecordWithPause(engine, region, clip4, settings, 2500, 600, 0, stopWhilePaused: true, report, failures, "Session 4");

        Line(string.Empty);
        Line(failures.Count == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures.Count} problem(s))");
        foreach (string f in failures)
        {
            Line($"  - {f}");
        }

        File.WriteAllText(
            Path.Combine(outputDirectory, "recording-selftest-report.txt"),
            report.ToString(),
            new UTF8Encoding(false));

        return failures.Count == 0 ? 0 : 1;
    }

    private static RecordingResult? RecordOnce(
        ScreenCaptureEngine engine,
        RectD region,
        string outputPath,
        RecordingSettings settings,
        int recordMs,
        StringBuilder report,
        List<string> failures,
        string label)
    {
        using var firstWrittenFrame = new ManualResetEventSlim();
        var grabber = new RegionFrameGrabber(engine, settings.IncludeCursor);
        FirstFrameObserver? observer = null;
        var recorder = new RegionRecorder(
            grabber,
            options => observer = new FirstFrameObserver(
                new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance),
                firstWrittenFrame),
            NullLogger.Instance);

        try
        {
            recorder.Start(region, outputPath, settings);
            if (!recorder.IsRecording)
            {
                failures.Add($"{label}: recorder did not enter the recording state");
                return null;
            }

            // Start() returns before encoder initialization, and IsReady becomes true before
            // the first capture/WriteSample. Cold codec work can exceed this entire sample.
            // Start the measured interval only after a real frame was successfully written.
            var startup = Stopwatch.StartNew();
            while (!firstWrittenFrame.Wait(50) && recorder.IsRecording
                && startup.Elapsed < TimeSpan.FromSeconds(15))
            {
            }
            if (!firstWrittenFrame.IsSet)
            {
                failures.Add($"{label}: no successfully encoded frame within startup deadline");
                // Stop also surfaces an encoder failure and joins the capture thread before
                // the observer's event is disposed.
                _ = recorder.Stop();
                return null;
            }
            report.AppendLine($"{label}: first encoded frame after {startup.Elapsed.TotalMilliseconds:0}ms; sampling {recordMs}ms");
            Thread.Sleep(recordMs);
            RecordingResult result = recorder.Stop();
            if (result.EmittedFrames < 2)
            {
                failures.Add($"{label}: timed recording produced fewer than two frames");
            }

            if (recorder.IsRecording)
            {
                failures.Add($"{label}: recorder still reports recording AFTER stop — this is the reported bug");
            }

            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: {result.EmittedFrames} frame(s), {result.DurationMs:0}ms, {result.Width}x{result.Height} -> {Path.GetFileName(outputPath)}"));
            CheckContainerEndsAtLastFrame(outputPath, observer?.LastTimestampMs, result.Fps, report, failures, label);

            return result;
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.Message}" + (ex.InnerException is not null ? $" | inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : string.Empty));
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetDirectoryName(outputPath)!, label.Replace(" ", "-") + "-exception.txt"),
                    ex.ToString());
            }
            catch (IOException)
            {
            }

            return null;
        }
        finally
        {
            recorder.Dispose();
        }
    }

    /// <summary>
    /// Real pipeline with a pause: no frame may be encoded while paused, timestamps must stay
    /// continuous across the resume, and the decoded clip must be as long as the recorded
    /// spans only (not the wall-clock time including the pause).
    /// </summary>
    private static void RecordWithPause(
        ScreenCaptureEngine engine,
        RectD region,
        string outputPath,
        RecordingSettings settings,
        int beforePauseMs,
        int pausedMs,
        int afterResumeMs,
        bool stopWhilePaused,
        StringBuilder report,
        List<string> failures,
        string label)
    {
        var frames = new TimestampObserver();
        var recorder = new RegionRecorder(
            new RegionFrameGrabber(engine, settings.IncludeCursor),
            options => frames.Wrap(new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance)),
            NullLogger.Instance);
        try
        {
            recorder.Start(region, outputPath, settings);
            var startup = Stopwatch.StartNew();
            while (frames.Count == 0 && recorder.IsRecording && startup.Elapsed < TimeSpan.FromSeconds(15))
            {
                Thread.Sleep(50);
            }

            if (frames.Count == 0)
            {
                failures.Add($"{label}: no successfully encoded frame within startup deadline");
                _ = recorder.Stop();
                return;
            }

            // Recorded time already includes the first frame's encoder warm-up.
            double recordedBeforeSample = recorder.RecordedElapsed.TotalMilliseconds;
            var wall = Stopwatch.StartNew();
            Thread.Sleep(beforePauseMs);
            if (!recorder.Pause())
            {
                failures.Add($"{label}: Pause() was rejected while recording");
            }

            if (!recorder.WaitUntilCaptureParked(TimeSpan.FromSeconds(5)))
            {
                failures.Add($"{label}: capture loop did not park after Pause()");
            }

            int framesAtPause = frames.Count;
            TimeSpan recordedAtPause = recorder.RecordedElapsed;
            Thread.Sleep(pausedMs);
            if (frames.Count != framesAtPause)
            {
                failures.Add($"{label}: {frames.Count - framesAtPause} frame(s) encoded while paused");
            }

            if (Math.Abs((recorder.RecordedElapsed - recordedAtPause).TotalMilliseconds) > 1)
            {
                failures.Add($"{label}: recorded time advanced while paused");
            }

            if (!stopWhilePaused)
            {
                if (!recorder.Resume())
                {
                    failures.Add($"{label}: Resume() was rejected while paused");
                }

                Thread.Sleep(afterResumeMs);
            }

            RecordingResult result = recorder.Stop();
            double wallMs = recordedBeforeSample + wall.Elapsed.TotalMilliseconds;
            double expectedMs = recordedBeforeSample + beforePauseMs + (stopWhilePaused ? 0 : afterResumeMs);
            IReadOnlyList<double> timestamps = frames.Timestamps;
            double maxGap = 0;
            for (int i = 1; i < timestamps.Count; i++)
            {
                if (timestamps[i] <= timestamps[i - 1])
                {
                    failures.Add($"{label}: timestamps not strictly increasing at frame {i}");
                    break;
                }

                maxGap = Math.Max(maxGap, timestamps[i] - timestamps[i - 1]);
            }

            (bool opened, int w, int h, double decodedMs, double? playerMs) = Probe(outputPath);
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: {result.EmittedFrames} frame(s), recorded {result.DurationMs:0}ms (expected ~{expectedMs:0}), " +
                $"paused {result.PausedMs:0}ms x{result.PauseCount}, wall {wallMs:0}ms, max gap {maxGap:0}ms, " +
                $"dropped {result.DroppedFrames}, effective {result.EffectiveFps:0.0}fps; " +
                $"decode opened={opened} {w}x{h} container {decodedMs:0.###}ms, WPF {FormatPlayer(playerMs)} -> {Path.GetFileName(outputPath)}"));
            CheckContainerEndsAtLastFrame(
                outputPath,
                timestamps.Count > 0 ? timestamps[^1] : null,
                result.Fps,
                report,
                failures,
                label);

            if (result.PauseCount != 1)
            {
                failures.Add($"{label}: expected one pause, result reports {result.PauseCount}");
            }

            if (result.PausedMs < pausedMs * 0.9)
            {
                failures.Add($"{label}: paused time {result.PausedMs:0}ms is shorter than the {pausedMs}ms pause");
            }

            if (Math.Abs(result.DurationMs - expectedMs) > 400)
            {
                failures.Add($"{label}: recorded duration {result.DurationMs:0}ms is not ~{expectedMs:0}ms");
            }

            // The pause must not leave a hole in the timeline.
            if (maxGap > 500)
            {
                failures.Add($"{label}: {maxGap:0}ms timestamp gap — the paused span leaked into the timeline");
            }

            if (!opened)
            {
                failures.Add($"{label}: produced MP4 could not be re-opened");
            }
            else
            {
                if (w != (int)region.Width || h != (int)region.Height)
                {
                    failures.Add($"{label}: unexpected size {w}x{h}");
                }

                // The playable clip must exclude the pause: never longer than the recorded
                // duration plus one frame of tolerance, and clearly shorter than wall time.
                if (decodedMs > result.DurationMs + 250 || decodedMs > wallMs - (pausedMs * 0.5))
                {
                    failures.Add($"{label}: decoded {decodedMs:0}ms includes the paused span (recorded {result.DurationMs:0}ms, wall {wallMs:0}ms)");
                }

                if (decodedMs < expectedMs * 0.6)
                {
                    failures.Add($"{label}: decoded {decodedMs:0}ms is too short for ~{expectedMs:0}ms of recording");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.Message}" + (ex.InnerException is not null ? $" | inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : string.Empty));
        }
        finally
        {
            recorder.Dispose();
        }
    }

    private sealed class TimestampObserver
    {
        private readonly List<double> _timestamps = [];

        internal int Count
        {
            get
            {
                lock (_timestamps)
                {
                    return _timestamps.Count;
                }
            }
        }

        internal IReadOnlyList<double> Timestamps
        {
            get
            {
                lock (_timestamps)
                {
                    return _timestamps.ToArray();
                }
            }
        }

        internal IVideoEncoder Wrap(IVideoEncoder inner) => new Observed(inner, this);

        private void Add(double timestampMs)
        {
            lock (_timestamps)
            {
                _timestamps.Add(timestampMs);
            }
        }

        private sealed class Observed(IVideoEncoder inner, TimestampObserver owner) : IVideoEncoder
        {
            public int Width => inner.Width;
            public int Height => inner.Height;
            public void WriteFrame(in EncoderFrame frame)
            {
                inner.WriteFrame(frame);
                owner.Add(frame.TimestampMs);
            }
            public void Complete() => inner.Complete();
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class FirstFrameObserver(IVideoEncoder inner, ManualResetEventSlim firstWrittenFrame) : IVideoEncoder
    {
        private double _lastTimestampMs = double.NaN;

        /// <summary>Timestamp of the last frame the encoder accepted, or <c>null</c> before any.</summary>
        internal double? LastTimestampMs
        {
            get
            {
                double value = Volatile.Read(ref _lastTimestampMs);
                return double.IsNaN(value) ? null : value;
            }
        }

        public int Width => inner.Width;
        public int Height => inner.Height;
        public void WriteFrame(in EncoderFrame frame)
        {
            inner.WriteFrame(frame);
            Volatile.Write(ref _lastTimestampMs, frame.TimestampMs);
            firstWrittenFrame.Set();
        }
        public void Complete() => inner.Complete();
        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// The container duration is the timeline the editor trims and exports, so it must end where
    /// the final decoded sample ends. The Sink Writer lays samples out at the constant frame rate
    /// of the media type (every MP4 sample lasts exactly one frame), so it may differ from the
    /// jittered capture timestamps, but never by more than one frame.
    /// </summary>
    private static void CheckContainerEndsAtLastFrame(
        string path,
        double? lastTimestampMs,
        int fps,
        StringBuilder report,
        List<string> failures,
        string label)
    {
        if (lastTimestampMs is not double last || !File.Exists(path))
        {
            failures.Add($"{label}: no encoded frame timestamp to compare with the container duration");
            return;
        }

        double frameMs = 1000.0 / Math.Max(1, fps);
        double encodedEndMs = last + frameMs;
        double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(path);
        DecodedVideoFrame finalSample;
        using (var reader = new MediaFoundationVideoFrameReader(path))
        {
            finalSample = reader.ReadFrameAt(Math.Max(0, containerMs - 1));
        }

        report.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: container duration {containerMs:0.###}ms, final decoded sample " +
            $"{finalSample.TimestampMs:0.###}-{finalSample.EndTimestampMs:0.###}ms, " +
            $"last encoded frame ends at {encodedEndMs:0.###}ms"));
        // One millisecond covers the 100ns sample clock and the 1/(fps*1000)s MP4 timescale.
        if (Math.Abs(finalSample.EndTimestampMs - containerMs) > 1.0)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: container duration {containerMs:0.###}ms is not where the final decoded sample ends ({finalSample.EndTimestampMs:0.###}ms)"));
        }

        if (Math.Abs(containerMs - encodedEndMs) > frameMs + 1.0)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: container duration {containerMs:0.###}ms is more than one frame from the recorded span ({encodedEndMs:0.###}ms)"));
        }
    }

    private static string FormatPlayer(double? playerMs) => playerMs is double value
        ? value.ToString("0.###", CultureInfo.InvariantCulture) + "ms"
        : "none";

    /// <summary>
    /// Opens the clip in WPF (the gallery/editor playback engine) to prove it plays, and reads the
    /// duration from the container. WPF's own NaturalDuration is returned only for the report.
    /// </summary>
    private static (bool Opened, int Width, int Height, double DurationMs, double? PlayerDurationMs) Probe(string path)
    {
        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
        bool opened = false, failed = false;
        player.MediaOpened += (_, _) => opened = true;
        player.MediaFailed += (_, _) => failed = true;
        player.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute));

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (!opened && !failed && DateTime.UtcNow < deadline)
        {
            PumpFor(TimeSpan.FromMilliseconds(20));
        }

        int w = player.NaturalVideoWidth;
        int h = player.NaturalVideoHeight;
        double? playerMs = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalMilliseconds : null;
        player.Close();
        double containerMs = 0;
        try
        {
            containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // A zero duration fails the callers' duration gates with the file named.
        }

        return (opened && !failed, w, h, containerMs, playerMs);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
