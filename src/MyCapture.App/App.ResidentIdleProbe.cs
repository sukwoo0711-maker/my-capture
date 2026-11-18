using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;

namespace MyCapture.App;

public partial class App
{
    /// <summary>
    /// An isolated, fixed-state CPU observation. Normal runs observe 360 ten-second intervals;
    /// quick runs only check the measurement plumbing and cannot satisfy the idle CPU budget.
    /// The ordinary resident heartbeat and production idle reclamation stay enabled.
    /// </summary>
    private async Task ProbeIdleCpuAsync(ResidentProbeOptions probe, ResidentProbeRecorder recorder, string scenario)
    {
        try { await ProbeIdleCpuCoreAsync(probe, recorder, scenario); }
        finally { if (scenario == "idle-library-open") _galleryWindow?.Close(); }
    }

    private async Task ProbeIdleCpuCoreAsync(ResidentProbeOptions probe, ResidentProbeRecorder recorder, string scenario)
    {
        if (_queue is null || _settings is null)
            throw new InvalidOperationException("The capture library is unavailable.");
        if (_queue.Count != 0)
            throw new InvalidOperationException("An idle CPU condition requires its own fresh profile.");
        if (probe.Scenarios.Count != 1)
            throw new InvalidOperationException("An idle CPU condition must be the only scenario in a fresh process.");
        if (probe.Audit || probe.Variants.Count != 0 || probe.SkipIndexing || probe.SoftwareRender)
            throw new InvalidOperationException("An idle CPU condition requires ordinary production settings.");

        _settings.Ocr.Quality = OcrQuality.Fast;
        OcrQualityProfile.Synchronize(_settings.Ocr);
        int expectedCount = scenario == "idle-empty" ? 0 : 2;
        for (int index = 0; index < expectedCount; index++)
            await ProbeCaptureToCardAsync(recorder, "idle preparation", dwellMs: 300);
        await _capturePersistenceTask;
        await ProbeWaitAsync(() => !UnexpectedIdleWindowVisible(), 10_000, "idle preparation windows closed");
        // Let queued automatic indexing requests enter, then wait for their actual completion.
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        await ProbeWaitAsync(() => !_indexingRunning && !_indexingRequested, 60_000, "idle indexing completion");
        if (_queue.Count != expectedCount)
            throw new InvalidOperationException($"Expected {expectedCount} captures; found {_queue.Count}.");
        if (scenario is "idle-library-open" or "idle-library-closed")
        {
            await ProbeLibraryAsync(recorder, "idle preparation library", dwellMs: 500);
            if (scenario == "idle-library-open") HandleGalleryRequested();
        }
        await recorder.SettleAsync("idle condition warmup", probe.Quick ? [0, 2] : [0, 30, 60]);

        int intervalCount = probe.Quick ? 2 : 360;
        int intervalSeconds = probe.Quick ? 1 : 10;
        string samplesPath = DiagnosticOutputPaths.Child(probe.Output, scenario + "-cpu-samples.jsonl");
        using var activities = new IdleActivityObservation(_queue.Activities);
        using var process = Process.GetCurrentProcess();
        using var stream = new FileStream(samplesPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            Kind = "start", Scenario = scenario, Utc = DateTimeOffset.UtcNow, ProcessId = Environment.ProcessId,
            LogicalProcessors = Environment.ProcessorCount, Quality = _settings.Ocr.Quality.ToString(),
            Captures = _queue.Count, probe.Quick, TargetSeconds = intervalCount * intervalSeconds,
            Note = "Process CPU includes diagnostic heartbeat and sampling overhead; updates, shortcuts and clipboard are isolated by resident probe.",
        }));
        var percentages = new List<double>(intervalCount);
        var clock = Stopwatch.StartNew();
        TimeSpan initialCpu = process.TotalProcessorTime;
        TimeSpan lastCpu = initialCpu;
        double lastWall = 0;
        bool stateChanged = false;
        bool samplingInterrupted = false;
        for (int sample = 1; sample <= intervalCount; sample++)
        {
            TimeSpan deadline = TimeSpan.FromSeconds(sample * intervalSeconds);
            while (true)
            {
                TimeSpan remaining = deadline - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining);
            }
            process.Refresh();
            TimeSpan cpu = process.TotalProcessorTime;
            double wall = clock.Elapsed.TotalMilliseconds;
            double cpuMs = (cpu - lastCpu).TotalMilliseconds;
            double wallMs = wall - lastWall;
            samplingInterrupted |= wallMs > intervalSeconds * 1500;
            double percent = cpuMs / wallMs / Environment.ProcessorCount * 100;
            percentages.Add(percent);
            bool libraryVisible = _galleryWindow is { IsVisible: true };
            bool changed = _queue.Count != expectedCount || _indexingRunning || _indexingRequested || activities.Count != 0
                || _settings.Ocr.Quality != OcrQuality.Fast || !_capturePersistenceTask.IsCompleted
                || libraryVisible != (scenario == "idle-library-open") || UnexpectedIdleWindowVisible();
            stateChanged |= changed;
            TimeSpan sampleCpuBefore = process.TotalProcessorTime;
            long sampleWallBefore = Stopwatch.GetTimestamp();
            ResidentProbeRecorder.Row row = recorder.Sample(scenario, (int)(wall / 1000));
            double sampleWallMs = Stopwatch.GetElapsedTime(sampleWallBefore).TotalMilliseconds;
            double sampleCpuMs = (process.TotalProcessorTime - sampleCpuBefore).TotalMilliseconds;
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                Kind = "sample", Sample = sample, Utc = DateTimeOffset.UtcNow, ElapsedMilliseconds = wall,
                CpuMilliseconds = cpuMs, IntervalMilliseconds = wallMs, MachineCpuPercent = percent,
                Captures = _queue.Count, IndexingRunning = _indexingRunning, IndexingRequested = _indexingRequested,
                LibraryVisible = libraryVisible, StateChanged = changed,
                SamplingInterrupted = samplingInterrupted,
                ActivityEventsSinceStart = activities.Count, SampleWallMs = sampleWallMs, SampleCpuMs = sampleCpuMs,
                row.PrivateMb, row.WorkingSetMb, row.UiP95Ms, row.UiMaxMs, row.UiSamples,
                row.GdiHandles, row.UserHandles, row.Handles, row.Threads, row.Gen0, row.Gen1, row.Gen2,
            }));
            lastCpu = cpu;
            lastWall = wall;
        }
        double mean = (lastCpu - initialCpu).TotalMilliseconds / lastWall / Environment.ProcessorCount * 100;
        double p95 = percentages.Order().ElementAt((int)Math.Ceiling(percentages.Count * 0.95) - 1);
        bool complete = !probe.Quick && lastWall >= 3_600_000 && !stateChanged && !samplingInterrupted;
        bool budgetMet = mean <= 0.1 && p95 <= 0.5;
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            Kind = "complete", ElapsedMilliseconds = lastWall,
            CpuMilliseconds = (lastCpu - initialCpu).TotalMilliseconds,
            AverageMachineCpuPercent = mean, IntervalP95MachineCpuPercent = p95,
            StateChanged = stateChanged, SamplingInterrupted = samplingInterrupted, FullDurationCompleted = complete,
            CpuBudgetResult = complete ? (budgetMet ? "PASS" : "FAIL") : "NOT_ASSESSED",
        }));
        if (stateChanged) throw new InvalidOperationException("The idle condition changed during observation.");
        if (samplingInterrupted) throw new InvalidOperationException("The idle CPU sampling interval was interrupted.");
        if (complete && !budgetMet)
            throw new InvalidDataException($"Idle CPU observation completed; budget failed: mean {mean:F5}%, interval p95 {p95:F5}%.");
    }

    private bool UnexpectedIdleWindowVisible() => _quickAccess is { IsVisible: true }
        || ProbeOverlay is { IsVisible: true } || ProbeEditor is { IsVisible: true }
        || ProbeOcrWindow() is { IsVisible: true }
        || (_pins?.SnapshotOpenPinWindows().Any(window => window.IsShown) ?? false);

    /// <summary>Observes every library-activity event, including work wholly between two samples.</summary>
    private sealed class IdleActivityObservation : IDisposable
    {
        private readonly LibraryActivityTracker _tracker;
        private int _count;
        internal IdleActivityObservation(LibraryActivityTracker tracker)
        {
            _tracker = tracker;
            tracker.Changed += Changed;
        }
        internal int Count => Volatile.Read(ref _count);
        private void Changed(object? sender, EventArgs args) => Interlocked.Increment(ref _count);
        public void Dispose() => _tracker.Changed -= Changed;
    }
}
