using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>Observes each presenter condition in an owned, bounded process before calibrating it.</summary>
internal static class VideoRangeSelfTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() },
    };

    internal static int Run(string outputDirectory)
    {
        string root = DiagnosticOutputPaths.Create(VideoRangeDiagnosticPlan.ValidateOutputDirectory(outputDirectory));
        // Short names keep each cell's calibration clips under MAX_PATH, which the Media Foundation
        // sink writer still requires. Cells are numbered in plan order; the JSON lists their ids.
        string runDirectory = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "c-" + Guid.NewGuid().ToString("N")[..8]));
        string executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the current executable.");
        var results = new List<VideoRangeCellResult>();
        using var cancellation = new CancellationTokenSource(VideoRangeDiagnosticPlan.RunTimeout);
        var elapsed = Stopwatch.StartNew();
        // No parent window is presented. Children run serially so they cannot compete for the
        // presenter/GPU; cancellation also covers a driver that hangs in a child startup/close.
        Task.Run(async () =>
        {
            for (int index = 0; index < VideoRangeDiagnosticPlan.Cells.Count; index++)
            {
                VideoRangeCell cell = VideoRangeDiagnosticPlan.Cells[index];
                if (cancellation.IsCancellationRequested)
                {
                    results.Add(Failed(cell, "Whole-run five-minute budget exhausted before this cell."));
                    continue;
                }
                string cellRoot = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(runDirectory,
                    index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)));
                string dataRoot = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(cellRoot, "data"));
                results.Add(await RunCellAsync(executable, cellRoot, dataRoot, cell, cancellation.Token));
            }
        }).GetAwaiter().GetResult();
        bool pass = results.Count == VideoRangeDiagnosticPlan.Cells.Count && results.All(r => r.Failure is null);
        string[] summary = VideoRangeDiagnosticPlan.Summarize(results);
        string command = $"& '{executable.Replace("'", "''")}' {VideoRangeDiagnosticPlan.CommandLineSwitch} '{root.Replace("'", "''")}'";
        File.WriteAllText(DiagnosticOutputPaths.Child(root, VideoRangeDiagnosticPlan.JsonName), JsonSerializer.Serialize(new
        {
            Result = pass ? "PASS" : "FAIL", Command = command, Os = Environment.OSVersion.ToString(),
            ElapsedSeconds = elapsed.Elapsed.TotalSeconds, RunDirectory = runDirectory,
            ExpectedCells = VideoRangeDiagnosticPlan.Cells.Count, ExpectedSamples = VideoRangeDiagnosticPlan.Cells.Sum(c => c.FullRangeSteps.Length),
            Summary = summary, Cells = results,
        }, JsonOptions));
        var report = new StringBuilder().AppendLine(pass ? "RESULT: PASS" : "RESULT: FAIL")
            .AppendLine(command).AppendLine($"Artifacts: {runDirectory}")
            .AppendLine($"Elapsed: {elapsed.Elapsed.TotalSeconds:F1}s; per cell 20s; whole run 300s.")
            .AppendLine("PASS means all observations were collected, regardless of colour classification.")
            .AppendLine("All windows are off-screen. Probe uses source-sized DIP content; gallery/editor retain product layout.")
            .AppendLine("H1-H5 samples precede explicit in-process calibration. H6 starts calibration with opening; H7 uses the test colour pattern. Sequence steps reuse the host window; the diagnostic probe uses a fresh MediaElement per step. Gallery/editor include product verification outcomes.")
            .AppendLine("RGBA patches: source greys [0,64,192,255]; five raw observations per step. Classification requires the last two to agree.")
            .AppendLine("H7 patch order: " + string.Join(", ", VideoRangeColourPattern.Patches.Select(p => $"{p.Name}=({p.R},{p.G},{p.B})")))
            .AppendLine("GPU descriptions enumerate available adapters; they do not identify the adapter/decoder selected by MediaElement.")
            .AppendLine("H1-H7 lines report classification differences, not causal conclusions. Scale is physical presented pixels / source pixels.")
            .AppendLine().AppendLine(string.Join(Environment.NewLine, summary)).AppendLine()
            .AppendLine("cell | pid | step | input | classification | RGBA | scale | DPI | tier | PS2 | diagnostic studio/full | player verification")
            .AppendLine("A step marked 'after calibration' re-samples the same player once the selected calibration has presented its studio and full clips.");
        foreach (VideoRangeCellResult result in results)
        {
            foreach (VideoRangeSample sample in result.Samples)
                report.AppendLine($"{result.Cell.Id} | {result.ProcessId} | {sample.Step}{(sample.Phase != "before-calibration" ? " " + sample.Phase : string.Empty)} | {sample.InputRange} | {sample.Classification} | "
                    + (sample.Observations.Length == 0 ? "-"
                        : string.Join(";", sample.Observations[^1].Patches.Select(p => $"({p.R},{p.G},{p.B},{p.A})")))
                    + $" | {sample.ScaleX:F3}/{sample.ScaleY:F3} | {sample.DpiX:F0}/{sample.DpiY:F0} | {sample.RenderTier} | {sample.PixelShader20}"
                    + $" | {result.Calibration.Studio}/{result.Calibration.Full} | {FormatVerification(sample.Verification)}");
            if (result.Timing is { } timing)
                report.AppendLine($"TIMING: {result.Cell.Id}: opening={timing.OpeningMs:F1}ms; "
                    + $"calibration={timing.CalibrationStartMs:F1}..{timing.CalibrationEndMs:F1}ms ({result.Cell.CalibrationMode}).");
            if (result.Failure is not null) report.AppendLine($"INFRASTRUCTURE: {result.Cell.Id}: {result.Failure}");
            if (result.CalibrationFailure is not null) report.AppendLine($"CALIBRATION: {result.Cell.Id}: {result.CalibrationFailure}");
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(root, VideoRangeDiagnosticPlan.ReportName), report.ToString());
        return pass ? 0 : 1;
    }

    private static async Task<VideoRangeCellResult> RunCellAsync(string executable, string root, string dataRoot,
        VideoRangeCell cell, CancellationToken runToken)
    {
        Process? child = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        deadline.CancelAfter(VideoRangeDiagnosticPlan.CellTimeout);
        try
        {
            child = Process.Start(VideoRangeDiagnosticPlan.BuildChildStartInfo(executable, root, dataRoot, cell))
                ?? throw new IOException("The diagnostic child could not be started.");
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string file = DiagnosticOutputPaths.Child(root, VideoRangeDiagnosticPlan.JsonName);
            if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024)
                throw new IOException($"Child exit {child.ExitCode}; missing or oversized result JSON.");
            VideoRangeCellResult result = JsonSerializer.Deserialize<VideoRangeCellResult>(
                await File.ReadAllTextAsync(file, deadline.Token).ConfigureAwait(false), JsonOptions)
                ?? throw new IOException("Child result JSON was empty.");
            if (result.Cell.Id != cell.Id || result.ProcessId != child.Id)
                throw new IOException("Child result identity did not match its owned process/cell.");
            // One sample per step; an extra post-calibration re-sample is optional (open players only).
            if (child.ExitCode != 0
                || result.Samples.Count(s => s.Phase != "after-calibration") != cell.FullRangeSteps.Length)
                return result with { Failure = result.Failure ?? $"Child exit {child.ExitCode}; incomplete sample count." };
            if (cell.Kind == "concurrent" && !result.Samples.Any(s => s.Phase == "after-calibration"))
                return result with { Failure = "Concurrent cell is missing the completed-calibration sample." };
            return result;
        }
        catch (Exception ex)
        {
            string failure = ex is OperationCanceledException ? "Child exceeded the per-cell or whole-run deadline." : ex.ToString();
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        // This Process came directly from our own Start; never enumerate/kill by name.
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                }
                catch (Exception stopError) { failure += Environment.NewLine + "Owned child stop: " + stopError; }
            }
            return Failed(cell, failure, child?.Id ?? 0);
        }
        finally { child?.Dispose(); }
    }

    private static VideoRangeCellResult Failed(VideoRangeCell cell, string failure, int processId = 0) =>
        new(cell, processId, [], default, null, [], null, failure);

    internal static int RunChild(string outputDirectory, VideoRangeCell cell)
    {
        string root = DiagnosticOutputPaths.Create(VideoRangeDiagnosticPlan.ValidateOutputDirectory(outputDirectory));
        string dataRoot = DiagnosticOutputPaths.Create(VideoRangeDiagnosticPlan.ValidateOutputDirectory(
            Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT") ?? throw new IOException("Child data root is missing.")));
        // A directly invoked internal switch must be just as confined as one launched by Run.
        Environment.SetEnvironmentVariable("TEMP", dataRoot);
        Environment.SetEnvironmentVariable("TMP", dataRoot);
        StudioRangeVideoEffect.SuppressForDiagnostic = true;
        var diagnosticCalibration = StudioRangePresentationCalibration.CreateInProcessForDiagnostic();
        var clock = Stopwatch.StartNew();
        double? openingMs = null, calibrationStartMs = null, calibrationEndMs = null;
        Task<VideoRangeCalibrationResult>? calibrationTask = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(19));
        var samples = new List<VideoRangeSample>();
        var windows = new List<Window>();
        VideoRangeCalibrationResult calibration = default;
        string? calibrationFailure = null, adapterFailure = null, failure = null;
        string[] adapters = [];
        Task task = CollectAsync();
        try
        {
            while (!task.IsCompleted && !cancellation.IsCancellationRequested) Pump();
            if (!task.IsCompleted) throw new TimeoutException("Child observation budget exhausted.");
            task.GetAwaiter().GetResult();
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            _ = cancellation.CancelAsync();
            foreach (Window window in windows) window.Close();
        }
        var result = new VideoRangeCellResult(cell, Environment.ProcessId, samples.ToArray(), calibration,
            calibrationFailure, adapters, adapterFailure, failure, new(openingMs, calibrationStartMs, calibrationEndMs));
        File.WriteAllText(DiagnosticOutputPaths.Child(root, VideoRangeDiagnosticPlan.JsonName), JsonSerializer.Serialize(result, JsonOptions));
        File.WriteAllText(DiagnosticOutputPaths.Child(root, VideoRangeDiagnosticPlan.ReportName),
            (failure is null ? "RESULT: PASS" : "RESULT: FAIL") + Environment.NewLine + failure);
        return failure is null ? 0 : 1;

        async Task<VideoRangeCalibrationResult> StartCalibrationAsync()
        {
            calibrationStartMs = clock.Elapsed.TotalMilliseconds;
            try { return await diagnosticCalibration.GetResultAsync(); }
            finally { calibrationEndMs = clock.Elapsed.TotalMilliseconds; }
        }

        VideoRangeObservation Observe(MediaElement media) => MediaElementRangeVerification.WithoutEffect(media,
            () => cell.Pattern == "colour-patches" ? VideoRangeColourPattern.Observe(media)
                : VideoRangeDiagnosticPlan.Observe(StudioRangeCalibrationProbe.Sample(media)));

        async Task CollectAsync()
        {
            CancellationToken token = cancellation.Token;
            try { adapters = await Task.Run(() => DisplayAdapterProbe.Describe().ToArray(), token); }
            catch (Exception ex) { adapterFailure = ex.ToString(); }
            // Generate only synthetic fixture clips. Encoding precedes presentation and uses
            // the same pattern, encoder and range flags as the diagnostic probe.
            var paths = new Dictionary<bool, string>();
            foreach (bool full in cell.FullRangeSteps.Distinct())
            {
                string clip = DiagnosticOutputPaths.Child(root, full ? "full.mp4" : "studio.mp4");
                await Task.Run(() =>
                {
                    if (cell.Pattern == "colour-patches") VideoRangeColourPattern.Encode(clip, cell.Width, cell.Height, full);
                    else StudioRangeCalibrationProbe.Encode(clip, full, token, cell.Width, cell.Height);
                }, token);
                paths.Add(full, clip);
            }
            StudioRangeVideoEffect.SuppressForDiagnostic = cell.Host == "probe";
            Window host = cell.Host switch
            {
                "probe" => StudioRangeCalibrationProbe.CreateWindow(cell.Width, cell.Height),
                "gallery" => new GalleryVideoPlayerWindow(),
                "editor" => new VideoEditorWindow(new RecordingResult(paths[cell.FullRangeSteps[0]], 1000, 30, 30, cell.Width, cell.Height),
                    AppPaths.CreateForRoot(dataRoot), NullLoggerFactory.Instance),
                _ => throw new InvalidOperationException("Unknown host."),
            };
            windows.Add(host);
            host.WindowStartupLocation = WindowStartupLocation.Manual;
            host.Left = -10000; host.Top = -10000; host.ShowActivated = false; host.ShowInTaskbar = false;
            if (host is VideoEditorWindow) { host.Width = 1100; host.Height = 760; }
            for (int step = 0; step < cell.FullRangeSteps.Length; step++)
            {
                bool full = cell.FullRangeSteps[step];
                MediaElement media = host switch
                {
                    GalleryVideoPlayerWindow gallery => gallery.MediaForDiagnostic,
                    VideoEditorWindow editor => editor.MediaForTest,
                    _ => StudioRangeCalibrationProbe.CreateMediaElement(),
                };
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Exception? mediaFailure = null;
                RoutedEventHandler onOpened = (_, _) => opened.TrySetResult();
                EventHandler<ExceptionRoutedEventArgs> onFailed = (_, e) =>
                {
                    mediaFailure = e.ErrorException;
                    opened.TrySetException(e.ErrorException);
                };
                media.MediaOpened += onOpened;
                media.MediaFailed += onFailed;
                try
                {
                    if (cell.Host == "probe") host.Content = media;
                    if (step == 0)
                    {
                        // Same dispatcher turn as the open. Never await calibration here: that would
                        // miss the process-shared presenter state race this cell is designed to observe.
                        openingMs = clock.Elapsed.TotalMilliseconds;
                        if (cell.Kind == "concurrent") calibrationTask = StartCalibrationAsync();
                        host.Show();
                    }
                    if (host is GalleryVideoPlayerWindow gallery)
                    {
                        if (!gallery.Open(Guid.NewGuid(), "Video range fixture", paths[full], autoPlay: false))
                            throw new IOException("Gallery could not open the generated fixture.");
                    }
                    else if (cell.Host == "probe")
                    {
                        media.Source = new Uri(paths[full]);
                        media.Play();
                    }
                    await opened.Task.WaitAsync(token);
                    while ((host is GalleryVideoPlayerWindow g && !g.IsMediaReadyForDiagnostic)
                        || (host is VideoEditorWindow e && !e.IsMediaReadyForTest))
                    {
                        if (mediaFailure is not null) throw mediaFailure;
                        await Task.Delay(20, token);
                    }
                    await Task.Delay(250, token);
                    RangeVerificationResult? verification = null;
                    while (StudioRangeVideoEffect.VerificationFor(media) is { } session)
                    {
                        try { verification = await session.Completion.WaitAsync(token); break; }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested
                            && !ReferenceEquals(session, StudioRangeVideoEffect.VerificationFor(media))) { }
                    }
                    if (cell.Host != "probe" && verification is null)
                        verification = new(null, new(255, 255, 255), [], 0, 0, 0, media.Position.TotalMilliseconds,
                            "hardware-shaders-unavailable") { IsFallback = true };
                    var observations = new List<VideoRangeObservation>();
                    for (int index = 0; index < 5; index++)
                    {
                        await Task.Delay(80, token);
                        if (mediaFailure is not null) throw mediaFailure;
                        if (cell.Kind != "concurrent" && diagnosticCalibration.IsStarted)
                            throw new InvalidOperationException("Correction/calibration contaminated the raw sample.");
                        host.UpdateLayout();
                        observations.Add(Observe(media));
                    }
                    samples.Add(DescribeSample(host, media, cell, step, full, observations.ToArray()) with
                    {
                        Phase = cell.Kind == "concurrent" ? "opening-with-calibration" : "before-calibration",
                        ElapsedMs = clock.Elapsed.TotalMilliseconds, Verification = verification,
                        CalibrationStartedBeforeSample = diagnosticCalibration.IsStarted,
                    });
                }
                catch (Exception) when (mediaFailure is not null && !token.IsCancellationRequested
                    && VideoRangeDiagnosticPlan.RecordsSwitchFailureAsObservation(cell, step))
                {
                    // A player that fails while switching clips within a sequence (for example
                    // MILAVERR_UNEXPECTEDWMPFAILURE) is itself an observation; keep collecting.
                    // Any other open failure leaves the cell without a raw sample: an infrastructure FAIL.
                    samples.Add(FailedSample(step, full, mediaFailure));
                }
                finally
                {
                    media.MediaOpened -= onOpened;
                    media.MediaFailed -= onFailed;
                    if (cell.Host == "probe") { media.Close(); media.Source = null; host.Content = null; }
                }
            }
            // All sequence observations finish first: calibration itself presents studio then
            // full in this process, and would otherwise destroy the fresh/sticky distinction.
            // A product player stays open through calibration and is sampled once more, which
            // reproduces a preview that was opened before the calibration clips were presented.
            MediaElement? openPlayer = host switch
            {
                GalleryVideoPlayerWindow gallery when gallery.IsMediaReadyForDiagnostic => gallery.MediaForDiagnostic,
                VideoEditorWindow editor when editor.IsMediaReadyForTest => editor.MediaForTest,
                _ => null,
            };
            if (openPlayer is null)
            {
                host.Close();
                windows.Remove(host);
            }
            calibrationTask ??= StartCalibrationAsync();
            calibration = await calibrationTask.WaitAsync(token);
            calibrationFailure = diagnosticCalibration.Failure?.ToString();
            if (openPlayer is not null)
            {
                int last = cell.FullRangeSteps.Length - 1;
                await Task.Delay(250, token);
                var after = new List<VideoRangeObservation>();
                for (int index = 0; index < 5; index++)
                {
                    await Task.Delay(80, token);
                    host.UpdateLayout();
                    after.Add(Observe(openPlayer));
                }
                samples.Add(DescribeSample(host, openPlayer, cell, last, cell.FullRangeSteps[last], after.ToArray()) with
                {
                    Phase = "after-calibration", ElapsedMs = clock.Elapsed.TotalMilliseconds,
                    CalibrationStartedBeforeSample = true, Verification = StudioRangeVideoEffect.VerificationFor(openPlayer)?.LastResult,
                });
                host.Close();
                windows.Remove(host);
            }
        }
    }

    private static VideoRangeSample FailedSample(int step, bool full, Exception failure) =>
        new(step, full ? "full" : "studio", $"media-failed 0x{failure.HResult:X8}", [], 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            RenderCapability.Tier >> 16, RenderCapability.IsPixelShaderVersionSupported(2, 0),
            false);

    private static VideoRangeSample DescribeSample(Window host, MediaElement media, VideoRangeCell cell,
        int step, bool full, VideoRangeObservation[] observations)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(media);
        double sx = media.ActualWidth / cell.Width, sy = media.ActualHeight / cell.Height;
        if (media.Stretch == Stretch.Uniform) sx = sy = Math.Min(sx, sy);
        double pixelsX = cell.Width * sx * dpi.DpiScaleX, pixelsY = cell.Height * sy * dpi.DpiScaleY;
        if (pixelsX <= 0 || pixelsY <= 0) throw new InvalidOperationException("Media content was not laid out.");
        string classification = observations[^1].Classification == observations[^2].Classification
            ? observations[^1].Classification : "unknown";
        return new(step, full ? "full" : "studio", classification, observations,
            host.ActualWidth, host.ActualHeight, media.ActualWidth, media.ActualHeight,
            pixelsX, pixelsY, pixelsX / cell.Width, pixelsY / cell.Height, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            RenderCapability.Tier >> 16, RenderCapability.IsPixelShaderVersionSupported(2, 0),
            false);
    }

    private static string FormatVerification(RangeVerificationResult? result) => result is null ? "unavailable"
        : $"{result.Details}, points={result.Points}, dynamic-range={result.DynamicRange}, "
            + $"attempts={result.Attempts}, position={result.PositionMs:F2}ms";

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
