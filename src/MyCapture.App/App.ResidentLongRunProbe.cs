using System.Diagnostics;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Primitives;

namespace MyCapture.App;

public partial class App
{
    private static ResidentLongRunPrivacy? s_longRunPrivacy;
    private ResidentLongRunEvidence? _longRunEvidence;

    private async Task RunResidentLongRunPreflightAsync(ResidentProbeOptions probe)
    {
        int exit = 1;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        object? result = null;
        ResidentLongRunEvidence? evidence = null;
        try
        {
            evidence = new ResidentLongRunEvidence(probe.Output);
            _longRunEvidence = evidence;
            if (!probe.LongRunPreflight || s_longRunPrivacy is not { } privacy || _overlay is null)
                throw new InvalidOperationException("Long-run preflight inputs were not installed before shell startup.");
            await Task.WhenAll(_initialLibraryRecoveryTask, _initialEditRecoveryTask).WaitAsync(TimeSpan.FromSeconds(60));
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            await ProbeWaitAsync(() => privacy.PrewarmFinished, 10_000, "synthetic prewarm completion");
            if (!privacy.PrewarmSucceeded || !ClipboardWriter.IsTestOverrideInstalled || !ClipboardAvailability.IsTestOverrideInstalled)
                throw new InvalidOperationException("Privacy prerequisite failed.");
            if (ClipboardAvailability.HasPinnableContent()) throw new InvalidOperationException("Synthetic availability must be false.");
            // Exercise the same providers wired to selector and reader prewarm, without opening a capture.
            await CaptureReaderPrewarm.RunAsync(() => privacy.ReadWindows(new RectD(0, 0, 1, 1)),
                windows => _overlay.ReadPageUrls(windows), windows => _overlay.ReadWindowSources!(windows));
            var frame = privacy.AcquireFrame(new RectD(0, 0, 16, 16));
            if (frame.Bitmap.PixelWidth != 16 || frame.Bitmap.PixelHeight != 16)
                throw new InvalidOperationException("Synthetic frame dimensions differ.");
            evidence.Sample("preflight-only");
            ResidentLongRunPrivacy.Observation observed = privacy.Snapshot();
            if (observed.SyntheticPrewarmCalls != 1 || observed.WindowSupplierCalls < 1 || observed.PageSupplierCalls < 1
                || observed.SourceSupplierCalls < 1 || observed.ClipboardAvailabilitySupplierCalls < 1
                || string.IsNullOrEmpty(observed.SyntheticPixelSha256) || evidence.ObserverError is not null)
                throw new InvalidOperationException("Required preflight observations are incomplete.");
            result = new { status = "PREFLIGHT_ONLY", revision = ResidentLongRunContract.Revision,
                startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
                fullWorkloadImplemented = ResidentLongRunContract.FullWorkloadImplemented, fullRunStarted = false, completedCoreActions = 0,
                longRunGate = "NOT_RUN", memoryGate = "NOT_EVALUATED", naturalGen2Heap = "NOT_OBSERVED",
                privacy = observed, productionIdleGcPreserved = true, driverForcesGc = false,
                missingAdapters = ResidentLongRunContract.MissingAdapters };
            exit = 0;
        }
        catch (Exception error)
        {
            result = new { status = "PREFLIGHT_FAILED", revision = ResidentLongRunContract.Revision,
                startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
                fullRunStarted = false, completedCoreActions = 0, longRunGate = "NOT_RUN",
                errorType = error.GetType().FullName, error.HResult, error.Message };
        }
        finally
        {
            _longRunEvidence = null;
            try
            {
                evidence?.Record("preflight-finish", new { exitCode = exit });
                if (result is not null) ResidentLongRunEvidence.WriteNew(probe.Output, "long-run-preflight.json", result);
            }
            catch (Exception error)
            {
                exit = 2;
                Trace.TraceError("Long-run preflight evidence failed: {0}", error.GetType().Name);
            }
            finally
            {
                try { evidence?.Dispose(); }
                catch (Exception error)
                {
                    exit = 2;
                    Trace.TraceError("Long-run preflight close failed: {0}", error.GetType().Name);
                }
            }
            Shutdown(exit);
        }
    }
}
