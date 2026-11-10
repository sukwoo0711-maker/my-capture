using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Pinning;
using MyCapture.App.Diagnostics;

namespace MyCapture.App;

public partial class App
{
    private readonly IdleMemoryReclamation _idleMemory = new();
    private DispatcherTimer? _idleMemoryTimer;
    private bool _ocrMemoryBusy;
    private bool _memoryReclamationStopped;

    private void CancelIdleMemoryReclamation()
    {
        _idleMemoryTimer?.Stop();
        // Capture commands remain usable when the optional resident memory state is absent.
        _idleMemory?.FlowStarted();
    }

    private void ScheduleIdleMemoryReclamation()
    {
        if (_idleMemory is null || _memoryReclamationStopped || _shutdownRequested || Dispatcher.HasShutdownStarted
            || s_residentProbe?.Has("no-idle-gc") == true) return;
        _idleMemory.FlowEnded(Environment.TickCount64);
        _idleMemoryTimer ??= new DispatcherTimer(DispatcherPriority.ContextIdle, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(IdleMemoryReclamation.DelayMilliseconds),
        };
        _idleMemoryTimer.Tick -= ReclaimIdleMemory;
        _idleMemoryTimer.Tick += ReclaimIdleMemory;
        _idleMemoryTimer.Stop();
        _idleMemoryTimer.Start();
    }

    private bool CanReclaimIdleMemory() =>
        !_shutdownRequested && !_preparingExit && !_storeInstallationPending
        && _overlay?.IsActive != true && _recorder?.IsActive != true
        && !_preparingAdvancedCapture && _activeCountdown is null && _scrollCancellation is null
        && !_pasteToScreenInFlight && !_gitHubUploadInFlight && !_ocrMemoryBusy && !_indexingRunning
        && _textCaptureCancellation is null
        && _capturePersistenceTask.IsCompleted && _sourceBackfillTask.IsCompleted
        && _pins?.IsInteractionActive != true
        && Mouse.LeftButton != MouseButtonState.Pressed && Mouse.RightButton != MouseButtonState.Pressed
        && !Windows.Cast<Window>().Any(window => window.IsVisible && window is not PinWindow);

    private void ReclaimIdleMemory(object? sender, EventArgs e)
    {
        _idleMemoryTimer?.Stop();
        _idleMemory?.TryCollect(Environment.TickCount64, CanReclaimIdleMemory(), () =>
        {
            // Remain on the dispatcher: no background check/collect race with a capture
            // starting on the UI thread. Input and capture work outrank ContextIdle.
            long started = Stopwatch.GetTimestamp();
            ResidentLongRunEvidence? evidence = _longRunEvidence;
            int before = evidence is null ? 0 : GC.CollectionCount(2);
            evidence?.ObserveIdleGc(new("start", started, started, before, before, null, null, null));
            double? collectMs = null;
            double? workMs = null;
            Exception? failure = null;
            try
            {
                Capture.NativeSelectorChromeCache.TrimForIdle();
                long workStart = evidence is null ? 0 : Stopwatch.GetTimestamp();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                if (evidence is not null) collectMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
                TrimProcessHeap();
                if (evidence is not null) workMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
                _log?.LogInformation("Idle memory collection completed in {PauseMs:F2} ms",
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                evidence?.ObserveIdleGc(new("finish", started, Stopwatch.GetTimestamp(), before,
                    GC.CollectionCount(2), collectMs, workMs,
                    failure?.GetType().FullName));
            }
        });
    }

    /// <summary>
    /// Native code that frees into the process heap (ONNX Runtime without its arena, Skia, WIC) leaves
    /// the freed pages committed for later allocations; hands them back like the collection does.
    /// </summary>
    private static void TrimProcessHeap()
    {
        IntPtr heap = GetProcessHeap();
        _ = HeapCompact(heap, 0);
        var information = new HeapOptimizeResourcesInformation { Version = HeapOptimizeResourcesCurrentVersion };
        _ = HeapSetInformation(heap, HeapOptimizeResources, ref information, (nuint)Marshal.SizeOf<HeapOptimizeResourcesInformation>());
    }

    private void ObserveIdleMemoryWindow(Window window)
    {
        // Loaded can repeat when content is reattached; named handlers make registration idempotent.
        window.IsVisibleChanged -= OnMemoryWindowVisibilityChanged;
        window.IsVisibleChanged += OnMemoryWindowVisibilityChanged;
        window.Closed -= OnMemoryWindowClosed;
        window.Closed += OnMemoryWindowClosed;
        window.PreviewMouseDown -= OnMemoryWindowInputStarted;
        window.PreviewMouseDown += OnMemoryWindowInputStarted;
        window.PreviewMouseUp -= OnMemoryWindowInputEnded;
        window.PreviewMouseUp += OnMemoryWindowInputEnded;
        CancelIdleMemoryReclamation();
    }

    private void OnMemoryWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) CancelIdleMemoryReclamation();
        else ScheduleIdleMemoryReclamation();
    }

    private void OnMemoryWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.IsVisibleChanged -= OnMemoryWindowVisibilityChanged;
            window.Closed -= OnMemoryWindowClosed;
            window.PreviewMouseDown -= OnMemoryWindowInputStarted;
            window.PreviewMouseUp -= OnMemoryWindowInputEnded;
        }
        ScheduleIdleMemoryReclamation();
    }

    private void OnMemoryWindowInputStarted(object sender, MouseButtonEventArgs e) => CancelIdleMemoryReclamation();

    private void OnMemoryWindowInputEnded(object sender, MouseButtonEventArgs e)
    {
        if (sender is PinWindow) ScheduleIdleMemoryReclamation();
    }

    private async Task ObserveMemoryWorkAsync(Task work)
    {
        try { await work; }
        finally { ScheduleIdleMemoryReclamation(); }
    }

    private const int HeapOptimizeResources = 3;
    private const uint HeapOptimizeResourcesCurrentVersion = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapOptimizeResourcesInformation
    {
        public uint Version;
        public uint Flags;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetProcessHeap();
    [DllImport("kernel32.dll")] private static extern nuint HeapCompact(IntPtr heap, uint flags);
    [DllImport("kernel32.dll")]
    private static extern bool HeapSetInformation(IntPtr heap, int informationClass,
        ref HeapOptimizeResourcesInformation information, nuint length);
}
