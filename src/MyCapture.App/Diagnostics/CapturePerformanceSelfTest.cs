using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Diagnostics;

/// <summary>Same-process synthetic capture lifecycle. Never acquires the desktop or uses the clipboard.</summary>
internal static class CapturePerformanceSelfTest
{
    internal const string CommandLineSwitch = "--selftest-capture-performance";
    private const int Width = 3840, Height = 2160;
    private static readonly RectD Bounds = new(0, 0, Width, Height);
    private static readonly RectD Selection = new(120, 120, 640, 480);

    internal static int Run(string outputDirectory)
    {
        string output = DiagnosticOutputPaths.Create(outputDirectory);
        var failures = new List<string>();
        var iterations = new List<object>();
        var weak = new List<Lifetime>();
        var natural = new List<MemorySample>();
        object? residentAudit = null, disposedAudit = null;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        long runStarted = Stopwatch.GetTimestamp();
        var settings = new AppSettings();
        settings.Queue.MaxItems = 4;
        AppPaths paths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Child(output, "synthetic-capture-queue-" + Guid.NewGuid().ToString("N")));
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance,
            _ => Task.FromResult(true)); // Acknowledge the Done copy contract without publishing to the user's clipboard.
        CaptureRecord? currentRecord = null;
        CaptureEditSession? editSession = null;
        int round = -1;
        double acquisitionMs = 0;
        using var metrics = new Metrics();
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance,
            _ =>
            {
                long started = Stopwatch.GetTimestamp();
                // Deterministically expose the frame-less selector interval; no native capture.
                Thread.Sleep(150);
                BitmapSource image = SyntheticBitmap(round);
                weak.Add(new Lifetime(round, "desktop-bitmap", new WeakReference(image)));
                acquisitionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return new FrozenFrame(image, Bounds, null, acquisitionMs);
            }, () => Bounds);
        coordinator.ApplyCaptureExclusion = _ => true; // Safe: acquisition only synthesizes pixels.
        coordinator.TransitionFailed += ex => failures.Add(ex.ToString());
        coordinator.SelectionPersistRequested = async selected =>
        {
            weak.Add(new Lifetime(round, "crop", new WeakReference(selected.SelectedBitmap)));
            currentRecord = await persistence.PersistOriginalAsync(selected.SelectedBitmap, 1, "Synthetic", "Synthetic");
            editSession = commit.BeginEditSession(currentRecord);
        };
        coordinator.CommitRequested = result => commit.CommitAsync(currentRecord, result, editSession);
        try
        {
            Pump(200);
            natural.Add(metrics.Sample(-1, "initial-idle"));
            // Two warm-up cycles plus twenty measured cycles, without forced collections.
            for (round = 0; round < 22; round++)
            {
                if (Stopwatch.GetElapsedTime(runStarted).TotalSeconds > 75)
                    throw new TimeoutException("Synthetic capture soak exceeded its 75-second work budget.");
                long allocated = GC.GetTotalAllocatedBytes(false);
                iterations.Add(RunIteration(coordinator, metrics, weak, round, () => acquisitionMs));
                editSession?.Dispose();
                editSession = null;
                currentRecord = null;
                Pump(150);
                natural.Add(metrics.Sample(round, "closed-idle"));
                iterations.Add(new { Round = round, Kind = "natural-lifetime-after-close",
                    Alive = weak.Where(item => item.Round == round && item.Reference.IsAlive).Select(item => item.Kind).ToArray() });
                iterations.Add(new { Round = round, Kind = "allocation", Warmup = round < 2,
                    AllocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated, QueueRecords = queue.Count });
            }
            Pump(300);
            natural.Add(metrics.Sample(round, "natural-immediate-idle"));
            // Observe a real idle interval separately from the following forced lifetime audit.
            Pump(10_000);
            natural.Add(metrics.Sample(round, "natural-final-idle"));
            metrics.StopSampling();
            // Lifetime audit is diagnostic-only and AFTER all natural-memory/timing samples.
            CollectForAudit();
            residentAudit = Audit(weak, metrics.Sample(round, "post-gc-resident"));
            coordinator.Dispose();
            Pump(100);
            CollectForAudit();
            disposedAudit = Audit(weak, metrics.Sample(round, "post-gc-disposed"));
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            metrics.StopSampling();
            coordinator.Cancel();
            editSession?.Dispose();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        object report = new
        {
            Utc = DateTimeOffset.UtcNow,
            Build = typeof(CapturePerformanceSelfTest).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Os = Environment.OSVersion.ToString(), LogicalProcessors = Environment.ProcessorCount,
            RenderingTier = RenderCapability.Tier >> 16,
            MaxHardwareTextureSize = typeof(RenderCapability).GetProperty("MaxHardwareTextureSize",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null)?.ToString(),
            Width, Height, Selection, WarmupRounds = 2, MeasuredRounds = 20,
            Limits = "Fresh synthetic desktop bitmap per round; real visible selector/editor, crop and isolated PNG persistence. Done every fifth round, otherwise editor close. Clipboard sink acknowledges Done without publishing bytes. No native desktop capture, screenshots, clipboard, OCR, user queue, or resident App field wiring. Pointer stream calls actual UpdatePointer/magnifier with injected coordinates; 8ms timer bursts of eight updates for 300ms with no native warp in the measured stream; one initial cursor placement and conditional restoration exercise IsMouseOver. Hardware/OS input latency is excluded. Render gaps are WPF observations, not DWM presentation proof. MaxHardwareTextureSize is null if not publicly exposed. No forced GC during natural soak. Same fixture/settings/session required for baseline comparison. No performance or memory threshold is silently treated as a pass.",
            Iterations = iterations, NaturalMemory = natural, PeriodicMemory = metrics.Samples,
            ResidentWeakReferenceAudit = residentAudit, DisposedWeakReferenceAudit = disposedAudit,
            WallMs = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds,
            Failures = failures,
        };
        File.WriteAllText(DiagnosticOutputPaths.Child(output, "capture-performance.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(DiagnosticOutputPaths.Child(output, "capture-performance-report.txt"),
            (failures.Count == 0 ? "RESULT: PASS (functional lifecycle only; inspect measurement JSON)" : "RESULT: FAIL")
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
        return failures.Count == 0 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object RunIteration(CaptureOverlayCoordinator coordinator, Metrics metrics,
        List<Lifetime> weak, int round, Func<double> acquisition)
    {
        long started = Stopwatch.GetTimestamp();
        double firstOverlay = -1, firstFrame = -1, firstEditor = -1;
        int blankFrames = 0;
        PointD previousCursor = CursorLocator.GetPosition();
        PointD diagnosticCursor = new(320, 320);
        bool cursorPlaced = false;
        var gaps = new List<double>();
        var heartbeatGaps = new List<double>();
        long previousRender = 0;
        long previousHeartbeat = Stopwatch.GetTimestamp();
        var heartbeat = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
        EventHandler heartbeatTick = (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            heartbeatGaps.Add(Stopwatch.GetElapsedTime(previousHeartbeat, now).TotalMilliseconds);
            previousHeartbeat = now;
        };
        void Rendering(object? sender, EventArgs args)
        {
            long now = Stopwatch.GetTimestamp();
            if (previousRender != 0) gaps.Add(Stopwatch.GetElapsedTime(previousRender, now).TotalMilliseconds);
            previousRender = now;
            CaptureOverlayWindow? window = ReadField<CaptureOverlayWindow>(coordinator, "_activeOverlay");
            if (window?.IsVisible == true)
            {
                double elapsed = Stopwatch.GetElapsedTime(started, now).TotalMilliseconds;
                if (firstOverlay < 0) firstOverlay = elapsed;
                if (window.Content is CaptureOverlayView view && view.Frame is not null)
                { if (firstFrame < 0) firstFrame = elapsed; }
                else blankFrames++;
            }
            if (ReadField<AnnotationEditorWindow>(coordinator, "_activeEditor")?.IsVisible == true && firstEditor < 0)
                firstEditor = Stopwatch.GetElapsedTime(started, now).TotalMilliseconds;
        }
        CompositionTarget.Rendering += Rendering;
        heartbeat.Tick += heartbeatTick;
        heartbeat.Start();
        try
        {
            long call = Stopwatch.GetTimestamp();
            coordinator.Start(false, false, true);
            double startCallMs = Stopwatch.GetElapsedTime(call).TotalMilliseconds;
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted, 10_000);
            coordinator.LastPreparationForTest.GetAwaiter().GetResult();
            PumpUntil(() => firstFrame >= 0, 3000);
            CaptureOverlayWindow overlay = ReadField<CaptureOverlayWindow>(coordinator, "_activeOverlay")
                ?? throw new InvalidOperationException("Synthetic selector did not open.");
            weak.Add(new Lifetime(round, "overlay", new WeakReference(overlay)));
            weak.Add(new Lifetime(round, "overlay-view", new WeakReference(overlay.Content)));
            // Exercise the real IsMouseOver/magnifier branch. Only this initial placement is
            // native; the measured pointer stream uses injected coordinates. Restore below
            // only if the user has not moved the cursor during the diagnostic.
            cursorPlaced = CursorLocator.TrySetPosition(diagnosticCursor);
            Pump(50);
            MemorySample selectedMemory = metrics.Sample(round, "selector-ready");
            object pointer = MeasurePointer((CaptureOverlayView)overlay.Content);
            long select = Stopwatch.GetTimestamp();
            overlay.CompleteSelection(Selection);
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted, 10_000);
            coordinator.LastTransitionForTest.GetAwaiter().GetResult();
            PumpUntil(() => firstEditor >= 0, 3000);
            AnnotationEditorWindow editor = ReadField<AnnotationEditorWindow>(coordinator, "_activeEditor")
                ?? throw new InvalidOperationException("Synthetic editor did not open.");
            weak.Add(new Lifetime(round, "editor", new WeakReference(editor)));
            weak.Add(new Lifetime(round, "editor-control", new WeakReference(editor.Editor)));
            MemorySample editorMemory = metrics.Sample(round, "editor-ready");
            string[] aliveInEditor = weak.Where(item => item.Round == round && item.Reference.IsAlive)
                .Select(item => item.Kind).ToArray();
            double selectToEditorMs = Stopwatch.GetElapsedTime(select).TotalMilliseconds;
            Pump(60);
            bool save = round % 5 == 4;
            long finish = Stopwatch.GetTimestamp();
            if (save)
            {
                // Calls the same private command as the Done button; no production-only seam.
                MethodInfo method = typeof(AnnotationEditorControl).GetMethod("Commit", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("AnnotationEditorControl.Commit");
                object?[] arguments = method.GetParameters().Length == 1
                    ? [EditorCommitAction.Done] : [EditorCommitAction.Done, false];
                method.Invoke(editor.Editor, arguments);
            }
            else editor.Close();
            PumpUntil(() => !coordinator.IsActive, 10_000);
            if (save && !editor.WasCommitted) throw new InvalidOperationException("Done did not commit the synthetic capture.");
            return new { Round = round, Warmup = round < 2, Saved = save, StartCallMs = startCallMs,
                AcquisitionMs = acquisition(), FirstVisibleOverlayRenderMs = firstOverlay,
                FirstFrameRenderMs = firstFrame, BlankRenderObservations = blankFrames,
                VisibleWithoutFrameMs = firstOverlay >= 0 ? Math.Max(0, firstFrame - firstOverlay) : 0,
                FirstEditorRenderMs = firstEditor, SelectToEditorMs = selectToEditorMs,
                FinishMs = Stopwatch.GetElapsedTime(finish).TotalMilliseconds,
                PointerStream = pointer,
                RenderGaps = Summarize(gaps), DispatcherHeartbeatGaps = Summarize(heartbeatGaps),
                AliveWhileEditorOpen = aliveInEditor, SelectorMemory = selectedMemory, EditorMemory = editorMemory };
        }
        finally
        {
            heartbeat.Stop();
            heartbeat.Tick -= heartbeatTick;
            CompositionTarget.Rendering -= Rendering;
            coordinator.Cancel();
            if (cursorPlaced && CursorLocator.GetPosition() == diagnosticCursor)
                _ = CursorLocator.TrySetPosition(previousCursor);
        }
    }

    private static T? ReadField<T>(object owner, string name) where T : class =>
        typeof(CaptureOverlayCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) as T;

    private static object MeasurePointer(CaptureOverlayView view)
    {
        Func<PointD> previousRead = view.ReadCursor;
        Func<PointD, bool> previousPlace = view.PlaceCursor;
        PointD pointer = new(160, 160);
        view.ReadCursor = () => pointer;
        view.PlaceCursor = target => { pointer = target; return true; };
        var callbacks = new List<double>();
        var renderGaps = new List<double>();
        long previousRender = 0, callbackAllocations = 0;
        int updates = 0, mouseOverCallbacks = 0;
        bool mouseOverAtStart = view.IsMouseOver;
        long started = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
        EventHandler tick = (_, _) =>
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long callbackStarted = Stopwatch.GetTimestamp();
            if (view.IsMouseOver) mouseOverCallbacks++;
            for (int burst = 0; burst < 8; burst++)
            {
                updates++;
                pointer = new PointD(160 + updates * 7 % 2600, 160 + updates * 3 % 1200);
                view.UpdatePointer(precision: false, active: true);
            }
            double elapsed = Stopwatch.GetElapsedTime(callbackStarted).TotalMilliseconds;
            callbackAllocations += GC.GetAllocatedBytesForCurrentThread() - allocated;
            callbacks.Add(elapsed);
        };
        EventHandler rendering = (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            if (previousRender != 0) renderGaps.Add(Stopwatch.GetElapsedTime(previousRender, now).TotalMilliseconds);
            previousRender = now;
        };
        timer.Tick += tick;
        CompositionTarget.Rendering += rendering;
        try { timer.Start(); Pump(300); }
        finally
        {
            timer.Stop(); timer.Tick -= tick;
            CompositionTarget.Rendering -= rendering;
            view.ReadCursor = previousRead;
            view.PlaceCursor = previousPlace;
        }
        return new { Updates = updates, WallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MouseOverAtStart = mouseOverAtStart, MouseOverAtEnd = view.IsMouseOver,
            MouseOverCallbacks = mouseOverCallbacks, TotalCallbacks = callbacks.Count,
            CallbackAllocatedBytes = callbackAllocations, Callback = Summarize(callbacks), RenderGaps = Summarize(renderGaps) };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BitmapSource SyntheticBitmap(int seed)
    {
        int stride = Width * 4;
        byte[] row = new byte[stride];
        for (int x = 0; x < Width; x++)
        {
            row[x * 4] = (byte)(80 + (x / 32 + seed) % 120);
            row[x * 4 + 1] = (byte)(100 + (x / 64) % 100);
            row[x * 4 + 2] = 220;
            row[x * 4 + 3] = 255;
        }
        // Match ScreenCaptureEngine's direct native back-buffer ownership. A full managed
        // pixel array here would add a fixture-only 32 MiB LOH allocation every round and
        // distort both native bitmap pressure and collection timing.
        var image = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
        image.Lock();
        try
        {
            for (int y = 0; y < Height; y++)
                Marshal.Copy(row, 0, IntPtr.Add(image.BackBuffer, y * image.BackBufferStride), stride);
            image.AddDirtyRect(new Int32Rect(0, 0, Width, Height));
        }
        finally { image.Unlock(); }
        image.Freeze();
        return image;
    }

    private static object Summarize(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return new { Count = sorted.Length, P50Ms = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2],
            P95Ms = sorted.Length == 0 ? 0 : sorted[(int)Math.Floor((sorted.Length - 1) * .95)],
            MaxMs = sorted.LastOrDefault(), Over50Ms = sorted.Count(value => value > 50), Over100Ms = sorted.Count(value => value > 100) };
    }

    private static object Audit(List<Lifetime> weak, MemorySample memory) => new
    { Memory = memory, Alive = weak.Where(item => item.Reference.IsAlive).Select(item => new { item.Round, item.Kind }).ToArray(), Tracked = weak.Count };

    private static void CollectForAudit()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Pump(100);
    }

    private static void Pump(int milliseconds)
    {
        long started = Stopwatch.GetTimestamp();
        PumpUntil(() => Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds, milliseconds + 3000);
    }

    private static void PumpUntil(Func<bool> complete, int timeoutMs)
    {
        long started = Stopwatch.GetTimestamp();
        while (!complete())
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > timeoutMs) throw new TimeoutException("Synthetic UI phase timed out.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            EventHandler tick = (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Tick += tick;
            try { timer.Start(); Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); timer.Tick -= tick; }
        }
    }

    private sealed record Lifetime(int Round, string Kind, WeakReference Reference);
    private sealed record MemorySample(double ElapsedMs, int Round, string Phase, long PrivateBytes,
        long WorkingSetBytes, long ManagedBytes, long HeapBytes, long CommittedBytes, long FragmentedBytes,
        int Handles, uint UserHandles, uint GdiHandles, int Gen0, int Gen1, int Gen2);

    private sealed class Metrics : IDisposable
    {
        private readonly object _gate = new();
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly System.Threading.Timer _timer;
        private int _stopped;
        internal List<MemorySample> Samples { get; } = [];
        internal Metrics() => _timer = new System.Threading.Timer(_ =>
        {
            lock (_gate) Samples.Add(Sample(-1, "periodic-natural"));
        }, null, 50, 50);
        internal MemorySample Sample(int round, string phase)
        {
            lock (_gate)
            {
                _process.Refresh();
                GCMemoryInfo gc = GC.GetGCMemoryInfo();
                return new(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, round, phase,
                    _process.PrivateMemorySize64, _process.WorkingSet64, GC.GetTotalMemory(false),
                    gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes, _process.HandleCount,
                    GetGuiResources(_process.Handle, 1), GetGuiResources(_process.Handle, 0),
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            }
        }
        internal void StopSampling()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            using var stopped = new ManualResetEvent(false);
            if (_timer.Dispose(stopped)) stopped.WaitOne();
        }
        public void Dispose() { StopSampling(); _process.Dispose(); }
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
