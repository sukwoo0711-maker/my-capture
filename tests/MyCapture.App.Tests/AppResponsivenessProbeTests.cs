using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Shell;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in runtime probe for the app's perceived speed outside the editor's drawing loop: the capture
/// shortcut to the region selector, Done to the editor window gone, startup to the tray and the
/// library window with large libraries, the editor's first and second open, and the library work
/// that follows a save or an OCR result.
/// </summary>
/// <remarks>
/// <para>
/// Never runs by default. Set <c>MYCAPTURE_APP_PERF</c> to an output directory. Every section runs in
/// its own child test process, so each first measurement is as cold as it is in a freshly started
/// app; <c>MYCAPTURE_APP_PERF_SECTIONS</c> (for example <c>done,library-5000</c>) narrows the run.
/// Each section writes the editor probe's CSV set (prefix <c>app-responsiveness</c>) in its own
/// folder, and the parent joins the scenario rows and the summaries at the top level.
/// </para>
/// <para>
/// Libraries are generated in isolated temporary roots (small PNG files, records spread over the
/// last six days, limits above the record count so nothing expires or is evicted). The automatic
/// removal path is wired to a trash that refuses every call, so no file can reach the Recycle Bin.
/// The clipboard is never written: copies run the real PNG encode and data object and skip only
/// the OLE write. The capture section shows the real selector over the desktop (it reads the screen
/// and window list the way a capture does, keeps nothing) and cancels it; it also runs the
/// product's synthetic <see cref="CapturePerformanceSelfTest"/>.
/// </para>
/// </remarks>
public sealed class AppResponsivenessProbeTests
{
    internal const string OutputVariable = "MYCAPTURE_APP_PERF";
    private const string SectionVariable = "MYCAPTURE_APP_PERF_CHILD_SECTION";
    private const string LibraryVariable = "MYCAPTURE_APP_PERF_CHILD_LIBRARY";

    internal static readonly string[] DefaultSections =
        ["capture", "editor-open", "editor-open-prewarmed", "done", "startup-1000", "startup-5000", "library-1000", "library-5000",
            "receipt-region", "receipt-1080", "receipt-1440", "receipt-4k"];

    [AppPerfFact]
    public async Task MeasureAppResponsiveness()
    {
        string output = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(OutputVariable)!);
        if (Environment.GetEnvironmentVariable(SectionVariable) is { Length: > 0 } section)
        {
            string folder = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(output, section));
            AppResponsivenessProbe.RunOnStaThread(section, folder, DiagnosticOutputPaths.Child(folder, "probe-completed.txt"),
                Environment.GetEnvironmentVariable(LibraryVariable));
            return;
        }

        var failures = new List<string>();
        foreach (string name in Sections())
        {
            string folder = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(output, name));
            string receipt = DiagnosticOutputPaths.Child(folder, "probe-completed.txt");
            if (File.Exists(receipt)) global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(receipt);
            string? library = AppResponsivenessProbe.LibrarySize(name) is int count ? ProbeLibrary.Create(count) : null;
            try
            {
                (int exitCode, string console) = await RunChildAsync(name, library);
                File.WriteAllText(DiagnosticOutputPaths.Child(folder, "probe-child-console.txt"), console);
                if (exitCode != 0 || !File.Exists(receipt) || File.ReadAllText(receipt) != "PASS")
                    failures.Add($"{name}: exit {exitCode}, receipt {(File.Exists(receipt) ? File.ReadAllText(receipt) : "missing")}");
            }
            finally
            {
                if (library is not null) TestRecycleBin.DeleteDirectory(library, recursive: true);
            }
        }

        ProbeJoin.Write(output, Sections());
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string[] Sections()
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_APP_PERF_SECTIONS");
        return string.IsNullOrWhiteSpace(requested)
            ? DefaultSections
            : requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<(int ExitCode, string Console)> RunChildAsync(string section, string? library)
    {
        string runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        string dotnet = Path.Combine(runtimeRoot, "dotnet.exe");
        Assert.True(File.Exists(dotnet), "The test runtime must provide its dotnet host.");
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(AppResponsivenessProbeTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(AppResponsivenessProbeTests).FullName
            + "." + nameof(MeasureAppResponsiveness));
        start.ArgumentList.Add("--Logger:console;verbosity=normal");
        start.Environment[SectionVariable] = section;
        if (library is not null) start.Environment[LibraryVariable] = library;
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using Process child = Process.Start(start) ?? throw new IOException("Could not start the app probe process.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }

            return (child.ExitCode, await stdout + Environment.NewLine + await stderr);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }
}

/// <summary>Skips unless <c>MYCAPTURE_APP_PERF</c> names an output directory.</summary>
internal sealed class AppPerfFactAttribute : FactAttribute
{
    public AppPerfFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppResponsivenessProbeTests.OutputVariable)))
            Skip = "Opt-in app responsiveness probe: set MYCAPTURE_APP_PERF to an output directory.";
    }
}

/// <summary>
/// One section of the app probe, driven from a background "input" thread while the STA thread runs
/// its dispatcher, with the editor probe's heartbeat and dispatcher-operation tracking.
/// </summary>
internal sealed partial class AppResponsivenessProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const double PointerPaceMs = 8;

    private readonly string _section;
    private readonly string _output;
    private readonly string? _library;
    private readonly ProbeRecorder _recorder = new();
    private readonly List<string> _environment = [];
    private readonly List<string> _substitutions = [];
    private JitMethodMap? _jit;
    private Dispatcher _ui = null!;
    private PreciseDelay _delay = null!;
    private string _size = "-";
    private string _variant = "-";

    private AppResponsivenessProbe(string section, string output, string? library)
    {
        _section = section;
        _output = output;
        _library = library;
    }

    internal static int? LibrarySize(string section) =>
        section.StartsWith("startup-", StringComparison.Ordinal) || section.StartsWith("library-", StringComparison.Ordinal)
            ? int.Parse(section[(section.IndexOf('-') + 1)..], CultureInfo.InvariantCulture) : null;

    internal static void RunOnStaThread(string section, string output, string receipt, string? library)
    {
        using var jit = new JitMethodMap();
        StaTestHost.Run(() =>
        {
            try
            {
                new AppResponsivenessProbe(section, output, library) { _jit = jit }.Run();
                File.WriteAllText(receipt, "PASS");
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
    }

    private void Run()
    {
        _ui = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(_ui));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        // App.xaml's own converters, which the library window looks up.
        application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
        application.Resources["PinLabel"] = new PinLabelConverter();
        application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
        ThemeService.ApplyFromSettings(new AppSettings().General);

        Exception? failure = null;
        _environment.Add("Section: " + _section);
        var hang = new HangContext(_output, NativeStacks.CurrentThreadId(), _jit,
            () => WriteReport(new TimeoutException("The UI thread stopped responding; the probe wrote hang-*.txt and ended itself.")));
        // The receipt sections time the dispatcher's own idle order: an 8 ms Input-priority beat would
        // sit in front of the card's ContextIdle turn, so they run without it.
        bool beat = !IsReceiptSection(_section);
        using (var tracker = new DispatcherOperationTracker(_ui, _recorder))
        using (UiHeartbeat? heartbeat = beat ? new UiHeartbeat(_ui, _recorder, tracker, hang, sampleStacks: false) : null)
        {
            _environment.Add(heartbeat is null ? "Heartbeat: off (receipt section)"
                : "Heartbeat timer: " + (heartbeat.HighResolution ? "high-resolution waitable timer, 8 ms" : "Thread.Sleep(8) fallback"));
            var frame = new DispatcherFrame();
            var driver = new Thread(() =>
            {
                try
                {
                    using (_delay = new PreciseDelay())
                        Script();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    _ui.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false));
                }
            })
            {
                IsBackground = true,
                Name = "App probe input driver",
            };
            driver.Start();
            Dispatcher.PushFrame(frame);
            driver.Join();
            heartbeat?.Stop();
        }

        AnnotationEditorPreferences.Read = null;
        AnnotationEditorPreferences.ReadShowSourceWindowTitle = null;
        AnnotationEditorPreferences.Write = null;
        WriteReport(failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private readonly object _reportGate = new();

    private void WriteReport(Exception? failure)
    {
        lock (_reportGate)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    new ProbeReport(_output, _recorder, _environment, _substitutions, failure, "app-responsiveness",
                        "앱 체감 속도 측정: " + _section).Write();
                    return;
                }
                catch (InvalidOperationException)
                {
                    // A late heartbeat sample changed a list while it was being written; try again.
                }
            }
        }
    }

    private void Script()
    {
        Ui(() =>
        {
            MonitorInfo primary = MonitorEnumerator.GetPrimary();
            _environment.Add($"OS: {Environment.OSVersion}; logical processors: {Environment.ProcessorCount}; runtime: {RuntimeInformation.FrameworkDescription}");
            _environment.Add($"WPF render tier: {RenderCapability.Tier >> 16}; primary monitor: {primary.Bounds.Width}x{primary.Bounds.Height} px, scale {primary.ScaleFactor.ToString(CultureInfo.InvariantCulture)}");
            _environment.Add("Monitors: " + string.Join("; ", MonitorEnumerator.GetAll().Select(monitor =>
                $"{monitor.Bounds.Width}x{monitor.Bounds.Height} at {monitor.Bounds.X},{monitor.Bounds.Y} ({monitor.Dpi} dpi{(monitor.IsPrimary ? ", primary" : "")})")));
        });

        switch (_section)
        {
            case "capture": RunCaptureStart(); break;
            case "editor-open": RunEditorOpen(prewarm: false); break;
            case "editor-open-prewarmed": RunEditorOpen(prewarm: true); break;
            case "done": RunDone(); break;
            case var receipt when IsReceiptSection(receipt): RunReceipt(); break;
            default:
                int count = LibrarySize(_section) ?? throw new ArgumentException("Unknown app probe section: " + _section);
                if (_section.StartsWith("startup-", StringComparison.Ordinal)) RunStartup(count);
                else RunLibrary(count);
                break;
        }
    }

    // ---- Capture shortcut → region selector ------------------------------------------------

    private static readonly FieldInfo ActiveOverlayField =
        typeof(CaptureOverlayCoordinator).GetField("_activeOverlay", Private)!;

    /// <summary>
    /// The capture command's own work after the shortcut: <see cref="CaptureOverlayCoordinator.Start"/>
    /// with the real screen grab, window list and page reads, until the selector has rendered over its
    /// frozen frame. "multi" grabs the whole virtual desktop as the app does; "single" grabs the primary
    /// monitor only, the one-monitor equivalent on this PC.
    /// </summary>
    private void RunCaptureStart()
    {
        RunCaptureSelfTest();
        var capture = new CaptureSettings();
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        var candidates = new WindowCandidateService(NullLogger<WindowCandidateService>.Instance);

        // As the app does at launch: warm the grab on a worker, then the selector view at idle.
        _recorder.Step = "setup: capture prewarm (as at app launch)";
        Task.Run(engine.Prewarm).Wait();
        Ui(() => CaptureOverlayPrewarm.Run(MonitorEnumerator.GetVirtualDesktopBounds()));
        WaitIdle();

        MonitorInfo primary = Ui(MonitorEnumerator.GetPrimary);
        int monitors = Ui(() => MonitorEnumerator.GetAll().Count);
        foreach ((string mode, Func<bool, FrozenFrame>? acquire, Func<RectD>? bounds) in new (string, Func<bool, FrozenFrame>?, Func<RectD>?)[]
        {
            ($"multi ({monitors} monitors; virtual desktop)", null, null),
            ("single (primary monitor)", include => engine.CaptureMonitor(primary, include), () => primary.Bounds),
        })
        {
            _size = "desktop";
            _variant = mode;
            CaptureOverlayCoordinator coordinator = Ui(() => new CaptureOverlayCoordinator(engine, candidates,
                NullLogger<CaptureOverlayCoordinator>.Instance, acquire, bounds));
            try
            {
                for (int round = 0; round < 6; round++)
                {
                    MeasureCaptureStart(coordinator, capture, round == 0 ? "shortcut → selector (first)" : $"shortcut → selector #{round + 1}");
                    // Past the presentation settle window, as a later capture normally is.
                    _delay.Wait(CaptureOverlayCoordinator.PresentationSettleMs + 250);
                }
            }
            finally
            {
                Ui(coordinator.Dispose);
            }
        }
    }

    private void MeasureCaptureStart(CaptureOverlayCoordinator coordinator, CaptureSettings capture, string name)
    {
        using var framed = new ManualResetEventSlim();
        long started = 0, visibleAt = 0, framedAt = 0;
        EventHandler rendering = (_, _) =>
        {
            if (ActiveOverlayField.GetValue(coordinator) is not CaptureOverlayWindow { IsVisible: true } overlay) return;
            long now = Stopwatch.GetTimestamp();
            if (visibleAt == 0) visibleAt = now;
            if (framedAt == 0 && overlay.Content is CaptureOverlayView { Frame: not null })
            {
                framedAt = now;
                framed.Set();
            }
        };
        Ui(() => CompositionTarget.Rendering += rendering);
        ProbeScenario s = Begin(name);
        try
        {
            Op(s, "CaptureOverlayCoordinator.Start", () =>
            {
                started = Stopwatch.GetTimestamp();
                coordinator.Start(capture.IncludeCursor, abortOnFocusLoss: false, capture.ShowMagnifier, capture.ShowSelectionGrid);
            }, idle: false);
            WaitTask(Ui(() => coordinator.LastPreparationForTest), "capture preparation");
            long prepared = Stopwatch.GetTimestamp();
            Wait(framed, "selector render over its frame");
            s.Extra["frame-ready-ms"] = Ms(started, prepared);
            s.Extra["selector-visible-ms"] = Ms(started, visibleAt);
            s.Extra["selector-rendered-ms"] = Ms(started, framedAt);
            Op(s, "CaptureOverlayCoordinator.Cancel", coordinator.Cancel, idle: false);
            End(s);
        }
        finally
        {
            Ui(() => CompositionTarget.Rendering -= rendering);
            if (Ui(() => coordinator.IsActive)) Ui(coordinator.Cancel);
        }
    }

    /// <summary>The product's own synthetic lifecycle (4K desktop, real selector and editor, no native grab).</summary>
    private void RunCaptureSelfTest()
    {
        _size = "desktop";
        _variant = "CapturePerformanceSelfTest";
        string folder = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(_output, "capture-selftest"));
        ProbeScenario s = Begin("synthetic selector lifecycle, 2 warm-up + 20 rounds");
        int exit = 0;
        Op(s, "CapturePerformanceSelfTest.Run", () => exit = CapturePerformanceSelfTest.Run(folder), idle: false);
        End(s);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "capture-performance.json")));
        JsonElement[] rounds = report.RootElement.GetProperty("Iterations").EnumerateArray()
            .Where(item => item.TryGetProperty("StartCallMs", out _)).ToArray();
        foreach ((string label, JsonElement[] set) in new[] { ("warm-up", rounds.Where(r => r.GetProperty("Warmup").GetBoolean()).ToArray()),
                     ("measured", rounds.Where(r => !r.GetProperty("Warmup").GetBoolean()).ToArray()) })
        {
            foreach (string field in new[] { "StartCallMs", "AcquisitionMs", "FirstVisibleOverlayRenderMs", "FirstFrameRenderMs", "SelectToEditorMs", "FinishMs" })
            {
                double[] values = set.Select(r => r.GetProperty(field).GetDouble()).ToArray();
                if (values.Length == 0) continue;
                s.Extra[$"{label} {field} avg"] = values.Average();
                s.Extra[$"{label} {field} max"] = values.Max();
            }
        }

        s.Notes = $"exit {exit}; synthetic acquisition sleeps 150 ms by design; size {report.RootElement.GetProperty("Width").GetInt32()}x{report.RootElement.GetProperty("Height").GetInt32()}";
    }

    // ---- Editor first and second open ------------------------------------------------------

    private void RunEditorOpen(bool prewarm)
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mcap-").FullName;
        ProbeServices app = Ui(() => new ProbeServices(root));
        try
        {
            if (prewarm)
            {
                // Resolved by name so the probe also measures a build without the editor prewarm.
                MethodInfo? run = typeof(AnnotationEditorWindow).Assembly.GetType("MyCapture.App.Editing.AnnotationEditorPrewarm")
                    ?.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes);
                if (run is null)
                {
                    _environment.Add("This build has no AnnotationEditorPrewarm; the prewarmed section measured a cold process.");
                }
                else
                {
                    _size = "-";
                    _variant = "prewarm";
                    ProbeScenario warm = Begin("idle prewarm (AnnotationEditorPrewarm.Run)");
                    Op(warm, "AnnotationEditorPrewarm.Run", () => run.Invoke(null, null), idle: true);
                    End(warm);
                }
            }

            foreach ((int width, int height, string name) in new[]
            {
                (1920, 1080, "first open"), (1920, 1080, "second open"), (3840, 2160, "first 4K open"), (3840, 2160, "second 4K open"),
            })
            {
                _size = $"{width}x{height}";
                _variant = prewarm ? "after idle prewarm" : "cold process";
                BitmapSource image = Ui(() => SyntheticBitmap(width, height, width + height + name.Length));
                CaptureRecord record = WaitTask(Ui(() => app.Persistence.PersistOriginalAsync(image, 1.0, "Probe", "Probe")), "persist original");
                CaptureEditSession session = Ui(() => app.Commit.BeginEditSession(record));
                EditorUnderTest ed = OpenEditor(app, image, record, session, name);
                try
                {
                    ProbeScenario close = Begin(name + ": close");
                    Op(close, "Window.Close", () => ed.Window.Close(), idle: false);
                    Wait(ed.Closed, "editor close");
                    WaitTask(Ui(EditDraftSession.WaitForFinalizationsAsync), "draft finalization");
                    End(close);
                }
                finally
                {
                    CloseIfOpen(ed);
                    Ui(session.Dispose);
                }
            }
        }
        finally
        {
            Ui(app.Dispose);
            DeleteRoot(root);
        }
    }

    // ---- Done → editor window gone -----------------------------------------------------------

    /// <summary>
    /// Done (<see cref="AnnotationEditorControl.SaveAndClose"/>) wired as the capture flow wires it:
    /// flatten, persist into the isolated library, copy (OLE write skipped), retire the draft, close.
    /// The first size runs cold, as the first Done after launch does.
    /// </summary>
    private void RunDone()
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mcap-").FullName;
        ProbeServices app = Ui(() => new ProbeServices(root));
        try
        {
            foreach ((int width, int height, string variant) in new[]
            {
                (1920, 1080, "first after launch"), (1920, 1080, "warm"), (3840, 2160, "warm"), (7680, 2160, "warm (two 4K monitors)"),
            })
            {
                _size = $"{width}x{height}";
                _variant = variant;
                MeasureDone(app, width, height);
                DoneBreakdown(app, width, height);
                _recorder.Step = "setup: full GC between sizes";
                Ui(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            }
        }
        finally
        {
            Ui(app.Dispose);
            DeleteRoot(root);
        }
    }

    private void MeasureDone(ProbeServices app, int width, int height)
    {
        BitmapSource image = Ui(() => SyntheticBitmap(width, height, width * 31 + height));
        CaptureRecord record = WaitTask(Ui(() => app.Persistence.PersistOriginalAsync(image, 1.0, "Probe done", "Probe")), "persist original");
        CaptureEditSession session = Ui(() => app.Commit.BeginEditSession(record));
        EditorUnderTest ed = OpenEditor(app, image, record, session, "open before Done");
        try
        {
            int w = width, h = height;
            _recorder.Step = "draw: rectangle, arrow, pen";
            Ui(() => ed.Editor.ShortcutActions["Rectangle"]());
            Stroke(ed, Line(new PointD(w * 0.20, h * 0.20), new PointD(w * 0.35, h * 0.40), 24));
            Ui(() => ed.Editor.ShortcutActions["Arrow"]());
            Stroke(ed, Line(new PointD(w * 0.50, h * 0.18), new PointD(w * 0.70, h * 0.33), 24));
            Ui(() => ed.Editor.ShortcutActions["Pen"]());
            Stroke(ed, Enumerable.Range(0, 80).Select(i => new PointD(w * (0.1 + 0.8 * i / 79.0), h * (0.6 + 0.05 * Math.Sin(i / 6.0)))).ToList());
            // A user pauses before Done; the draft written after the last change is not part of Done.
            _delay.Wait(1000);
            WaitTask(Ui(() => ed.Draft?.LastWrite ?? Task.CompletedTask), "draft write before Done");

            long started = 0, hiddenAt = 0, persistedAt = 0, copiedAt = 0, commitAt = 0, closedAt = 0;
            DependencyPropertyChangedEventHandler visibility = (_, e) =>
            {
                if (e.NewValue is false && hiddenAt == 0) hiddenAt = Stopwatch.GetTimestamp();
            };
            EventHandler<CaptureRecord> persisted = (_, saved) =>
            {
                if (saved.Id == record.Id && persistedAt == 0) persistedAt = Stopwatch.GetTimestamp();
            };
            app.ClipboardWritten = () => copiedAt = Stopwatch.GetTimestamp();
            Ui(() =>
            {
                ed.Window.IsVisibleChanged += visibility;
                ed.Window.Closed += (_, _) => closedAt = Stopwatch.GetTimestamp();
                app.Persistence.ImagePersisted += persisted;
            });
            ed.CommitDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ProbeScenario s = Begin("Done → editor window gone");
            try
            {
                Op(s, "SaveAndClose", () =>
                {
                    started = Stopwatch.GetTimestamp();
                    ed.Editor.SaveAndClose();
                }, idle: false);
                bool saved = WaitTask(ed.CommitDone.Task, "Done commit");
                commitAt = Stopwatch.GetTimestamp();
                Wait(ed.Closed, "editor close after Done");
                WaitTask(Ui(EditDraftSession.WaitForFinalizationsAsync), "draft finalization");
                End(s);
                s.Extra["window-hidden-ms"] = Ms(started, hiddenAt);
                s.Extra["window-closed-ms"] = Ms(started, closedAt);
                s.Extra["clipboard-ready-ms"] = copiedAt == 0 ? -1 : Ms(started, copiedAt);
                s.Extra["image-persisted-ms"] = persistedAt == 0 ? -1 : Ms(started, persistedAt);
                s.Extra["commit-returned-ms"] = Ms(started, commitAt);
                s.Notes = $"saved={saved}, committed={Ui(() => ed.Window.WasCommitted)}, annotations={record.HasAnnotations}, items={Ui(() => ed.Controller.Document.Items.Count)}";
            }
            finally
            {
                app.ClipboardWritten = null;
                Ui(() =>
                {
                    ed.Window.IsVisibleChanged -= visibility;
                    app.Persistence.ImagePersisted -= persisted;
                });
            }
        }
        finally
        {
            CloseIfOpen(ed);
            Ui(session.Dispose);
        }
    }

    /// <summary>Times Done's parts by calling them directly, three times each, while the UI thread is idle.</summary>
    private void DoneBreakdown(ProbeServices app, int width, int height)
    {
        _recorder.Step = "breakdown: Done parts";
        BitmapSource image = Ui(() => SyntheticBitmap(width, height, width * 17 + height));
        AnnotationDocument document = AnnotationDocument.CreateFor(width, height);
        document.Items.Add(new RectangleAnnotation { Rect = new RectD(width * 0.2, height * 0.2, width * 0.15, height * 0.2) });
        for (int round = 0; round < 3; round++)
        {
            long begin = Stopwatch.GetTimestamp();
            BitmapSource flattened = WaitTask(MyCapture.App.Threading.StaThreadTask.RunAsync(
                () => CaptureCommitService.Flatten(image, document, new Dictionary<string, BitmapSource>()), "probe flatten"), "flatten");
            _recorder.AddBreakdown(_size, _variant, "CaptureCommitService.Flatten (STA) ms", Ms(begin));

            begin = Stopwatch.GetTimestamp();
            _ = WaitTask(Task.Run(() => ImageCodec.EncodePng(flattened, null)), "encode");
            _recorder.AddBreakdown(_size, _variant, "ImageCodec.EncodePng (one full PNG) ms", Ms(begin));

            begin = Stopwatch.GetTimestamp();
            _ = WaitTask(ClipboardImageService.CopyImageAsync(flattened, null, static _ => { }), "clipboard data object");
            _recorder.AddBreakdown(_size, _variant, "ClipboardImageService.CopyImageAsync (OLE write skipped) ms", Ms(begin));

            CaptureRecord record = WaitTask(Ui(() => app.Persistence.PersistOriginalAsync(image, 1.0, "Probe parts", "Probe")), "persist original (parts)");
            begin = Stopwatch.GetTimestamp();
            WaitTask(Ui(() => app.Persistence.FinalizeAsync(record, flattened, document.Clone(), new Dictionary<string, BitmapSource>())), "finalize");
            _recorder.AddBreakdown(_size, _variant, "CapturePersistenceService.FinalizeAsync (render, layers, thumbnail, swap, meta, index) ms", Ms(begin));
        }
    }

    // ---- Startup → tray, library window ------------------------------------------------------

    /// <summary>
    /// The library-dependent part of <c>App.InitializeShell</c> before the tray icon appears, in its
    /// order (settings, InitializeQueue, history maintenance), then the library window's first and
    /// second open. The tray icon, hotkeys and OCR engine are not created: their cost does not depend on
    /// the library, and creating them would touch the user's shell and the running app's shortcuts.
    /// </summary>
    private void RunStartup(int count)
    {
        AppPaths paths = AppPaths.CreateForRoot(_library ?? throw new InvalidOperationException("The startup section needs a library."));
        _size = $"library-{count}";
        _variant = "startup";
        ProbeScenario s = Begin("start → tray ready (library-dependent steps)");
        long started = Stopwatch.GetTimestamp();
        AppSettings settings = Op(s, "SettingsStore.Load", () => new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Load());
        ProbeLibraryServices library = Op(s, "InitializeQueue (CaptureQueue.Load and library services)",
            () => ProbeLibraryServices.Initialize(paths, settings));
        Op(s, "MaintainHistory (RequestAutomaticMaintenance)", library.Queue.RequestAutomaticMaintenance, idle: false);
        long trayReady = Stopwatch.GetTimestamp();
        WaitTask(Ui(() => library.Recovery.RecoverInterruptedRestoresAsync()), "interrupted-restore recovery");
        End(s);
        s.Extra["tray-ready-ms"] = Ms(started, trayReady);
        s.Extra["startup-idle-ms"] = Ms(started, s.EndTimestamp);
        s.Notes = $"records loaded: {Ui(() => library.Queue.Count)}";
        try
        {
            GalleryWindow window = OpenLibrary(library, "library window first open");
            ProbeScenario hide = Begin("library window close (hide)");
            Op(hide, "GalleryWindow.Close", window.Close, idle: true);
            End(hide);
            _ = OpenLibrary(library, "library window second open");
            StartupBreakdown(paths, settings);
        }
        finally
        {
            Ui(() => library.Window?.CloseForExit());
            Ui(library.Dispose);
        }
    }

    /// <summary>Times InitializeQueue's parts again, warm, against the same library.</summary>
    private void StartupBreakdown(AppPaths paths, AppSettings settings)
    {
        _recorder.Step = "breakdown: startup parts (warm)";
        MethodInfo rebuild = typeof(CaptureQueue).GetMethod("RebuildFromDisk", Private)!;
        MethodInfo resolve = typeof(CaptureQueue).GetMethod("TryResolveDirectory", Private)!;
        MethodInfo parse = typeof(CaptureQueue).GetMethod("TryParseIndex", BindingFlags.Static | BindingFlags.NonPublic)!;
        Ui(() =>
        {
            for (int round = 0; round < 3; round++)
            {
                long begin = Stopwatch.GetTimestamp();
                var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
                using (queue.SuspendEviction()) queue.Load();
                _recorder.AddBreakdown(_size, _variant, "CaptureQueue.Load (warm) ms", Ms(begin));

                begin = Stopwatch.GetTimestamp();
                string text = File.ReadAllText(paths.IndexFile);
                _recorder.AddBreakdown(_size, _variant, "index.json read ms", Ms(begin));
                begin = Stopwatch.GetTimestamp();
                _ = parse.Invoke(null, [text, null]);
                _recorder.AddBreakdown(_size, _variant, "index.json parse and validation ms", Ms(begin));

                begin = Stopwatch.GetTimestamp();
                _ = rebuild.Invoke(queue, [true]);
                _recorder.AddBreakdown(_size, _variant, "RebuildFromDisk(pendingOnly) (recursive sidecar walks) ms", Ms(begin));

                begin = Stopwatch.GetTimestamp();
                foreach (CaptureRecord record in queue.Records)
                {
                    object?[] arguments = [record, null];
                    if ((bool)resolve.Invoke(queue, arguments)!)
                        _ = File.Exists(Path.Combine((string)arguments[1]!, CaptureFileNames.Original));
                }
                _recorder.AddBreakdown(_size, _variant, "per-record TryResolveDirectory + File.Exists ms", Ms(begin));

                begin = Stopwatch.GetTimestamp();
                var recovery = new RecoverableLibraryService(queue, paths, new ProbeLibraryServices.RefusingTrash(), NullLogger.Instance);
                _recorder.AddBreakdown(_size, _variant, "new RecoverableLibraryService ms", Ms(begin));
                begin = Stopwatch.GetTimestamp();
                _ = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
                _recorder.AddBreakdown(_size, _variant, "new CapturePersistenceService ms", Ms(begin));
                begin = Stopwatch.GetTimestamp();
                _ = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
                _recorder.AddBreakdown(_size, _variant, "new VideoLibraryService ms", Ms(begin));
                begin = Stopwatch.GetTimestamp();
                _ = new GalleryController(queue, NullLogger<GalleryController>.Instance) { Recovery = recovery };
                _recorder.AddBreakdown(_size, _variant, "new GalleryController ms", Ms(begin));
                begin = Stopwatch.GetTimestamp();
                queue.RequestAutomaticMaintenance();
                _recorder.AddBreakdown(_size, _variant, "RequestAutomaticMaintenance ms", Ms(begin));
            }
        });
    }

    private GalleryWindow OpenLibrary(ProbeLibraryServices library, string name)
    {
        using var rendered = new ManualResetEventSlim();
        long started = 0, constructed = 0, renderedAt = 0;
        ProbeScenario s = Begin(name);
        bool first = library.Window is null;
        Op(s, first ? "EnsureGalleryWindow (GalleryViewModel + GalleryWindow)" : "(window exists)", () =>
        {
            started = Stopwatch.GetTimestamp();
            library.EnsureWindow();
            constructed = Stopwatch.GetTimestamp();
            if (!first)
            {
                // ContentRendered is raised once per window, on its first show.
                rendered.Set();
                return;
            }

            EventHandler? onRendered = null;
            onRendered = (_, _) =>
            {
                library.Window!.ContentRendered -= onRendered;
                renderedAt = Stopwatch.GetTimestamp();
                rendered.Set();
            };
            library.Window!.ContentRendered += onRendered;
        }, idle: false);
        GalleryWindow window = library.Window!;
        Op(s, "GalleryWindow.ShowGallery", () =>
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = -30000;
            window.ShowActivated = false;
            window.ShowGallery();
        }, idle: false);
        Wait(rendered, "library window render");
        End(s);
        s.Extra["construct-ms"] = Ms(started, constructed);
        if (renderedAt != 0) s.Extra["first-render-ms"] = Ms(started, renderedAt);
        s.Extra["open-to-idle-ms"] = Ms(started, s.EndTimestamp);
        s.Notes = $"visible tiles {Ui(() => library.ViewModel!.VisibleCount)}, rows {Ui(() => library.ViewModel!.Rows.Count)}";
        return window;
    }

    // ---- Library refresh after a save or OCR -------------------------------------------------

    /// <summary>
    /// What follows a save and an OCR result in a large library, with the library window hidden (closed
    /// to the tray) and visible: the index publication's serialization, retention checks, and the
    /// App's idle-priority <c>RefreshFromQueue</c> + <c>MaintainHistory</c> after ImagePersisted.
    /// </summary>
    private void RunLibrary(int count)
    {
        AppPaths paths = AppPaths.CreateForRoot(_library ?? throw new InvalidOperationException("The library section needs a library."));
        _size = $"library-{count}";
        _variant = "setup";
        _recorder.Step = "setup: load library and open its window once";
        AppSettings settings = Ui(() => new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Load());
        ProbeLibraryServices library = Ui(() => ProbeLibraryServices.Initialize(paths, settings));
        // App.InitializeShell: after a save the library and retention follow at ApplicationIdle.
        var idleWork = new List<(string Step, double Ms)>();
        EventHandler<CaptureRecord> afterSave = (_, _) => _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            long begin = Stopwatch.GetTimestamp();
            library.Window?.RefreshFromQueue();
            idleWork.Add(("RefreshFromQueue", Ms(begin)));
            begin = Stopwatch.GetTimestamp();
            library.Queue.RequestAutomaticMaintenance();
            idleWork.Add(("MaintainHistory", Ms(begin)));
        }));
        Ui(() => library.Persistence.ImagePersisted += afterSave);
        try
        {
            _ = OpenLibrary(library, "setup open");
            foreach (bool visible in new[] { false, true })
            {
                _variant = visible ? "library window open" : "library window closed (tray)";
                Ui(() =>
                {
                    if (visible) library.Window!.ShowGallery();
                    else library.Window!.Close();
                });
                WaitIdle();
                MeasureSaveCompletion(library, idleWork);
                MeasureOcrResults(library);
                LibraryBreakdown(library);
            }
        }
        finally
        {
            Ui(() => library.Persistence.ImagePersisted -= afterSave);
            Ui(() => library.Window?.CloseForExit());
            Ui(library.Dispose);
        }
    }

    private void MeasureSaveCompletion(ProbeLibraryServices library, List<(string Step, double Ms)> idleWork)
    {
        BitmapSource image = Ui(() => SyntheticBitmap(1920, 1080, idleWork.Count + 7));
        idleWork.Clear();
        ProbeScenario s = Begin("capture saved (persist original, finalize, idle refresh)");
        long started = Stopwatch.GetTimestamp();
        CaptureRecord record = WaitTask(Op(s, "PersistOriginalAsync",
            () => library.Persistence.PersistOriginalAsync(image, 1.0, "Probe save", "Probe")), "persist original");
        AnnotationDocument document = AnnotationDocument.CreateFor(1920, 1080);
        document.Items.Add(new RectangleAnnotation { Rect = new RectD(200, 200, 300, 200) });
        WaitTask(Op(s, "FinalizeAsync", () => library.Persistence.FinalizeAsync(record, image, document, new Dictionary<string, BitmapSource>())), "finalize");
        WaitIdle();
        End(s);
        s.Extra["save-to-idle-ms"] = Ms(started, s.EndTimestamp);
        foreach (IGrouping<string, (string Step, double Ms)> step in idleWork.GroupBy(work => work.Step))
            s.Extra["idle " + step.Key + " ms"] = step.Sum(work => work.Ms);
    }

    private void MeasureOcrResults(ProbeLibraryServices library)
    {
        CaptureRecord[] targets = Ui(() => library.Queue.Records.Where(record => record.IsImage).Take(20).ToArray());
        ProbeScenario s = Begin("OCR results for 20 captures (CacheOcrAsync each, then refresh)");
        foreach (CaptureRecord record in targets)
        {
            Guid id = record.Id;
            long revision = Ui(() => record.ContentRevision);
            Task<bool> cached = Op(s, "GalleryController.CacheOcrAsync", () =>
                library.Controller.CacheOcrAsync(id, "probe text " + id.ToString("N")[..8], "ko", revision));
            WaitTask(cached, "OCR cache");
        }

        Op(s, "RefreshFromQueue (end of indexing pass)", () => library.Window?.RefreshFromQueue(), idle: true);
        End(s);
    }

    private void LibraryBreakdown(ProbeLibraryServices library)
    {
        _recorder.Step = "breakdown: library refresh, index serialization, retention checks";
        Ui(() =>
        {
            CaptureQueue queue = library.Queue;
            CaptureRecord newest = queue.Records[0];
            CaptureRecord oldest = queue.Records[^1];
            for (int round = 0; round < 5; round++)
            {
                long begin = Stopwatch.GetTimestamp();
                library.Window!.RefreshFromQueue();
                _recorder.AddBreakdown(_size, _variant, "GalleryWindow.RefreshFromQueue ms", Ms(begin));

                begin = Stopwatch.GetTimestamp();
                library.ViewModel!.Refresh();
                _recorder.AddBreakdown(_size, _variant, "GalleryViewModel.Refresh ms", Ms(begin));

                begin = Stopwatch.GetTimestamp();
                Task written = queue.SaveAsync();
                _recorder.AddBreakdown(_size, _variant, "CaptureQueue.SaveAsync on the owner thread (index snapshot + JSON) ms", Ms(begin));
                written.GetAwaiter().GetResult();

                begin = Stopwatch.GetTimestamp();
                queue.UpdateByteCount(newest.Id, newest.TotalBytes);
                _recorder.AddBreakdown(_size, _variant, "CaptureQueue.UpdateByteCount (EnforceLimits) ms", Ms(begin));

                begin = Stopwatch.GetTimestamp();
                for (int i = 0; i < 100; i++) _ = queue.Find(oldest.Id);
                _recorder.AddBreakdown(_size, _variant, "CaptureQueue.Find (oldest) × 100 ms", Ms(begin));
            }
        });
    }

    // ---- Editor plumbing ---------------------------------------------------------------------

    private EditorUnderTest OpenEditor(ProbeServices app, BitmapSource image, CaptureRecord record, CaptureEditSession session, string name)
    {
        var ed = new EditorUnderTest();
        MonitorInfo primary = Ui(MonitorEnumerator.GetPrimary);
        ProbeScenario open = Begin(name);
        long started = 0, constructed = 0;
        Op(open, "new AnnotationEditorWindow + Show", () =>
        {
            started = Stopwatch.GetTimestamp();
            var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
            var window = new AnnotationEditorWindow(new FrozenFrame(image, region, null, 0), region, image,
                sourceWindowTitle: "Probe", pointerMonitor: primary)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000, Top = -30000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            constructed = Stopwatch.GetTimestamp();
            // As the capture flow (CaptureOverlayCoordinator) wires it, on builds that have it.
            typeof(AnnotationEditorWindow).GetProperty("FinishesOutOfSight", Private)?.SetValue(window, true);
            window.DraftRecordSource = () => (record.Id, session.ExpectedContentRevision);
            // As the editor probe does: the app's draft session against the isolated store.
            window.AdoptRecoveredDraft(app.Drafts, Guid.NewGuid(), 0);
            typeof(AnnotationEditorWindow).GetField("_recoveredDraft", Private)!.SetValue(window, false);
            window.ConfirmClose = () => EditorCloseChoice.DontSave;
            window.CommitRequested = async result =>
            {
                try
                {
                    bool saved = await app.Commit.CommitAsync(record, result, session);
                    ed.CommitDone?.TrySetResult(saved);
                    return saved;
                }
                catch (Exception ex)
                {
                    ed.CommitDone?.TrySetException(ex);
                    throw;
                }
            };
            window.ContentRendered += (_, _) =>
            {
                ed.RenderedAt = Stopwatch.GetTimestamp();
                ed.Rendered.Set();
            };
            window.Closed += (_, _) => ed.Closed.Set();
            long showing = Stopwatch.GetTimestamp();
            window.Show();
            open.Extra["show-ms"] = Ms(showing);
            ed.Window = window;
            ed.Editor = window.Editor;
            ed.Controller = (AnnotationEditorController)typeof(AnnotationEditorControl).GetField("_controller", Private)!.GetValue(window.Editor)!;
            ed.Draft = (EditDraftSession?)typeof(AnnotationEditorWindow).GetField("_draftSession", Private)!.GetValue(window);
        }, idle: false);
        Wait(ed.Rendered, "first editor render");
        End(open);
        open.Extra["ctor-ms"] = Ms(started, constructed);
        open.Extra["first-render-ms"] = Ms(started, ed.RenderedAt);
        open.Extra["open-to-idle-ms"] = Ms(started, open.EndTimestamp);
        return ed;
    }

    private void Stroke(EditorUnderTest ed, IReadOnlyList<PointD> path)
    {
        Ui(() => ed.Editor.SimulatePointerDown(ed.Editor.ViewportPointOf(path[0])));
        for (int i = 1; i < path.Count - 1; i++)
        {
            _delay.Wait(PointerPaceMs);
            PointD point = path[i];
            Ui(() => ed.Editor.SimulatePointerMove(ed.Editor.ViewportPointOf(point), leftPressed: true));
        }

        _delay.Wait(PointerPaceMs);
        Ui(() => ed.Editor.SimulatePointerUp(ed.Editor.ViewportPointOf(path[^1])));
        WaitIdle();
    }

    private void CloseIfOpen(EditorUnderTest ed)
    {
        if (ed.Window is null || ed.Closed.IsSet) return;
        Ui(() => ed.Window.Close());
        ed.Closed.Wait(TimeSpan.FromSeconds(30));
    }

    private void DeleteRoot(string root)
    {
        _recorder.Step = "setup: remove isolated data root";
        WaitTask(Ui(EditDraftSession.WaitForFinalizationsAsync), "draft finalization before cleanup");
        try
        {
            TestRecycleBin.DeleteDirectory(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _environment.Add($"Could not remove isolated data root {root}: {ex.Message}");
        }
    }

    private static BitmapSource SyntheticBitmap(int width, int height, int seed)
    {
        byte[] pixels = SyntheticImage.PhotoLikePixels(width, height, seed);
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static List<PointD> Line(PointD from, PointD to, int count) =>
        Enumerable.Range(0, count).Select(i =>
        {
            double t = i / (double)(count - 1);
            return new PointD(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
        }).ToList();

    // ---- Measurement plumbing (as in the editor probe) ---------------------------------------

    private ProbeScenario Begin(string name)
    {
        WaitIdle();
        var scenario = new ProbeScenario(_size, _variant, name);
        scenario.Start();
        _recorder.Current = scenario;
        return scenario;
    }

    private void End(ProbeScenario scenario)
    {
        _recorder.Step = "settle to ApplicationIdle";
        long before = Stopwatch.GetTimestamp();
        long idle = WaitIdle();
        scenario.SettleMs = Ms(before, idle);
        scenario.Finish(idle);
        _recorder.Current = null;
        _recorder.Step = null;
        _recorder.Add(scenario);
    }

    private double Op(ProbeScenario scenario, string label, Action action, bool idle)
    {
        _recorder.Step = label;
        long started = 0;
        double sync = 0;
        _ui.Invoke(() =>
        {
            started = Stopwatch.GetTimestamp();
            action();
            sync = Ms(started);
        }, DispatcherPriority.Input);
        scenario.AddSync(label, sync);
        if (idle) scenario.AddIdle(Ms(started, WaitIdle()));
        return sync;
    }

    private T Op<T>(ProbeScenario scenario, string label, Func<T> action)
    {
        T result = default!;
        Op(scenario, label, () => result = action(), idle: false);
        return result;
    }

    private long WaitIdle()
    {
        long reached = 0;
        _ui.Invoke(() => reached = Stopwatch.GetTimestamp(), DispatcherPriority.ApplicationIdle);
        return reached;
    }

    private void Ui(Action action) => _ui.Invoke(action, DispatcherPriority.Input);

    private T Ui<T>(Func<T> action) => _ui.Invoke(action, DispatcherPriority.Input);

    private static T WaitTask<T>(Task<T> task, string what)
    {
        WaitTask((Task)task, what);
        return task.Result;
    }

    private static void WaitTask(Task task, string what)
    {
        if (!task.Wait(TimeSpan.FromMinutes(3))) throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static void Wait(ManualResetEventSlim signal, string what)
    {
        if (!signal.Wait(TimeSpan.FromMinutes(3))) throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static double Ms(long start) => Ms(start, Stopwatch.GetTimestamp());

    private static double Ms(long start, long end) => end == 0 ? -1 : (end - start) * 1000.0 / Stopwatch.Frequency;

    private sealed class EditorUnderTest
    {
        internal AnnotationEditorWindow Window = null!;
        internal AnnotationEditorControl Editor = null!;
        internal AnnotationEditorController Controller = null!;
        internal EditDraftSession? Draft;
        internal readonly ManualResetEventSlim Rendered = new();
        internal readonly ManualResetEventSlim Closed = new();
        internal TaskCompletionSource<bool>? CommitDone;
        internal long RenderedAt;
    }

    /// <summary>The services App builds around an editor, rooted in an isolated temporary directory.</summary>
    private sealed class ProbeServices : IDisposable
    {
        private readonly DispatcherTimer _preferenceTimer;

        internal ProbeServices(string root)
        {
            Paths = AppPaths.CreateForRoot(root);
            Paths.EnsureCreated();
            Settings = new AppSettings();
            var settingsStore = new SettingsStore(Paths, NullLogger<SettingsStore>.Instance);
            Queue = new CaptureQueue(Paths, Settings.Queue, NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, Paths, () => Settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            Commit = new CaptureCommitService(Persistence, () => Settings, () => Paths, NullLogger<CaptureCommitService>.Instance,
                (bitmap, sourceUrl) => ClipboardImageService.CopyImageAsync(bitmap, sourceUrl, _ => ClipboardWritten?.Invoke()));
            Drafts = new EditDraftStore(Path.Combine(root, "edit-drafts"));
            _preferenceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _preferenceTimer.Tick += (_, _) =>
            {
                _preferenceTimer.Stop();
                settingsStore.Save(Settings);
            };
            AnnotationEditorPreferences.Read = () => Settings.Annotation;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => Settings.Export.ShowSourceWindowTitle;
            AnnotationEditorPreferences.Write = value =>
            {
                Settings.Annotation = value;
                _preferenceTimer.Stop();
                _preferenceTimer.Start();
            };
        }

        internal AppPaths Paths { get; }
        internal AppSettings Settings { get; }
        internal CaptureQueue Queue { get; }
        internal CapturePersistenceService Persistence { get; }
        internal CaptureCommitService Commit { get; }
        internal EditDraftStore Drafts { get; }

        /// <summary>Called where the clipboard write would happen (the data object is complete).</summary>
        internal Action? ClipboardWritten { get; set; }

        public void Dispose()
        {
            _preferenceTimer.Stop();
            AnnotationEditorPreferences.Read = null;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = null;
            AnnotationEditorPreferences.Write = null;
        }
    }
}

/// <summary>
/// The library services <c>App.InitializeQueue</c> builds, in the same order, and the library window
/// <c>App.EnsureGalleryWindow</c> builds. Automatic removal is connected as the app connects it, but
/// may remove nothing, and its trash refuses every call.
/// </summary>
internal sealed class ProbeLibraryServices : IDisposable
{
    private ProbeLibraryServices(AppPaths paths, AppSettings settings, CaptureQueue queue, RecoverableLibraryService recovery)
    {
        Paths = paths;
        Settings = settings;
        Queue = queue;
        Recovery = recovery;
        Persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        Commit = new CaptureCommitService(Persistence, () => settings, () => paths, NullLogger<CaptureCommitService>.Instance,
            (bitmap, sourceUrl) => ClipboardImageService.CopyImageAsync(bitmap, sourceUrl, static _ => { }));
        Videos = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        Controller = new GalleryController(queue, NullLogger<GalleryController>.Instance) { Recovery = recovery };
        Reedit = new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance);
        Ocr = new AvailableOcr();
        Presenter = new OcrResultPresenter(Ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        Indexing = new OcrIndexingService(Controller, Ocr, record => queue.GetDirectory(record), () => settings.Ocr,
            NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
    }

    internal static ProbeLibraryServices Initialize(AppPaths paths, AppSettings settings)
    {
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        ProbeLibraryServices services;
        using (queue.SuspendEviction())
        {
            var recovery = new RecoverableLibraryService(queue, paths, new RefusingTrash(), NullLogger.Instance);
            recovery.ConnectAutomaticMaintenance(_ => false, _ => { });
            queue.Load();
            services = new ProbeLibraryServices(paths, settings, queue, recovery);
        }

        return services;
    }

    internal AppPaths Paths { get; }
    internal AppSettings Settings { get; }
    internal CaptureQueue Queue { get; }
    internal RecoverableLibraryService Recovery { get; }
    internal CapturePersistenceService Persistence { get; }
    internal CaptureCommitService Commit { get; }
    internal VideoLibraryService Videos { get; }
    internal GalleryController Controller { get; }
    internal GalleryReeditLoader Reedit { get; }
    internal AvailableOcr Ocr { get; }
    internal OcrResultPresenter Presenter { get; }
    internal OcrIndexingService Indexing { get; }
    internal GalleryViewModel? ViewModel { get; private set; }
    internal GalleryWindow? Window { get; private set; }

    internal void EnsureWindow()
    {
        if (Window is not null) return;
        ViewModel = new GalleryViewModel(Controller, record => Path.Combine(Queue.GetDirectory(record), CaptureFileNames.Thumbnail),
            Settings.Queue.ThumbnailLongEdge, imageRetentionHours: () => Settings.Queue.ImageRetentionHours);
        Window = new GalleryWindow(ViewModel, Controller, Reedit, Commit, Queue, Videos, Paths, NullLoggerFactory.Instance,
            Presenter, () => Settings.Ocr, Indexing, new NoPrivacy(), NullLogger.Instance);
    }

    public void Dispose()
    {
    }

    /// <summary>OCR that reports an engine (so coverage is computed as on a PC with one) and recognizes nothing.</summary>
    internal sealed class AvailableOcr : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko", "en-US"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OcrResult.NoText("ko", TimeSpan.Zero));
    }

    private sealed class NoPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The library probe never searches for private text.");
    }

    /// <summary>The probe library never expires or evicts anything; a call here is a probe defect.</summary>
    internal sealed class RefusingTrash : IRecoverableTrash
    {
        public string GetIdentity(string path) => throw Refused();
        public RecycledPathReceipt RecycleDirectory(string path) => throw Refused();
        public void RestoreDirectory(RecycledPathReceipt receipt) => throw Refused();
        public RecycledPathReceipt? FindRecycledDirectory(string originalPath, string identity) => throw Refused();
        private static IOException Refused() => new("The app probe never uses the Recycle Bin.");
    }
}

/// <summary>
/// A library of <c>count</c> image captures in an isolated temporary root: settings with limits above
/// the count, small PNG originals and thumbnails, records spread over the last six days (inside the
/// default seven-day retention), a third with cached OCR text, one index and one metadata file each.
/// </summary>
internal static class ProbeLibrary
{
    internal static string Create(int count)
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mcap-lib-").FullName;
        byte[] original = [], thumbnail = [];
        StaTestHost.Run(() =>
        {
            original = Png(64, 36, 1);
            thumbnail = Png(320, 180, 2);
        });

        AppPaths paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        settings.General.IsFirstRun = false;
        settings.Queue.MaxItems = count + 1000;
        settings.Queue.MaxBytes = 1L << 40;
        new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Save(settings);

        DateTimeOffset now = DateTimeOffset.Now;
        var records = new List<CaptureRecord>(count);
        for (int i = 0; i < count; i++)
        {
            DateTimeOffset created = now - TimeSpan.FromMinutes((i + 1) * (6 * 24 * 60.0 / count));
            var record = new CaptureRecord
            {
                CreatedAt = created,
                UpdatedAt = created,
                Width = 1920,
                Height = 1080,
                SourceWindowTitle = $"Probe window {i % 37}",
                Title = i % 5 == 0 ? $"Probe title {i}" : string.Empty,
                Tags = i % 7 == 0 ? "probe" : string.Empty,
                OcrText = i % 3 == 0 ? $"probe recognized text {i}" : null,
                OcrLanguage = i % 3 == 0 ? "ko" : null,
                OcrContentRevision = i % 3 == 0 ? 0 : null,
                TotalBytes = original.Length + thumbnail.Length,
            };
            record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, created);
            string directory = Path.Combine(paths.CapturesRoot, record.RelativeDirectory);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), original);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Thumbnail), thumbnail);
            records.Add(record);
        }

        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        IReadOnlyList<CaptureRecord> restored = queue.RestoreRecordsAsync(records).GetAwaiter().GetResult();
        if (restored.Count != count) throw new InvalidOperationException($"The probe library indexed {restored.Count} of {count} captures.");
        return root;
    }

    private static byte[] Png(int width, int height, int seed)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
            SyntheticImage.PhotoLikePixels(width, height, seed), width * 4);
        bitmap.Freeze();
        return ImageCodec.EncodePng(bitmap, null);
    }
}

/// <summary>Joins every section's scenario rows and summary at the top of the output folder.</summary>
internal static class ProbeJoin
{
    internal static void Write(string output, IEnumerable<string> sections)
    {
        var csv = new StringBuilder();
        var md = new StringBuilder();
        md.AppendLine("# 앱 체감 속도 측정");
        md.AppendLine();
        md.AppendLine($"측정 시각: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        md.AppendLine();
        foreach (string section in sections)
        {
            string folder = Path.Combine(output, section);
            string rows = Path.Combine(folder, "app-responsiveness.csv");
            if (File.Exists(rows))
            {
                string[] lines = File.ReadAllLines(rows, Encoding.UTF8);
                if (csv.Length == 0 && lines.Length > 0) csv.AppendLine("section," + lines[0]);
                foreach (string line in lines.Skip(1)) csv.AppendLine(section + "," + line);
            }

            string summary = Path.Combine(folder, "app-responsiveness-summary.md");
            if (File.Exists(summary))
            {
                // One heading level down: each section's title becomes a second-level heading.
                foreach (string line in File.ReadAllLines(summary, Encoding.UTF8))
                    md.AppendLine(line.StartsWith('#') ? "#" + line : line);
            }

            string receipt = Path.Combine(folder, "receipt-summary.md");
            if (File.Exists(receipt))
            {
                md.AppendLine();
                foreach (string line in File.ReadAllLines(receipt, Encoding.UTF8))
                    md.AppendLine(line.StartsWith('#') ? "#" + line : line);
            }
            else
            {
                md.AppendLine($"## {section}");
                md.AppendLine();
                md.AppendLine("결과 없음(probe-child-console.txt 참조).");
            }

            md.AppendLine();
        }

        File.WriteAllText(Path.Combine(output, "app-responsiveness.csv"), csv.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(output, "app-responsiveness-summary.md"), md.ToString(), new UTF8Encoding(false));
    }
}
