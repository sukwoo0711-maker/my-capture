using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MyCapture.App.Diagnostics;

/// <summary>Streaming raw events. No retained sample list, forced collection, or cleanup.</summary>
internal sealed class ResidentLongRunEvidence : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _process = Process.GetCurrentProcess();
    private long _sequence;
    private bool _disposed;
    private string? _observerError;
    private long? _pendingIdleGcStart;
    private int _completedIdleGcCount;
    internal string? ObserverError => Volatile.Read(ref _observerError);
    internal int CompletedIdleGcCount => Volatile.Read(ref _completedIdleGcCount);

    internal ResidentLongRunEvidence(string root)
    {
        string path = DiagnosticOutputPaths.Child(root, "long-run-events.jsonl");
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
        Record("begin", new { revision = ResidentLongRunContract.Revision, processId = Environment.ProcessId,
            processStartUtc = _process.StartTime.ToUniversalTime(), stopwatchFrequency = Stopwatch.Frequency,
            fullWorkloadImplemented = ResidentLongRunContract.FullWorkloadImplemented,
            driverForcesGc = false, productionIdleGcPreserved = true });
    }

    internal void Record(string kind, object data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.WriteLine(JsonSerializer.Serialize(new { sequence = ++_sequence, kind,
                utc = DateTimeOffset.UtcNow, elapsedSeconds = _clock.Elapsed.TotalSeconds, data }));
        }
    }

    internal sealed record Memory(long PrivateBytes, long WorkingSetBytes, long ManagedBytes, long GcHeapBytes,
        long GcCommittedBytes, long GcFragmentedBytes, long GcIndex, int LastGcGeneration,
        int Gen0, int Gen1, int Gen2, int Handles, int Threads, uint GdiObjects, uint UserObjects,
        int GdiError, int UserError, double CpuMilliseconds, string GcReasonCoverage);

    internal Memory Sample(string checkpoint)
    {
        _process.Refresh();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        uint gdi = GetGuiResources(_process.Handle, 0);
        int gdiError = gdi == 0 ? Marshal.GetLastWin32Error() : 0;
        uint user = GetGuiResources(_process.Handle, 1);
        int userError = user == 0 ? Marshal.GetLastWin32Error() : 0;
        var row = new Memory(_process.PrivateMemorySize64, _process.WorkingSet64, GC.GetTotalMemory(false),
            gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes, gc.Index, gc.Generation,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), _process.HandleCount,
            _process.Threads.Count, gdi, user, gdiError, userError,
            _process.TotalProcessorTime.TotalMilliseconds, "UNCLASSIFIED");
        Record("memory", new { checkpoint, sample = row });
        return row;
    }

    internal sealed record IdleGcObservation(string Phase, long StartTimestamp, long Timestamp,
        int Gen2Before, int Gen2After, double? CollectMilliseconds, double? CollectAndTrimMilliseconds,
        string? ErrorType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, uint flags);

    // Product observers must not alter GC behavior if evidence IO fails. The driver checks ObserverError.
    internal void ObserveIdleGc(IdleGcObservation row)
    {
        try
        {
            lock (_gate)
            {
                // Record flushes before publishing completion. The same gate serializes pairing
                // with the event stream; a waiter can also observe a pair completed before it starts.
                Record("product-induced-idle-gc", row);
                if (row.Phase == "start") _pendingIdleGcStart = row.StartTimestamp;
                else if (row.Phase == "finish")
                {
                    if (_pendingIdleGcStart == row.StartTimestamp && row.Timestamp >= row.StartTimestamp
                        && row.ErrorType is null)
                        Volatile.Write(ref _completedIdleGcCount, _completedIdleGcCount + 1);
                    _pendingIdleGcStart = null;
                }
            }
        }
        catch (Exception error) { Volatile.Write(ref _observerError, error.GetType().FullName ?? error.GetType().Name); }
    }

    internal static object[] LoadedNative()
    {
        using Process process = Process.GetCurrentProcess();
        var rows = new List<object>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessModule module in process.Modules)
        {
            string name = Path.GetFileName(module.FileName);
            if (!name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("sentencepiece", StringComparison.OrdinalIgnoreCase)) continue;
            if (!names.Add(name)) throw new InvalidDataException("Duplicate loaded native module name.");
            rows.Add(new { name, path = module.FileName, bytes = new FileInfo(module.FileName).Length,
                sha256 = ResidentLongRunInputs.HashFile(module.FileName) });
        }
        if (!names.Contains("onnxruntime.dll") || !names.Contains("libSkiaSharp.dll"))
            throw new InvalidDataException("Actual primary native model modules were not observed.");
        return rows.ToArray();
    }

    internal static void WriteNew(string root, string name, object value)
    {
        string path = DiagnosticOutputPaths.Child(root, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer.Dispose(); }
            finally { _process.Dispose(); }
        }
    }
}
