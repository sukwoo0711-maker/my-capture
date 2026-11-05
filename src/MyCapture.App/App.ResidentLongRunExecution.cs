using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App;

public partial class App
{
    private async Task RunResidentLongRunAsync(ResidentProbeOptions probe)
    {
        int exit = 1;
        bool smoke = string.Equals(probe.Scenarios.Single(), ResidentLongRunContract.SmokeScenario,
            StringComparison.OrdinalIgnoreCase);
        int required = smoke ? 10 : ResidentLongRunContract.ActionCount;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        ResidentLongRunEvidence? evidence = null;
        ResidentLongRunInputs? inputs = null;
        ResidentLongRunAwake? awake = null;
        ResidentLongRunContract.AwakeSample? awakeFinal = null;
        var samples = new List<ResidentLongRunContract.ActionSample>(required);
        Stopwatch? measured = null;
        object? report = null;
        try
        {
            evidence = new ResidentLongRunEvidence(probe.Output);
            _longRunEvidence = evidence;
            s_longRunCallbackEvidence = evidence;
            if (!probe.LongRun || probe.LongRunPreflight || s_longRunPrivacy is not { } privacy
                || _queue is null || _pins is null || _settings is null || _services is null
                || !ClipboardWriter.IsTestOverrideInstalled || !ClipboardAvailability.IsTestOverrideInstalled)
                throw new InvalidOperationException("Synthetic long-run boundaries were not installed before App initialization.");
            inputs = new ResidentLongRunInputs(probe.Output);
            await Task.WhenAll(_initialLibraryRecoveryTask, _initialEditRecoveryTask).WaitAsync(TimeSpan.FromSeconds(60));
            await ProbeWaitAsync(() => privacy.PrewarmFinished, 10_000, "synthetic prewarm");
            if (!privacy.PrewarmSucceeded || _queue.Count != 0 || _queue.PrepareRemoval is null)
                throw new InvalidOperationException("The fresh isolated profile or recoverable-removal boundary is unavailable.");
            _ = ClipboardAvailability.HasPinnableContent();
            NeuralOcrEngine neural = _services.GetRequiredService<NeuralOcrEngine>();
            await neural.ModelVerificationTask.WaitAsync(TimeSpan.FromSeconds(60));
            if (!neural.CanRun || !await _services.GetRequiredService<TextTranslationService>().IsModelInstalledAsync(CancellationToken.None))
                throw new InvalidOperationException("Pinned local OCR/translation models are missing or invalid; no download is performed.");
            if (!smoke) awake = new ResidentLongRunAwake();
            evidence.Record("workload-contract", new { measuredActionTarget = required,
                durationTargetSeconds = smoke ? 0 : ResidentLongRunContract.TargetSeconds,
                scheduleSeconds = 28.8, liveQueueImages = 2, liveQueueVideos = 1,
                productionIdleGcPreserved = true, driverForcesGc = false, optionalVariants = Array.Empty<string>(),
                automaticOcrQuality = _settings.Ocr.Quality.ToString(), explicitOcrActionQuality = "Accurate",
                syntheticFrameAcquisition = true, nativeAcquisitionMeasured = false,
                naturalGcReason = "UNCLASSIFIED", modelManifestSha256 = inputs.Spec.ModelManifestSha256 });

            // Prime the production bounded history through real pin paths, not by clearing it or
            // resizing its limit. Then one scheduled cycle establishes the same recurring cache phase.
            for (int pin = 0; pin < _settings.Pin.ClosedWindowRestoreLimit; pin++)
                _ = await LongRunPinAsync();
            var warmup = Stopwatch.StartNew();
            for (int index = 1; index <= 10; index++)
            {
                if (!smoke) await DelayToLongRunAsync(warmup, (index - 1) * 28.8);
                string kind = ResidentLongRunContract.Kind(index);
                evidence.Record("warmup-action-begin", new { index, kind });
                object oracle = await RunLongRunActionAsync(kind, inputs, evidence);
                await LongRunQuiescentAsync();
                await NormalizeLongRunQueueAsync();
                ResidentLongRunContract.State state = await LongRunQuiescentAsync();
                evidence.Record("warmup-action-complete", new { index, kind, oracle, state });
            }
            if (!smoke) await DelayToLongRunAsync(warmup, 288);
            ResidentLongRunEvidence.WriteNew(probe.Output, "loaded-native.json", ResidentLongRunEvidence.LoadedNative());
            if (awake is not null) evidence.Record("awake-measured-begin", awake.BeginMeasurement());
            measured = Stopwatch.StartNew();
            evidence.Record("measured-begin", new { required, fullRunStarted = !smoke });
            for (int index = 1; index <= required; index++)
            {
                if (!smoke) await DelayToLongRunAsync(measured, ResidentLongRunContract.ScheduledSeconds(index));
                if (awake is not null) evidence.Record("awake-action-begin", new { index, sample = awake.Sample() });
                string kind = ResidentLongRunContract.Kind(index);
                double actionStarted = measured.Elapsed.TotalSeconds;
                evidence.Record("action-begin", new { index, kind, measuredSeconds = actionStarted });
                object oracle = await RunLongRunActionAsync(kind, inputs, evidence);
                await LongRunQuiescentAsync();
                await NormalizeLongRunQueueAsync();
                // Observe near the end of each anchored slot; never force GC or reset model/cache
                // ownership to manufacture state equivalence. Overruns are retained in chronology.
                if (!smoke) await DelayToLongRunAsync(measured, index * 28.8 - 1);
                ResidentLongRunContract.State state = await LongRunQuiescentAsync();
                if (state.QueueCount != 3) throw new InvalidDataException("Fixed live queue did not contain two images and one video.");
                ResidentLongRunEvidence.Memory memory = evidence.Sample("action-" + index);
                if (awake is not null) evidence.Record("awake-checkpoint", new { index, sample = awake.Sample() });
                var sample = new ResidentLongRunContract.ActionSample(index, kind, actionStarted,
                    measured.Elapsed.TotalSeconds, true, memory.PrivateBytes, state);
                samples.Add(sample);
                evidence.Record("action-complete", new { sample, oracle });
                if (evidence.ObserverError is not null) throw new IOException("Product GC observation failed: " + evidence.ObserverError);
            }
            if (awake is not null)
            {
                await DelayToLongRunAsync(measured, ResidentLongRunContract.TargetSeconds);
                // The unbiased clock has tick granularity; satisfy both original durations,
                // without changing system timer resolution or reducing either threshold.
                while ((awakeFinal = awake.Sample()).ElapsedSeconds < ResidentLongRunContract.TargetSeconds)
                    await Task.Delay(TimeSpan.FromSeconds(1));
            }
            _ = await LongRunQuiescentAsync();
            if (smoke)
            {
                // The ten-action smoke has no anchored 28.8-second idle slots. Only observe
                // the existing product timer; never schedule, shorten, or invoke collection.
                long settleStarted = Stopwatch.GetTimestamp();
                const int settleTimeoutMs = 10_000;
                int completedBefore = evidence.CompletedIdleGcCount;
                bool settled = false;
                evidence.Record("smoke-idle-gc-settle-begin", new { timeoutMilliseconds = settleTimeoutMs,
                    completedIdleGcCount = completedBefore, idlePending = _idleMemory.IsPending,
                    timerEnabled = _idleMemoryTimer?.IsEnabled == true, canReclaim = CanReclaimIdleMemory() });
                try
                {
                    await ProbeWaitAsync(() => evidence.CompletedIdleGcCount > 0 || evidence.ObserverError is not null,
                        settleTimeoutMs, "smoke product idle-GC completion");
                    if (evidence.ObserverError is not null)
                        throw new IOException("Product GC observation failed: " + evidence.ObserverError);
                    if (Stopwatch.GetElapsedTime(settleStarted).TotalMilliseconds > settleTimeoutMs)
                        throw new TimeoutException("Smoke product idle-GC completion exceeded the observation deadline.");
                    settled = true;
                }
                finally
                {
                    evidence.Record("smoke-idle-gc-settle-finish", new { settled,
                        elapsedMilliseconds = Stopwatch.GetElapsedTime(settleStarted).TotalMilliseconds,
                        completedBefore, completedIdleGcCount = evidence.CompletedIdleGcCount,
                        idlePending = _idleMemory.IsPending, timerEnabled = _idleMemoryTimer?.IsEnabled == true,
                        canReclaim = CanReclaimIdleMemory(), observerError = evidence.ObserverError });
                }
            }
            inputs.VerifyUnchanged();
            if (evidence.ObserverError is not null) throw new IOException("Product GC observation failed.");
            double observed = measured.Elapsed.TotalSeconds;
            if (awake is not null)
            {
                awakeFinal = awake.Sample();
                evidence.Record("awake-measured-finish", awakeFinal);
                awake.Dispose();
                awake = null;
            }
            ResidentLongRunContract.MemoryEvaluation? memoryGate = smoke ? null : ResidentLongRunContract.Evaluate(samples, observed);
            bool completed = samples.Count == required;
            bool accepted = smoke ? completed : completed && ResidentLongRunContract.IsAwakeIntervalValid(awakeFinal)
                && memoryGate is { EvidenceValid: true, BudgetPassed: true };
            exit = accepted ? 0 : 3;
            report = new { status = accepted ? (smoke ? "SMOKE_COMPLETED" : "SOAK_COMPLETED") : "SOAK_GATE_FAILED",
                revision = ResidentLongRunContract.Revision, startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId, fullWorkloadImplemented = true, fullRunStarted = !smoke,
                completedCoreActions = samples.Count, measuredSeconds = observed, counts = samples.GroupBy(row => row.Kind)
                    .ToDictionary(group => group.Key, group => group.Count()), memoryEvaluation = memoryGate,
                longRunGate = smoke ? "NOT_RUN" : accepted ? "PASS" : "FAIL", memoryGate = smoke ? "NOT_EVALUATED" : accepted ? "PASS" : "FAIL",
                productionIdleGcPreserved = true, driverForcesGc = false, naturalGen2Heap = "NOT_OBSERVED",
                runtimeGcReasonCoverage = "UNCLASSIFIED; actual product idle-GC call sites are recorded separately",
                privacy = privacy.Snapshot(), inputPixelSha256 = inputs.PixelSha256,
                modelManifestSha256 = inputs.Spec.ModelManifestSha256,
                awakeInterval = awakeFinal, awakeGate = smoke ? "NOT_EVALUATED"
                    : ResidentLongRunContract.IsAwakeIntervalValid(awakeFinal) ? "PASS" : "FAIL", samples };
        }
        catch (Exception error)
        {
            if (error is ResidentLongRunPowerInterruptedException interrupted) awakeFinal = interrupted.Sample;
            report = new { status = "SOAK_FAILED", revision = ResidentLongRunContract.Revision,
                startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
                fullRunStarted = !smoke && measured is not null, completedCoreActions = samples.Count,
                measuredSeconds = measured?.Elapsed.TotalSeconds, longRunGate = smoke ? "NOT_RUN" : "FAIL",
                memoryGate = "NOT_EVALUATED", awakeInterval = awakeFinal,
                awakeGate = smoke ? "NOT_EVALUATED" : "FAIL",
                errorType = error.GetType().FullName, error.HResult, error.Message, samples };
        }
        finally
        {
            s_longRunCallbackEvidence = null;
            _longRunEvidence = null;
            try
            {
                try { awake?.Dispose(); }
                catch (Exception error)
                {
                    exit = 2;
                    evidence?.Record("awake-unsubscribe-failed", new { type = error.GetType().FullName, error.HResult });
                }
                evidence?.Record("execution-finish", new { exitCode = exit, completedCoreActions = samples.Count });
                if (report is not null) ResidentLongRunEvidence.WriteNew(probe.Output, "long-run-report.json", report);
            }
            catch (Exception error) { exit = 2; Trace.TraceError("Long-run evidence failed: {0}", error.GetType().Name); }
            finally
            {
                try { evidence?.Dispose(); }
                catch (Exception error) { exit = 2; Trace.TraceError("Long-run evidence close failed: {0}", error.GetType().Name); }
            }
            // Existing App shutdown owns live-window/recorder shutdown. A failed action remains
            // failed; the driver never converts forced shutdown into a completed operation.
            Shutdown(exit);
        }
    }

    private static async Task DelayToLongRunAsync(Stopwatch clock, double seconds)
    {
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            double remaining = seconds - clock.Elapsed.TotalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(remaining, 1)));
        }
    }
}
