using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Explicit desktop diagnostic, dispatched before resident ownership and profile initialization.
/// Only its explicit microphone/tone flags may open the corresponding capture/render devices.
/// </summary>
internal static class RecordingAudioSelfTest
{
    internal const string ReportName = "recording-audio-report.txt";
    private static readonly Guid AacSubtype = new("00001610-0000-0010-8000-00AA00389B71");
    private static readonly Color BackgroundColor = Color.FromRgb(18, 90, 166);

    internal static void ValidateOutputRoot(string output) => RecordingAudioSelfTestOptions.ValidateIsolatedRoot(output,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppFolderName));

    internal static RecordingSettings SettingsFor(RecordingAudioSelfTestOptions options) => new()
    {
        FrameRate = RecordingFrameRate.Fps30, IncludeCursor = false, UseStartDelay = false,
        RecordSystemAudio = options.SystemAudio, RecordMicrophone = options.Microphone,
    };

    internal static int Run(RecordingAudioSelfTestOptions options)
    {
        ValidateOutputRoot(options.OutputDirectory);
        string output = DiagnosticOutputPaths.Create(options.OutputDirectory);
        string clip = DiagnosticOutputPaths.Child(output, "recording-audio.mp4");
        if (File.Exists(clip) || File.Exists(DiagnosticOutputPaths.Child(output, "stream-timestamps.csv")))
            throw new IOException("Use a new diagnostic output directory for each recording.");
        AppPaths paths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Child(output, "isolated-data"));
        Directory.CreateDirectory(paths.DataRoot);
        var failures = new List<string>();
        using var trace = new RecordingAudioDiagnosticTrace(output);
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        long started = Stopwatch.GetTimestamp();
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        var grabber = new RegionFrameGrabber(engine, includeCursor: false);
        using var recorder = new RegionRecorder(grabber,
            encoderOptions => trace.Bind(new MediaFoundationVideoEncoder(encoderOptions, NullLogger.Instance)),
            NullLogger.Instance);
        var surface = new MovingSurface();
        var fixture = new Window
        {
            Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Content = surface,
            Background = new SolidColorBrush(BackgroundColor), Width = 660, Height = 380,
        };
        var motion = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        motion.Tick += (_, _) => surface.InvalidateVisual();
        RecordingResult? result = null;
        AudioFileVerification? verification = null;
        RecordingAudioDiagnosticTone? tone = null;
        double? firstFrameMs = null;
        double stopWallMs = 0;
        bool pauseObserved = false, pausedCountersStable = true;
        try
        {
            IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetAll();
            MonitorInfo monitor = monitors.FirstOrDefault(item => item.IsPrimary) ?? monitors.First();
            if (monitor.Bounds.Width < 700 || monitor.Bounds.Height < 420)
                throw new InvalidOperationException("The diagnostic fixture needs a display of at least 700 by 420 pixels.");
            fixture.Show();
            var bounds = new RectD(monitor.Bounds.Left + 20, monitor.Bounds.Top + 20, 660, 380);
            PhysicalWindowPositioner.PlaceTopmost(new WindowInteropHelper(fixture).Handle, bounds);
            fixture.UpdateLayout();
            Pump(150);
            var region = new RectD(bounds.Left + 10, bounds.Top + 10, 640, 360);
            VerifyFixture(engine, region);
            surface.Animate = true;
            motion.Start();
            if (options.Tone) tone = RecordingAudioDiagnosticTone.Start();
            recorder.Start(region, clip, SettingsFor(options));
            long startup = Stopwatch.GetTimestamp();
            while (!trace.FirstVideo.IsSet && recorder.IsRecording && Stopwatch.GetElapsedTime(startup).TotalSeconds < 20)
                Pump(10);
            if (!trace.FirstVideo.IsSet) throw new InvalidOperationException("No video frame was written within 20 seconds.");
            firstFrameMs = Stopwatch.GetElapsedTime(startup).TotalMilliseconds;
            long deadlineStart = Stopwatch.GetTimestamp();
            while (recorder.IsRecording && recorder.RecordedElapsed.TotalSeconds < options.Seconds)
            {
                if (!pauseObserved && options.PauseAtSeconds is { } pauseAt && recorder.RecordedElapsed.TotalSeconds >= pauseAt)
                {
                    pauseObserved = true;
                    if (!recorder.Pause() || !recorder.WaitUntilCaptureParked(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("The recording did not park at the requested pause.");
                    long videoBefore = trace.VideoFrames, audioBefore = trace.AudioFrames;
                    Pump(checked((int)Math.Round(options.PauseSeconds * 1000)));
                    pausedCountersStable = videoBefore == trace.VideoFrames && audioBefore == trace.AudioFrames;
                    if (!recorder.Resume()) throw new InvalidOperationException("The diagnostic recording did not resume.");
                }
                if (Stopwatch.GetElapsedTime(deadlineStart).TotalSeconds > options.Seconds + options.PauseSeconds + 30)
                    throw new TimeoutException("The recording stopped advancing its active timeline.");
                Pump(10);
            }
            long stopStarted = Stopwatch.GetTimestamp();
            result = recorder.Stop();
            stopWallMs = Stopwatch.GetElapsedTime(stopStarted).TotalMilliseconds;
            if (!pausedCountersStable) failures.Add("Encoded samples changed while the capture loop was parked.");
            if (options.PauseAtSeconds is not null && (!pauseObserved || result.PauseCount != 1))
                failures.Add("The requested pause was not included in the recording timeline.");
            verification = VerifyFile(clip, options.SystemAudio || options.Microphone, result, trace);
            if (verification.Failures.Count > 0) failures.AddRange(verification.Failures);
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            // Close the capture first; stopping the optional stimulus cannot prolong the recording.
            if (recorder.IsRecording)
            {
                try
                {
                    long stopStarted = Stopwatch.GetTimestamp();
                    result = recorder.Stop();
                    stopWallMs = Stopwatch.GetElapsedTime(stopStarted).TotalMilliseconds;
                }
                catch (Exception ex) { failures.Add("Recording stop: " + ex); }
            }
            try { tone?.Dispose(); } catch (Exception ex) { failures.Add("Diagnostic tone release: " + ex); }
            motion.Stop();
            fixture.Close();
        }
        RecordingAudioDiagnostics audio = result?.AudioDiagnostics ?? recorder.AudioDiagnostics;
        if (audio.SourceThreadsStillRunning != 0 || audio.System.ActiveObjects != 0 || audio.Microphone.ActiveObjects != 0)
            failures.Add("A source worker or native source object is still active after the bounded stop wait.");
        double wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        string signal = trace.AudioFrames == 0 ? "NO AUDIO TRACK"
            : trace.AllAudioIsSilence ? "ALL SILENCE: every PCM sample submitted to the AAC writer was zero. The playback device may be muted or the sources unavailable."
            : "NONZERO SIGNAL: submitted PCM contains nonzero samples.";
        var report = new
        {
            Utc = DateTimeOffset.UtcNow, Result = failures.Count == 0 ? "PASS" : "FAIL", Options = options,
            IsolatedDataRoot = paths.DataRoot, ProfileInitialized = false, ResidentOwnershipAcquired = false,
            SyntheticFixture = "Owned opaque 640x360 moving region; initial full-area screen coverage verified; cursor excluded.",
            DurationMeaning = "Seconds is active recording time; a requested pause adds wall-clock time.",
            ToneRequested = options.Tone, ToneAmplitudeDbfs = options.Tone ? (double?)RecordingAudioDiagnosticTone.AmplitudeDbfs : null,
            MicrophoneRequested = options.Microphone, Signal = signal, FirstWrittenVideoFrameMs = firstFrameMs,
            StopCallWallMs = stopWallMs, Audio = audio, Recording = result, Streams = trace.Snapshot(), File = verification,
            ProcessCpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds, WallMs = wallMs,
            LogicalProcessors = Environment.ProcessorCount, PrivateBytes = process.PrivateMemorySize64,
            WorkingSetBytes = process.WorkingSet64, Handles = process.HandleCount,
            PausedCountersStable = pausedCountersStable,
            MeasurementScope = "Timestamp CSV and PCM peak scan are diagnostic overhead in both paired runs. Real display/playback latency and microphone acoustic latency are not inferred from stored timestamps.",
            Failures = failures,
        };
        var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        File.WriteAllText(DiagnosticOutputPaths.Child(output, "recording-audio.json"), JsonSerializer.Serialize(report, json));
        File.WriteAllText(DiagnosticOutputPaths.Child(output, ReportName),
            $"RESULT: {(failures.Count == 0 ? "PASS" : "FAIL")}\n{signal}\n" +
            string.Create(CultureInfo.InvariantCulture,
                $"Video frames: {trace.VideoFrames}; PCM frames: {trace.AudioFrames}; source join: {audio.SourceJoinWaitMs:F3} ms; stop: {stopWallMs:F3} ms; finalize: {audio.FinalizeMs:F3} ms\n") +
            string.Join(Environment.NewLine, failures));
        return failures.Count == 0 ? 0 : 1;
    }

    internal static AudioFileVerification VerifyFile(string path, bool requestedAudio, RecordingResult result,
        RecordingAudioDiagnosticTrace trace)
    {
        var failures = new List<string>();
        (int total, int audioTracks) = MediaFoundationAudioReader.GetStreamCounts(path);
        VideoSourceInfo video = MediaFoundationVideoFrameReader.ReadSourceInfo(path);
        if (total - audioTracks != 1) failures.Add("Expected exactly one video stream.");
        if (audioTracks != (requestedAudio ? 1 : 0)) failures.Add("The file does not contain the requested audio stream count.");
        if (result.ExpectedAudio != requestedAudio) failures.Add("The requested audio stream was unavailable at encoder setup.");
        if (trace.VideoFrames != result.EmittedFrames || !trace.PresentationTimesOrdered)
            failures.Add("Submitted video count or presentation timestamp order differs from the recorder result.");
        using (var frames = new MediaFoundationVideoFrameReader(path))
        {
            _ = frames.ReadFrameAt(0);
            _ = frames.ReadFrameAt(Math.Max(0, video.DurationMs - 1000d / result.Fps));
        }
        long decodedFrames = 0;
        int peak = 0;
        bool exactBudget = !requestedAudio;
        if (requestedAudio && audioTracks == 1)
        {
            if (video.Audio is not { Subtype: var subtype, SampleRate: 48_000, Channels: 2 } || subtype != AacSubtype)
                failures.Add("Expected AAC at 48000 Hz and two channels.");
            using var reader = new MediaFoundationAudioReader(path);
            long previousPts = -1;
            while (reader.ReadNext() is { } block)
            {
                if (block.Timestamp100ns < previousPts) failures.Add("Decoded audio timestamps moved backwards.");
                previousPts = block.Timestamp100ns;
                decodedFrames += block.FrameCount;
                foreach (short sample in block.Samples) peak = Math.Max(peak, Math.Abs((int)sample));
            }
            RecordingAudioDiagnostics audio = result.AudioDiagnostics ?? throw new InvalidOperationException("Audio diagnostics are absent.");
            long removed100ns = audio.PauseIntervals.Sum(pause => checked(pause.End100ns - pause.Start100ns));
            long independentBudget = RecordingAudioFormat.SampleAt100ns(Math.Max(0, audio.StopElapsed100ns - removed100ns));
            // This end is derived from actual video submissions and the unchanged sink's fixed
            // frame duration, independently of the mixer's output/padding/trim counters.
            long videoEnd100ns = checked(trace.LastVideoPts100ns!.Value + RecordingAudioFormat.TicksPerSecond / result.Fps);
            long expectedEndSample = RecordingAudioFormat.SampleAt100ns(videoEnd100ns);
            long expectedPadding = Math.Max(0, expectedEndSample - independentBudget);
            long expectedTrim = Math.Max(0, independentBudget - expectedEndSample);
            exactBudget = trace.AudioIsContiguous && trace.FirstAudioPts100ns == 0
                && independentBudget == audio.ActiveSampleBudget
                && trace.AudioFrames == audio.OutputFrames
                && trace.AudioFrames == expectedEndSample
                && audio.PaddingFrames == expectedPadding && audio.TrimmedFrames == expectedTrim;
            if (!exactBudget) failures.Add("The output PCM does not exactly match the active timeline and the recorded video end.");
            if (Math.Abs(video.DurationMs * 10_000 - videoEnd100ns) > 10_000)
                failures.Add("The stored video duration differs from the submitted video end by more than one millisecond.");
            if (decodedFrames < trace.AudioFrames || decodedFrames > trace.AudioFrames + 1024)
                failures.Add("Decoded audio length differs from submitted PCM beyond one AAC frame of padding.");
        }
        return new(total, audioTracks, decodedFrames, peak, exactBudget, failures);
    }

    internal sealed record AudioFileVerification(int Streams, int AudioStreams, long DecodedFrames,
        int DecodedPeakPcm16, bool PauseSampleBudgetVerified, IReadOnlyList<string> Failures);

    private static void VerifyFixture(ScreenCaptureEngine engine, RectD region)
    {
        int width = (int)region.Width, height = (int)region.Height, stride = width * 4;
        byte[] pixels = new byte[stride * height];
        engine.CaptureRegionInto(region, false, pixels, stride);
        if (RecordingPerformanceSelfTest.CountFixtureMismatches(pixels, stride, 0, 0, width, height) != 0)
            throw new InvalidOperationException("The owned synthetic fixture does not cover the entire capture region.");
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class MovingSurface : FrameworkElement
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        internal bool Animate { get; set; }
        protected override void OnRender(DrawingContext drawing)
        {
            drawing.DrawRectangle(new SolidColorBrush(BackgroundColor), null, new Rect(RenderSize));
            if (!Animate) return;
            double phase = Stopwatch.GetElapsedTime(_started).TotalSeconds;
            double x = Math.Max(0, ActualWidth - 80) * (Math.Sin(phase * 2) + 1) / 2;
            drawing.DrawRectangle(Brushes.White, null, new Rect(x, ActualHeight / 3, 70, ActualHeight / 3));
            drawing.DrawRectangle(Brushes.Gold, null, new Rect(0, ActualHeight - 18,
                ActualWidth * (phase % 1), 8));
        }
    }
}
