// Work-only scenario, paired with the opt-in OcrWholeSpinLifecycleTests harness.
// Requires the accompanying optional progress-observer proposal in NeuralOcrEngine.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using MyCapture.Ocr;
using Xunit;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;
using Evidence = MyCapture.App.Tests.OcrWholeSpinExperimentEvidence;

namespace MyCapture.App.Tests;

internal sealed class WholeSpinLifecycleScenario : IDisposable
{
    internal sealed record Input(Data.Sample Sample, OcrRequest Request);
    internal sealed record NativeFailure(long Timestamp, int ThreadId, int HResult,
        string Message, string ExceptionStack, string ObserverStack);
    internal sealed record ProgressPoint(long Timestamp, int Completed, int Total);
    internal sealed record ConfigEntry(string Key, string Value);
    // Completed AddSessionConfigEntry calls on one initialization's SessionOptions; not a native readback.
    internal sealed record ConfigApplication(int Initialization, long Timestamp, int ThreadId, ConfigEntry[] Entries);
    private sealed class Frame(string id)
    {
        internal readonly string Id = id;
        internal object? Raw;
        internal OcrResult? Neural, Windows, Final;
        internal Exception? NeuralError, WindowsError;
        internal int RawCalls, NeuralCalls, WindowsCalls;
        internal long Started, Finished, NeuralFinished;
    }

    private readonly NeuralOcrEngine _engine;
    private readonly CaptureOcrService _service;
    private readonly ProgressObserver _progress = new();
    private Frame? _active;
    private int _initializations, _rowCalls;
    private TaskCompletionSource<long> _released = Signal();
    internal readonly ConcurrentQueue<Evidence.SessionReceipt> Sessions = new();
    internal readonly ConcurrentQueue<ConfigApplication> ConfigApplications = new();
    internal ConfigEntry[] RequestedConfigEntries { get; }
    internal readonly ConcurrentQueue<long> Releases = new();
    internal readonly List<object> Observations = [];
    internal readonly List<string> CompletedPhases = [];
    internal bool WindowsAvailable { get; }
    internal string[] WindowsLanguages { get; }

    internal WholeSpinLifecycleScenario(OcrModelStore verifiedOwnedStore, bool noSpin,
        ConfigEntry[] configEntries, Action<Evidence.SessionReceipt> initializing)
    {
        // no-spin stays on the product flag; callback entries are only the deliberate candidate difference.
        Assert.False(noSpin && configEntries.Length > 0);
        Assert.Equal(configEntries.Length, configEntries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count());
        RequestedConfigEntries = configEntries.ToArray();
        _engine = new NeuralOcrEngine(verifiedOwnedStore, NullLogger<NeuralOcrEngine>.Instance,
            runtime: null, engineIdleRelease: null,
            disableWholeSessionSpinning: noSpin,
            wholeSessionInitializing: (options, detector, classifier, recognizer, dictionary) =>
            {
                int initialization = Interlocked.Increment(ref _initializations);
                // Applied before the receipt and before the engine opens InitModels with these options.
                var applied = new List<ConfigEntry>(RequestedConfigEntries.Length);
                try
                {
                    foreach (ConfigEntry entry in RequestedConfigEntries)
                    {
                        options.AddSessionConfigEntry(entry.Key, entry.Value);
                        applied.Add(entry);
                    }
                }
                finally
                {
                    ConfigApplications.Enqueue(new(initialization, Stopwatch.GetTimestamp(),
                        Environment.CurrentManagedThreadId, applied.ToArray()));
                }
                var session = Evidence.Session(options, detector, classifier, recognizer, dictionary, noSpin);
                Sessions.Enqueue(session);
                initializing(session);
            },
            wholeResultObserved: raw =>
            {
                Frame frame = Volatile.Read(ref _active) ?? throw new InvalidOperationException("Unowned OCR callback.");
                Interlocked.Increment(ref frame.RawCalls);
                frame.Raw = Evidence.Raw(raw);
            },
            wholeProgressObserved: _progress, wholeStageObserved: null,
            // This contract predates #223, which made the bounded spin the product default. Apart from
            // no-spin the product adds no spin entries here, so only the candidate callback adds them.
            runtimeWholeSessionSpinning: !noSpin);
        _engine.EngineReleased += () =>
        {
            long now = Stopwatch.GetTimestamp();
            Releases.Enqueue(now);
            Volatile.Read(ref _released).TrySetResult(now);
        };
        var windows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        WindowsAvailable = windows.IsAvailable;
        WindowsLanguages = windows.SupportedLanguages.ToArray();
        _service = new CaptureOcrService(new ObservedWindows(windows, () => Current),
            async (request, token) =>
            {
                Frame frame = Current;
                Interlocked.Increment(ref frame.NeuralCalls);
                try { return frame.Neural = await _engine.RecognizeAsync(request, token).ConfigureAwait(false); }
                catch (Exception error) { frame.NeuralError = error; throw; }
                finally { frame.NeuralFinished = Stopwatch.GetTimestamp(); }
            }, () => _engine.CanRun, () => _engine.IsReady, NullLogger<CaptureOcrService>.Instance,
            readNeuralRows: (_, _, _) =>
            {
                Interlocked.Increment(ref _rowCalls);
                throw new InvalidOperationException("Whole lifecycle scenario entered row recognition.");
            });
    }

    private Frame Current => Volatile.Read(ref _active) ?? throw new InvalidOperationException("No active call.");
    private static TaskCompletionSource<long> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task<object> RunAsync(Input[] inputs, IReadOnlyDictionary<string, string>? defaultReference,
        Action<IReadOnlyDictionary<string, string>> warmupCompleted)
    {
        // The eventual harness must pin all five PNG/truth/model/source/native hashes before this call.
        Assert.Equal(new[] { "ko-00-100-light", "en-00-100-light", "mixed-00-100-light", "small-00-100-light", "code-00-100-light" },
            inputs.Select(input => input.Sample.Id).ToArray());
        Assert.All(inputs, input =>
        {
            Assert.Equal("development", input.Sample.Split);
            Assert.True(input.Request.UseNeuralModel);
            Assert.False(input.Request.RecoverLatinIntrusions);
        });
        await _engine.ModelVerificationTask.ConfigureAwait(false);
        Assert.True(_engine.CanRun);
        Assert.Equal(TimeSpan.FromSeconds(20), NeuralOcrEngine.EngineIdleRelease);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Input input in inputs)
        {
            Frame first = await Invoke(input, "warm-control", CancellationToken.None).ConfigureAwait(false);
            string exact = Quality(first, input);
            expected.Add(input.Sample.Id, exact);
            if (defaultReference is not null) Assert.Equal(defaultReference[input.Sample.Id], exact);
        }
        Assert.True(_engine.IsReady);
        Assert.Equal(1, Volatile.Read(ref _initializations));
        RequireConfigApplications(1);
        Assert.Empty(Releases);
        warmupCompleted(expected);
        CompletedPhases.Add("warm-control");

        Frame cancelled;
        object cancellationEvidence;
        using (var cancellation = new CancellationTokenSource())
        {
            // Invoke owns a separate 3-minute safety deadline. This CTS is cancelled only by real progress.
            using (_progress.Arm(cancellation))
                cancelled = await Invoke(inputs[0], "cancel-after-first-real-line", cancellation.Token).ConfigureAwait(false);
            cancellationEvidence = _progress.Snapshot();
        }
        // Keep diagnostic insufficiency separate from state recovery: recover and retain both outcomes.
        bool cancelledStatus = cancelled.Final?.Status == OcrStatus.Cancelled;
        bool neuralCancelled = cancelled.NeuralError is OperationCanceledException;
        bool noPartialRaw = cancelled.RawCalls == 0;
        bool actualProgress = _progress.CancelAt >= cancelled.Started &&
            _progress.Trigger is { Completed: 1, Total: > 1 } &&
            Stopwatch.GetElapsedTime(cancelled.Started, _progress.CancelAt) < TimeSpan.FromMinutes(3);
        bool nativeTermination = _progress.NativeFailures.Count > 0 && _progress.ObserverError is null;
        bool pipelineCancelled = cancelledStatus && neuralCancelled && noPartialRaw && actualProgress &&
            cancelled.NeuralCalls == 1 && cancelled.WindowsCalls is >= 0 and <= 1;
        if (pipelineCancelled) CompletedPhases.Add("actual-progress-cancellation");
        foreach (Input input in inputs)
            Assert.Equal(expected[input.Sample.Id], Quality(await Invoke(input, "after-cancel", CancellationToken.None).ConfigureAwait(false), input));
        Assert.Equal(1, Volatile.Read(ref _initializations));
        Assert.Equal(0, Volatile.Read(ref _rowCalls));
        Assert.True(_engine.IsReady);
        Assert.False(_released.Task.IsCompleted);
        CompletedPhases.Add("after-cancel-exact-recovery");

        // No Dispose, TryInitialize, GC or recognition during this interval. Constructor uses the real default 20s.
        long beforeIdle = Stopwatch.GetTimestamp();
        long releasedAt = await _released.Task.WaitAsync(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
        Assert.True(releasedAt >= Current.NeuralFinished);
        Assert.False(_engine.IsReady);
        Assert.Single(Releases);
        double observedIdleMs = Stopwatch.GetElapsedTime(beforeIdle, releasedAt).TotalMilliseconds;
        CompletedPhases.Add("first-natural-release");
        Volatile.Write(ref _released, Signal());

        foreach (Input input in inputs)
            Assert.Equal(expected[input.Sample.Id], Quality(await Invoke(input, "after-natural-idle-reload", CancellationToken.None).ConfigureAwait(false), input));
        Assert.True(_engine.IsReady);
        Assert.Equal(2, Volatile.Read(ref _initializations));
        Assert.Equal(0, Volatile.Read(ref _rowCalls));
        Evidence.SessionReceipt[] sessions = Sessions.ToArray();
        Assert.Equal(2, sessions.Length);
        Assert.Equal(sessions[0], sessions[1]);
        // Reload must reapply the identical requested entries to the fresh initialization.
        ConfigApplication[] configApplications = RequireConfigApplications(2);
        CompletedPhases.Add("after-natural-idle-exact-reload");

        // A second natural release verifies that reload rearmed the normal timer; cleanup follows this gate.
        long secondRelease = await _released.Task.WaitAsync(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
        Assert.True(secondRelease >= Current.NeuralFinished);
        Assert.False(_engine.IsReady);
        Assert.Equal(2, Releases.Count);
        CompletedPhases.Add("second-natural-release");
        return new
        {
            cancellationEvidence, cancelledStatus, neuralCancelled, noPartialRaw, actualProgress, nativeTermination,
            pipelineCancellationGatePassed = pipelineCancelled,
            nativeTerminateObserved = nativeTermination,
            activeKernelOverlap = "NOT_OBSERVED: progress and ORT terminate cannot exclude a Register-to-Run race",
            idleReloadGatePassed = true, configuredIdleSeconds = NeuralOcrEngine.EngineIdleRelease.TotalSeconds,
            observedIdleMs, releases = Releases.ToArray(), sessions, quality = expected, observations = Observations,
            completedPhases = CompletedPhases, requestedConfigEntries = RequestedConfigEntries, configApplications,
        };
    }

    // The eventual harness writes this from its finally path even if a strict assertion fails.
    internal object DiagnosticSnapshot() => new
    {
        initializations = Volatile.Read(ref _initializations), rows = Volatile.Read(ref _rowCalls),
        cancellation = _progress.Snapshot(), releases = Releases.ToArray(),
        sessions = Sessions.ToArray(), observations = Observations, completedPhases = CompletedPhases,
        requestedConfigEntries = RequestedConfigEntries, configApplications = ConfigApplications.ToArray(),
    };

    private ConfigApplication[] RequireConfigApplications(int count)
    {
        ConfigApplication[] applied = ConfigApplications.ToArray();
        Assert.Equal(count, applied.Length);
        for (int index = 0; index < applied.Length; index++)
        {
            Assert.Equal(index + 1, applied[index].Initialization);
            Assert.Equal(RequestedConfigEntries, applied[index].Entries);
        }
        return applied;
    }

    private async Task<Frame> Invoke(Input input, string stage, CancellationToken token)
    {
        var frame = new Frame(input.Sample.Id);
        Volatile.Write(ref _active, frame);
        frame.Started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try { frame.Final = await _service.RecognizeAsync(input.Request, deadline.Token).ConfigureAwait(false); }
        finally
        {
            frame.Finished = Stopwatch.GetTimestamp();
            Observations.Add(new { stage, frame.Id, frame.Started, frame.Finished, frame.NeuralFinished,
                frame.RawCalls, frame.NeuralCalls, frame.WindowsCalls, frame.Raw,
                neural = frame.Neural is null ? null : Evidence.Result(frame.Neural),
                windows = frame.Windows is null ? null : Evidence.Result(frame.Windows),
                final = frame.Final is null ? null : Evidence.Result(frame.Final),
                neuralError = frame.NeuralError?.ToString(), neuralErrorType = frame.NeuralError?.GetType().FullName,
                neuralErrorHResult = frame.NeuralError?.HResult, neuralCancelled = frame.NeuralError is OperationCanceledException,
                windowsError = frame.WindowsError?.ToString(), windowsErrorType = frame.WindowsError?.GetType().FullName,
                windowsErrorHResult = frame.WindowsError?.HResult });
        }
        return frame;
    }

    private static string Quality(Frame frame, Input input)
    {
        Assert.Equal(1, frame.NeuralCalls); Assert.Equal(1, frame.RawCalls);
        Assert.NotNull(frame.Raw); Assert.NotNull(frame.Neural); Assert.NotNull(frame.Final);
        Assert.Null(frame.NeuralError); Assert.Null(frame.WindowsError);
        Assert.InRange(frame.WindowsCalls, 0, 1);
        Assert.Equal(frame.Windows is null ? 0 : 1, frame.WindowsCalls);
        if (frame.Windows is not null) Assert.True(frame.Windows.Status is OcrStatus.Success or OcrStatus.NoText);
        Assert.True(frame.Neural!.Status is OcrStatus.Success or OcrStatus.NoText);
        Assert.True(frame.Final!.Status is OcrStatus.Success or OcrStatus.NoText);
        return JsonSerializer.Serialize(new { frame.Raw, neural = Evidence.Result(frame.Neural!),
            windows = frame.Windows is null ? null : Evidence.Result(frame.Windows), merged = Evidence.Result(frame.Final!),
            input.Sample.PngSha256, input.Sample.TruthSha256 }, Data.Json);
    }

    public void Dispose() => _engine.Dispose(); // Harness calls only after all real requests have completed.

    private sealed class ObservedWindows(WindowsOcrService actual, Func<Frame> active) : IOcrService
    {
        public bool IsAvailable => actual.IsAvailable;
        public IReadOnlyList<string> SupportedLanguages => actual.SupportedLanguages;
        public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Frame frame = active(); Interlocked.Increment(ref frame.WindowsCalls);
            try { return frame.Windows = await actual.RecognizeAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { frame.WindowsError = error; throw; }
        }
    }

    private sealed class ProgressObserver : IProgress<(int Completed, int Total)>, IDisposable
    {
        private CancellationTokenSource? _armed;
        private int _failures;
        internal long CancelAt;
        internal ProgressPoint? Trigger;
        internal string? ObserverError;
        internal readonly ConcurrentQueue<NativeFailure> NativeFailures = new();
        internal IDisposable Arm(CancellationTokenSource cancellation)
        {
            Assert.Null(Interlocked.CompareExchange(ref _armed, cancellation, null));
            AppDomain.CurrentDomain.FirstChanceException += ObserveException;
            return this;
        }
        public void Report((int Completed, int Total) value)
        {
            CancellationTokenSource? cancellation = Volatile.Read(ref _armed);
            if (cancellation is null || value.Completed != 1 || value.Total <= 1) return;
            long now = Stopwatch.GetTimestamp();
            if (Interlocked.CompareExchange(ref CancelAt, now, 0) != 0) return;
            Trigger = new(now, value.Completed, value.Total);
            // Synchronous IProgress implementation, not a SynchronizationContext-posted Progress<T>.
            cancellation.Cancel();
        }
        private void ObserveException(object? sender, FirstChanceExceptionEventArgs args)
        {
            CancellationTokenSource? cancellation = Volatile.Read(ref _armed);
            if (CancelAt == 0 || cancellation?.IsCancellationRequested != true || args.Exception is not OnnxRuntimeException error) return;
            if (!error.Message.Contains("Exiting due to terminate flag being set to true", StringComparison.Ordinal)) return;
            try
            {
                string observer = new StackTrace(fNeedFileInfo: false).ToString();
                if (!observer.Contains("RapidOcrNet.OrtRun.Run", StringComparison.Ordinal) ||
                    !observer.Contains("Microsoft.ML.OnnxRuntime.InferenceSession.Run", StringComparison.Ordinal)) return;
                if (Interlocked.Increment(ref _failures) > 64) { ObserverError = "Native exception evidence overflow."; return; }
                NativeFailures.Enqueue(new(Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId,
                    error.HResult, error.Message, error.StackTrace ?? string.Empty, observer));
            }
            catch (Exception failure) { ObserverError = failure.GetType().FullName; }
        }
        internal object Snapshot() => new { CancelAt, Trigger, ObserverError, native = NativeFailures.ToArray() };
        public void Dispose()
        {
            AppDomain.CurrentDomain.FirstChanceException -= ObserveException;
            Interlocked.Exchange(ref _armed, null);
        }
    }
}
