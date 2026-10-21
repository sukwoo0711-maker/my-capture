using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Tests;

/// <summary>Opt-in, synthetic F3/restore measurements. Each content kind gets its own process.</summary>
internal sealed class PinOpenProbe
{
    private static readonly string[] ChildSections = ["pin-open-image", "pin-open-code", "pin-open-table", "pin-open-burst"];
    private readonly string _output;
    private readonly string _kind;
    private readonly ProbeRecorder _recorder = new();
    private readonly List<PinOpenSample> _samples = [];
    private readonly List<PinRendererSample> _renderers = [];
    private readonly List<PinBurstSample> _bursts = [];
    private readonly List<string> _environment = [];
    private readonly List<string> _notes =
    [
        "Synthetic content only; no real clipboard, session file, user app data or HKCU Run startup-key access is requested. WPF/product system-setting reads remain intact.",
        "Each image/code/table child process creates its first pin for the cold row, then ten warm pins. WPF resources and source rasterization precede open timing. On builds with PinWindowPrewarm the app's idle prewarm runs first, so the cold row is the first F3 after launch as the app sees it.",
        "Restore bursts save a synthetic session (image, code, table in turn) into the isolated data root, then construct PinSessionService and run RestoreAsync on a Background-priority turn as App.InitializePinSession does: read, decode, and reopen. start is the service construction; frames cover the first restored Show entry through 300 ms after the last Show return.",
        "start is the completed synthetic clipboard read callback immediately before the real F3 PasteFromClipboardAsync continuation enters OpenPin (activate:false).",
        "rendered_ms=0 means the frozen content was ready before start and no DPI reraster was needed. When DPI reraster is needed rendered_ms is blank: rendered_upper_bound_ms is BeforeShowForTest, after rasterization, placement, construction and registration; it is NOT the exact raster end.",
        "shown_ms is immediately after the actual Show() returns. loaded_ms is the Loaded callback; Loaded can occur within Show(). first_frame_ms is the first CompositionTarget.Rendering callback observed after Loaded, not a DWM present or proof of visible pixels.",
        "reveal_end_estimate_ms is loaded_ms + 167 when client-area animations are enabled, otherwise loaded_ms; it is not an observed animation completion. Scheduling can change actual presentation.",
        "Frame gaps use distinct RenderingEventArgs.RenderingTime values. Wall-clock callback timestamps delimit the observation window; initial show-to-first-frame latency and the trailing unobserved interval are reported separately. Zero callbacks/gaps means unavailable, not smooth rendering.",
        "Single frame window: Show entry through 300 ms after Show return. Overlay: Show entry through 600 ms after Show return. Burst: first Show entry through 300 ms after the last Show return.",
        "Overlay A/B/C/D are warm round-robin repetitions; delta_vs_a_ms is paired by repetition, not a cross-process cold comparison. Foreground equality compares HWND before open and after at least 600 ms from the effect first frame (frame gaps still cover only 0-600 ms from pin Show); transient foreground changes may escape this check; overlay Activated count is also recorded.",
        "Renderer costs run on a dedicated STA per sample, excluding thread startup/join. PNG decode is measured for image; code HTML parse and code raster, or table raster, use the product renderers at 96 DPI. UI-monitor reraster remains in the open measurement.",
        "No UiHeartbeat is installed in this section: frame pacing is Rendering-based. Generic heartbeat fields in joined CSV are zero placeholders, not measurements. Detailed pin CSV/summary.txt are authoritative.",
    ];
    private MonitorInfo _monitor = null!;
    private PinManager _manager = null!;

    private PinOpenProbe(string section, string output) { _kind = section["pin-open-".Length..]; _output = output; }

    internal static bool IsChildSection(string section) => ChildSections.Contains(section, StringComparer.Ordinal);

    internal static async Task RunChildrenAsync(string output)
    {
        // Retain each run's data and receipts, never mistake a previous PASS for this run.
        string run = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(output, "run-" + Guid.NewGuid().ToString("N")));
        var attempted = new List<string>();
        try
        {
            foreach (string section in ChildSections)
            {
                attempted.Add(section);
                string folder = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(run, section));
                string receipt = DiagnosticOutputPaths.Child(folder, "probe-completed.txt");
                File.WriteAllText(receipt, "RUNNING");
                (int code, string console) = await AppResponsivenessProbeTests.RunChildAsync(section, null, run);
                File.WriteAllText(DiagnosticOutputPaths.Child(folder, "probe-child-console.txt"), console);
                if (code != 0 || File.ReadAllText(receipt) != "PASS")
                    throw new InvalidOperationException($"{section}: child exit {code}; receipt {File.ReadAllText(receipt)}. See {folder}. No further pin children were attempted.");
            }
        }
        finally
        {
            ProbeJoin.Write(run, attempted);
            File.Copy(Path.Combine(run, "app-responsiveness-summary.md"), Path.Combine(output, "app-responsiveness-summary.md"), overwrite: true);
            // The outer ProbeJoin adds its own section column; keep this section's original common schema.
            File.WriteAllLines(Path.Combine(output, "app-responsiveness.csv"),
                File.ReadAllLines(Path.Combine(run, "app-responsiveness.csv")).Select(line => line[(line.IndexOf(',') + 1)..]),
                new UTF8Encoding(true));
            var summary = new StringBuilder("Run folder: " + run + Environment.NewLine);
            foreach (string section in attempted)
            {
                string path = Path.Combine(run, section, "summary.txt");
                if (File.Exists(path)) summary.AppendLine(File.ReadAllText(path));
            }
            File.WriteAllText(Path.Combine(output, "summary.txt"), summary.ToString());
            foreach (string name in new[] { "pin-open.csv", "renderer.csv", "restore-burst.csv", "frame-samples.csv" })
            {
                var joined = new List<string>();
                foreach (string section in attempted)
                {
                    string path = Path.Combine(run, section, name);
                    if (!File.Exists(path)) continue;
                    string[] lines = File.ReadAllLines(path);
                    joined.AddRange(joined.Count == 0 ? lines : lines.Skip(1));
                }
                if (joined.Count > 0) File.WriteAllLines(Path.Combine(output, name), joined, new UTF8Encoding(true));
            }
        }
    }

    internal static void RunOnStaThread(string section, string output)
    {
        string? previousRoot = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT");
        string dataRoot = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(output, "isolated-data"));
        Environment.SetEnvironmentVariable("MYCAPTURE_DATA_ROOT", dataRoot);
        try
        {
            StaTestHost.Run(() =>
            {
                using var dpiScope = PinProbeDisplay.PerMonitorDpiScope();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
                ThemeService.ApplyFromSettings(new AppSettings().General);
                var probe = new PinOpenProbe(section, output);
                Task work = probe.RunAsync();
                var frame = new DispatcherFrame();
                // The continuation posts to the captured UI dispatcher, never to a worker's dispatcher.
                Dispatcher ui = Dispatcher.CurrentDispatcher;
                _ = work.ContinueWith(_ => ui.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                work.GetAwaiter().GetResult();
            });
        }
        finally { Environment.SetEnvironmentVariable("MYCAPTURE_DATA_ROOT", previousRoot); }
    }

    private async Task RunAsync()
    {
        Exception? failure = null;
        try
        {
            _monitor = MonitorEnumerator.GetFromCursor();
            var settings = new PinSettings { ClosedWindowRestoreLimit = 0 };
            _manager = new PinManager(() => settings, NullLogger.Instance)
            {
                // Freeze placement to the real initially selected monitor so a moving cursor cannot change DPI mid-run.
                ReadPlacementMonitorForTest = () => _monitor,
            };
            _environment.Add($"Process {Environment.ProcessId}; section {_kind}; {RuntimeInformation.FrameworkDescription}; WPF tier {RenderCapability.Tier >> 16}");
            _environment.Add($"Target monitor {_monitor.DeviceName}; {_monitor.Dpi} DPI; scale {PinOpenReport.F(_monitor.ScaleFactor)}; refresh {PinOpenReport.F(PinProbeDisplay.RefreshRate(_monitor.DeviceName))} Hz (EnumDisplaySettings current mode, not measured cadence)");
            _environment.Add("Data root: " + Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT"));
            _environment.Add($"ClientAreaAnimation={SystemParameters.ClientAreaAnimation}; IsRemoteSession={SystemParameters.IsRemoteSession}");
            _environment.Add("Probe STA requests thread-scoped PerMonitorV2 before creating WPF resources; actual per-pin WPF/HWND DPI are recorded alongside target monitor scale. No process-global DPI setting is changed. Cursor position is untouched and may trigger normal pin hover chrome.");
            if (_kind == "burst") await MeasureBurstsAsync();
            else
            {
                await RunAppPrewarmAsync();
                await MeasureKindAsync();
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _manager?.CloseAllForExit();
            PinOpenReport.Write(_output, _kind, _samples, _renderers, _bursts, _environment, _notes, failure);
            new ProbeReport(_output, _recorder, _environment, _notes, failure, "app-responsiveness", "핀 열기 측정: " + _kind).Write();
            // Preserve the common CSV schema without inheriting the editor report's heartbeat methodology.
            File.WriteAllText(Path.Combine(_output, "app-responsiveness-summary.md"),
                "# 핀 열기 측정: " + _kind + "\n\n```text\n" + File.ReadAllText(Path.Combine(_output, "summary.txt")) + "\n```\n",
                new UTF8Encoding(false));
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>
    /// As the app does once its shell is up: the pin window prewarm on an idle turn, then idle again
    /// before the first F3. A restore-on-launch session prewarms inside the restore instead (burst).
    /// </summary>
    private async Task RunAppPrewarmAsync()
    {
        // Resolved by name so the probe also measures a build without the pin prewarm.
        MethodInfo? run = typeof(PinWindow).Assembly.GetType("MyCapture.App.Pinning.PinWindowPrewarm")
            ?.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes);
        if (run is null)
        {
            _environment.Add("This build has no PinWindowPrewarm; the cold row is the first pin of a cold process.");
            return;
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var scenario = new ProbeScenario(_kind, "prewarm", "idle prewarm");
        scenario.Start();
        long started = Stopwatch.GetTimestamp();
        run.Invoke(null, null);
        double sync = Ms(started, Stopwatch.GetTimestamp());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        double toIdle = Ms(started, Stopwatch.GetTimestamp());
        scenario.AddSync("PinWindowPrewarm.Run", sync);
        scenario.Finish(Stopwatch.GetTimestamp());
        _recorder.Add(scenario);
        _environment.Add($"Idle prewarm (PinWindowPrewarm.Run) before the cold row: {PinOpenReport.F(sync)} ms on the UI thread, {PinOpenReport.F(toIdle)} ms to the next idle turn.");
    }

    private async Task MeasureKindAsync()
    {
        PinContent? content = null;
        for (int repeat = 0; repeat <= 10; repeat++)
        {
            (content, PinRendererSample renderer) = await PinOpenFixtures.RenderAsync(_kind, repeat);
            _renderers.Add(renderer);
            await MeasureSingleAsync(content, repeat == 0 ? "cold" : "warm", "A", repeat, 300);
        }
        if (_kind == "table") return;

        // Prime each prototype once outside the measured rows, so all ten overlay repetitions are warm.
        foreach (string variant in new[] { "B", "C", "D" })
            await MeasureSingleAsync(content!, "overlay-prime", variant, 0, 600);
        for (int repeat = 1; repeat <= 10; repeat++)
        {
            PinOpenSample? baseline = null;
            foreach (string variant in new[] { "A", "B", "C", "D" })
            {
                PinOpenSample sample = await MeasureSingleAsync(content!, "overlay-warm", variant, repeat, 600);
                if (variant == "A") baseline = sample;
                if (sample.FirstFrame != 0 && baseline is { FirstFrame: not 0 })
                    sample.DeltaVsA = Ms(sample.Start, sample.FirstFrame) - Ms(baseline.Start, baseline.FirstFrame);
            }
        }
    }

    private async Task<PinOpenSample> MeasureSingleAsync(PinContent content, string phase, string variant, int repeat, int windowMs)
    {
        var sample = new PinOpenSample(_kind, phase, variant, repeat, windowMs)
        {
            AnimationsEnabled = SystemParameters.ClientAreaAnimation,
            MonitorScale = _monitor.ScaleFactor,
            RefreshHz = PinProbeDisplay.RefreshRate(_monitor.DeviceName),
        };
        _samples.Add(sample);
        var scenario = new ProbeScenario(_kind, variant, phase + "-" + repeat);
        scenario.Start();
        using var frames = new PinFrameCollector();
        PinWindow? pin = null;
        ProbePinDropOverlay? overlay = null;
        ProbePinDropVisual? inline = null;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler rendering = (_, _) =>
        {
            if (sample.Loaded == 0 || sample.FirstFrame != 0) return;
            sample.FirstFrame = Stopwatch.GetTimestamp();
            first.TrySetResult();
            if (variant == "C" && pin is not null)
            {
                overlay = new ProbePinDropOverlay(pin);
                overlay.Show();
            }
        };
        _manager.BeforeShowForTest = value =>
        {
            sample.RenderedUpperBound = Stopwatch.GetTimestamp();
            sample.RenderedAtStart = ReferenceEquals(value.PinnedContent, content);
            pin = value;
            value.Loaded += (_, _) => sample.Loaded = Stopwatch.GetTimestamp();
            if (variant == "D")
            {
                inline = ProbePinDropOverlay.AttachToPin(value);
                if (inline is null) sample.Status = "SKIP-D: public pin content has no compatible container";
            }
        };
        _manager.ShowPinForTest = (value, show) =>
        {
            sample.ShowEnter = Stopwatch.GetTimestamp();
            show();
            sample.Shown = Stopwatch.GetTimestamp();
            sample.WpfScale = VisualTreeHelper.GetDpi(value).DpiScaleX;
            sample.WindowDpi = PinProbeDisplay.GetDpiForWindow(value.Handle);
            if (variant == "B")
            {
                overlay = new ProbePinDropOverlay(value);
                overlay.Show();
            }
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            sample.ForegroundBefore = PinProbeDisplay.GetForegroundWindow();
            PasteResult result = await _manager.PasteFromClipboardAsync(() =>
            {
                var read = Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(content));
                sample.Start = Stopwatch.GetTimestamp();
                return read;
            });
            if (result != PasteResult.Pinned || pin is null || pin.ShowActivated)
                throw new InvalidOperationException("The synthetic F3 path did not show an inactive product pin.");
            await WaitUntilAsync(sample.Shown, windowMs);
            sample.Frames = frames.Snapshot(sample.ShowEnter, sample.Shown, windowMs);
            sample.OverlayFirstFrame = overlay?.FirstFrameTimestamp ?? inline?.FirstFrameTimestamp;
            bool expectsEffect = variant is "B" or "C" || (variant == "D" && inline is not null);
            if (expectsEffect && (sample.OverlayFirstFrame is not long effectObserved
                || Ms(sample.Shown, effectObserved) > windowMs))
            {
                sample.Status = "incomplete: no effect first frame within observation window";
                throw new TimeoutException($"Variant {variant} did not deliver an effect Rendering callback within {windowMs} ms of pin Show.");
            }
            if (sample.OverlayFirstFrame is long effectFrame)
                await WaitUntilAsync(effectFrame, 600);
            sample.ObservedEnd = Stopwatch.GetTimestamp();
            sample.ForegroundAfter = PinProbeDisplay.GetForegroundWindow();
            sample.ActivationCount = overlay?.ActivationCount ?? 0;
            if (sample.FirstFrame == 0)
            {
                // Preserve the fixed observation data even when WPF never delivers a frame.
                await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
                sample.Status = "first frame arrived after observation window";
            }
            if (sample.ActivationCount != 0)
                throw new InvalidOperationException($"Overlay {variant} received {sample.ActivationCount} Activated events.");
            if (sample.ForegroundBefore == IntPtr.Zero || sample.ForegroundAfter == IntPtr.Zero)
            {
                sample.Status = "incomplete: foreground HWND unavailable";
                throw new InvalidOperationException("The probe could not observe the foreground window before and after the effect.");
            }
            else if (sample.ForegroundBefore != sample.ForegroundAfter)
                throw new InvalidOperationException($"Foreground changed during {variant}: {sample.ForegroundBefore:X} -> {sample.ForegroundAfter:X}.");
            scenario.AddSync("F3 to Show return", Ms(sample.Start, sample.Shown));
            scenario.Extra["first-frame-ms"] = Ms(sample.Start, sample.FirstFrame);
            scenario.Extra["rendered-upper-bound-ms"] = Ms(sample.Start, sample.RenderedUpperBound);
            scenario.Extra["frame-max-gap-ms"] = sample.Frames.MaxGap ?? double.NaN;
            scenario.Extra["frame-p95-gap-ms"] = sample.Frames.P95Gap ?? double.NaN;
            scenario.Notes = "Rendering callback, not DWM present; see pin-open.csv and summary.txt for timestamp bounds.";
            sample.Completed = !sample.Status.StartsWith("SKIP-", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            if (!sample.Status.StartsWith("incomplete:", StringComparison.Ordinal))
                sample.Status = "failed: " + ex.GetType().Name;
            throw;
        }
        finally
        {
            // Preserve observable failures even when an effect never rendered or an await failed.
            sample.ActivationCount = overlay?.ActivationCount ?? 0;
            if (sample.ObservedEnd == 0)
            {
                sample.ForegroundAfter = PinProbeDisplay.GetForegroundWindow();
                sample.ObservedEnd = Stopwatch.GetTimestamp();
            }
            CompositionTarget.Rendering -= rendering;
            _manager.BeforeShowForTest = null;
            _manager.ShowPinForTest = null;
            overlay?.Dispose();
            inline?.Dispose();
            _manager.CloseAllForExit();
            scenario.Finish(Stopwatch.GetTimestamp());
            _recorder.Add(scenario);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        return sample;
    }

    private async Task MeasureBurstsAsync()
    {
        var content = new List<PinContent>();
        foreach (string kind in new[] { "image", "code", "table" })
        {
            (PinContent value, PinRendererSample renderer) = await PinOpenFixtures.RenderAsync(kind, 0);
            content.Add(value);
            _renderers.Add(renderer);
        }
        string dataRoot = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")!;
        foreach (int count in new[] { 10, 30 })
        {
            // A saved session in the isolated data root, read, decoded and reopened by the product service.
            var store = new PinSessionStore(DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(dataRoot, "pins-" + count)), NullLogger.Instance);
            if (!store.Save(Enumerable.Range(0, count).Select(index => Snapshot(content[index % content.Count], index)).ToArray()))
                throw new InvalidOperationException("The synthetic pin session could not be saved.");
            var sample = new PinBurstSample(count);
            _bursts.Add(sample);
            var scenario = new ProbeScenario("mixed", "restore", "burst-" + count);
            scenario.Start();
            using var frames = new PinFrameCollector();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler firstRendering = (_, _) =>
            {
                if (sample.FirstShowEnter == 0 || sample.FirstFrame != 0) return;
                sample.FirstFrame = Stopwatch.GetTimestamp();
                first.TrySetResult();
            };
            CompositionTarget.Rendering += firstRendering;
            _manager.ShowPinForTest = (pin, show) =>
            {
                if (sample.FirstShowEnter == 0) sample.FirstShowEnter = Stopwatch.GetTimestamp();
                show();
                long now = Stopwatch.GetTimestamp();
                if (sample.FirstShown == 0) sample.FirstShown = now;
                sample.AllShown = now;
                sample.ShownCount++;
                if (pin.ShowActivated) throw new InvalidOperationException("Restore activated a pin.");
            };
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true, ClosedWindowRestoreLimit = 0 };
            PinSessionService? service = null;
            try
            {
                // As App.InitializePinSession: the service starts its read when constructed and the
                // restore follows on a Background-priority turn.
                sample.Start = Stopwatch.GetTimestamp();
                service = new PinSessionService(_manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance);
                Task restoring = await Dispatcher.CurrentDispatcher.InvokeAsync(service.RestoreAsync, DispatcherPriority.Background);
                await restoring.WaitAsync(TimeSpan.FromSeconds(60));
                sample.LoopEnd = Stopwatch.GetTimestamp();
                if (sample.ShownCount != count) throw new InvalidOperationException($"Restore showed {sample.ShownCount} of {count} synthetic pins.");
                // Through the last pin's reveal: every pin's Show and the frames between them.
                await WaitUntilAsync(sample.AllShown, BurstTailMs);
                sample.Frames = frames.Snapshot(sample.FirstShowEnter, sample.AllShown, BurstTailMs);
                if (sample.FirstFrame == 0) await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
                scenario.AddSync("restore to all Show returns", Ms(sample.Start, sample.AllShown));
                scenario.Extra["frame-max-gap-ms"] = sample.Frames.MaxGap ?? double.NaN;
                scenario.Extra["frame-p95-gap-ms"] = sample.Frames.P95Gap ?? double.NaN;
                scenario.Notes = "PinSessionService.RestoreAsync over a saved isolated session; see restore-burst.csv.";
            }
            finally
            {
                CompositionTarget.Rendering -= firstRendering;
                _manager.ShowPinForTest = null;
                service?.Dispose();
                _manager.CloseAllForExit();
                scenario.Finish(Stopwatch.GetTimestamp());
                _recorder.Add(scenario);
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Frames are observed from the first restored pin's Show through this long after the last one's.</summary>
    internal const int BurstTailMs = 300;

    private PinSessionSnapshot Snapshot(PinContent item, int index)
    {
        double scale = _monitor.ScaleFactor;
        double width = item.UsesDeviceIndependentPixels ? item.LayoutWidthDip : item.Image.PixelWidth / scale;
        double height = item.UsesDeviceIndependentPixels ? item.LayoutHeightDip : item.Image.PixelHeight / scale;
        double zoom = Math.Min(1, Math.Min(_monitor.WorkArea.Width / (width * scale), _monitor.WorkArea.Height / (height * scale)) * 0.7);
        var placement = new PinClosedPlacement(new RectD(_monitor.WorkArea.Left + 24 + (index % 5) * 18,
            _monitor.WorkArea.Top + 24 + (index % 5) * 18, width * zoom * scale, height * zoom * scale),
            _monitor.DeviceName, _monitor.Bounds);
        PinSessionContentKind kind = item.Kind switch
        {
            PinContentKind.Image => PinSessionContentKind.Image,
            PinContentKind.Table => PinSessionContentKind.Table,
            _ => PinSessionContentKind.Text,
        };
        var pin = new PinSessionPin(Guid.NewGuid(), kind, item.OriginalText, item.SourcePageUrl,
            item.UsesDeviceIndependentPixels, width, height, zoom, 1, placement, index + 1, item.TextStyle, item.CodeDocument);
        return new PinSessionSnapshot(pin, Png(item.Image));
    }

    /// <summary>A decoded fixture frame keeps its decoder's thread, so the PNG is encoded from a pixel copy.</summary>
    private static byte[] Png(BitmapSource image)
    {
        int stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        BitmapSource copy = BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
            image.Format, image.Palette, pixels, stride);
        copy.Freeze();
        return ImageCodec.EncodePng(copy, null);
    }

    private static async Task WaitUntilAsync(long start, int milliseconds)
    {
        double remaining = milliseconds - Ms(start, Stopwatch.GetTimestamp());
        if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
    }

    internal static double Ms(long start, long end) => (end - start) * 1000.0 / Stopwatch.Frequency;
}

internal sealed class PinOpenSample(string kind, string phase, string variant, int repeat, int windowMs)
{
    internal string Kind { get; } = kind;
    internal string Phase { get; } = phase;
    internal string Variant { get; } = variant;
    internal int Repeat { get; } = repeat;
    internal int WindowMs { get; } = windowMs;
    internal long Start, RenderedUpperBound, ShowEnter, Shown, Loaded, FirstFrame, ObservedEnd;
    internal bool RenderedAtStart;
    internal bool AnimationsEnabled;
    internal double MonitorScale, WpfScale;
    internal uint WindowDpi;
    internal double? RefreshHz;
    internal long? OverlayFirstFrame;
    internal double? DeltaVsA;
    internal IntPtr ForegroundBefore, ForegroundAfter;
    internal int ActivationCount;
    internal string Status = "measured";
    internal bool Completed;
    internal PinFrames Frames = new([]);
}

internal sealed class PinBurstSample(int count)
{
    internal int Count { get; } = count;
    internal long Start, FirstShowEnter, FirstShown, AllShown, LoopEnd, FirstFrame;
    internal int ShownCount;
    internal PinFrames Frames = new([]);
}

internal sealed record PinFrame(long Timestamp, TimeSpan RenderingTime);

internal sealed class PinFrames(IReadOnlyList<PinFrame> samples)
{
    internal IReadOnlyList<PinFrame> Samples { get; } = samples;
    internal double[] Gaps => Samples.Zip(Samples.Skip(1), (a, b) => (b.RenderingTime - a.RenderingTime).TotalMilliseconds).ToArray();
    internal double? MaxGap => Gaps.Length == 0 ? null : Gaps.Max();
    internal double? P95Gap => PinOpenReport.Percentile(Gaps, 0.95);
    internal int Over25 => Gaps.Count(value => value > 25);
}

internal sealed class PinFrameCollector : IDisposable
{
    private readonly List<PinFrame> _samples = [];
    internal PinFrameCollector() { CompositionTarget.Rendering += OnRendering; }
    private void OnRendering(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs frame && (_samples.Count == 0 || frame.RenderingTime != _samples[^1].RenderingTime))
            _samples.Add(new PinFrame(Stopwatch.GetTimestamp(), frame.RenderingTime));
    }
    internal PinFrames Snapshot(long showEnter, long shown, int windowMs) => new(_samples
        .Where(frame => frame.Timestamp >= showEnter && PinOpenProbe.Ms(shown, frame.Timestamp) <= windowMs).ToArray());
    public void Dispose() { CompositionTarget.Rendering -= OnRendering; }
}

internal static class PinProbeDisplay
{
    internal static IDisposable PerMonitorDpiScope() => new DpiScope();
    private sealed class DpiScope : IDisposable
    {
        private readonly IntPtr _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        internal DpiScope()
        {
            if (_previous == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not set the pin probe STA to PerMonitorV2.");
        }
        public void Dispose()
        {
            if (SetThreadDpiAwarenessContext(_previous) == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not restore the probe thread DPI context.");
        }
    }
    internal static double? RefreshRate(string deviceName)
    {
        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(deviceName, -1, ref mode)) return null;
        return mode.DisplayFrequency > 1 ? mode.DisplayFrequency : null;
    }
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode mode);
    // DEVMODEW's explicit layout avoids reading the wrong union fields on x86/x64.
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 220)]
    private struct DevMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public uint DisplayFrequency;
    }
}
