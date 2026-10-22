using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in runtime probe: measures how long the image editor keeps its UI thread from taking
/// input while it opens, draws, moves, types, blurs, undoes, autosaves, copies and saves.
/// </summary>
/// <remarks>
/// <para>
/// Never runs by default. Set <c>MYCAPTURE_EDITOR_PERF</c> to an output directory; the probe
/// writes CSV files and a summary there. It runs in a child test process (like the isolated
/// theme tests) because it creates a WPF <see cref="Application"/> with the product theme
/// dictionaries. <c>MYCAPTURE_EDITOR_PERF_SIZES</c> (for example <c>1920x1080</c>) narrows
/// the image sizes for a quick run.
/// </para>
/// <para>
/// The editor is wired as the app wires it after a capture: the original is persisted into an
/// isolated temporary library, the window gets an edit session, a commit service and an edit
/// draft session, and editor preferences are saved 500 ms after a change as App does. The
/// clipboard is never touched: copies run the real PNG encode and data object construction
/// and skip only the final OLE clipboard write.
/// </para>
/// <para>
/// Diagnosis switches: <c>MYCAPTURE_EDITOR_PERF_MODE=overlap</c> draws while the first autosave
/// encodes the base PNG (<c>MYCAPTURE_EDITOR_PERF_OVERLAP_ATTEMPTS</c> per size);
/// <c>MYCAPTURE_EDITOR_PERF_STACKS=1</c> samples the UI thread's stack during stalls;
/// <c>MYCAPTURE_EDITOR_PERF_JIT_ALL=1</c> runs the probe without ReadyToRun so framework frames
/// have names; <c>MYCAPTURE_EDITOR_PERF_HANG_KILL_S</c> sets when a stopped UI thread ends the
/// probe (default 90 s). A stall over 5 s always writes every thread's stack to hang-*.txt.
/// </para>
/// </remarks>
public sealed class EditorResponsivenessProbeTests
{
    internal const string OutputVariable = "MYCAPTURE_EDITOR_PERF";
    private const string ChildFlag = "MYCAPTURE_EDITOR_PERF_CHILD";

    [EditorPerfFact]
    public async Task MeasureEditorUiThreadBlocking()
    {
        // The output folder comes from the environment: confine every artifact to it.
        string output = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(OutputVariable)!);
        string receipt = DiagnosticOutputPaths.Child(output, "probe-completed.txt");
        if (Environment.GetEnvironmentVariable(ChildFlag) == "1")
        {
            EditorResponsivenessProbe.RunOnStaThread(output, receipt);
            return;
        }

        if (File.Exists(receipt)) File.Delete(receipt);
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
        start.ArgumentList.Add(typeof(EditorResponsivenessProbeTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(EditorResponsivenessProbeTests).FullName
            + "." + nameof(MeasureEditorUiThreadBlocking));
        start.ArgumentList.Add("--Logger:console;verbosity=normal");
        start.Environment[ChildFlag] = "1";
        // Diagnosis only: JIT every framework method so stack samples name WPF frames too.
        if (Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_PERF_JIT_ALL") == "1") start.Environment["DOTNET_ReadyToRun"] = "0";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using Process child = Process.Start(start) ?? throw new IOException("Could not start the editor probe process.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }

            string console = await stdout + Environment.NewLine + await stderr;
            File.WriteAllText(DiagnosticOutputPaths.Child(output, "probe-child-console.txt"), console);
            Assert.True(child.ExitCode == 0, console);
            Assert.True(File.Exists(receipt), "The probe child finished without writing its receipt.\n" + console);
            Assert.Equal("PASS", File.ReadAllText(receipt));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }
}

/// <summary>Skips unless <c>MYCAPTURE_EDITOR_PERF</c> names an output directory.</summary>
internal sealed class EditorPerfFactAttribute : FactAttribute
{
    public EditorPerfFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EditorResponsivenessProbeTests.OutputVariable)))
            Skip = "Opt-in editor responsiveness probe: set MYCAPTURE_EDITOR_PERF to an output directory.";
    }
}

/// <summary>
/// Drives the editor from a background "input" thread while the STA thread runs its dispatcher,
/// as the app's message loop does, and records how long input waited.
/// </summary>
internal sealed class EditorResponsivenessProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const double PointerPaceMs = 8;
    private const double KeyPaceMs = 30;

    private readonly string _output;
    private readonly ProbeRecorder _recorder = new();
    private readonly List<string> _environment = [];
    private readonly List<string> _substitutions = [];
    private Dispatcher _ui = null!;
    private PreciseDelay _delay = null!;
    private string _size = "";
    private string _variant = "";

    private EditorResponsivenessProbe(string output) => _output = output;

    internal static void RunOnStaThread(string output, string receipt)
    {
        // Names JIT-compiled code for stack samples; started before any editor code is compiled.
        using var jit = new JitMethodMap();
        StaTestHost.Run(() =>
        {
            try
            {
                new EditorResponsivenessProbe(output) { _jit = jit }.Run();
                File.WriteAllText(receipt, "PASS");
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
    }

    private JitMethodMap? _jit;

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
        ThemeService.ApplyFromSettings(new AppSettings().General);

        Exception? failure = null;
        bool sampleStacks = Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_PERF_STACKS") == "1";
        _environment.Add("UI-thread stack samples during stalls: " + (sampleStacks
            ? "on (MYCAPTURE_EDITOR_PERF_STACKS=1)" : "off; all-thread stacks are written only for a stall over 5 s"));
        _environment.Add("ReadyToRun: " + (Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") == "0" ? "disabled for the probe process (framework frames are named)" : "default"));
        _environment.Add("Mode: " + (Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_PERF_MODE") == "overlap"
            ? "overlap (draw while the first autosave encodes the base PNG)" : "scenarios"));
        var hang = new HangContext(_output, NativeStacks.CurrentThreadId(), _jit, () => WriteReport(new TimeoutException("The UI thread stopped responding; the probe wrote hang-*.txt and ended itself.")));
        using (var tracker = new DispatcherOperationTracker(_ui, _recorder))
        using (var heartbeat = new UiHeartbeat(_ui, _recorder, tracker, hang, sampleStacks))
        {
            _environment.Add("Heartbeat timer: " + (heartbeat.HighResolution ? "high-resolution waitable timer, 8 ms" : "Thread.Sleep(8) fallback"));
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
                Name = "Editor probe input driver",
            };
            driver.Start();
            Dispatcher.PushFrame(frame);
            driver.Join();
            heartbeat.Stop();
        }

        AnnotationEditorPreferences.Read = null;
        AnnotationEditorPreferences.ReadShowSourceWindowTitle = null;
        AnnotationEditorPreferences.Write = null;
        WriteReport(failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private readonly object _reportGate = new();

    /// <summary>Writes every report from what has been recorded so far; called after each size too.</summary>
    private void WriteReport(Exception? failure)
    {
        lock (_reportGate)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    new ProbeReport(_output, _recorder, _environment, _substitutions, failure).Write();
                    return;
                }
                catch (InvalidOperationException)
                {
                    // A late heartbeat sample changed a list while it was being written; try again.
                }
            }
        }
    }

    // ---- Script ------------------------------------------------------------------------------

    private void Script()
    {
        CaptureEnvironment();
        bool overlap = Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_PERF_MODE") == "overlap";
        foreach ((int width, int height) in Sizes())
        {
            if (overlap) RunOverlapCheck(width, height);
            else RunSize(width, height);
            WriteReport(null);
        }
    }

    /// <summary>
    /// Draws while the first autosave encodes the full-resolution base PNG: the user picks a tool
    /// and starts drawing at once, so the 750 ms draft debounce fires mid-stroke. Each attempt uses
    /// a new bitmap object, so the draft PNG cache misses as it does for every new capture.
    /// </summary>
    private void RunOverlapCheck(int width, int height)
    {
        _size = $"{width}x{height}";
        _variant = "first-autosave-overlap";
        int attempts = int.TryParse(Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_PERF_OVERLAP_ATTEMPTS"), out int requested) ? requested : 3;
        byte[] pixels = SyntheticImage.PhotoLikePixels(width, height, seed: width * 31 + height);
        string root = TestRecycleBin.CreateTempSubdirectory("mced-").FullName;
        AppEnvironment app = Ui(() => new AppEnvironment(root));
        try
        {
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                BitmapSource image = Ui(() =>
                {
                    BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
                    bitmap.Freeze();
                    return bitmap;
                });
                CaptureRecord record = WaitTask(Ui(() => app.Persistence.PersistOriginalAsync(image, 1.0, "Probe overlap", "Probe")), "persist original (overlap)");
                CaptureEditSession session = Ui(() => app.Commit.BeginEditSession(record));
                EditorUnderTest ed = OpenEditor(app, image, record, session, null, null, $"open (attempt {attempt})");
                try
                {
                    ProbeScenario s = Begin($"draw during first autosave, attempt {attempt}");
                    Op(s, "tool Pen (DraftChanged)", () => ed.Editor.ShortcutActions["Pen"](), idle: false);
                    Stroke(s, ed, "pen", Wave(width, height, 500));
                    End(s);
                    WaitTask(Ui(() => ed.Draft!.LastWrite), "overlapping draft write");
                    s.Notes = "completed without a deadlock";
                    Ui(() => ed.Window.Close());
                    Wait(ed.Closed, "editor close (overlap)");
                    WaitTask(Ui(EditDraftSession.WaitForFinalizationsAsync), "draft finalization (overlap)");
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
            _recorder.Step = "setup: full GC between sizes";
            Ui(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            try
            {
                TestRecycleBin.DeleteDirectory(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _environment.Add($"Could not remove isolated data root {root}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<(int Width, int Height)> Sizes()
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_PERF_SIZES");
        if (string.IsNullOrWhiteSpace(requested))
        {
            return [(1920, 1080), (3840, 2160), (7680, 2160)];
        }

        return requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(size => size.Split('x'))
            .Select(parts => (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture)))
            .ToArray();
    }

    private void CaptureEnvironment()
    {
        Ui(() =>
        {
            MonitorInfo primary = MonitorEnumerator.GetPrimary();
            _environment.Add($"OS: {Environment.OSVersion}; logical processors: {Environment.ProcessorCount}; runtime: {RuntimeInformation.FrameworkDescription}");
            _environment.Add($"GC: server={System.Runtime.GCSettings.IsServerGC}, latency={System.Runtime.GCSettings.LatencyMode}");
            _environment.Add($"WPF render tier: {RenderCapability.Tier >> 16}; primary monitor: {primary.Bounds.Width}x{primary.Bounds.Height} px, scale {primary.ScaleFactor.ToString(CultureInfo.InvariantCulture)}");
            _environment.Add("DispatcherOperation fields: " + string.Join(", ", typeof(DispatcherOperation)
                .GetFields(Private).Select(field => field.FieldType.Name + " " + field.Name)));
        });
    }

    private void RunSize(int width, int height)
    {
        _size = $"{width}x{height}";
        _variant = "setup";
        byte[] pixels = SyntheticImage.PhotoLikePixels(width, height, seed: width * 31 + height);
        _recorder.Step = "setup: create test bitmap";
        BitmapSource image = Ui(() =>
        {
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        });
        pixels = [];
        string root = TestRecycleBin.CreateTempSubdirectory("mced-").FullName;
        AppEnvironment app = Ui(() => new AppEnvironment(root));
        try
        {
            RunBaseVariant(app, image);
            RunManyVariant(app, image);
            if (width >= 1200 && height >= 800) RunHotPathsVariant(app, image);
            RunLibraryVariant(app, image);
        }
        finally
        {
            Ui(app.Dispose);
            _variant = "setup";
            // Outside every measured scenario: drop this size's heap before the next size starts.
            _recorder.Step = "setup: full GC between sizes";
            Ui(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            try
            {
                TestRecycleBin.DeleteDirectory(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _environment.Add($"Could not remove isolated data root {root}: {ex.Message}");
            }
        }
    }

    private void RunBaseVariant(AppEnvironment app, BitmapSource image)
    {
        _variant = "base";
        int w = image.PixelWidth, h = image.PixelHeight;

        ProbeScenario persist = Begin("persist-original");
        Task<CaptureRecord> persisting = Op(persist, "CapturePersistenceService.PersistOriginalAsync",
            () => app.Persistence.PersistOriginalAsync(image, 1.0, "Probe", "Probe"));
        long persistStart = Stopwatch.GetTimestamp();
        CaptureRecord record = WaitTask(persisting, "persist original");
        persist.Extra["persist-wall-ms"] = Ms(persistStart);
        CaptureEditSession session = Op(persist, "CaptureCommitService.BeginEditSession", () => app.Commit.BeginEditSession(record));
        End(persist);
        string original = Path.Combine(app.Queue.GetDirectory(record), CaptureFileNames.Original);
        if (File.Exists(original))
        {
            long bytes = new FileInfo(original).Length;
            persist.Notes = $"original.png {bytes / 1024} KiB ({100.0 * bytes / (4.0 * w * h):0}% of raw BGRA)";
        }

        EditorUnderTest ed = OpenEditor(app, image, record, session, null, null, "open");
        try
        {
            Tool(ed, "Pen");
            // Selecting a tool raises DraftChanged (RefreshCrop), so 750 ms later the first draft
            // encodes the full-resolution base PNG. Drawing during that encode deadlocks the UI
            // thread (see the overlap mode); here the user pauses until it has finished.
            Autosave(ed, "first tool selection (first full-resolution draft encode)");
            Gesture(ed, "pen-stroke-500", "pen", Wave(w, h, 500));
            Autosave(ed, "pen-stroke-500");

            Tool(ed, "Rectangle");
            Gesture(ed, "rectangle", "rectangle", Line(new PointD(w * 0.20, h * 0.20), new PointD(w * 0.35, h * 0.40), 32));
            Tool(ed, "Arrow");
            Gesture(ed, "arrow", "arrow", Line(new PointD(w * 0.50, h * 0.18), new PointD(w * 0.70, h * 0.33), 32));
            Autosave(ed, "rectangle+arrow");

            Tool(ed, "Select");
            // The rectangle's left edge, so the press hits its stroke.
            PointD edge = new(w * 0.20, h * 0.30);
            Gesture(ed, "select-drag-200", "rectangle drag", Line(edge, new PointD(edge.X + w * 0.10, edge.Y + h * 0.12), 202));
            Autosave(ed, "select-drag-200");

            TypeText(ed);
            Autosave(ed, "text-type-100");

            CoverScenario(ed, "Blur", "blur", new PointD(w * 0.55, h * 0.55), new PointD(w * 0.70, h * 0.75));
            CoverScenario(ed, "Mosaic", "mosaic", new PointD(w * 0.25, h * 0.66), new PointD(w * 0.40, h * 0.86));

            Tool(ed, "Rectangle");
            ProbeScenario rects = Begin("add-40-rectangles");
            for (int i = 0; i < 40; i++)
            {
                double x = w * (0.74 + 0.05 * (i % 4)), y = h * (0.05 + 0.045 * (i / 4));
                Stroke(rects, ed, "small rectangle", Line(new PointD(x, y), new PointD(x + w * 0.035, y + h * 0.03), 6));
            }
            End(rects);
            Autosave(ed, "add-40-rectangles");

            UndoRedo(ed);
            Autosave(ed, "undo-50+redo-50");

            ProbeScenario rotate = Begin("rotate right+left (extra)");
            Op(rotate, "RotateRight", () => ed.Editor.ShortcutActions["RotateRight"](), idle: true);
            _delay.Wait(PointerPaceMs);
            Op(rotate, "RotateLeft", () => ed.Editor.ShortcutActions["RotateLeft"](), idle: true);
            End(rotate);
            Autosave(ed, "rotate");

            ResizeWindow(ed);
            Breakdown(ed);
            TextAndEncoderBreakdown(ed);
            Copy(ed);
            Done(ed, record);
        }
        finally
        {
            CloseIfOpen(ed);
            Ui(session.Dispose);
        }
    }

    private void RunManyVariant(AppEnvironment app, BitmapSource image)
    {
        _variant = "200-items";
        int w = image.PixelWidth, h = image.PixelHeight;
        CaptureRecord record = WaitTask(Ui(() => app.Persistence.PersistOriginalAsync(image, 1.0, "Probe 200", "Probe")), "persist original (200)");
        CaptureEditSession session = Ui(() => app.Commit.BeginEditSession(record));
        _recorder.Step = "setup: build 200-item document";
        (AnnotationDocument document, Dictionary<string, BitmapSource> assets, RectD target) = Ui(() => ManyItems.Create(w, h));
        EditorUnderTest ed = OpenEditor(app, image, record, session, document, assets, "open-200");
        try
        {
            Tool(ed, "Select");
            Autosave(ed, "first tool selection (first draft encode of 9 image assets)");
            PointD edge = new(target.Left, target.Top + target.Height / 2);
            Gesture(ed, "select-drag-200", "target drag", Line(edge, new PointD(edge.X + w * 0.08, edge.Y + h * 0.10), 202));
            Autosave(ed, "select-drag-200");

            Tool(ed, "Pen");
            Gesture(ed, "pen-stroke-500", "pen", Wave(w, h, 500));
            Autosave(ed, "pen-stroke-500");

            Tool(ed, "Select");
            ProbeScenario all = Begin("select-all-drag-100");
            Op(all, "SelectAll", () => ed.Editor.ShortcutActions["SelectAll"](), idle: true);
            int selected = Ui(() => ed.Controller.SelectedItems.Count);
            PointD press = Ui(() =>
            {
                RectangleAnnotation top = ed.Controller.Document.Items.OfType<RectangleAnnotation>().Last(item => item.Rect.Width > w * 0.1);
                return new PointD(top.Rect.Left, top.Rect.Top + top.Rect.Height / 2);
            });
            Stroke(all, ed, "select-all drag", Line(press, new PointD(press.X + w * 0.03, press.Y + h * 0.03), 102));
            End(all);
            all.Notes = $"{selected} items selected and dragged together";
            Autosave(ed, "select-all-drag-100");

            ProbeScenario nudge = Begin("nudge-50 (history for undo)");
            Op(nudge, "StepBack (clear selection)", () => ed.Editor.ShortcutActions["Cancel"](), idle: true);
            PointD targetPress = Ui(() =>
            {
                RectangleAnnotation top = ed.Controller.Document.Items.OfType<RectangleAnnotation>().Last(item => item.Rect.Width > w * 0.1);
                return new PointD(top.Rect.Left, top.Rect.Top + top.Rect.Height / 2);
            });
            Op(nudge, "select target", () =>
            {
                Point at = ed.Editor.ViewportPointOf(targetPress);
                ed.Editor.SimulatePointerDown(at);
                ed.Editor.SimulatePointerUp(at);
            }, idle: true);
            for (int i = 0; i < 50; i++)
            {
                _delay.Wait(PointerPaceMs);
                Op(nudge, "Nudge right", () => ed.Editor.ShortcutActions["Right"](), idle: true);
            }
            End(nudge);
            Autosave(ed, "nudge-50");

            UndoRedo(ed);
            Autosave(ed, "undo-50+redo-50");
            Breakdown(ed);
            TextAndEncoderBreakdown(ed);

            ProbeScenario close = Begin("close with unsaved edits (draft flush)");
            Op(close, "Window.Close", () => ed.Window.Close(), idle: false);
            Wait(ed.Closed, "editor close (200)");
            WaitTask(Ui(EditDraftSession.WaitForFinalizationsAsync), "draft finalization (200)");
            End(close);
        }
        finally
        {
            CloseIfOpen(ed);
            Ui(session.Dispose);
        }
    }

    /// <summary>Large-stroke cache, selection and mosaic work on a 300-annotation document.</summary>
    private void RunHotPathsVariant(AppEnvironment app, BitmapSource image)
    {
        _variant = "300-items-long-ink-mosaic";
        int w = image.PixelWidth, h = image.PixelHeight;
        CaptureRecord record = WaitTask(Ui(() => app.Persistence.PersistOriginalAsync(image, 1.0,
            "Probe 300", "Probe")), "persist original (300)");
        CaptureEditSession session = Ui(() => app.Commit.BeginEditSession(record));
        _recorder.Step = "setup: 299 annotations including 30 strokes of 8192 points";
        AnnotationDocument document = Ui(() =>
        {
            var doc = AnnotationDocument.CreateFor(w, h);
            for (int i = 0; i < 30; i++)
            {
                int row = i;
                doc.Add(new PenAnnotation
                {
                    Points = [.. Enumerable.Range(0, 8192).Select(p => new PointD(
                        20 + p * (w - 40.0) / 8191, 20 + row * (h - 80.0) / 30 + Math.Sin(p * .025) * 12))],
                    StrokeThickness = 3,
                });
            }
            for (int i = 0; i < 268; i++)
                doc.Add(new RectangleAnnotation
                {
                    Rect = new RectD(20 + i % 20 * (w - 60.0) / 20, 20 + i / 20 * (h - 60.0) / 14, 24, 18),
                    StrokeThickness = 2,
                });
            doc.Add(new TextAnnotation { IsStepNumber = true, Text = "12", Rect = new RectD(30, 30, 32, 32) });
            return doc;
        });
        EditorUnderTest ed = OpenEditor(app, image, record, session, document, null, "open-299-long-ink");
        try
        {
            Tool(ed, "Mosaic");
            Autosave(ed, "first tool selection (long ink)");
            ProbeScenario mosaic = Begin("mosaic-1200x800");
            Op(mosaic, "ApplyMosaic 1200x800", () => ed.Editor.ApplyMosaic(new RectD(0, 0, 1200, 800), 12), idle: true);
            End(mosaic);
            Ui(() =>
            {
                Assert.Equal(300, ed.Controller.Document.Items.Count);
                var patch = Assert.IsType<ImageAnnotation>(ed.Controller.Document.Items[^1]);
                Assert.Equal(new RectD(0, 0, 1200, 800), patch.Rect);
            });
            Autosave(ed, "mosaic-1200x800");
            Breakdown(ed);

            Tool(ed, "Select");
            ProbeScenario selection = Begin("selection-bounds-300");
            Op(selection, "SelectAll", () => ed.Controller.SelectAll(), idle: true);
            for (int i = 0; i < 100; i++)
                Op(selection, "SelectionBounds", () => { _ = ed.Controller.SelectionBounds; }, idle: false);
            End(selection);

            ProbeScenario hit = Begin("hit-tests-300");
            for (int i = 0; i < 100; i++)
            {
                int sample = i;
                Op(hit, "point and region hit tests", () =>
                {
                    _ = ed.Controller.Document.HitTest(new PointD(w - 10, sample * h / 100.0), 6);
                    _ = ed.Controller.Document.HitTestRegion(new RectD(sample, sample, 400, 300));
                }, idle: false);
            }
            End(hit);

            Tool(ed, "StepNumber");
            ProbeScenario steps = Begin("step-number-300");
            for (int i = 0; i < 100; i++)
                Op(steps, "NextStepNumber", () => { _ = ed.Controller.NextStepNumber; }, idle: false);
            End(steps);

            // Alt reveal and tool changes on a busy document: both used to reflow the editor.
            ProbeScenario alt = Begin("alt-toggle-40");
            for (int i = 0; i < 40; i++)
            {
                bool reveal = i % 2 == 0;
                Op(alt, reveal ? "Alt reveal" : "Alt hide", () => ShortcutPresentation.SetAltRevealed(ed.Window, reveal), idle: true);
            }
            End(alt);

            string[] tools = ["Pen", "Rectangle", "Text", "Select"];
            ProbeScenario switches = Begin("tool-switch-40");
            for (int i = 0; i < 40; i++)
            {
                string tool = tools[i % tools.Length];
                Op(switches, "tool " + tool, () => ed.Editor.ShortcutActions[tool](), idle: true);
            }
            End(switches);
            Done(ed, record);
        }
        finally
        {
            CloseIfOpen(ed);
            Ui(session.Dispose);
        }
    }

    /// <summary>
    /// The same capture, edit and Done, in a library of 1000 captures (the new-install retention
    /// limit): Done publishes the whole library index from the UI thread.
    /// </summary>
    private void RunLibraryVariant(AppEnvironment app, BitmapSource image)
    {
        _variant = "library-1000";
        int w = image.PixelWidth, h = image.PixelHeight;
        if (Ui(() => app.Queue.Count) < 1000)
        {
            // Setup, unmeasured: 1000 indexed captures (metadata only) and no eviction.
            _recorder.Step = "setup: library of 1000 captures";
            Ui(() => app.Settings.Queue.MaxItems = 5000);
            CaptureRecord[] records = Ui(() => Enumerable.Range(0, 1000).Select(i =>
            {
                CaptureRecord library = CapturePersistenceService.CreatePendingRecord(image, 1.0, $"Library capture {i}", "Probe");
                Directory.CreateDirectory(app.Queue.GetDirectory(library));
                return library;
            }).ToArray());
            WaitTask(Ui(() => app.Queue.RestoreRecordsAsync(records)), "library of 1000 captures");
        }

        ProbeScenario persist = Begin("persist-original");
        Task<CaptureRecord> persisting = Op(persist, "CapturePersistenceService.PersistOriginalAsync",
            () => app.Persistence.PersistOriginalAsync(image, 1.0, "Probe library", "Probe"));
        CaptureRecord record = WaitTask(persisting, "persist original (library)");
        CaptureEditSession session = Op(persist, "CaptureCommitService.BeginEditSession", () => app.Commit.BeginEditSession(record));
        End(persist);
        persist.Notes = $"library records: {Ui(() => app.Queue.Count)}";
        EditorUnderTest ed = OpenEditor(app, image, record, session, null, null, "open");
        try
        {
            Tool(ed, "Rectangle");
            Gesture(ed, "rectangle", "rectangle", Line(new PointD(w * 0.20, h * 0.20), new PointD(w * 0.35, h * 0.40), 32));
            Done(ed, record);
        }
        finally
        {
            CloseIfOpen(ed);
            Ui(session.Dispose);
        }
    }

    // ---- Editor ------------------------------------------------------------------------------

    private EditorUnderTest OpenEditor(AppEnvironment app, BitmapSource image, CaptureRecord record, CaptureEditSession session,
        AnnotationDocument? document, IReadOnlyDictionary<string, BitmapSource>? assets, string name)
    {
        var ed = new EditorUnderTest { Width = image.PixelWidth, Height = image.PixelHeight };
        MonitorInfo primary = Ui(MonitorEnumerator.GetPrimary);
        ProbeScenario open = Begin(name);
        long started = 0, constructed = 0, shown = 0;
        Op(open, "new AnnotationEditorWindow + Show", () =>
        {
            started = Stopwatch.GetTimestamp();
            var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
            var window = new AnnotationEditorWindow(new FrozenFrame(image, region, null, 0), region, image,
                initialDocument: document, initialAssets: assets, sourceWindowTitle: "Probe", pointerMonitor: primary)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000, Top = -30000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            constructed = Stopwatch.GetTimestamp();
            window.DraftRecordSource = () => (record.Id, session.ExpectedContentRevision);
            // The app wires a fresh draft session in InitializeDraftRecovery when App.DraftStore
            // exists. Tests have no App, so wire the same session type against the isolated
            // store and clear the "recovered draft" flag that this entry point sets.
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
            shown = Stopwatch.GetTimestamp();
            open.Extra["show-ms"] = Ms(showing, shown);
            ed.Window = window;
            ed.Editor = window.Editor;
            ed.Controller = Field<AnnotationEditorController>(window.Editor, "_controller");
            ed.Draft = Field<EditDraftSession?>(window, "_draftSession");
        }, idle: false);
        Wait(ed.Rendered, "first editor render");
        open.Extra["ctor-ms"] = Ms(started, constructed);
        open.Extra["first-render-ms"] = Ms(started, ed.RenderedAt);
        if (document?.Items.OfType<BlurAnnotation>().Any() == true)
        {
            long blurStart = Stopwatch.GetTimestamp();
            WaitTask(Ui(() => Field<AnnotationBlurCache>(ed.Editor, "_blurCache").Pending), "blur frames");
            open.Extra["blur-frame-ready-ms"] = Ms(blurStart);
        }

        End(open);
        open.Extra["open-to-idle-ms"] = Ms(started, open.EndTimestamp);
        (double windowWidth, double windowHeight, double dpi) = Ui(() =>
            (ed.Window.ActualWidth, ed.Window.ActualHeight, VisualTreeHelper.GetDpi(ed.Window).PixelsPerDip));
        open.Notes = $"window {windowWidth:0}x{windowHeight:0} DIP at {dpi:0.##}x, items={document?.Items.Count ?? 0}";
        return ed;
    }

    private void Tool(EditorUnderTest ed, string tool)
    {
        _recorder.Step = "tool " + tool;
        Ui(() => ed.Editor.ShortcutActions[tool]());
        WaitIdle();
    }

    private void Gesture(EditorUnderTest ed, string scenario, string label, IReadOnlyList<PointD> path)
    {
        ProbeScenario s = Begin(scenario);
        Stroke(s, ed, label, path);
        End(s);
    }

    /// <summary>
    /// Press, move and release through the editor's own pointer handling
    /// (<see cref="AnnotationEditorControl.SimulatePointerDown"/> and friends, the route the
    /// tutorial generator uses), one dispatcher input operation per event, 8 ms apart.
    /// </summary>
    private void Stroke(ProbeScenario s, EditorUnderTest ed, string label, IReadOnlyList<PointD> path)
    {
        PointD first = path[0];
        Op(s, label + " down", () => ed.Editor.SimulatePointerDown(ed.Editor.ViewportPointOf(first)), idle: false);
        for (int i = 1; i < path.Count - 1; i++)
        {
            _delay.Wait(PointerPaceMs);
            PointD point = path[i];
            Op(s, label + " move", () => ed.Editor.SimulatePointerMove(ed.Editor.ViewportPointOf(point), leftPressed: true), idle: false);
        }

        _delay.Wait(PointerPaceMs);
        PointD last = path[^1];
        Op(s, label + " up", () => ed.Editor.SimulatePointerUp(ed.Editor.ViewportPointOf(last)), idle: false);
    }

    private void TypeText(EditorUnderTest ed)
    {
        ProbeScenario s = Begin("text-type-100");
        Op(s, "tool Text", () => ed.Editor.ShortcutActions["Text"](), idle: true);
        PointD at = new(ed.Width * 0.08, ed.Height * 0.62);
        Op(s, "text click", () =>
        {
            Point point = ed.Editor.ViewportPointOf(at);
            ed.Editor.SimulatePointerDown(point);
            ed.Editor.SimulatePointerUp(point);
        }, idle: true);
        TextBox box = Ui(() => Field<TextBox?>(ed.Editor, "_activeTextBox"))
            ?? throw new InvalidOperationException("The text tool did not open a live text box.");
        const string Sample = "편집 응답성 측정 MyCapture 0123456789 텍스트 입력 지연 확인 ";
        string text = string.Concat(Enumerable.Repeat(Sample, 4))[..100];
        bool textInputRoute = true;
        for (int i = 0; i < text.Length; i++)
        {
            _delay.Wait(KeyPaceMs);
            string ch = text[i].ToString();
            bool route = textInputRoute;
            Op(s, "type character", () =>
            {
                if (route) TypeCharacter(box, ch);
                else
                {
                    box.SelectedText = ch;
                    box.CaretIndex = box.Text.Length;
                }
            }, idle: false);
            if (i == 0 && WaitIdle() > 0 && Ui(() => box.Text.Length) == 0)
            {
                textInputRoute = false;
                _substitutions.Add("Typing: a raised TextInput event did not reach the live TextBox, so characters were inserted with TextBox.SelectedText (same TextChanged handler).");
                Op(s, "type character (fallback)", () => { box.SelectedText = ch; box.CaretIndex = box.Text.Length; }, idle: false);
            }
        }

        int typed = Ui(() => box.Text.Length);
        _delay.Wait(KeyPaceMs);
        Op(s, "commit text (canvas press)", () =>
        {
            Point point = ed.Editor.ViewportPointOf(new PointD(ed.Width * 0.5, ed.Height * 0.92));
            ed.Editor.SimulatePointerDown(point);
            ed.Editor.SimulatePointerUp(point);
        }, idle: true);
        End(s);
        s.Notes = $"typed {typed} characters via {(textInputRoute ? "TextCompositionManager.TextInput events" : "TextBox.SelectedText")}, {KeyPaceMs} ms apart";
    }

    private static void TypeCharacter(TextBox box, string text)
    {
        var composition = new TextComposition(InputManager.Current, box, text);
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.TextInputEvent,
        };
        box.RaiseEvent(args);
    }

    private void CoverScenario(EditorUnderTest ed, string tool, string name, PointD from, PointD to)
    {
        int w = ed.Width, h = ed.Height;
        ProbeScenario create = Begin(name + "-create");
        Op(create, "tool " + tool, () => ed.Editor.ShortcutActions[tool](), idle: true);
        Stroke(create, ed, name + " region", Line(from, to, 22));
        if (tool == "Blur")
        {
            long blurStart = Stopwatch.GetTimestamp();
            WaitTask(Ui(() => Field<AnnotationBlurCache>(ed.Editor, "_blurCache").Pending), "blur frame");
            create.Extra["blur-frame-ready-ms"] = Ms(blurStart);
        }

        End(create);
        Autosave(ed, name + "-create");

        Tool(ed, "Select");
        RectD bounds = SelectedBounds(ed, name);
        PointD center = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        Gesture(ed, name + "-drag-100", name + " drag", Line(center, new PointD(center.X + w * 0.05, center.Y + h * 0.04), 102));
        bounds = SelectedBounds(ed, name);
        PointD corner = new(bounds.Right, bounds.Bottom);
        Gesture(ed, name + "-resize-100", name + " resize", Line(corner, new PointD(corner.X + w * 0.06, corner.Y + h * 0.04), 102));
        Autosave(ed, name + "-drag+resize");
    }

    private RectD SelectedBounds(EditorUnderTest ed, string what) => Ui(() =>
        ed.Controller.Selected?.Bounds ?? throw new InvalidOperationException($"The {what} layer is not selected."));

    private void UndoRedo(EditorUnderTest ed)
    {
        int depth = Ui(() => ed.Controller.Undo.UndoCount);
        ProbeScenario undo = Begin("undo-50");
        for (int i = 0; i < 50; i++)
        {
            _delay.Wait(PointerPaceMs);
            Op(undo, "Undo", () => ed.Editor.ShortcutActions["Undo"](), idle: true);
        }

        End(undo);
        undo.Notes = $"history depth before undo: {depth}";
        ProbeScenario redo = Begin("redo-50");
        for (int i = 0; i < 50; i++)
        {
            _delay.Wait(PointerPaceMs);
            Op(redo, "Redo", () => ed.Editor.ShortcutActions["Redo"](), idle: true);
        }

        End(redo);
        redo.Notes = $"history depth after redo: {Ui(() => ed.Controller.Undo.UndoCount)}";
    }

    private void ResizeWindow(EditorUnderTest ed)
    {
        (double width, double height) = Ui(() => (ed.Window.Width, ed.Window.Height));
        ProbeScenario s = Begin("window-resize-20 (zoom substitute)");
        for (int i = 1; i <= 20; i++)
        {
            double factor = i <= 10 ? 1 - 0.04 * i : 0.6 + 0.04 * (i - 10);
            _delay.Wait(PointerPaceMs);
            Op(s, "resize window", () =>
            {
                ed.Window.Width = width * factor;
                ed.Window.Height = height * factor;
            }, idle: true);
        }

        End(s);
        s.Notes = "The image editor has no zoom or pan; it always fits the image to the viewport. Resizing the window changes that fit scale and re-renders, the closest equivalent.";
    }

    private void Breakdown(EditorUnderTest ed)
    {
        ProbeScenario s = Begin("breakdown (direct calls)");
        FrameworkElement surface = Ui(() => Field<FrameworkElement>(ed.Editor, "_surface"));
        for (int i = 0; i < 5; i++)
        {
            long allocated = 0;
            double ms = Op(s, "surface OnRender", () =>
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                surface.InvalidateVisual();
                surface.UpdateLayout();
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }, idle: true);
            _recorder.AddBreakdown(_size, _variant, "AnnotationEditorSurface.OnRender (InvalidateVisual + UpdateLayout) ms", ms);
            _recorder.AddBreakdown(_size, _variant, "AnnotationEditorSurface.OnRender allocated KB", allocated / 1024.0);
            _recorder.AddBreakdown(_size, _variant, "Frame after InvalidateVisual: op start to ApplicationIdle ms", s.Idle[^1]);
        }

        if (Ui(() => ed.Controller.Document.Items.Count) >= 50)
        {
            // Which annotation kinds the surface's renderer spends its time and allocations on.
            _recorder.Step = "breakdown: renderer by annotation kind";
            Ui(() =>
            {
                var renderer = Field<AnnotationRenderer>(surface, "_renderer");
                BitmapSource baseBitmap = ed.Editor.DisplayedBitmap;
                double pixelsPerDip = VisualTreeHelper.GetDpi(surface).PixelsPerDip;
                AnnotationDocument document = ed.Controller.Document;
                foreach (IGrouping<string, AnnotationItem> kind in document.Items.GroupBy(KindOf).Append(document.Items.GroupBy(_ => "all items").Single()))
                {
                    var subset = AnnotationDocument.CreateFor(document.CanvasWidth, document.CanvasHeight);
                    foreach (AnnotationItem item in kind) subset.Items.Add(item);
                    bool blur = kind.Key is "Blur" or "all items";
                    for (int i = 0; i < 3; i++)
                    {
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        long begin = Stopwatch.GetTimestamp();
                        var visual = new DrawingVisual();
                        using (DrawingContext dc = visual.RenderOpen())
                            renderer.Render(dc, subset, pixelsPerDip, null, blur ? baseBitmap : null);
                        double ms = Ms(begin);
                        double kb = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024.0;
                        _recorder.AddBreakdown(_size, _variant, $"AnnotationRenderer.Render {kind.Count()} × {kind.Key} ms", ms);
                        _recorder.AddBreakdown(_size, _variant, $"AnnotationRenderer.Render {kind.Count()} × {kind.Key} allocated KB", kb);
                    }
                }
            });
        }

        for (int i = 0; i < 3; i++)
        {
            Task<DraftSnapshot?>? snapshot = null;
            long begin = Stopwatch.GetTimestamp();
            double sync = Op(s, "CaptureDraftAsync (UI part)", () => snapshot = ed.Editor.CaptureDraftAsync(), idle: false);
            WaitTask(snapshot!, "draft snapshot");
            _recorder.AddBreakdown(_size, _variant, "AnnotationEditorControl.CaptureDraftAsync: UI-thread part", sync);
            _recorder.AddBreakdown(_size, _variant, "AnnotationEditorControl.CaptureDraftAsync: until PNG files ready", Ms(begin));
            double json = Op(s, "snapshot + ToJson", () => ed.Editor.CreateCurrentImageSnapshot().ToJson(), idle: false);
            _recorder.AddBreakdown(_size, _variant, "CreateCurrentImageSnapshot + ToJson", json);
        }

        End(s);
    }

    /// <summary>
    /// Splits the outlined-caption cost into the two calls AnnotationRenderer.DrawText makes per
    /// caption per paint, and times the draft encoder's first step (the window in which it holds
    /// the base bitmap's lock) on a background STA while the UI thread is idle.
    /// </summary>
    private void TextAndEncoderBreakdown(EditorUnderTest ed)
    {
        _recorder.Step = "breakdown: caption formatting";
        Ui(() =>
        {
            var captions = ed.Controller.Document.Items.OfType<TextAnnotation>().Where(text => text.HasOutline).ToArray();
            if (captions.Length == 0) return;
            double pixelsPerDip = VisualTreeHelper.GetDpi(ed.Editor).PixelsPerDip;
            for (int round = 0; round < 3; round++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long begin = Stopwatch.GetTimestamp();
                var formatted = captions.Select(text => TextAnnotationLayout.Format(text, pixelsPerDip)).ToArray();
                double format = Ms(begin);
                double formatKb = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024.0;
                allocated = GC.GetAllocatedBytesForCurrentThread();
                begin = Stopwatch.GetTimestamp();
                for (int i = 0; i < formatted.Length; i++) _ = formatted[i].BuildGeometry(new Point(captions[i].Rect.Left, captions[i].Rect.Top));
                double geometry = Ms(begin);
                double geometryKb = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024.0;
                _recorder.AddBreakdown(_size, _variant, $"TextAnnotationLayout.Format × {captions.Length} outlined captions ms", format);
                _recorder.AddBreakdown(_size, _variant, $"TextAnnotationLayout.Format × {captions.Length} outlined captions allocated KB", formatKb);
                _recorder.AddBreakdown(_size, _variant, $"FormattedText.BuildGeometry × {captions.Length} outlined captions ms", geometry);
                _recorder.AddBreakdown(_size, _variant, $"FormattedText.BuildGeometry × {captions.Length} outlined captions allocated KB", geometryKb);
            }
        });

        _recorder.Step = "breakdown: draft encoder copy window";
        BitmapSource baseBitmap = Ui(() => ed.Editor.DisplayedBitmap);
        double copy = WaitTask(MyCapture.App.Threading.StaThreadTask.RunAsync(() =>
        {
            long begin = Stopwatch.GetTimestamp();
            var independent = new WriteableBitmap(baseBitmap);
            double elapsed = Ms(begin);
            independent.Freeze();
            return elapsed;
        }, "probe WriteableBitmap timing"), "WriteableBitmap timing");
        _recorder.AddBreakdown(_size, _variant, "new WriteableBitmap(base) on a fresh STA (holds the base bitmap lock) ms", copy);
        long started = Stopwatch.GetTimestamp();
        byte[] png = WaitTask(MyCapture.App.Threading.StaThreadTask.RunAsync(() => AnnotationEditorControl.EncodeDraftBitmap(baseBitmap),
            "probe draft encode timing"), "draft encode timing");
        _recorder.AddBreakdown(_size, _variant, "AnnotationEditorControl.EncodeDraftBitmap(base) total ms", Ms(started));
        _recorder.AddBreakdown(_size, _variant, "AnnotationEditorControl.EncodeDraftBitmap(base) PNG KB", png.Length / 1024.0);
    }

    private static string KindOf(AnnotationItem item) => item switch
    {
        TextAnnotation { HasOutline: true } => "Text (outlined)",
        TextAnnotation => "Text",
        PenAnnotation => "Pen",
        RectangleAnnotation => "Rectangle",
        EllipseAnnotation => "Ellipse",
        PolylineAnnotation => "Arrow",
        BlurAnnotation => "Blur",
        ImageAnnotation => "Image",
        _ => item.GetType().Name,
    };

    private void Copy(EditorUnderTest ed)
    {
        ProbeScenario s = Begin("copy (clipboard write stubbed)");
        ed.CommitDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long started = Stopwatch.GetTimestamp();
        Op(s, "Commit(CopyToClipboard)", () => InvokePrivate(ed.Editor, "Commit", EditorCommitAction.CopyToClipboard, false), idle: false);
        bool copied = WaitTask(ed.CommitDone.Task, "copy");
        s.Extra["commit-ms"] = Ms(started);
        End(s);
        s.Notes = $"copied={copied}; status \"{Ui(() => ed.Editor.StatusText)}\"";
    }

    private void Done(EditorUnderTest ed, CaptureRecord record)
    {
        ProbeScenario s = Begin("done (flatten + persist + copy, closes)");
        ed.CommitDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long started = Stopwatch.GetTimestamp();
        Op(s, "SaveAndClose", () => ed.Editor.SaveAndClose(), idle: false);
        bool saved = WaitTask(ed.CommitDone.Task, "done commit");
        s.Extra["commit-ms"] = Ms(started);
        Wait(ed.Closed, "editor close after done");
        s.Extra["closed-ms"] = Ms(started);
        WaitTask(Ui(EditDraftSession.WaitForFinalizationsAsync), "draft finalization");
        End(s);
        s.Notes = $"saved={saved}, committed={Ui(() => ed.Window.WasCommitted)}, record has annotations={record.HasAnnotations}";
    }

    private void Autosave(EditorUnderTest ed, string after)
    {
        if (ed.Draft is not { } draft) return;
        ProbeScenario s = Begin("autosave after " + after);
        Task before = Ui(() => draft.LastWrite);
        long started = Stopwatch.GetTimestamp();
        // EditDraftSession debounces 750 ms (Background-priority DispatcherTimer) after the last change.
        _delay.Wait(1000);
        Task last = Ui(() => draft.LastWrite);
        WaitTask(last, "draft write");
        s.Extra["draft-wait-ms"] = Ms(started);
        End(s);
        s.Notes = ReferenceEquals(before, last) ? "no draft write in this window" : "draft written";
    }

    private void CloseIfOpen(EditorUnderTest ed)
    {
        if (ed.Window is null || ed.Closed.IsSet) return;
        Ui(() => ed.Window.Close());
        ed.Closed.Wait(TimeSpan.FromSeconds(30));
    }

    // ---- Measurement plumbing ----------------------------------------------------------------

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

    /// <summary>
    /// Runs <paramref name="action"/> as one Input-priority dispatcher operation and records its
    /// synchronous duration and, when asked, the time from its start until ApplicationIdle.
    /// </summary>
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

    internal static double Ms(long start) => Ms(start, Stopwatch.GetTimestamp());

    internal static double Ms(long start, long end) => (end - start) * 1000.0 / Stopwatch.Frequency;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    private static void InvokePrivate(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);

    private static List<PointD> Line(PointD from, PointD to, int count) =>
        Enumerable.Range(0, count).Select(i =>
        {
            double t = i / (double)(count - 1);
            return new PointD(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
        }).ToList();

    private static List<PointD> Wave(int width, int height, int count) =>
        Enumerable.Range(0, count).Select(i =>
        {
            double t = i / (double)(count - 1);
            return new PointD(width * (0.10 + 0.80 * t), height * (0.50 + 0.08 * Math.Sin(t * Math.PI * 6)));
        }).ToList();

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
        internal int Width;
        internal int Height;
    }

    /// <summary>The services App builds around an editor, rooted in an isolated temporary directory.</summary>
    private sealed class AppEnvironment : IDisposable
    {
        private readonly DispatcherTimer _preferenceTimer;

        internal AppEnvironment(string root)
        {
            Paths = AppPaths.CreateForRoot(root);
            Paths.EnsureCreated();
            Settings = new AppSettings();
            var settingsStore = new SettingsStore(Paths, NullLogger<SettingsStore>.Instance);
            Queue = new CaptureQueue(Paths, Settings.Queue, NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, Paths, () => Settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            Commit = new CaptureCommitService(Persistence, () => Settings, () => Paths, NullLogger<CaptureCommitService>.Instance,
                CopyWithoutClipboard);
            Drafts = new EditDraftStore(Path.Combine(root, "edit-drafts"));

            // App.RememberEditorPreferences: keep the value, save settings 500 ms after the last change.
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

        /// <summary>
        /// The product clipboard path (PNG encode on a worker, data object built on an isolated
        /// STA) with only the final OLE clipboard write replaced, so the user's clipboard stays untouched.
        /// </summary>
        private static Task<bool> CopyWithoutClipboard(BitmapSource bitmap, string? sourceUrl) =>
            ClipboardImageService.CopyImageAsync(bitmap, sourceUrl, static _ => { });

        public void Dispose()
        {
            _preferenceTimer.Stop();
            AnnotationEditorPreferences.Read = null;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = null;
            AnnotationEditorPreferences.Write = null;
        }
    }
}

/// <summary>Deterministic test images: smooth photographic colour fields with sensor-like noise plus flat UI panels with text-like rows.</summary>
internal static class SyntheticImage
{
    internal static byte[] PhotoLikePixels(int width, int height, int seed)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        const int Cell = 64;
        int gridWidth = width / Cell + 2, gridHeight = height / Cell + 2;
        var random = new Random(seed);
        float[] grid = new float[gridWidth * gridHeight * 3];
        for (int i = 0; i < grid.Length; i++) grid[i] = 30 + (float)random.NextDouble() * 200;
        Parallel.For(0, height, y =>
        {
            float fy = (float)y / Cell;
            int y0 = (int)fy;
            float ty = fy - y0;
            uint state = (uint)(y * 7919 + seed) * 2654435761u | 1u;
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                float fx = (float)x / Cell;
                int x0 = (int)fx;
                float tx = fx - x0;
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                int noise = (int)(state & 31) - 16;
                float texture = 14f * MathF.Sin(x * 0.071f + y * 0.053f) * MathF.Sin(x * 0.013f - y * 0.029f);
                int offset = row + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    int a = (y0 * gridWidth + x0) * 3 + c;
                    int b = (y0 * gridWidth + x0 + 1) * 3 + c;
                    int d = ((y0 + 1) * gridWidth + x0) * 3 + c;
                    int e = ((y0 + 1) * gridWidth + x0 + 1) * 3 + c;
                    float top = grid[a] + (grid[b] - grid[a]) * tx;
                    float bottom = grid[d] + (grid[e] - grid[d]) * tx;
                    float value = top + (bottom - top) * ty + texture + noise;
                    pixels[offset + c] = (byte)Math.Clamp((int)value, 0, 255);
                }

                pixels[offset + 3] = 255;
            }
        });

        // Flat panels with rows of glyph-like blocks, as captured application windows have.
        for (int panel = 0; panel < 90; panel++)
        {
            int panelWidth = (int)(width * (0.05 + random.NextDouble() * 0.18));
            int panelHeight = (int)(height * (0.05 + random.NextDouble() * 0.20));
            int left = random.Next(0, Math.Max(1, width - panelWidth));
            int top = random.Next(0, Math.Max(1, height - panelHeight));
            byte shade = (byte)random.Next(200, 250);
            byte ink = (byte)random.Next(10, 70);
            for (int y = top; y < top + panelHeight; y++)
            {
                int line = (y - top) % 22;
                bool textRow = line is >= 6 and < 18;
                int row = y * width * 4;
                for (int x = left; x < left + panelWidth; x++)
                {
                    bool glyph = textRow && ((x - left) % 11) < 7 && (((x - left) / 11 + (y - top) / 22) % 9) != 0;
                    byte value = glyph ? ink : shade;
                    int offset = row + x * 4;
                    pixels[offset] = value;
                    pixels[offset + 1] = value;
                    pixels[offset + 2] = value;
                }
            }
        }

        return pixels;
    }
}

/// <summary>A 200-item layer of every annotation kind, as a heavily marked-up re-edited capture has.</summary>
internal static class ManyItems
{
    internal static (AnnotationDocument Document, Dictionary<string, BitmapSource> Assets, RectD Target) Create(int width, int height)
    {
        var random = new Random(200);
        var document = AnnotationDocument.CreateFor(width, height);
        var assets = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        ColorRgba[] colors =
        [
            ColorRgba.FromRgb(0xEF, 0x44, 0x44), ColorRgba.FromRgb(0x22, 0xC5, 0x5E), ColorRgba.FromRgb(0x3B, 0x82, 0xF6),
            ColorRgba.FromRgb(0xF5, 0x9E, 0x0B), ColorRgba.FromRgb(0xA8, 0x55, 0xF7), ColorRgba.White,
        ];
        RectD Box(double minW, double maxW, double minH, double maxH)
        {
            double w = width * (minW + random.NextDouble() * (maxW - minW));
            double h = height * (minH + random.NextDouble() * (maxH - minH));
            return new RectD(width * 0.02 + random.NextDouble() * (width * 0.96 - w), height * 0.02 + random.NextDouble() * (height * 0.96 - h), w, h);
        }
        PointD Point() => new(width * (0.03 + random.NextDouble() * 0.94), height * (0.03 + random.NextDouble() * 0.94));
        ColorRgba Color() => colors[random.Next(colors.Length)];

        for (int i = 0; i < 50; i++)
            document.Add(new RectangleAnnotation { Rect = Box(0.02, 0.12, 0.03, 0.15), Stroke = Color(), StrokeThickness = 4 });
        for (int i = 0; i < 25; i++)
            document.Add(new EllipseAnnotation { Rect = Box(0.02, 0.10, 0.03, 0.12), Stroke = Color(), StrokeThickness = 4 });
        for (int i = 0; i < 25; i++)
            document.Add(PolylineAnnotation.CreateArrow(Point(), Point(), Color(), 5));
        for (int i = 0; i < 40; i++)
        {
            PointD at = Point();
            var points = new List<PointD>(150);
            for (int p = 0; p < 150; p++)
            {
                at = new PointD(Math.Clamp(at.X + random.Next(-12, 13), 2, width - 2), Math.Clamp(at.Y + random.Next(-12, 13), 2, height - 2));
                points.Add(at);
            }

            document.Add(new PenAnnotation { Points = points, Stroke = Color(), StrokeThickness = 4 });
        }

        for (int i = 0; i < 40; i++)
        {
            PointD at = Point();
            document.Add(new TextAnnotation
            {
                Rect = new RectD(Math.Min(at.X, width * 0.8), at.Y, 320, 40),
                Text = i % 4 == 0 ? $"설명 {i}: 이 영역을 확인하십시오\n두 번째 줄 {i}" : $"메모 {i} — check this value",
                FontSize = 28,
                FontFamily = "Malgun Gothic",
                Foreground = Color(),
                HasOutline = true,
                OutlineStyle = TextOutlineStyle.Consistent,
                Background = i % 3 == 0 ? ColorRgba.Black : ColorRgba.Transparent,
                AutoSize = true,
            });
        }

        for (int i = 0; i < 10; i++)
            document.Add(new BlurAnnotation { Rect = Box(0.03, 0.10, 0.03, 0.10), Radius = BlurAnnotation.DefaultRadius });
        for (int i = 0; i < 9; i++)
        {
            string name = $"asset-{i + 1:D2}.png";
            byte[] patch = SyntheticImage.PhotoLikePixels(160, 120, 900 + i);
            BitmapSource bitmap = BitmapSource.Create(160, 120, 96, 96, PixelFormats.Bgr32, null, patch, 160 * 4);
            bitmap.Freeze();
            assets[name] = bitmap;
            RectD box = Box(0.04, 0.08, 0.05, 0.10);
            document.Add(new ImageAnnotation { AssetFileName = name, SourceWidth = 160, SourceHeight = 120, Rect = box });
        }

        var target = new RectD(width * 0.40, height * 0.10, width * 0.15, height * 0.15);
        document.Add(new RectangleAnnotation { Rect = target, Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44), StrokeThickness = 6 });
        return (document, assets, target);
    }
}

// ---- Recording -------------------------------------------------------------------------------

internal sealed class ProbeScenario
{
    private readonly object _gate = new();
    private long _startTimestamp;
    private long _allocatedStart;
    private TimeSpan _gcPauseStart;
    private int _gen0, _gen1, _gen2;

    internal ProbeScenario(string size, string variant, string name)
    {
        Size = size;
        Variant = variant;
        Name = name;
    }

    internal string Size { get; }
    internal string Variant { get; }
    internal string Name { get; }
    internal List<double> Sync { get; } = [];
    internal Dictionary<string, (int Count, double Total, double Max)> SyncByStep { get; } = [];
    internal List<double> Idle { get; } = [];
    internal List<double> Beats { get; } = [];
    internal List<ProbeStall> Stalls { get; } = [];
    internal Dictionary<string, (int Count, double Total, double Max)> Operations { get; } = [];
    internal Dictionary<string, double> Extra { get; } = [];
    internal double SettleMs { get; set; }
    internal double WallMs { get; private set; }
    internal long EndTimestamp { get; private set; }
    internal string Notes { get; set; } = "";
    internal int Gen0 { get; private set; }
    internal int Gen1 { get; private set; }
    internal int Gen2 { get; private set; }
    internal double GcPauseMs { get; private set; }
    internal long PrivateBytes { get; private set; }
    internal double AllocatedMb { get; private set; }

    internal void Start()
    {
        _allocatedStart = GC.GetTotalAllocatedBytes(false);
        _gen0 = GC.CollectionCount(0);
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
        _gcPauseStart = GC.GetTotalPauseDuration();
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    internal void Finish(long end)
    {
        EndTimestamp = end;
        WallMs = EditorResponsivenessProbe.Ms(_startTimestamp, end);
        Gen0 = GC.CollectionCount(0) - _gen0;
        Gen1 = GC.CollectionCount(1) - _gen1;
        Gen2 = GC.CollectionCount(2) - _gen2;
        GcPauseMs = (GC.GetTotalPauseDuration() - _gcPauseStart).TotalMilliseconds;
        AllocatedMb = (GC.GetTotalAllocatedBytes(false) - _allocatedStart) / 1048576.0;
        using var process = Process.GetCurrentProcess();
        PrivateBytes = process.PrivateMemorySize64;
    }

    internal void AddSync(string step, double ms)
    {
        lock (_gate)
        {
            Sync.Add(ms);
            SyncByStep[step] = SyncByStep.TryGetValue(step, out var value)
                ? (value.Count + 1, value.Total + ms, Math.Max(value.Max, ms)) : (1, ms, ms);
        }
    }

    internal void AddIdle(double ms)
    {
        lock (_gate) Idle.Add(ms);
    }

    internal void AddBeat(double delay, ProbeStall? stall)
    {
        lock (_gate)
        {
            Beats.Add(delay);
            if (stall is not null) Stalls.Add(stall);
        }
    }

    internal void AddOperation(string name, double exclusive)
    {
        lock (_gate)
        {
            Operations[name] = Operations.TryGetValue(name, out var value)
                ? (value.Count + 1, value.Total + exclusive, Math.Max(value.Max, exclusive)) : (1, exclusive, exclusive);
        }
    }
}

internal sealed record ProbeStall(double DelayMs, string StepAtPost, string Operations, double GcPauseMs, int Gen2Collections, string Stacks);

internal sealed record ProbeLongOperation(string Size, string Variant, string Scenario, string Step, string Operation,
    string Priority, double ExclusiveMs, double TotalMs);

internal sealed class ProbeRecorder
{
    private readonly object _gate = new();
    private volatile ProbeScenario? _current;
    private volatile string? _step;

    internal ProbeScenario? Current { get => _current; set => _current = value; }
    internal string? Step { get => _step; set => _step = value; }
    internal List<ProbeScenario> Scenarios { get; } = [];
    internal List<ProbeLongOperation> LongOperations { get; } = [];
    internal List<double> Between { get; } = [];
    internal List<ProbeStall> BetweenStalls { get; } = [];
    internal Dictionary<(string Size, string Variant, string Item), List<double>> Breakdown { get; } = [];

    internal void Add(ProbeScenario scenario)
    {
        lock (_gate) Scenarios.Add(scenario);
    }

    internal void AddBetween(double delay, ProbeStall? stall)
    {
        lock (_gate)
        {
            Between.Add(delay);
            if (stall is not null) BetweenStalls.Add(stall);
        }
    }

    internal void AddLongOperation(ProbeLongOperation operation)
    {
        lock (_gate) LongOperations.Add(operation);
    }

    internal void AddBreakdown(string size, string variant, string item, double ms)
    {
        lock (_gate)
        {
            if (!Breakdown.TryGetValue((size, variant, item), out List<double>? values))
                Breakdown[(size, variant, item)] = values = [];
            values.Add(ms);
        }
    }
}

/// <summary>
/// Times every dispatcher operation through <see cref="DispatcherHooks"/> (exclusive of nested
/// operations) and names what it runs, so a stall can be traced to the code that held the thread.
/// </summary>
internal sealed class DispatcherOperationTracker : IDisposable
{
    private static readonly FieldInfo? MethodField = FindField(typeof(DispatcherOperation), "_method");
    private static readonly FieldInfo? ArgsField = FindField(typeof(DispatcherOperation), "_args");
    private static readonly FieldInfo? TimerTickField = typeof(DispatcherTimer).GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Dispatcher _dispatcher;
    private readonly ProbeRecorder _recorder;
    private readonly List<Frame> _stack = [];
    private volatile string? _current;

    internal DispatcherOperationTracker(Dispatcher dispatcher, ProbeRecorder recorder)
    {
        _dispatcher = dispatcher;
        _recorder = recorder;
        _dispatcher.Hooks.OperationStarted += OnStarted;
        _dispatcher.Hooks.OperationCompleted += OnFinished;
        _dispatcher.Hooks.OperationAborted += OnFinished;
    }

    /// <summary>The operation the UI thread is running now; read by the heartbeat thread.</summary>
    internal string? Current => _current;

    public void Dispose()
    {
        _dispatcher.Hooks.OperationStarted -= OnStarted;
        _dispatcher.Hooks.OperationCompleted -= OnFinished;
        _dispatcher.Hooks.OperationAborted -= OnFinished;
    }

    private void OnStarted(object? sender, DispatcherHookEventArgs e)
    {
        try
        {
            string name = Describe(e.Operation);
            if (name == "probe step") name = "step: " + (_recorder.Step ?? "?");
            _stack.Add(new Frame(e.Operation, name, Stopwatch.GetTimestamp(), _recorder.Current, _recorder.Step));
            _current = name;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Probe hook failed: " + ex.Message);
        }
    }

    private void OnFinished(object? sender, DispatcherHookEventArgs e)
    {
        int index = _stack.FindLastIndex(frame => ReferenceEquals(frame.Operation, e.Operation));
        if (index < 0) return;
        long now = Stopwatch.GetTimestamp();
        for (int i = _stack.Count - 1; i >= index; i--)
        {
            Frame frame = _stack[i];
            _stack.RemoveAt(i);
            double total = EditorResponsivenessProbe.Ms(frame.Started, now);
            double exclusive = Math.Max(0, total - frame.ChildMs);
            if (_stack.Count > 0) _stack[^1].ChildMs += total;
            if (frame.Name == UiHeartbeat.OperationName) continue;
            ProbeScenario? scenario = frame.Scenario ?? _recorder.Current;
            scenario?.AddOperation(frame.Name, exclusive);
            if (exclusive >= 16)
            {
                _recorder.AddLongOperation(new ProbeLongOperation(scenario?.Size ?? "-", scenario?.Variant ?? "-",
                    scenario?.Name ?? "(between scenarios)", frame.Step ?? "-", frame.Name, e.Operation.Priority.ToString(), exclusive, total));
            }
        }

        _current = _stack.Count > 0 ? _stack[^1].Name : null;
    }

    internal static string Describe(DispatcherOperation operation)
    {
        Delegate? method = MethodField?.GetValue(operation) as Delegate;
        object? args = ArgsField?.GetValue(operation);
        if (method is null) return "(unknown operation)";
        if ((method.Target as DispatcherTimer ?? args as DispatcherTimer) is { } timer)
        {
            var tick = TimerTickField?.GetValue(timer) as EventHandler;
            return "DispatcherTimer.Tick → " + (tick is null ? "?" : string.Join(" + ", tick.GetInvocationList().Select(d => Name(d.Method))));
        }

        if (method.Method.DeclaringType == typeof(UiHeartbeat)) return UiHeartbeat.OperationName;
        string declaring = method.Method.DeclaringType?.FullName ?? "";
        if (declaring.StartsWith("MyCapture.App.Tests.", StringComparison.Ordinal)) return "probe step";
        if (args is not null && (declaring.StartsWith("System.Threading", StringComparison.Ordinal)
            || declaring.StartsWith("System.Windows.Threading.DispatcherSynchronizationContext", StringComparison.Ordinal)))
        {
            string? state = DescribeState(args);
            if (state is not null) return "await continuation → " + state;
        }

        string name = Name(method.Method);
        if (args is Delegate inner && name.StartsWith("System.Windows.Threading", StringComparison.Ordinal))
            return name + " → " + (DescribeState(inner) ?? Name(inner.Method));
        return name;
    }

    private static string? DescribeState(object state)
    {
        if (state is Delegate d)
        {
            if (d.Target is not null && StateMachineName(d.Target.GetType()) is { } machine) return machine;
            return d.Method.DeclaringType?.FullName?.StartsWith("MyCapture.App.Tests.", StringComparison.Ordinal) == true
                ? "probe step" : Name(d.Method);
        }

        if (StateMachineName(state.GetType()) is { } boxed) return boxed;
        foreach (FieldInfo field in state.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetValue(state) is Delegate nested) return DescribeState(nested);
        }

        return state.GetType().FullName;
    }

    private static string? StateMachineName(Type type)
    {
        if (typeof(IAsyncStateMachine).IsAssignableFrom(type)) return Clean(type.FullName ?? type.Name);
        if (!type.IsGenericType) return null;
        foreach (Type argument in type.GetGenericArguments())
        {
            if (typeof(IAsyncStateMachine).IsAssignableFrom(argument)) return Clean(argument.FullName ?? argument.Name);
        }

        return null;
    }

    private static string Name(MethodInfo method) => Clean((method.DeclaringType?.FullName ?? "?") + "." + method.Name);

    private static string Clean(string name)
    {
        // MyCapture.App.Editing.EditDraftSession+<WriteAsync>d__19 → MyCapture.App.Editing.EditDraftSession.WriteAsync (async)
        int marker = name.IndexOf("+<", StringComparison.Ordinal);
        if (marker >= 0)
        {
            int close = name.IndexOf('>', marker);
            if (close > marker + 2 && name.AsSpan(close).StartsWith(">d__")) return name[..marker] + "." + name[(marker + 2)..close] + " (async)";
        }

        return name.Replace("System.Windows.", "", StringComparison.Ordinal).Replace("MyCapture.App.", "", StringComparison.Ordinal);
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null) return field;
        }

        return null;
    }

    private sealed class Frame(DispatcherOperation operation, string name, long started, ProbeScenario? scenario, string? step)
    {
        internal DispatcherOperation Operation { get; } = operation;
        internal string Name { get; } = name;
        internal long Started { get; } = started;
        internal ProbeScenario? Scenario { get; } = scenario;
        internal string? Step { get; } = step;
        internal double ChildMs { get; set; }
    }
}

/// <summary>What the heartbeat needs to diagnose a UI thread that stops responding.</summary>
internal sealed class HangContext(string output, int uiThreadId, JitMethodMap? jit, Action writeReport)
{
    internal double KillAfterMs { get; } = double.TryParse(Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_PERF_HANG_KILL_S"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 6 ? seconds * 1000 : 90_000;
    internal string Output { get; } = output;
    internal int UiThreadId { get; } = uiThreadId;
    internal JitMethodMap? Jit { get; } = jit;
    internal void WriteReport() => writeReport();
}

/// <summary>
/// Every 8 ms posts one Input-priority operation to the UI dispatcher and records how long it
/// waited to run. Only one probe is outstanding, so one stall yields one sample of its length.
/// While a probe waits it samples, every 10 ms, which dispatcher operation holds the thread and,
/// when enabled, the UI thread's call stack. A wait over 5 s writes every thread's stack to
/// hang-*.txt; a longer wait (90 s by default) writes the reports and ends the probe process.
/// </summary>
internal sealed class UiHeartbeat : IDisposable
{
    internal const string OperationName = "MyCapture.Tests.UiHeartbeat.Beat";
    private static readonly double[] SampleAtMs = [100, 250, 500, 1000, 2000, 3500];
    private readonly Dispatcher _ui;
    private readonly ProbeRecorder _recorder;
    private readonly DispatcherOperationTracker _tracker;
    private readonly HangContext _hang;
    private readonly bool _sampleStacks;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private volatile bool _stop;
    private int _sampleBudget = 400;

    internal UiHeartbeat(Dispatcher ui, ProbeRecorder recorder, DispatcherOperationTracker tracker, HangContext hang, bool sampleStacks)
    {
        _ui = ui;
        _recorder = recorder;
        _tracker = tracker;
        _hang = hang;
        _sampleStacks = sampleStacks;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Editor probe heartbeat", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _started.Wait();
    }

    internal bool HighResolution { get; private set; }

    internal void Stop()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        using var delay = new PreciseDelay();
        HighResolution = delay.IsHighResolution;
        _started.Set();
        var callback = new DispatcherOperationCallback(Beat);
        while (!_stop)
        {
            delay.Wait(8);
            ProbeScenario? scenario = _recorder.Current;
            string step = _recorder.Step ?? "-";
            TimeSpan gcBefore = GC.GetTotalPauseDuration();
            int gen2Before = GC.CollectionCount(2);
            var beat = new BeatState { Posted = Stopwatch.GetTimestamp() };
            _ui.BeginInvoke(DispatcherPriority.Input, callback, beat);
            Dictionary<string, int>? seen = null;
            Dictionary<string, int>? stacks = null;
            int samples = 0;
            bool dumped = false;
            while (!beat.Done.Wait(10))
            {
                // An abandoned beat may still run during dispatcher teardown; it is not disposed.
                if (_stop) return;
                double waited = EditorResponsivenessProbe.Ms(beat.Posted);
                if (waited < 40) continue;
                seen ??= [];
                string holder = (_tracker.Current ?? "(no dispatcher operation: Win32 message or native wait)") + " [step: " + (_recorder.Step ?? "-") + "]";
                seen[holder] = seen.TryGetValue(holder, out int count) ? count + 1 : 1;
                if (_sampleStacks && samples < SampleAtMs.Length && waited >= SampleAtMs[samples] && _sampleBudget > 0)
                {
                    samples++;
                    _sampleBudget--;
                    string stack = NativeStacks.Condensed(_hang.UiThreadId, _hang.Jit);
                    stacks ??= [];
                    stacks[stack] = stacks.TryGetValue(stack, out int times) ? times + 1 : 1;
                }

                if (!dumped && waited >= 5000)
                {
                    dumped = true;
                    DumpHang(scenario, step, waited);
                }

                if (waited >= _hang.KillAfterMs)
                {
                    var hung = new ProbeStall(waited, step, Join(seen, 10), (GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds,
                        GC.CollectionCount(2) - gen2Before, "UI thread still blocked when the probe ended itself; see hang-*.txt");
                    if (scenario is not null) scenario.AddBeat(waited, hung);
                    else _recorder.AddBetween(waited, hung);
                    _hang.WriteReport();
                    using var self = Process.GetCurrentProcess();
                    self.Kill();
                    return;
                }
            }

            beat.Dispose();
            double delayMs = EditorResponsivenessProbe.Ms(beat.Posted, beat.Ran);
            ProbeStall? stall = delayMs < 50 ? null : new ProbeStall(delayMs, step, seen is null ? "(sampled none)" : Join(seen, 10),
                (GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds, GC.CollectionCount(2) - gen2Before,
                stacks is null ? "" : string.Join(" || ", stacks.Select(pair => $"{pair.Value}× {pair.Key}")));
            if (scenario is not null) scenario.AddBeat(delayMs, stall);
            else _recorder.AddBetween(delayMs, stall);
        }
    }

    private void DumpHang(ProbeScenario? scenario, string step, double waited)
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine($"UI dispatcher has not run an Input-priority operation for {waited:0} ms.");
            text.AppendLine($"Scenario: {scenario?.Size} {scenario?.Variant} {scenario?.Name ?? "(between scenarios)"}; step when posted: {step}; step now: {_recorder.Step}");
            text.AppendLine($"Dispatcher operation running now: {_tracker.Current ?? "(none: Win32 message or native wait)"}");
            text.AppendLine($"JIT method map entries: {_hang.Jit?.Count ?? 0}");
            text.AppendLine();
            text.Append(NativeStacks.DumpAll(_hang.Jit, _hang.UiThreadId));
            File.WriteAllText(Path.Combine(_hang.Output, $"hang-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("Hang dump failed: " + ex);
        }
    }

    private static string Join(Dictionary<string, int>? seen, int msPerSample) => seen is null ? "(sampled none)"
        : string.Join("; ", seen.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} ×{pair.Value * msPerSample}ms"));

    private static object? Beat(object state)
    {
        var beat = (BeatState)state;
        beat.Ran = Stopwatch.GetTimestamp();
        beat.Done.Set();
        return null;
    }

    private sealed class BeatState : IDisposable
    {
        internal long Posted;
        internal long Ran;
        internal readonly ManualResetEventSlim Done = new();

        public void Dispose() => Done.Dispose();
    }
}

/// <summary>
/// Names JIT-compiled code from the runtime's own MethodLoadVerbose events (the in-process
/// equivalent of a profiler's method map). ReadyToRun framework code that never re-JITs stays
/// module+offset unless DOTNET_ReadyToRun=0.
/// </summary>
internal sealed class JitMethodMap : EventListener
{
    private readonly object _gate = new();
    private readonly List<(ulong Start, ulong End, string Name)> _methods = [];
    private bool _sorted = true;

    internal int Count
    {
        get
        {
            lock (_gate) return _methods.Count;
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // JitKeyword (0x10); MethodLoadVerbose is a Verbose event.
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x10);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName is null || !eventData.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal)
            || eventData.Payload is null || eventData.PayloadNames is null) return;
        int start = eventData.PayloadNames.IndexOf("MethodStartAddress");
        int size = eventData.PayloadNames.IndexOf("MethodSize");
        int type = eventData.PayloadNames.IndexOf("MethodNamespace");
        int name = eventData.PayloadNames.IndexOf("MethodName");
        if (start < 0 || size < 0 || type < 0 || name < 0) return;
        ulong address = Convert.ToUInt64(eventData.Payload[start], CultureInfo.InvariantCulture);
        ulong length = Convert.ToUInt64(eventData.Payload[size], CultureInfo.InvariantCulture);
        string method = $"{eventData.Payload[type]}.{eventData.Payload[name]}";
        lock (_gate)
        {
            _methods.Add((address, address + length, method));
            _sorted = false;
        }
    }

    internal string? Resolve(ulong ip)
    {
        lock (_gate)
        {
            if (!_sorted)
            {
                _methods.Sort((a, b) => a.Start.CompareTo(b.Start));
                _sorted = true;
            }

            int low = 0, high = _methods.Count - 1, found = -1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (_methods[middle].Start <= ip)
                {
                    found = middle;
                    low = middle + 1;
                }
                else high = middle - 1;
            }

            for (int i = found; i >= 0 && i > found - 4; i--)
                if (ip >= _methods[i].Start && ip < _methods[i].End) return _methods[i].Name;
            return null;
        }
    }
}

/// <summary>
/// In-process thread stacks: suspend a thread, read its context, unwind with the OS unwinder
/// (which also walks JIT code through the runtime's registered function tables), resume, then name
/// each frame from the JIT map or the containing module.
/// </summary>
/// <remarks>
/// Between suspend and resume nothing is allocated. A garbage collection that starts in that
/// window could still wait for the suspended thread, so callers take few samples.
/// </remarks>
internal static class NativeStacks
{
    private const int ContextSize = 0x4D0;
    private const int ContextFlagsOffset = 0x30;
    private const int RspOffset = 0x98;
    private const int RipOffset = 0xF8;
    private const int ContextFull = 0x0010000B;
    private const uint ThreadAccess = 0x0002 | 0x0008 | 0x0040 | 0x0800;
    private static readonly object Gate = new();
    private static readonly ulong[] Frames = new ulong[160];
    private static readonly byte[] ThreadInformation = new byte[48];
    private static IntPtr _contextBlock;
    private static IntPtr _context;
    private static bool _symbolsReady;

    internal static int CurrentThreadId() => (int)GetCurrentThreadId();

    internal static ulong[] Capture(int threadId)
    {
        lock (Gate)
        {
            if (_context == IntPtr.Zero)
            {
                _contextBlock = Marshal.AllocHGlobal(ContextSize + 16);
                _context = (IntPtr)(((long)_contextBlock + 15) & ~15L);
            }

            IntPtr thread = OpenThread(ThreadAccess, false, (uint)threadId);
            if (thread == IntPtr.Zero) return [];
            try
            {
                if (NtQueryInformationThread(thread, 0, ThreadInformation, ThreadInformation.Length, out _) != 0) return [];
                IntPtr teb = (IntPtr)BitConverter.ToInt64(ThreadInformation, 8);
                ulong stackBase = (ulong)Marshal.ReadInt64(teb, 0x8);
                ulong stackLimit = (ulong)Marshal.ReadInt64(teb, 0x10);
                if (SuspendThread(thread) == uint.MaxValue) return [];
                int count;
                try
                {
                    count = Unwind(thread, stackLimit, stackBase);
                }
                finally
                {
                    ResumeThread(thread);
                }

                return Frames.AsSpan(0, count).ToArray();
            }
            finally
            {
                CloseHandle(thread);
            }
        }
    }

    private static int Unwind(IntPtr thread, ulong low, ulong high)
    {
        for (int i = 0; i < ContextSize; i += 8) Marshal.WriteInt64(_context, i, 0);
        Marshal.WriteInt32(_context, ContextFlagsOffset, ContextFull);
        if (!GetThreadContext(thread, _context)) return 0;
        int count = 0;
        ulong previous = 0;
        while (count < Frames.Length)
        {
            ulong rip = (ulong)Marshal.ReadInt64(_context, RipOffset);
            ulong rsp = (ulong)Marshal.ReadInt64(_context, RspOffset);
            if (rip == 0 || rsp < low || rsp + 8 > high || (count > 0 && rsp <= previous)) break;
            Frames[count++] = rip;
            previous = rsp;
            IntPtr entry = RtlLookupFunctionEntry(rip, out ulong imageBase, IntPtr.Zero);
            if (entry == IntPtr.Zero)
            {
                Marshal.WriteInt64(_context, RipOffset, Marshal.ReadInt64((IntPtr)(long)rsp));
                Marshal.WriteInt64(_context, RspOffset, (long)(rsp + 8));
            }
            else
            {
                RtlVirtualUnwind(0, imageBase, rip, entry, _context, out _, out _, IntPtr.Zero);
            }
        }

        return count;
    }

    internal static string Condensed(int threadId, JitMethodMap? jit, int maxFrames = 40)
    {
        ulong[] frames = Capture(threadId);
        var parts = new List<string>();
        string? lastModule = null;
        int repeat = 0;
        for (int i = 0; i < frames.Length && i < maxFrames; i++)
        {
            (string text, string? module) = Name(i == 0 ? frames[i] : frames[i] - 1, jit);
            if (module is not null && module == lastModule)
            {
                repeat++;
                continue;
            }

            if (repeat > 0) parts[^1] += $" (×{repeat + 1})";
            repeat = 0;
            lastModule = module;
            parts.Add(text);
        }

        if (repeat > 0) parts[^1] += $" (×{repeat + 1})";
        return parts.Count == 0 ? "(no frames)" : string.Join(" < ", parts);
    }

    internal static string DumpAll(JitMethodMap? jit, int uiThreadId)
    {
        var text = new StringBuilder();
        int self = CurrentThreadId();
        int[] ids;
        using (var process = Process.GetCurrentProcess())
            ids = process.Threads.Cast<ProcessThread>().Select(thread => thread.Id).OrderBy(id => id == uiThreadId ? 0 : 1).ToArray();
        foreach (int id in ids)
        {
            if (id == self) continue;
            ulong[] frames = Capture(id);
            text.AppendLine($"--- thread {id}{(id == uiThreadId ? " (UI thread)" : "")} \"{ThreadName(id)}\" ---");
            for (int i = 0; i < frames.Length; i++)
                text.AppendLine("  " + Name(i == 0 ? frames[i] : frames[i] - 1, jit).Text);
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>The frame's name, and its module when the frame is not a named JIT method.</summary>
    private static (string Text, string? Module) Name(ulong ip, JitMethodMap? jit)
    {
        if (jit?.Resolve(ip) is { } managed) return (managed, null);
        if (!GetModuleHandleExW(0x4 | 0x2, (IntPtr)(long)ip, out IntPtr module) || module == IntPtr.Zero)
            return ($"0x{ip:X} (JIT code or stub without a load event)", "?jit");
        var path = new StringBuilder(520);
        _ = GetModuleFileNameW(module, path, path.Capacity);
        string file = Path.GetFileName(path.ToString());
        ulong rva = ip - (ulong)(long)module;
        if (ExportName(ip) is { } export) return ($"{file}!{export}", null);
        return ($"{file}+0x{rva:X}", file);
    }

    private static string? ExportName(ulong ip)
    {
        lock (Gate)
        {
            IntPtr process = GetCurrentProcess();
            if (!_symbolsReady)
            {
                // Exports only: an explicit local search path keeps dbghelp off any symbol server.
                _ = SymSetOptions(0x2 | 0x4 | 0x200 | 0x80000);
                _symbolsReady = SymInitializeW(process, Path.GetTempPath(), true);
            }

            if (!_symbolsReady) return null;
            const int MaxName = 512;
            IntPtr buffer = Marshal.AllocHGlobal(88 + MaxName * 2);
            try
            {
                for (int i = 0; i < 88; i += 4) Marshal.WriteInt32(buffer, i, 0);
                Marshal.WriteInt32(buffer, 0, 88);
                Marshal.WriteInt32(buffer, 80, MaxName - 1);
                if (!SymFromAddrW(process, ip, out ulong displacement, buffer) || displacement > 0x2000) return null;
                int length = Marshal.ReadInt32(buffer, 76);
                return Marshal.PtrToStringUni(buffer + 84, Math.Min(length, MaxName - 1)) + $"+0x{displacement:X}";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string ThreadName(int threadId)
    {
        IntPtr thread = OpenThread(0x0800, false, (uint)threadId);
        if (thread == IntPtr.Zero) return "";
        try
        {
            if (GetThreadDescription(thread, out IntPtr description) < 0 || description == IntPtr.Zero) return "";
            string name = Marshal.PtrToStringUni(description) ?? "";
            _ = LocalFree(description);
            return name;
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadContext(IntPtr thread, IntPtr context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);

    [DllImport("kernel32.dll")]
    private static extern IntPtr RtlVirtualUnwind(uint handlerType, ulong imageBase, ulong controlPc, IntPtr functionEntry,
        IntPtr context, out IntPtr handlerData, out ulong establisherFrame, IntPtr contextPointers);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr thread, int informationClass, byte[] information, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder fileName, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern uint SymSetOptions(uint options);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymInitializeW(IntPtr process, string? searchPath, [MarshalAs(UnmanagedType.Bool)] bool invadeProcess);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymFromAddrW(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
}

/// <summary>Millisecond waits through a high-resolution waitable timer (Windows 10 1803+), without changing the system timer resolution.</summary>
internal sealed class PreciseDelay : IDisposable
{
    private const uint HighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private IntPtr _timer;

    internal PreciseDelay() => _timer = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, TimerAllAccess);

    internal bool IsHighResolution => _timer != IntPtr.Zero;

    internal void Wait(double milliseconds)
    {
        if (milliseconds <= 0) return;
        long due = -(long)(milliseconds * 10_000);
        if (_timer == IntPtr.Zero || !SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            Thread.Sleep((int)Math.Ceiling(milliseconds));
            return;
        }

        _ = WaitForSingleObject(_timer, 0xFFFFFFFF);
    }

    public void Dispose()
    {
        if (_timer == IntPtr.Zero) return;
        CloseHandle(_timer);
        _timer = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Writes the CSV files and the summary.</summary>
internal sealed class ProbeReport(string output, ProbeRecorder recorder, IReadOnlyList<string> environment,
    IReadOnlyList<string> substitutions, Exception? failure, string prefix = "editor-responsiveness",
    string title = "이미지 편집기 UI 스레드 정지 측정")
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal void Write()
    {
        WriteScenarios();
        WriteStalls();
        WriteOperations();
        WriteLongOperations();
        WriteBreakdown();
        WriteSummary();
    }

    private void WriteScenarios()
    {
        var csv = new StringBuilder();
        csv.AppendLine("size,variant,scenario,ops,sync_total_ms,sync_max_ms,sync_p95_ms,slowest_step,idle_max_ms,idle_avg_ms,settle_ms,wall_ms,beats,max_stall_ms,p95_beat_ms,stalls_gt50,stalls_gt200,stalls_gt1000,gc_gen0,gc_gen1,gc_gen2,gc_pause_ms,allocated_mb,private_mb,top_operation,extra,notes");
        foreach (ProbeScenario s in recorder.Scenarios)
        {
            string step = SlowestStep(s);
            csv.AppendLine(string.Join(",",
                s.Size, s.Variant, Q(s.Name), s.Sync.Count, F(s.Sync.Sum()), F(Max(s.Sync)), F(P95(s.Sync)), Q(step),
                F(Max(s.Idle)), F(s.Idle.Count == 0 ? 0 : s.Idle.Average()), F(s.SettleMs), F(s.WallMs), s.Beats.Count,
                F(Max(s.Beats)), F(P95(s.Beats)), s.Beats.Count(b => b > 50), s.Beats.Count(b => b > 200), s.Beats.Count(b => b > 1000),
                s.Gen0, s.Gen1, s.Gen2, F(s.GcPauseMs), F(s.AllocatedMb), F(s.PrivateBytes / 1048576.0), Q(TopOperation(s)),
                Q(string.Join("; ", s.Extra.Select(pair => $"{pair.Key}={F(pair.Value)}"))), Q(s.Notes)));
        }

        File.WriteAllText(Path.Combine(output, prefix + ".csv"), csv.ToString(), new UTF8Encoding(true));
    }

    private void WriteStalls()
    {
        var csv = new StringBuilder();
        csv.AppendLine("size,variant,scenario,delay_ms,step_at_post,gc_pause_ms,gen2,ui_thread_holder_samples,ui_thread_stack_samples");
        foreach (ProbeScenario s in recorder.Scenarios)
            foreach (ProbeStall stall in s.Stalls)
                csv.AppendLine(string.Join(",", s.Size, s.Variant, Q(s.Name), F(stall.DelayMs), Q(stall.StepAtPost), F(stall.GcPauseMs), stall.Gen2Collections, Q(stall.Operations), Q(stall.Stacks)));
        foreach (ProbeStall stall in recorder.BetweenStalls)
            csv.AppendLine(string.Join(",", "-", "-", Q("(between scenarios)"), F(stall.DelayMs), Q(stall.StepAtPost), F(stall.GcPauseMs), stall.Gen2Collections, Q(stall.Operations), Q(stall.Stacks)));
        File.WriteAllText(Path.Combine(output, prefix + "-stalls.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    private void WriteOperations()
    {
        var csv = new StringBuilder();
        csv.AppendLine("size,variant,scenario,operation,count,exclusive_total_ms,exclusive_max_ms");
        foreach (ProbeScenario s in recorder.Scenarios)
            foreach (var (name, value) in s.Operations.OrderByDescending(pair => pair.Value.Total))
                csv.AppendLine(string.Join(",", s.Size, s.Variant, Q(s.Name), Q(name), value.Count, F(value.Total), F(value.Max)));
        File.WriteAllText(Path.Combine(output, prefix + "-operations.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    private void WriteLongOperations()
    {
        var csv = new StringBuilder();
        csv.AppendLine("size,variant,scenario,step,operation,priority,exclusive_ms,total_ms");
        foreach (ProbeLongOperation op in recorder.LongOperations)
            csv.AppendLine(string.Join(",", op.Size, op.Variant, Q(op.Scenario), Q(op.Step), Q(op.Operation), op.Priority, F(op.ExclusiveMs), F(op.TotalMs)));
        File.WriteAllText(Path.Combine(output, prefix + "-long-operations.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    private void WriteBreakdown()
    {
        var csv = new StringBuilder();
        csv.AppendLine("size,variant,item,samples,avg,max");
        foreach (var ((size, variant, item), values) in recorder.Breakdown)
            csv.AppendLine(string.Join(",", size, variant, Q(item), values.Count, F(values.Average()), F(values.Max())));
        File.WriteAllText(Path.Combine(output, prefix + "-breakdown.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    private void WriteSummary()
    {
        var md = new StringBuilder();
        md.AppendLine("# " + title);
        md.AppendLine();
        md.AppendLine($"측정 시각: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        md.AppendLine();
        if (failure is not null)
        {
            md.AppendLine("**측정이 중간에 실패했습니다.** 아래 결과는 실패 전까지의 값입니다.");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine(failure.ToString());
            md.AppendLine("```");
            md.AppendLine();
        }

        md.AppendLine("## 환경");
        foreach (string line in environment) md.AppendLine("- " + line);
        md.AppendLine();
        md.AppendLine("## 측정 방법");
        md.AppendLine("- 정지(stall): 별도 스레드가 8 ms마다 Input 우선순위 작업 하나를 UI 디스패처에 올리고 실행되기까지 걸린 시간입니다. 미해결 작업은 한 번에 하나뿐이므로 정지 한 번이 표본 하나입니다.");
        md.AppendLine("- 동기 ms: 각 입력 작업(포인터 이벤트, 키, 명령)의 UI 스레드 동기 실행 시간입니다. idle ms: 작업 시작부터 ApplicationIdle 도달까지입니다(렌더링·레이아웃 포함).");
        md.AppendLine("- 원인 표본: 정지 중 10 ms마다 UI 스레드가 실행 중이던 디스패처 작업(DispatcherHooks)을 기록했습니다.");
        foreach (string line in substitutions.Distinct()) md.AppendLine("- 대체: " + line);
        md.AppendLine();

        foreach (IGrouping<string, ProbeScenario> size in recorder.Scenarios.GroupBy(s => s.Size))
        {
            md.AppendLine($"## {size.Key}");
            md.AppendLine();
            md.AppendLine("| 변형 | 시나리오 | 작업 수 | 동기 합 ms | 동기 최대 ms | idle 최대 ms | 최대 정지 ms | >200 ms | >1 s | 경과 ms | GC 정지 ms | 할당 MB | 가장 긴 UI 작업 |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
            foreach (ProbeScenario s in size)
            {
                md.AppendLine($"| {s.Variant} | {s.Name} | {s.Sync.Count} | {F(s.Sync.Sum())} | {F(Max(s.Sync))} | {F(Max(s.Idle))} | {F(Max(s.Beats))} | {s.Beats.Count(b => b > 200)} | {s.Beats.Count(b => b > 1000)} | {F(s.WallMs)} | {F(s.GcPauseMs)} | {F(s.AllocatedMb)} | {Md(TopOperation(s))} |");
            }

            md.AppendLine();
        }

        md.AppendLine("## 가장 긴 정지 20개");
        md.AppendLine();
        md.AppendLine("| 크기 | 변형 | 시나리오 | 정지 ms | GC 정지 ms | UI 스레드를 잡고 있던 작업 |");
        md.AppendLine("|---|---|---|---:|---:|---|");
        foreach (var (s, stall) in recorder.Scenarios.SelectMany(s => s.Stalls.Select(stall => (s, stall))).OrderByDescending(x => x.stall.DelayMs).Take(20))
            md.AppendLine($"| {s.Size} | {s.Variant} | {s.Name} | {F(stall.DelayMs)} | {F(stall.GcPauseMs)} | {Md(stall.Operations)} |");
        md.AppendLine();
        var sampled = recorder.Scenarios.SelectMany(s => s.Stalls.Select(stall => (s, stall)))
            .Where(x => !string.IsNullOrEmpty(x.stall.Stacks)).OrderByDescending(x => x.stall.DelayMs).Take(12).ToList();
        if (sampled.Count > 0)
        {
            md.AppendLine("## 긴 정지 중 UI 스레드 호출 스택 표본");
            md.AppendLine();
            md.AppendLine("가장 안쪽 프레임이 먼저입니다. `모듈+0x…`는 이름을 얻지 못한 ReadyToRun 프레임입니다.");
            md.AppendLine();
            foreach (var (s, stall) in sampled)
            {
                md.AppendLine($"### {s.Size} {s.Variant} {s.Name}: {F(stall.DelayMs)} ms");
                md.AppendLine("```");
                foreach (string sample in stall.Stacks.Split(" || "))
                    md.AppendLine(sample.Replace(" < ", Environment.NewLine + "    < ", StringComparison.Ordinal));
                md.AppendLine("```");
            }

            md.AppendLine();
        }

        md.AppendLine("## 가장 긴 디스패처 작업 20개 (중첩 제외 시간)");
        md.AppendLine();
        md.AppendLine("| 크기 | 변형 | 시나리오 | 단계 | 작업 | ms |");
        md.AppendLine("|---|---|---|---|---|---:|");
        foreach (ProbeLongOperation op in recorder.LongOperations.OrderByDescending(op => op.ExclusiveMs).Take(20))
            md.AppendLine($"| {op.Size} | {op.Variant} | {op.Scenario} | {Md(op.Step)} | {Md(op.Operation)} | {F(op.ExclusiveMs)} |");
        md.AppendLine();
        if (recorder.BetweenStalls.Count > 0)
        {
            md.AppendLine($"시나리오 사이(측정 준비) 구간의 정지 {recorder.BetweenStalls.Count}회, 최대 {F(recorder.BetweenStalls.Max(s => s.DelayMs))} ms는 표에서 제외했습니다({prefix}-stalls.csv에 포함).");
            md.AppendLine();
        }

        md.AppendLine("## 파일");
        md.AppendLine("- " + prefix + ".csv: 시나리오별 전체 지표");
        md.AppendLine("- " + prefix + "-stalls.csv: 50 ms를 넘은 모든 정지와 원인 표본");
        md.AppendLine("- " + prefix + "-operations.csv: 시나리오별 디스패처 작업 이름·횟수·시간");
        md.AppendLine("- " + prefix + "-long-operations.csv: 16 ms 이상 걸린 개별 디스패처 작업");
        md.AppendLine("- " + prefix + "-breakdown.csv: 제품 메서드 직접 호출 시간(렌더·초안 스냅숏)");
        File.WriteAllText(Path.Combine(output, prefix + "-summary.md"), md.ToString(), new UTF8Encoding(false));
    }

    private static string SlowestStep(ProbeScenario s) =>
        s.SyncByStep.Count == 0 ? "-" : s.SyncByStep.OrderByDescending(pair => pair.Value.Max)
            .Select(pair => $"{pair.Key} (max {F(pair.Value.Max)} ms, n={pair.Value.Count})").First();

    private static string TopOperation(ProbeScenario s) =>
        s.Operations.Count == 0 ? "-" : s.Operations.OrderByDescending(pair => pair.Value.Max)
            .Select(pair => $"{pair.Key} (max {F(pair.Value.Max)} ms, total {F(pair.Value.Total)} ms, n={pair.Value.Count})").First();

    private static double Max(List<double> values) => values.Count == 0 ? 0 : values.Max();

    private static double P95(List<double> values)
    {
        if (values.Count == 0) return 0;
        double[] sorted = [.. values.Order()];
        return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
    }

    private static string F(double value) => value.ToString("0.0", Invariant);

    private static string Q(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + "\"";

    private static string Md(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
