using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Spike measurement of GDI versus Windows.Graphics.Capture recording on one animated
/// synthetic fixture, with the same region, frame rate and encoder for both backends.
/// </summary>
/// <remarks>
/// <para>
/// <c>MyCapture.exe --selftest-recording-backends &lt;directory&gt;</c> records every region
/// size that fits the primary display at 30 and 60 fps with both backends in alternating
/// order, then probes the WGC border, pause/resume freshness and capture exclusion of the
/// real recording control window. <c>--selftest-recording-soak &lt;directory&gt;</c> records
/// one long clip with the backend chosen by <see cref="RecordingCaptureBackendSelection.EnvironmentVariable"/>.
/// </para>
/// <para>
/// Every captured pixel belongs to the owned fixture window; no user content is recorded and
/// the MP4 files are deleted after they were decoded. Performance numbers are reported, never
/// asserted: other workstation load is uncontrolled. Only functional checks decide the result.
/// </para>
/// </remarks>
internal static class RecordingBackendBenchmarkSelfTest
{
    internal const string CommandLineSwitch = "--selftest-recording-backends";
    internal const string SoakCommandLineSwitch = "--selftest-recording-soak";

    private const string SecondsVariable = "MYCAPTURE_BENCH_SECONDS";
    private const string RepeatsVariable = "MYCAPTURE_BENCH_REPEATS";
    private const string SoakSecondsVariable = "MYCAPTURE_SOAK_SECONDS";
    private const string SoakFpsVariable = "MYCAPTURE_SOAK_FPS";
    private const string MatrixVariable = "MYCAPTURE_BENCH_MATRIX";
    private const double MegaByte = 1024 * 1024;

    private static readonly (int Width, int Height)[] Sizes = [(1920, 1080), (2560, 1440), (3440, 1440)];
    private static readonly RecordingFrameRate[] Rates = [RecordingFrameRate.Fps30, RecordingFrameRate.Fps60];
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    internal static int Run(string outputDirectory)
    {
        outputDirectory = DiagnosticOutputPaths.Create(outputDirectory);
        int seconds = ReadInt(SecondsVariable, 10, 2, 120);
        int repeats = ReadInt(RepeatsVariable, 1, 1, 5);
        var failures = new List<string>();
        var measurements = new List<object>();
        var table = new StringBuilder();
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        MonitorInfo monitor = MonitorEnumerator.GetPrimary();
        object displays = DescribeDisplays();
        var fixture = new AnimatedFixture(monitor.Bounds);
        try
        {
            fixture.Show();
            Pump(500);
            measurements.Add(new { Kind = "fixture-only", Usage = MeasureIdle(seconds) });
            int pair = 0;
            // MYCAPTURE_BENCH_MATRIX=0 skips the size matrix for a quick static/probe-only rerun.
            bool matrix = ReadInt(MatrixVariable, 1, 0, 1) == 1;
            foreach ((int width, int height) in matrix ? Sizes : [])
            {
                if (width > monitor.Bounds.Width || height > monitor.Bounds.Height)
                {
                    table.AppendLine(CultureInfo.InvariantCulture, $"skip {width}x{height}: primary display is {monitor.PixelWidth}x{monitor.PixelHeight}");
                    continue;
                }

                var region = new RectD(monitor.Bounds.Left, monitor.Bounds.Top, width, height);
                foreach (RecordingFrameRate rate in Rates)
                {
                    for (int repeat = 0; repeat < repeats; repeat++)
                    {
                        RecordingCaptureBackend[] order = pair++ % 2 == 0
                            ? [RecordingCaptureBackend.Gdi, RecordingCaptureBackend.WindowsGraphicsCapture]
                            : [RecordingCaptureBackend.WindowsGraphicsCapture, RecordingCaptureBackend.Gdi];
                        foreach (RecordingCaptureBackend backend in order)
                        {
                            measurements.Add(RecordSample(engine, backend, region, rate, seconds, outputDirectory, failures, table));
                        }
                    }
                }
            }

            // Static scene: nothing is composed, so pacing alone decides the frame rate.
            fixture.Mode = FixtureMode.Static;
            Pump(300);
            var staticRegion = new RectD(monitor.Bounds.Left, monitor.Bounds.Top,
                Math.Min(1920, monitor.Bounds.Width), Math.Min(1080, monitor.Bounds.Height));
            foreach (RecordingFrameRate rate in Rates)
            {
                foreach (RecordingCaptureBackend backend in new[] { RecordingCaptureBackend.Gdi, RecordingCaptureBackend.WindowsGraphicsCapture })
                {
                    measurements.Add(RecordSample(engine, backend, staticRegion, rate, seconds, outputDirectory, failures, table, "static-"));
                }
            }

            fixture.Mode = FixtureMode.Animated;
            measurements.Add(ProbeBorder(engine, monitor, fixture));
            foreach (RecordingCaptureBackend backend in new[] { RecordingCaptureBackend.Gdi, RecordingCaptureBackend.WindowsGraphicsCapture })
            {
                measurements.Add(PauseResume(engine, monitor, fixture, backend, outputDirectory, failures, table));
                measurements.Add(Exclusion(engine, monitor, fixture, backend, outputDirectory, failures, table));
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex.ToString());
        }
        finally
        {
            fixture.Close();
        }

        File.WriteAllText(DiagnosticOutputPaths.Child(outputDirectory, "recording-backends.json"), JsonSerializer.Serialize(new
        {
            Utc = DateTimeOffset.UtcNow,
            Os = Environment.OSVersion.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            SecondsPerSample = seconds,
            Repeats = repeats,
            Displays = displays,
            CpuNormalization = "CPU milliseconds / wall milliseconds / logical processors * 100",
            Limitations = "Synthetic animated fixture; other workstation load uncontrolled; GPU time not measured; MP4 files deleted after decoding.",
            Measurements = measurements,
            Failures = failures,
        }, Json));
        WriteReport(outputDirectory, "recording-backends-report.txt", table, failures);
        return failures.Count == 0 ? 0 : 1;
    }

    internal static int RunSoak(string outputDirectory)
    {
        outputDirectory = DiagnosticOutputPaths.Create(outputDirectory);
        int seconds = ReadInt(SoakSecondsVariable, 600, 10, 7200);
        RecordingFrameRate rate = ReadInt(SoakFpsVariable, 30, 10, 60) >= 60 ? RecordingFrameRate.Fps60 : RecordingFrameRate.Fps30;
        RecordingCaptureBackend backend = RecordingCaptureBackendSelection.FromEnvironment();
        var failures = new List<string>();
        var samples = new List<object>();
        var table = new StringBuilder();
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        MonitorInfo monitor = MonitorEnumerator.GetPrimary();
        var region = new RectD(monitor.Bounds.Left, monitor.Bounds.Top,
            Math.Min(1920, monitor.Bounds.Width), Math.Min(1080, monitor.Bounds.Height));
        string progressPath = DiagnosticOutputPaths.Child(outputDirectory, "soak-progress.jsonl");
        string path = DiagnosticOutputPaths.Child(outputDirectory, $"soak-{backend}.mp4");
        object? summary = null;
        var fixture = new AnimatedFixture(monitor.Bounds);
        try
        {
            fixture.Show();
            Pump(500);
            var grabber = new RegionFrameGrabber(engine, includeCursor: false, backend: backend);
            var observer = new FrameObserver();
            using var recorder = new RegionRecorder(grabber, observer.Create, NullLogger.Instance);
            using Process process = Process.GetCurrentProcess();
            recorder.Start(region, path, new RecordingSettings { FrameRate = rate, IncludeCursor = false, CursorHighlight = false });
            if (!WaitForFirstFrame(recorder, observer))
            {
                failures.Add("Soak recording produced no frame within 15 seconds.");
                _ = TryStop(recorder);
            }
            else
            {
                var total = ResourceWindow.Start(process);
                var interval = ResourceWindow.Start(process);
                var started = Stopwatch.StartNew();
                double nextSampleMs = 10_000;
                double? privateAtOneMinute = null;
                ResourceUsage? last = null;
                while (started.Elapsed.TotalMilliseconds < seconds * 1000.0)
                {
                    Pump(250);
                    total.Sample();
                    interval.Sample();
                    if (!recorder.IsRecording)
                    {
                        failures.Add($"Recorder stopped unexpectedly after {started.Elapsed.TotalSeconds:0} s.");
                        break;
                    }

                    if (started.Elapsed.TotalMilliseconds >= nextSampleMs)
                    {
                        last = interval.Finish();
                        if (privateAtOneMinute is null && started.Elapsed.TotalSeconds >= 60)
                        {
                            privateAtOneMinute = last.PrivateEndMb;
                        }

                        var sample = new
                        {
                            ElapsedSeconds = Math.Round(started.Elapsed.TotalSeconds, 1),
                            RecordedSeconds = Math.Round(recorder.RecordedElapsed.TotalSeconds, 1),
                            Usage = last,
                            Handles = process.HandleCount,
                            GdiObjects = GetGuiResources(process.Handle, 0),
                        };
                        samples.Add(sample);
                        File.AppendAllText(progressPath, JsonSerializer.Serialize(sample) + Environment.NewLine);
                        interval = ResourceWindow.Start(process);
                        nextSampleMs += 10_000;
                    }
                }

                ResourceUsage overall = total.Finish();
                RecordingResult result = AwaitWithDispatcher(Task.Run(recorder.Stop));
                var decoded = AwaitWithDispatcher(Task.Run(() => DecodeAt(path, [1000, result.DurationMs / 2, Math.Max(0, result.DurationMs - 1000)])));
                foreach ((double at, int width, int height, string? error) in decoded)
                {
                    if (error is not null || width != result.Width || height != result.Height)
                    {
                        failures.Add($"Soak clip is not decodable at {at:0} ms ({width}x{height}) {error}");
                    }
                }

                if (grabber.ActiveBackend != backend)
                {
                    failures.Add($"Soak ran on {grabber.ActiveBackend} instead of {backend}: {grabber.FallbackReason}");
                }

                double? growth = privateAtOneMinute is null || last is null ? null : last.PrivateEndMb - privateAtOneMinute.Value;
                if (growth > 256)
                {
                    failures.Add($"Private memory grew {growth:0} MB between minute one and the end.");
                }

                summary = new
                {
                    Backend = backend.ToString(),
                    ActiveBackend = grabber.ActiveBackend?.ToString(),
                    grabber.FallbackReason,
                    Fps = (int)rate,
                    Result = Summarize(result),
                    Usage = overall,
                    PrivateGrowthAfterMinuteOneMb = growth,
                    Wgc = grabber.WgcDiagnostics,
                    Decoded = decoded.Select(entry => new { entry.At, entry.Width, entry.Height, entry.Error }),
                    FileBytes = new FileInfo(path).Length,
                };
                table.AppendLine(CultureInfo.InvariantCulture,
                    $"soak {backend} {result.Width}x{result.Height}@{(int)rate}: {result.EmittedFrames}/{result.ExpectedFrames} frames, " +
                    $"{result.EffectiveFps:0.0} fps, drop {result.DropRate:P1}, cpu {overall.ProcessCpuPercent:0.00}%, " +
                    $"private {overall.PrivateStartMb:0}->{overall.PrivateEndMb:0} MB (peak {overall.PrivatePeakMb:0}), growth after 1 min {growth:0.0} MB");
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex.ToString());
        }
        finally
        {
            fixture.Close();
            TryDelete(path);
        }

        File.WriteAllText(DiagnosticOutputPaths.Child(outputDirectory, "recording-soak.json"), JsonSerializer.Serialize(new
        {
            Utc = DateTimeOffset.UtcNow,
            Os = Environment.OSVersion.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            Seconds = seconds,
            Summary = summary,
            Samples = samples,
            Failures = failures,
        }, Json));
        WriteReport(outputDirectory, "recording-soak-report.txt", table, failures);
        return failures.Count == 0 ? 0 : 1;
    }

    private static object RecordSample(ScreenCaptureEngine engine, RecordingCaptureBackend backend, RectD region,
        RecordingFrameRate rate, int seconds, string outputDirectory, List<string> failures, StringBuilder table, string scene = "")
    {
        string label = $"{scene}{(backend == RecordingCaptureBackend.Gdi ? "gdi" : "wgc")}-{(int)region.Width}x{(int)region.Height}-{(int)rate}fps";
        string path = DiagnosticOutputPaths.Child(outputDirectory, $"{label}-{Guid.NewGuid():N}.mp4");
        var captureMs = new List<double>(((int)rate * (seconds + 30)) + 16);
        var grabber = new RegionFrameGrabber(engine, includeCursor: false, backend: backend) { FrameCaptureTimed = captureMs.Add };
        var observer = new FrameObserver();
        try
        {
            using var recorder = new RegionRecorder(grabber, observer.Create, NullLogger.Instance);
            using Process process = Process.GetCurrentProcess();
            double baselinePrivateMb = SettledPrivateMb(process);
            long startCalled = Stopwatch.GetTimestamp();
            recorder.Start(region, path, new RecordingSettings { FrameRate = rate, IncludeCursor = false, CursorHighlight = false });
            if (!WaitForFirstFrame(recorder, observer))
            {
                _ = TryStop(recorder);
                failures.Add($"{label}: no frame within 15 seconds.");
                return new { Kind = "sustained", Label = label, Failed = true };
            }

            double startLatencyMs = Stopwatch.GetElapsedTime(startCalled, observer.FirstWriteTimestamp).TotalMilliseconds;
            var window = ResourceWindow.Start(process);
            double windowStartMs = recorder.RecordedElapsed.TotalMilliseconds;
            var wall = Stopwatch.StartNew();
            while (wall.Elapsed.TotalMilliseconds < seconds * 1000.0)
            {
                Pump(250);
                window.Sample();
            }

            double windowEndMs = recorder.RecordedElapsed.TotalMilliseconds;
            ResourceUsage usage = window.Finish();
            RecordingResult result = AwaitWithDispatcher(Task.Run(recorder.Stop));
            Steady steady = Steady.Of(observer.Timestamps, captureMs, windowStartMs, windowEndMs, (int)rate);
            var decoded = AwaitWithDispatcher(Task.Run(() => DecodeAt(path, [result.DurationMs / 2])));
            (_, int decodedWidth, int decodedHeight, string? error) = decoded[0];
            if (error is not null || decodedWidth != result.Width || decodedHeight != result.Height || result.EmittedFrames < 2)
            {
                failures.Add($"{label}: clip not decodable as {result.Width}x{result.Height} ({decodedWidth}x{decodedHeight}) {error}");
            }

            if (grabber.ActiveBackend != backend)
            {
                failures.Add($"{label}: ran on {grabber.ActiveBackend}: {grabber.FallbackReason}");
            }

            Percentiles capture = Percentiles.Of(captureMs.Skip(1));
            double privateDeltaMb = usage.PrivatePeakMb - baselinePrivateMb;
            table.AppendLine(CultureInfo.InvariantCulture,
                $"{label,-24} {Short(grabber.ActiveBackend),-3} steady fps={steady.EffectiveFps,5:0.0} drop={steady.DropRate,6:P1} " +
                $"clip fps={result.EffectiveFps,5:0.0} drop={result.DropRate,6:P1} " +
                $"capture p50={steady.Capture.P50,6:0.00} p95={steady.Capture.P95,6:0.00} ms " +
                $"cpu={usage.ProcessCpuPercent,5:0.00}% dwm={Format(usage.DwmCpuPercent, "0.00")}% " +
                $"gpu3d={Format(usage.Gpu3dPercent, "0.0")}% copy={Format(usage.GpuCopyPercent, "0.0")}% sys={Format(usage.SystemCpuPercent, "0.0")}% " +
                $"start={startLatencyMs,5:0} ms open={grabber.SessionOpenMs,6:0.0} ms private+={privateDeltaMb,5:0} MB " +
                $"gap p50={steady.GapP50Ms,5:0.0} p95={steady.GapP95Ms,5:0.0} ms");
            return new
            {
                Kind = "sustained",
                Label = label,
                Backend = backend.ToString(),
                ActiveBackend = grabber.ActiveBackend?.ToString(),
                grabber.FallbackReason,
                Fps = (int)rate,
                StartLatencyMs = startLatencyMs,
                grabber.SessionOpenMs,
                FirstCaptureMs = captureMs.Count > 0 ? captureMs[0] : 0,
                Capture = capture,
                Steady = steady,
                Result = Summarize(result),
                Usage = usage,
                BaselinePrivateMb = baselinePrivateMb,
                PrivateDeltaPeakMb = privateDeltaMb,
                Wgc = grabber.WgcDiagnostics,
            };
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static object PauseResume(ScreenCaptureEngine engine, MonitorInfo monitor, AnimatedFixture fixture,
        RecordingCaptureBackend backend, string outputDirectory, List<string> failures, StringBuilder table)
    {
        string label = $"pause-{(backend == RecordingCaptureBackend.Gdi ? "gdi" : "wgc")}";
        string path = DiagnosticOutputPaths.Child(outputDirectory, $"{label}.mp4");
        var region = new RectD(monitor.Bounds.Left, monitor.Bounds.Top, 1280, 720);
        int captures = 0;
        fixture.Mode = FixtureMode.Animated;
        var grabber = new RegionFrameGrabber(engine, includeCursor: false, backend: backend)
        {
            FrameCaptureTimed = _ => Interlocked.Increment(ref captures),
        };
        var observer = new FrameObserver();
        try
        {
            using var recorder = new RegionRecorder(grabber, observer.Create, NullLogger.Instance);
            recorder.Start(region, path, new RecordingSettings { FrameRate = RecordingFrameRate.Fps30, IncludeCursor = false, CursorHighlight = false });
            if (!WaitForFirstFrame(recorder, observer))
            {
                _ = TryStop(recorder);
                failures.Add($"{label}: no frame within 15 seconds.");
                return new { Kind = "pause-resume", Label = label, Failed = true };
            }

            Pump(1500);
            bool paused = recorder.Pause();
            bool parked = AwaitWithDispatcher(Task.Run(() => recorder.WaitUntilCaptureParked(TimeSpan.FromSeconds(2))));
            double pausedAtMs = recorder.RecordedElapsed.TotalMilliseconds;
            int capturesAtPark = Volatile.Read(ref captures);
            // Content shown only while paused must never reach the clip, including through a
            // frame the capture backend buffered during the pause.
            fixture.Mode = FixtureMode.Marker;
            Pump(1000);
            fixture.Mode = FixtureMode.Animated;
            Pump(100);
            int capturesBeforeResume = Volatile.Read(ref captures);
            bool resumed = recorder.Resume();
            Pump(1500);
            RecordingResult result = AwaitWithDispatcher(Task.Run(recorder.Stop));

            double[] afterResume = observer.Timestamps.Where(timestamp => timestamp >= pausedAtMs - 1).Take(3).ToArray();
            var decoded = AwaitWithDispatcher(Task.Run(() => CountMarkerPixels(path, afterResume)));
            double maxGap = observer.Timestamps.Zip(observer.Timestamps.Skip(1), (a, b) => b - a).DefaultIfEmpty(0).Max();
            if (!paused || !parked || !resumed) failures.Add($"{label}: pause={paused} parked={parked} resume={resumed}.");
            if (capturesBeforeResume != capturesAtPark) failures.Add($"{label}: {capturesBeforeResume - capturesAtPark} capture(s) while paused.");
            if (result.PauseCount != 1 || result.PausedMs < 1000) failures.Add($"{label}: paused span {result.PausedMs:0} ms x{result.PauseCount}.");
            if (afterResume.Length == 0) failures.Add($"{label}: no frame after resume.");
            if (decoded.Any(entry => entry.Marker > 0 || entry.Error is not null))
                failures.Add($"{label}: paused-only content in post-resume frames: {string.Join(", ", decoded.Select(entry => $"{entry.At:0}ms={entry.Marker}{entry.Error}"))}.");
            if (maxGap > 250) failures.Add($"{label}: timestamp gap {maxGap:0} ms across the clip.");
            table.AppendLine(CultureInfo.InvariantCulture,
                $"{label}: active={grabber.ActiveBackend} paused {result.PausedMs:0} ms, duration {result.DurationMs:0} ms, " +
                $"captures while paused {capturesBeforeResume - capturesAtPark}, max gap {maxGap:0} ms, " +
                $"marker pixels after resume [{string.Join(", ", decoded.Select(entry => entry.Marker))}]");
            return new
            {
                Kind = "pause-resume",
                Label = label,
                ActiveBackend = grabber.ActiveBackend?.ToString(),
                CapturesWhilePaused = capturesBeforeResume - capturesAtPark,
                MaxTimestampGapMs = maxGap,
                PostResumeMarkerPixels = decoded,
                Result = Summarize(result),
                Wgc = grabber.WgcDiagnostics,
            };
        }
        finally
        {
            fixture.Mode = FixtureMode.Animated;
            TryDelete(path);
        }
    }

    private static object Exclusion(ScreenCaptureEngine engine, MonitorInfo monitor, AnimatedFixture fixture,
        RecordingCaptureBackend backend, string outputDirectory, List<string> failures, StringBuilder table)
    {
        string label = $"exclusion-{(backend == RecordingCaptureBackend.Gdi ? "gdi" : "wgc")}";
        string path = DiagnosticOutputPaths.Child(outputDirectory, $"{label}.mp4");
        // The whole work area: the control strip has to sit inside the recorded region.
        RectD work = monitor.WorkArea;
        var region = new RectD(work.Left, work.Top, work.Width - (work.Width % 2), work.Height - (work.Height % 2));
        fixture.Mode = FixtureMode.Static;
        var grabber = new RegionFrameGrabber(engine, includeCursor: false, backend: backend);
        var observer = new FrameObserver();
        var phases = new List<(string Name, double AtMs, bool ClockExpected)>();
        try
        {
            using var recorder = new RegionRecorder(grabber, observer.Create, NullLogger.Instance);
            Pump(300);
            recorder.Start(region, path, new RecordingSettings { FrameRate = RecordingFrameRate.Fps15, IncludeCursor = false, CursorHighlight = false });
            if (!WaitForFirstFrame(recorder, observer))
            {
                _ = TryStop(recorder);
                failures.Add($"{label}: no frame within 15 seconds.");
                return new { Kind = "exclusion", Label = label, Failed = true };
            }

            void Sample(string name, bool clockExpected)
            {
                Pump(700);
                phases.Add((name, recorder.RecordedElapsed.TotalMilliseconds, clockExpected));
                Pump(500);
            }

            Sample("plain", false);
            var controls = new RecordingControlWindow(region, new RecordingSettings(),
                static () => throw new InvalidOperationException("Diagnostic control window never records."),
                static () => throw new InvalidOperationException("Diagnostic control window never records."),
                NullLogger.Instance);
            try
            {
                controls.Show();
                Sample("control-window", false);
                var clock = new RecordingWallClockWindow(region, 1, 30);
                try
                {
                    clock.Show();
                    Sample("control-window+clock", true);
                }
                finally
                {
                    clock.Close();
                }
            }
            finally
            {
                controls.Close();
            }

            Sample("closed", false);
            RecordingResult result = AwaitWithDispatcher(Task.Run(recorder.Stop));
            var decoded = AwaitWithDispatcher(Task.Run(() => CountFixtureMismatchesAt(path, phases.Select(phase => phase.AtMs).ToArray())));
            var outcome = phases.Zip(decoded, (phase, frame) => new
            {
                phase.Name,
                phase.AtMs,
                phase.ClockExpected,
                frame.Mismatched,
                frame.Error,
            }).ToList();
            foreach (var phase in outcome)
            {
                bool wrong = phase.Error is not null || (phase.ClockExpected ? phase.Mismatched < 20 : phase.Mismatched > 10);
                if (wrong) failures.Add($"{label}/{phase.Name}: {phase.Mismatched} non-fixture pixels {phase.Error}");
            }

            string counts = string.Join(", ", outcome.Select(phase => $"{phase.Name}={phase.Mismatched}"));
            table.AppendLine(CultureInfo.InvariantCulture,
                $"{label}: active={grabber.ActiveBackend} region {result.Width}x{result.Height} non-fixture pixels {counts}");
            return new
            {
                Kind = "exclusion",
                Label = label,
                ActiveBackend = grabber.ActiveBackend?.ToString(),
                Region = region,
                Phases = outcome,
                Result = Summarize(result),
            };
        }
        finally
        {
            fixture.Mode = FixtureMode.Animated;
            TryDelete(path);
        }
    }

    /// <summary>
    /// Counts yellow pixels along the display edges, through GDI, with no session, with a
    /// default WGC session and with a borderless-requested one.
    /// </summary>
    private static object ProbeBorder(ScreenCaptureEngine engine, MonitorInfo monitor, AnimatedFixture fixture)
    {
        fixture.Mode = FixtureMode.Static;
        Pump(300);
        var probes = new List<object> { new { Mode = "no-session", YellowEdgePixels = CountYellowEdges(engine, monitor.Bounds) } };
        HashSet<long> before = TopLevelWindows().Select(window => window.Handle).ToHashSet();
        foreach (bool borderless in new[] { false, true })
        {
            using var ready = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var region = new RectD(monitor.Bounds.Left + 100, monitor.Bounds.Top + 100, 320, 240);
            Task<WgcCaptureDiagnostics> session = Task.Run(() =>
            {
                using WgcRegionCaptureSession capture = WgcRegionCaptureSession.Create(region, includeCursor: false, requestBorderless: borderless);
                capture.CaptureInto(new byte[320 * 240 * 4], 320 * 4);
                ready.Set();
                _ = release.Wait(TimeSpan.FromSeconds(10));
                return capture.GetDiagnostics();
            });
            while (!ready.IsSet && !session.IsCompleted) Pump(20);
            Pump(1000);
            int yellow = CountYellowEdges(engine, monitor.Bounds);
            // A system border surface would show up as a new top-level window while the session runs.
            var appeared = TopLevelWindows().Where(window => !before.Contains(window.Handle)).ToList();
            release.Set();
            WgcCaptureDiagnostics diagnostics = AwaitWithDispatcher(session);
            probes.Add(new
            {
                Mode = borderless ? "borderless-requested" : "default-border",
                YellowEdgePixels = yellow,
                NewTopLevelWindows = appeared,
                Diagnostics = diagnostics,
            });
            Pump(500);
        }

        fixture.Mode = FixtureMode.Animated;
        return new { Kind = "border-probe", Probes = probes };
    }

    private static int CountYellowEdges(ScreenCaptureEngine engine, RectD bounds)
    {
        const int Thickness = 10;
        int yellow = 0;
        foreach (RectD strip in new[]
        {
            new RectD(bounds.Left, bounds.Top, bounds.Width, Thickness),
            new RectD(bounds.Left, bounds.Bottom - Thickness, bounds.Width, Thickness),
            new RectD(bounds.Left, bounds.Top, Thickness, bounds.Height),
            new RectD(bounds.Right - Thickness, bounds.Top, Thickness, bounds.Height),
        })
        {
            int width = (int)strip.Width, height = (int)strip.Height, stride = width * 4;
            byte[] pixels = new byte[stride * height];
            engine.CaptureRegionInto(strip, false, pixels, stride);
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset + 2] >= 200 && pixels[offset + 1] >= 150 && pixels[offset] <= 100) yellow++;
            }
        }

        return yellow;
    }

    private static double SettledPrivateMb(Process process)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        return process.PrivateMemorySize64 / MegaByte;
    }

    private static string Short(RecordingCaptureBackend? backend) => backend switch
    {
        RecordingCaptureBackend.Gdi => "gdi",
        RecordingCaptureBackend.WindowsGraphicsCapture => "wgc",
        _ => "n/a",
    };

    private static string Format(double? value, string format) =>
        value?.ToString(format, CultureInfo.InvariantCulture) ?? "n/a";

    private static List<WindowSnapshot> TopLevelWindows()
    {
        var windows = new List<WindowSnapshot>();
        var className = new StringBuilder(256);
        _ = EnumWindows((handle, parameter) =>
        {
            if (!IsWindowVisible(handle)) return true;
            className.Clear();
            _ = GetClassName(handle, className, className.Capacity);
            _ = GetWindowThreadProcessId(handle, out uint processId);
            _ = GetWindowRect(handle, out WindowRect rect);
            string process;
            try
            {
                using Process owner = Process.GetProcessById((int)processId);
                process = owner.ProcessName;
            }
            catch (ArgumentException)
            {
                process = "exited";
            }

            windows.Add(new WindowSnapshot(handle.ToInt64(), className.ToString(), process, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static ResourceUsage MeasureIdle(int seconds)
    {
        using Process process = Process.GetCurrentProcess();
        var window = ResourceWindow.Start(process);
        var wall = Stopwatch.StartNew();
        while (wall.Elapsed.TotalMilliseconds < seconds * 1000.0)
        {
            Pump(250);
            window.Sample();
        }

        return window.Finish();
    }

    private static object Summarize(RecordingResult result) => new
    {
        result.Width,
        result.Height,
        result.Fps,
        result.EmittedFrames,
        result.ExpectedFrames,
        result.DroppedFrames,
        result.DropRate,
        result.EffectiveFps,
        result.DurationMs,
        result.PausedMs,
        result.PauseCount,
        result.Performance,
    };

    private static object DescribeDisplays()
    {
        try
        {
            return DisplayColorProbe.Describe();
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static bool WaitForFirstFrame(RegionRecorder recorder, FrameObserver observer)
    {
        var wait = Stopwatch.StartNew();
        while (observer.FirstWriteTimestamp == 0 && recorder.IsRecording && wait.Elapsed < TimeSpan.FromSeconds(15)) Pump(10);
        return observer.FirstWriteTimestamp != 0;
    }

    private static RecordingResult? TryStop(RegionRecorder recorder)
    {
        try
        {
            return AwaitWithDispatcher(Task.Run(recorder.Stop));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static List<(double At, int Width, int Height, string? Error)> DecodeAt(string path, double[] positions)
    {
        var frames = new List<(double, int, int, string?)>();
        try
        {
            using var reader = new MediaFoundationVideoFrameReader(path);
            foreach (double at in positions)
            {
                DecodedVideoFrame frame = reader.ReadFrameAt(at);
                frames.Add((at, frame.Width, frame.Height, null));
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or ArgumentException)
        {
            frames.Add((double.NaN, 0, 0, ex.Message));
        }

        return frames;
    }

    private static List<MarkerCount> CountMarkerPixels(string path, double[] positions)
    {
        var counts = new List<MarkerCount>();
        try
        {
            using var reader = new MediaFoundationVideoFrameReader(path);
            foreach (double at in positions)
            {
                DecodedVideoFrame frame = reader.ReadFrameAt(at);
                int marker = 0;
                for (int y = 0; y < frame.Height; y++)
                {
                    for (int x = 0; x < frame.Width; x++)
                    {
                        int offset = (y * frame.Stride) + (x * 4);
                        if (frame.Pixels[offset] >= 180 && frame.Pixels[offset + 2] >= 180 && frame.Pixels[offset + 1] <= 80) marker++;
                    }
                }

                counts.Add(new MarkerCount(at, marker, null));
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or ArgumentException)
        {
            counts.Add(new MarkerCount(double.NaN, 0, ex.Message));
        }

        return counts;
    }

    private static List<(int Mismatched, string? Error)> CountFixtureMismatchesAt(string path, double[] positions)
    {
        var counts = new List<(int, string?)>();
        try
        {
            using var reader = new MediaFoundationVideoFrameReader(path);
            foreach (double at in positions)
            {
                DecodedVideoFrame frame = reader.ReadFrameAt(at);
                counts.Add((RecordingPerformanceSelfTest.CountFixtureMismatches(frame.Pixels, frame.Stride, 0, 0, frame.Width, frame.Height), null));
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or ArgumentException)
        {
            while (counts.Count < positions.Length) counts.Add((-1, ex.Message));
        }

        return counts;
    }

    private static void WriteReport(string outputDirectory, string name, StringBuilder table, List<string> failures)
    {
        var report = new StringBuilder(table.ToString());
        report.AppendLine(failures.Count == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures.Count} problem(s))");
        foreach (string failure in failures)
        {
            report.AppendLine("  - " + failure);
        }

        File.WriteAllText(DiagnosticOutputPaths.Child(outputDirectory, name), report.ToString(), new UTF8Encoding(false));
    }

    private static int ReadInt(string variable, int fallback, int min, int max) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) _ = new MyCapture.Platform.Shell.WindowsRecycleBin().RecycleFile(System.IO.Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static T AwaitWithDispatcher<T>(Task<T> task)
    {
        while (!task.IsCompleted) Pump(10);
        return task.GetAwaiter().GetResult();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal sealed record WindowSnapshot(long Handle, string ClassName, string Process, int Left, int Top, int Width, int Height);

    /// <summary>Frame rate and capture time over the measured window only, excluding startup.</summary>
    internal sealed record Steady(double WindowMs, int Frames, double ExpectedFrames, double EffectiveFps, double DropRate,
        Percentiles Capture, double GapP50Ms, double GapP95Ms)
    {
        internal static Steady Of(IReadOnlyList<double> timestamps, IReadOnlyList<double> captureMs, double startMs, double endMs, int fps)
        {
            var window = new List<double>();
            var gaps = new List<double>();
            for (int index = 0; index < timestamps.Count; index++)
            {
                if (timestamps[index] >= startMs && timestamps[index] < endMs && index < captureMs.Count)
                {
                    window.Add(captureMs[index]);
                    if (index > 0) gaps.Add(timestamps[index] - timestamps[index - 1]);
                }
            }

            Percentiles gap = Percentiles.Of(gaps);

            double spanMs = Math.Max(1, endMs - startMs);
            double expected = spanMs * fps / 1000.0;
            return new Steady(spanMs, window.Count, expected, window.Count * 1000.0 / spanMs,
                Math.Max(0, 1 - (window.Count / expected)), Percentiles.Of(window), gap.P50, gap.P95);
        }
    }

    internal sealed record MarkerCount(double At, int Marker, string? Error);

    internal sealed record Percentiles(int Count, double P50, double P95, double Max, double Mean)
    {
        internal static Percentiles Of(IEnumerable<double> values)
        {
            double[] sorted = values.Order().ToArray();
            if (sorted.Length == 0) return new Percentiles(0, 0, 0, 0, 0);
            static double At(double[] data, double quantile) =>
                data[Math.Clamp((int)Math.Ceiling(data.Length * quantile) - 1, 0, data.Length - 1)];
            return new Percentiles(sorted.Length, At(sorted, 0.50), At(sorted, 0.95), sorted[^1], sorted.Average());
        }
    }

    internal sealed record ResourceUsage(
        double WallMs,
        double ProcessCpuMs,
        double ProcessCpuPercent,
        double? SystemCpuPercent,
        double? DwmCpuMs,
        double? DwmCpuPercent,
        double? Gpu3dPercent,
        double? GpuCopyPercent,
        double? Gpu3dSelfPercent,
        double? Gpu3dDwmPercent,
        double PrivateStartMb,
        double PrivateEndMb,
        double PrivatePeakMb,
        double WorkingSetStartMb,
        double WorkingSetPeakMb,
        double ManagedAllocatedMb);

    /// <summary>CPU and memory over one measured interval; sampled while the dispatcher pumps.</summary>
    private sealed class ResourceWindow
    {
        private readonly Process _process;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly TimeSpan _cpu;
        private readonly SystemCounters? _counters = SystemCounters.TryOpen();
        private readonly (long Idle, long Kernel, long User)? _system;
        private readonly long _allocated = GC.GetTotalAllocatedBytes(precise: false);
        private readonly long _privateStart;
        private readonly long _workingSetStart;
        private long _privatePeak;
        private long _workingSetPeak;

        private ResourceWindow(Process process)
        {
            _process = process;
            _process.Refresh();
            _cpu = _process.TotalProcessorTime;
            _counters?.Collect();
            _system = SystemTimes();
            _privateStart = _privatePeak = _process.PrivateMemorySize64;
            _workingSetStart = _workingSetPeak = _process.WorkingSet64;
        }

        internal static ResourceWindow Start(Process process) => new(process);

        internal void Sample()
        {
            _process.Refresh();
            _privatePeak = Math.Max(_privatePeak, _process.PrivateMemorySize64);
            _workingSetPeak = Math.Max(_workingSetPeak, _process.WorkingSet64);
        }

        internal ResourceUsage Finish()
        {
            Sample();
            double wallMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            double cpuMs = (_process.TotalProcessorTime - _cpu).TotalMilliseconds;
            CounterReading? counters = null;
            if (_counters is not null)
            {
                _counters.Collect();
                counters = _counters.Read(_process.Id);
                _counters.Dispose();
            }

            double? dwmMs = counters?.DwmPercentOfOneCore * wallMs / 100;
            (long Idle, long Kernel, long User)? systemNow = SystemTimes();
            double? systemPercent = null;
            if (_system is { } before && systemNow is { } after)
            {
                // Kernel time includes idle time.
                double total = (after.Kernel - before.Kernel) + (after.User - before.User);
                double idle = after.Idle - before.Idle;
                systemPercent = total > 0 ? (total - idle) / total * 100 : null;
            }

            int processors = Environment.ProcessorCount;
            return new ResourceUsage(
                wallMs,
                cpuMs,
                cpuMs / wallMs / processors * 100,
                systemPercent,
                dwmMs,
                dwmMs / wallMs / processors * 100,
                counters?.Gpu3d,
                counters?.GpuCopy,
                counters?.Gpu3dSelf,
                counters?.Gpu3dDwm,
                _privateStart / MegaByte,
                _process.PrivateMemorySize64 / MegaByte,
                _privatePeak / MegaByte,
                _workingSetStart / MegaByte,
                _workingSetPeak / MegaByte,
                (GC.GetTotalAllocatedBytes(precise: false) - _allocated) / MegaByte);
        }

        private static (long Idle, long Kernel, long User)? SystemTimes() =>
            GetSystemTimes(out long idle, out long kernel, out long user) ? (idle, kernel, user) : null;
    }

    internal sealed record CounterReading(double? DwmPercentOfOneCore, double? Gpu3d, double? GpuCopy, double? Gpu3dSelf, double? Gpu3dDwm);

    /// <summary>
    /// PDH rate counters for DWM CPU and GPU engine utilisation over one interval. DWM's
    /// process times are not readable through <see cref="Process"/> from a normal user token,
    /// while the performance counters are.
    /// </summary>
    private sealed class SystemCounters : IDisposable
    {
        private const uint FormatDouble = 0x00000200 | 0x00008000;
        private const uint MoreData = 0x800007D2;
        private readonly int? _dwmProcessId;
        private IntPtr _query;
        private IntPtr _dwm;
        private IntPtr _gpu;

        private SystemCounters(IntPtr query, IntPtr dwm, IntPtr gpu, int? dwmProcessId)
        {
            _query = query;
            _dwm = dwm;
            _gpu = gpu;
            _dwmProcessId = dwmProcessId;
        }

        internal static SystemCounters? TryOpen()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out IntPtr query) != 0)
            {
                return null;
            }

            _ = PdhAddEnglishCounter(query, @"\Process(dwm)\% Processor Time", IntPtr.Zero, out IntPtr dwm);
            _ = PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out IntPtr gpu);
            Process[] dwmProcesses = Process.GetProcessesByName("dwm");
            int? dwmId = dwmProcesses.Length == 1 ? dwmProcesses[0].Id : null;
            foreach (Process process in dwmProcesses) process.Dispose();
            return new SystemCounters(query, dwm, gpu, dwmId);
        }

        internal void Collect() => _ = PdhCollectQueryData(_query);

        internal CounterReading Read(int selfProcessId)
        {
            double? dwm = _dwm != IntPtr.Zero && PdhGetFormattedCounterValue(_dwm, FormatDouble, out _, out PdhValue value) == 0 && value.Status <= 1
                ? value.Value
                : null;
            if (_gpu == IntPtr.Zero)
            {
                return new CounterReading(dwm, null, null, null, null);
            }

            uint size = 0;
            if (PdhGetFormattedCounterArray(_gpu, FormatDouble, ref size, out _, IntPtr.Zero) != MoreData || size == 0)
            {
                return new CounterReading(dwm, null, null, null, null);
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_gpu, FormatDouble, ref size, out uint count, buffer) != 0)
                {
                    return new CounterReading(dwm, null, null, null, null);
                }

                double total3d = 0, totalCopy = 0, self3d = 0, dwm3d = 0;
                string selfPrefix = $"pid_{selfProcessId}_";
                string? dwmPrefix = _dwmProcessId is int id ? $"pid_{id}_" : null;
                int itemSize = IntPtr.Size + Marshal.SizeOf<PdhValue>();
                for (int index = 0; index < count; index++)
                {
                    IntPtr item = buffer + (index * itemSize);
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? string.Empty;
                    PdhValue sample = Marshal.PtrToStructure<PdhValue>(item + IntPtr.Size);
                    if (sample.Status > 1) continue;
                    if (name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        total3d += sample.Value;
                        if (name.StartsWith(selfPrefix, StringComparison.Ordinal)) self3d += sample.Value;
                        if (dwmPrefix is not null && name.StartsWith(dwmPrefix, StringComparison.Ordinal)) dwm3d += sample.Value;
                    }
                    else if (name.EndsWith("engtype_Copy", StringComparison.OrdinalIgnoreCase))
                    {
                        totalCopy += sample.Value;
                    }
                }

                return new CounterReading(dwm, total3d, totalCopy, self3d, dwmPrefix is null ? null : dwm3d);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
            {
                _ = PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                _dwm = IntPtr.Zero;
                _gpu = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhValue
        {
            public uint Status;
            public double Value;
        }

        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhValue value);

        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }

    /// <summary>Wraps the real encoder to record the first successful write and every timestamp.</summary>
    private sealed class FrameObserver
    {
        private readonly List<double> _timestamps = [];
        private long _firstWrite;

        internal long FirstWriteTimestamp => Interlocked.Read(ref _firstWrite);

        /// <summary>Written on the capture thread; read only after the recorder stopped.</summary>
        internal IReadOnlyList<double> Timestamps => _timestamps;

        internal IVideoEncoder Create(VideoEncoderOptions options) =>
            new Observed(new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance), this);

        private sealed class Observed(IVideoEncoder inner, FrameObserver owner) : IVideoEncoder
        {
            public int Width => inner.Width;

            public int Height => inner.Height;

            public void WriteFrame(in EncoderFrame frame)
            {
                inner.WriteFrame(frame);
                owner._timestamps.Add(frame.TimestampMs);
                _ = Interlocked.CompareExchange(ref owner._firstWrite, Stopwatch.GetTimestamp(), 0);
            }

            public void Complete() => inner.Complete();

            public void Dispose() => inner.Dispose();
        }
    }

    internal enum FixtureMode
    {
        Animated,
        Static,
        Marker,
    }

    /// <summary>
    /// Topmost window over the whole display. Animated mode moves a bar and a box every
    /// composition frame so DWM composes continuously, the same load for both backends.
    /// </summary>
    private sealed class AnimatedFixture
    {
        private static readonly Brush FixtureBrush = Freeze(new SolidColorBrush(RecordingPerformanceSelfTest.FixtureColor));
        private static readonly Brush MarkerBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 0, 255)));
        private readonly Window _window;
        private readonly Canvas _canvas;
        private readonly Rectangle _bar;
        private readonly Rectangle _box;
        private readonly RectD _bounds;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private FixtureMode _mode = FixtureMode.Animated;
        private bool _shown;
        private bool _rendering;

        internal AnimatedFixture(RectD physicalBounds)
        {
            _bounds = physicalBounds;
            _bar = new Rectangle { Width = 64, Fill = Brushes.White };
            _box = new Rectangle { Width = 280, Height = 280, Fill = new SolidColorBrush(Color.FromRgb(240, 128, 32)) };
            _canvas = new Canvas { Background = FixtureBrush, ClipToBounds = true };
            _canvas.Children.Add(_bar);
            _canvas.Children.Add(_box);
            _window = new Window
            {
                Style = null,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = FixtureBrush,
                Width = 800,
                Height = 600,
                Content = _canvas,
            };
        }

        internal FixtureMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                _canvas.Background = value == FixtureMode.Marker ? MarkerBrush : FixtureBrush;
                Visibility moving = value == FixtureMode.Animated ? Visibility.Visible : Visibility.Collapsed;
                _bar.Visibility = moving;
                _box.Visibility = moving;
                // Only the animated scene keeps WPF's per-frame render loop running; a static scene
                // leaves the process as idle as a real desktop being recorded.
                SetRendering(_shown && value == FixtureMode.Animated);
            }
        }

        private void SetRendering(bool enabled)
        {
            if (enabled == _rendering)
            {
                return;
            }

            _rendering = enabled;
            if (enabled)
            {
                CompositionTarget.Rendering += OnRendering;
            }
            else
            {
                CompositionTarget.Rendering -= OnRendering;
            }
        }

        internal void Show()
        {
            _window.Show();
            PhysicalWindowPositioner.PlaceTopmost(new WindowInteropHelper(_window).Handle, _bounds);
            _window.UpdateLayout();
            _shown = true;
            SetRendering(_mode == FixtureMode.Animated);
        }

        internal void Close()
        {
            SetRendering(false);
            _shown = false;
            _window.Close();
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (_mode != FixtureMode.Animated)
            {
                return;
            }

            double seconds = _clock.Elapsed.TotalSeconds;
            double width = Math.Max(1, _canvas.ActualWidth);
            double height = Math.Max(1, _canvas.ActualHeight);
            _bar.Height = height;
            Canvas.SetLeft(_bar, seconds * 900 % Math.Max(1, width - _bar.Width));
            Canvas.SetLeft(_box, Bounce(seconds * 610, width - _box.Width));
            Canvas.SetTop(_box, Bounce(seconds * 370, height - _box.Height));
        }

        private static double Bounce(double travel, double span)
        {
            span = Math.Max(1, span);
            double position = travel % (2 * span);
            return position <= span ? position : (2 * span) - position;
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
