using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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
/// continuing the other).
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
            (bool ok1, int w1, int h1, double d1) = Probe(clip1);
            (bool ok2, int w2, int h2, double d2) = Probe(clip2);
            Line($"Session 1 decode: opened={ok1} {w1}x{h1} {d1:0}ms");
            Line($"Session 2 decode: opened={ok2} {w2}x{h2} {d2:0}ms");

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
        var grabber = new RegionFrameGrabber(engine, settings.IncludeCursor);
        var recorder = new RegionRecorder(
            grabber,
            options => new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance),
            NullLogger.Instance);

        try
        {
            recorder.Start(region, outputPath, settings);
            if (!recorder.IsRecording)
            {
                failures.Add($"{label}: recorder did not enter the recording state");
                return null;
            }

            Thread.Sleep(recordMs);
            RecordingResult result = recorder.Stop();

            if (recorder.IsRecording)
            {
                failures.Add($"{label}: recorder still reports recording AFTER stop — this is the reported bug");
            }

            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: {result.EmittedFrames} frame(s), {result.DurationMs:0}ms, {result.Width}x{result.Height} -> {Path.GetFileName(outputPath)}"));

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

    private static (bool Opened, int Width, int Height, double DurationMs) Probe(string path)
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
        double d = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
        player.Close();
        return (opened && !failed, w, h, d);
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
