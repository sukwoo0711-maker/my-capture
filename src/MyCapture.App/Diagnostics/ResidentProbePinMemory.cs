using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MyCapture.App.Diagnostics;

/// <summary>Cheap VirtualQuery snapshots: no HeapWalk, pixel reads, GC or trimming between pins.</summary>
internal static class ResidentProbePinMemory
{
    internal const long MinimumRegionBytes = 1L << 20;
    internal sealed record Region(string Base, long Bytes, string Kind, uint Type, uint Protect);
    internal sealed record Bucket(string Kind, int Count, long Bytes);
    /// <summary>
    /// <paramref name="MappedBytes"/> is every committed view of a section in the process, of any
    /// size: page-file sections (a pin's adopted display, a layered window's system surface) and
    /// mapped data files alike. Private commit counts none of it, so a comparison of two pin
    /// hosts needs both; the mapped files are the same before and after a pin and cancel out.
    /// </summary>
    internal sealed record Map(long CommittedBytes, IReadOnlyList<Region> Regions,
        long MappedBytes = 0, long PrivateRegionBytes = 0, long ImageBytes = 0);
    internal sealed record Delta(IReadOnlyList<Region> Added, IReadOnlyList<Region> Removed,
        IReadOnlyList<Bucket> Net);

    internal static string Classify(uint type, uint protect) => type switch
    {
        0x40000 => "mapped/section",
        0x1000000 => "image",
        0x20000 when (protect & 0x400) != 0 => "private write-combined",
        0x20000 when (protect & 0xFF) is 0x04 or 0x08 => "private read/write",
        0x20000 => "private other",
        _ => "other",
    };

    internal static Map Take()
    {
        var regions = new List<Region>();
        long total = 0, address = 0, mapped = 0, privateBytes = 0, image = 0;
        int size = Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQuery(new IntPtr(address), out var info, (UIntPtr)size) != UIntPtr.Zero)
        {
            long bytes = checked((long)info.RegionSize);
            if (info.State == 0x1000)
            {
                total += bytes;
                if (info.Type == 0x40000) mapped += bytes;
                else if (info.Type == 0x20000) privateBytes += bytes;
                else if (info.Type == 0x1000000) image += bytes;
                if (bytes >= MinimumRegionBytes)
                    regions.Add(new("0x" + info.BaseAddress.ToInt64().ToString("X", CultureInfo.InvariantCulture),
                        bytes, Classify(info.Type, info.Protect), info.Type, info.Protect));
            }
            long next = info.BaseAddress.ToInt64() + bytes;
            if (next <= address || bytes <= 0) break;
            address = next;
        }
        if (address == 0) throw new InvalidOperationException("VirtualQuery did not return a memory map.");
        return new(total, regions, mapped, privateBytes, image);
    }

    // Exact region identity includes size and protection: growth/splits/protection changes are
    // represented as a removal plus an addition, rather than mistaken for new surface ownership.
    internal static Delta Compare(Map previous, Map current)
    {
        Region[] added = current.Regions.Except(previous.Regions).ToArray();
        Region[] removed = previous.Regions.Except(current.Regions).ToArray();
        string[] kinds = current.Regions.Concat(previous.Regions).Select(r => r.Kind).Distinct().Order().ToArray();
        return new(added, removed, kinds.Select(kind => new Bucket(kind,
            added.Count(r => r.Kind == kind) - removed.Count(r => r.Kind == kind),
            added.Where(r => r.Kind == kind).Sum(r => r.Bytes) - removed.Where(r => r.Kind == kind).Sum(r => r.Bytes))).ToArray());
    }

    internal static long ProcessCommit(IntPtr process)
    {
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        if (!GetProcessMemoryInfo(process, ref counters, counters.Size))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return checked((long)counters.PrivateUsage);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
}

internal sealed partial class ResidentProbeRecorder
{
    private readonly List<PinMemorySample> _pinMemorySamples = [];
    private readonly List<PinMeasurement> _pinMeasurements = [];
    private readonly List<PinObservation> _pinObservations = [];

    internal sealed record PinMemorySample(string Scenario, string Phase, int Pins, int PixelWidth, int PixelHeight,
        double Zoom, long PrivateBytes, long? PrivateWorkingSetBytes, string? PrivateWorkingSetError,
        long CommitBytes, long ManagedBytes, long GcHeapBytes, long GcCommittedBytes,
        ResidentProbePinMemory.Map Map, ResidentProbePinMemory.Delta? Delta, long ClosedHistoryBytes,
        double ElapsedSeconds);
    internal sealed record PinMeasurement(string Scenario, int Pin, string Operation, double? Milliseconds,
        string? Error, double? RenderingMs = null, IReadOnlyList<double>? IntervalsMs = null);
    internal sealed record PinObservation(string Scenario, int Pin, string Name, object Value);

    internal ResidentProbePinMemory.Map SamplePin(string scenario, string phase, int pins, int width, int height,
        double zoom, ResidentProbePinMemory.Map? previous, long closedHistoryBytes)
    {
        _process.Refresh();
        long privateBytes = _process.PrivateMemorySize64;
        long commit = ResidentProbePinMemory.ProcessCommit(_process.Handle);
        long managed = GC.GetTotalMemory(false);
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        (long? ws, string? error) = _options.Has("no-private-working-set")
            ? (null, "Disabled by diagnostic variant") : _workingSet.Read(_process.Handle, _process.WorkingSet64);
        ResidentProbePinMemory.Map map = ResidentProbePinMemory.Take();
        var sample = new PinMemorySample(scenario, phase, pins, width, height, zoom, privateBytes, ws, error,
            commit, managed, gc.HeapSizeBytes, gc.TotalCommittedBytes, map,
            previous is null ? null : ResidentProbePinMemory.Compare(previous, map), closedHistoryBytes, _clock.Elapsed.TotalSeconds);
        lock (_gate) _pinMemorySamples.Add(sample);
        Sample(scenario + " " + phase, pins, $"{pins} pins; {width}x{height}; zoom {zoom}");
        return map;
    }

    internal void PinFrame(string scenario, int pin, string operation, ResidentProbePresentation.Frame frame)
    {
        lock (_gate) _pinMeasurements.Add(new(scenario, pin, operation, frame.CompleteMs, frame.Error, frame.RenderingMs));
        if (frame.CompleteMs is { } ms) Time($"{scenario}: pin {pin} {operation}", ms);
    }

    internal void PinDuration(string scenario, int pin, string operation, double milliseconds)
    {
        lock (_gate) _pinMeasurements.Add(new(scenario, pin, operation, milliseconds, null));
        Time($"{scenario}: pin {pin} {operation}", milliseconds);
    }

    internal void PinIntervals(string scenario, int pin, IReadOnlyList<double> intervals)
    {
        double[] sorted = intervals.Order().ToArray();
        lock (_gate) _pinMeasurements.Add(new(scenario, pin, "landing-frame-intervals", null, null, IntervalsMs: intervals));
        if (sorted.Length == 0) return;
        double median = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        Time($"{scenario}: pin {pin} landing median interval", median);
        Time($"{scenario}: pin {pin} landing max interval", sorted[^1]);
    }

    internal void ObservePin(string scenario, int pin, string name, object value)
    {
        lock (_gate) _pinObservations.Add(new(scenario, pin, name, value));
    }
}
