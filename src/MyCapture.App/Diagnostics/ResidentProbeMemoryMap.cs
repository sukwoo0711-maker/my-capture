using System.Globalization;
using System.Runtime.InteropServices;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Diagnostic only (<c>--resident-audit</c>): every private allocation of this process at one moment,
/// with its owner where the process itself can tell (the managed heap, a native heap segment or
/// large block, a thread stack, otherwise a direct VirtualAlloc), and a fingerprint of what it holds
/// (zeros, the probe's synthetic pixels with their row stride, other pixels, other data).
/// </summary>
/// <remarks>
/// Memory is read with ReadProcessMemory on this process, so a block freed by another thread while
/// it is read fails the read instead of faulting. Heaps are walked under HeapLock. Nothing here runs
/// outside an audit run.
/// </remarks>
internal static class ResidentProbeMemoryMap
{
    private const uint MemCommit = 0x1000, MemFree = 0x10000, MemPrivate = 0x20000, MemMapped = 0x40000, MemImage = 0x1000000;
    private const uint PageGuard = 0x100, PageNoAccess = 0x01;
    private const ushort HeapRegion = 0x0001, HeapEntryBusy = 0x0004;

    internal sealed record Allocation(string Base, double ReservedMb, double CommittedMb, string Protect, string Owner,
        string Content, int StrideBytes, double HeapBusyMb, long CommittedBytes, double ResidentMb);

    internal sealed record HeapRow(int Index, string Handle, double CommittedMb, double AllocatedMb, double ReservedMb,
        double BusyMb, double FreeMb, int LargeBlocks, double LargeBlocksMb, string Note);

    internal sealed record Snapshot(string Step, double CommittedPrivateMb, double MappedMb, double ImageMb,
        IReadOnlyDictionary<string, double> PrivateByOwnerMb, IReadOnlyDictionary<string, double> ResidentByOwnerMb,
        IReadOnlyList<HeapRow> Heaps,
        IReadOnlyList<Allocation> Allocations, int SmallAllocations, double SmallAllocationsMb, double ElapsedMs);

    private sealed class Group
    {
        internal long Base;
        internal long Reserved;
        internal long Committed;
        internal uint Type;
        internal bool Guard;
        internal readonly List<(long Address, long Size, uint Protect)> Parts = [];
        internal string? Owner;
        internal long HeapBusy;
        internal long Resident;
    }

    /// <summary>Allocations at least this large are listed one by one and fingerprinted.</summary>
    private const long ListedBytes = 1L << 20;

    internal static Snapshot Take(string step)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        List<Group> groups = Enumerate();
        MarkManagedHeap(groups);
        List<HeapRow> heaps = WalkHeaps(groups);
        foreach (Group group in groups.Where(group => group.Type == MemPrivate && group.Owner is null))
            group.Owner = group.Guard ? "thread stack" : "direct VirtualAlloc";
        // Every committed private page's residency, before any page is read: committed memory never
        // touched (or trimmed) is commit charge without RAM. Earlier maps read at most 64 pages a block.
        var query = new WorkingSetExInformation[4096];
        foreach (Group group in groups.Where(group => group.Type == MemPrivate && group.Committed > 0))
            group.Resident = ResidentBytes(group, query);

        var allocations = new List<Allocation>();
        byte[] page = new byte[4096];
        byte[] window = new byte[64 * 1024];
        foreach (Group group in groups.Where(group => group.Type == MemPrivate && group.Committed >= ListedBytes)
                     .OrderByDescending(group => group.Committed))
        {
            (string content, int stride) = Fingerprint(group, page, window);
            uint protect = group.Parts.Count == 0 ? 0 : group.Parts.GroupBy(part => part.Protect)
                .OrderByDescending(parts => parts.Sum(part => part.Size)).First().Key;
            allocations.Add(new Allocation("0x" + group.Base.ToString("X", CultureInfo.InvariantCulture), Mb(group.Reserved),
                Mb(group.Committed), "0x" + protect.ToString("X", CultureInfo.InvariantCulture), group.Owner ?? "?", content,
                stride, Mb(group.HeapBusy), group.Committed, Mb(group.Resident)));
        }

        Group[] small = groups.Where(group => group.Type == MemPrivate && group.Committed > 0 && group.Committed < ListedBytes).ToArray();
        var byOwner = groups.Where(group => group.Type == MemPrivate && group.Committed > 0)
            .GroupBy(group => OwnerClass(group.Owner))
            .ToDictionary(owners => owners.Key, owners => Mb(owners.Sum(group => group.Committed)));
        var residentByOwner = groups.Where(group => group.Type == MemPrivate && group.Committed > 0)
            .GroupBy(group => OwnerClass(group.Owner))
            .ToDictionary(owners => owners.Key, owners => Mb(owners.Sum(group => group.Resident)));
        return new Snapshot(step,
            Mb(groups.Where(group => group.Type == MemPrivate).Sum(group => group.Committed)),
            Mb(groups.Where(group => group.Type == MemMapped).Sum(group => group.Committed)),
            Mb(groups.Where(group => group.Type == MemImage).Sum(group => group.Committed)),
            byOwner, residentByOwner, heaps, allocations, small.Length, Mb(small.Sum(group => group.Committed)),
            Math.Round(watch.Elapsed.TotalMilliseconds, 1));
    }

    private static string OwnerClass(string? owner) => owner switch
    {
        null => "?",
        _ when owner.StartsWith("heap", StringComparison.Ordinal) && owner.Contains("large block", StringComparison.Ordinal) => "native heap large blocks",
        _ when owner.StartsWith("heap", StringComparison.Ordinal) => "native heap segments",
        _ => owner,
    };

    private static List<Group> Enumerate()
    {
        var groups = new Dictionary<long, Group>();
        long address = 0;
        int size = Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQuery(new IntPtr(address), out MemoryBasicInformation info, (UIntPtr)size) != UIntPtr.Zero)
        {
            long regionSize = (long)info.RegionSize;
            if (info.State != MemFree)
            {
                long key = info.AllocationBase.ToInt64();
                if (!groups.TryGetValue(key, out Group? group))
                    groups[key] = group = new Group { Base = key, Type = info.Type };
                group.Reserved += regionSize;
                if (info.State == MemCommit)
                {
                    group.Committed += regionSize;
                    group.Parts.Add((info.BaseAddress.ToInt64(), regionSize, info.Protect));
                    if ((info.Protect & PageGuard) != 0) group.Guard = true;
                }
            }
            address = info.BaseAddress.ToInt64() + regionSize;
            if (address <= 0 || regionSize <= 0) break;
        }
        return groups.Values.OrderBy(group => group.Base).ToList();
    }

    /// <summary>The allocation holding <paramref name="address"/>; <paramref name="groups"/> is sorted by base.</summary>
    private static Group? Find(List<Group> groups, long address)
    {
        int low = 0, high = groups.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (groups[middle].Base <= address) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        if (found < 0) return null;
        Group group = groups[found];
        return address < group.Base + group.Reserved ? group : null;
    }

    /// <summary>The managed heap's reservation holds a pinned small, pinned-heap and large object.</summary>
    private static void MarkManagedHeap(List<Group> groups)
    {
        byte[] small = new byte[64];
        byte[] large = new byte[128 * 1024];
        byte[] pinned = GC.AllocateArray<byte>(64, pinned: true);
        foreach (byte[] target in new[] { small, large, pinned })
        {
            GCHandle handle = GCHandle.Alloc(target, GCHandleType.Pinned);
            try
            {
                if (Find(groups, handle.AddrOfPinnedObject().ToInt64()) is { } group) group.Owner = "managed heap (GC)";
            }
            finally { handle.Free(); }
        }
        GC.KeepAlive(small);
        GC.KeepAlive(large);
        GC.KeepAlive(pinned);
    }

    private static List<HeapRow> WalkHeaps(List<Group> groups)
    {
        var rows = new List<HeapRow>();
        uint count = GetProcessHeaps(0, null);
        var handles = new IntPtr[Math.Max(1, count + 8)];
        count = GetProcessHeaps((uint)handles.Length, handles);
        IntPtr processHeap = GetProcessHeap();
        var large = new List<(long Address, long Size)>(4096);
        var regions = new List<long>(256);
        for (int index = 0; index < count; index++)
        {
            IntPtr heap = handles[index];
            var summary = new HeapSummaryInfo { cb = (uint)Marshal.SizeOf<HeapSummaryInfo>() };
            bool summarized;
            try { summarized = HeapSummary(heap, 0, ref summary); }
            catch (EntryPointNotFoundException) { summarized = false; }
            long busy = 0, free = 0;
            large.Clear();
            regions.Clear();
            string note = heap == processHeap ? "process heap" : string.Empty;
            if (HeapLock(heap))
            {
                try
                {
                    var entry = new ProcessHeapEntry();
                    while (HeapWalk(heap, ref entry))
                    {
                        if ((entry.wFlags & HeapRegion) != 0)
                        {
                            if (regions.Count < regions.Capacity) regions.Add(entry.lpData.ToInt64());
                        }
                        else if ((entry.wFlags & HeapEntryBusy) != 0)
                        {
                            busy += entry.cbData;
                            if (entry.cbData >= 256 * 1024 && large.Count < large.Capacity)
                                large.Add((entry.lpData.ToInt64(), entry.cbData));
                        }
                        else if ((entry.wFlags & 0x0002) == 0)
                        {
                            free += entry.cbData;
                        }
                    }
                }
                finally { HeapUnlock(heap); }
            }
            else
            {
                note = (note + " not walked").Trim();
            }

            foreach (long region in regions)
                if (Find(groups, region) is { } group && group.Owner is null) group.Owner = $"heap#{index} segment";
            long largeBytes = 0;
            foreach ((long address, long size) in large)
            {
                largeBytes += size;
                if (Find(groups, address) is not { } group) continue;
                group.HeapBusy += size;
                if (group.Owner is null || group.Owner.StartsWith("heap", StringComparison.Ordinal))
                    group.Owner = group.Owner is { } existing && existing.EndsWith("segment", StringComparison.Ordinal)
                        ? existing
                        : $"heap#{index} large block";
            }
            if (Find(groups, heap.ToInt64()) is { } home && home.Owner is null) home.Owner = $"heap#{index} segment";
            rows.Add(new HeapRow(index, "0x" + heap.ToInt64().ToString("X", CultureInfo.InvariantCulture),
                summarized ? Mb((long)summary.cbCommitted) : -1, summarized ? Mb((long)summary.cbAllocated) : -1,
                summarized ? Mb((long)summary.cbReserved) : -1, Mb(busy), Mb(free), large.Count, Mb(largeBytes), note));
        }
        return rows;
    }

    /// <summary>
    /// What a block holds, from 64 sampled pages: zeros, the probe's synthetic desktop pixels (opaque,
    /// channel ranges of <see cref="ResidentProbeImages.Pattern"/>), white, other opaque pixels, or other
    /// data; for pixels, the row stride in bytes from the period of equal rows.
    /// </summary>
    private static (string Content, int Stride) Fingerprint(Group group, byte[] page, byte[] window)
    {
        long pages = group.Parts.Sum(part => part.Size / 4096);
        if (pages == 0) return ("empty", 0);
        int samples = (int)Math.Min(64, pages);
        // Residency first: reading a committed page that was never touched would fault it in.
        var query = new WorkingSetExInformation[samples];
        for (int sample = 0; sample < samples; sample++)
            query[sample].VirtualAddress = new IntPtr(AddressOfPage(group, pages * sample / samples));
        int resident = 0;
        if (QueryWorkingSetEx(GetCurrentProcess(), query, (uint)(samples * Marshal.SizeOf<WorkingSetExInformation>())))
            resident = query.Count(item => item.VirtualAddress != IntPtr.Zero && ((ulong)item.VirtualAttributes & 1) != 0);
        long zero = 0, white = 0, opaque = 0, synthetic = 0, dwords = 0, unreadable = 0;
        double sumB = 0, sumG = 0, sumR = 0;
        var distinct = new HashSet<uint>();
        for (int sample = 0; sample < samples; sample++)
        {
            long target = pages * sample / samples;
            long address = AddressOfPage(group, target);
            if (address == 0 || !Read(address, page, page.Length)) { unreadable++; continue; }
            for (int offset = 0; offset < page.Length; offset += 4)
            {
                uint value = BitConverter.ToUInt32(page, offset);
                dwords++;
                if (distinct.Count < 2048) distinct.Add(value);
                if (value == 0) { zero++; continue; }
                if (value == 0xFFFFFFFF) { white++; }
                byte b = page[offset], g = page[offset + 1], r = page[offset + 2], a = page[offset + 3];
                if (a != 0xFF) continue;
                opaque++;
                sumB += b; sumG += g; sumR += r;
                if (b >= 70 && b < 220 && g >= 90 && g < 210 && r >= 120 && r < 230) synthetic++;
            }
        }
        if (dwords == 0) return ($"unreadable ({unreadable} of {samples} pages)", 0);
        static string Pct(long part, long whole) => (100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture) + "%";
        string stats = $"resident {Pct(resident, samples)}, zero {Pct(zero, dwords)}, opaque {Pct(opaque, dwords)}, synthetic {Pct(synthetic, dwords)}, white {Pct(white, dwords)}, distinct {(distinct.Count >= 2048 ? "2048+" : distinct.Count.ToString(CultureInfo.InvariantCulture))}";
        if (opaque > 0)
            stats += $", mean BGR {sumB / opaque:0},{sumG / opaque:0},{sumR / opaque:0}";
        string kind = zero > dwords * 0.98 ? "zeros"
            : synthetic > dwords * 0.6 ? "synthetic pixels"
            : white > dwords * 0.9 ? "white pixels"
            : opaque > dwords * 0.6 ? "opaque pixels"
            : zero > dwords * 0.5 ? "mostly zeros"
            : "other data";
        int stride = 0;
        if (kind is "synthetic pixels" or "opaque pixels" or "white pixels" or "mostly zeros" && distinct.Count > 8)
            stride = Stride(group, window);
        return ($"{kind} ({stats})" + (stride > 0 ? $", row stride {stride} B = {stride / 4} px" : string.Empty), stride);
    }

    private static long ResidentBytes(Group group, WorkingSetExInformation[] query)
    {
        long resident = 0;
        int size = Marshal.SizeOf<WorkingSetExInformation>();
        foreach ((long address, long length, uint protect) in group.Parts)
        {
            for (long offset = 0; offset < length; offset += query.Length * 4096L)
            {
                int count = (int)Math.Min(query.Length, (length - offset) / 4096);
                for (int index = 0; index < count; index++)
                {
                    query[index].VirtualAddress = new IntPtr(address + offset + index * 4096L);
                    query[index].VirtualAttributes = UIntPtr.Zero;
                }
                if (!QueryWorkingSetEx(GetCurrentProcess(), query, (uint)(count * size))) continue;
                for (int index = 0; index < count; index++)
                    if (((ulong)query[index].VirtualAttributes & 1) != 0) resident += 4096;
            }
        }
        return resident;
    }

    private static long AddressOfPage(Group group, long pageIndex)
    {
        long remaining = pageIndex * 4096;
        foreach ((long address, long size, uint protect) in group.Parts)
        {
            if (remaining < size)
                return (protect & (PageGuard | PageNoAccess)) != 0 ? 0 : address + remaining;
            remaining -= size;
        }
        return 0;
    }

    /// <summary>The smallest offset at which most sampled dwords repeat: one pixel row.</summary>
    private static int Stride(Group group, byte[] window)
    {
        (long address, long size, uint protect) = group.Parts.Where(part => (part.Protect & (PageGuard | PageNoAccess)) == 0)
            .OrderByDescending(part => part.Size).FirstOrDefault();
        if (size < window.Length * 2L) return 0;
        long start = address + (size / 2 / 4096) * 4096;
        if (start + window.Length > address + size) start = address + size - window.Length;
        if (!Read(start, window, window.Length)) return 0;
        uint[] dwords = new uint[window.Length / 4];
        Buffer.BlockCopy(window, 0, dwords, 0, window.Length);
        const int SampleStep = 7, SampleSpan = 4096;
        double best = 0;
        var scores = new List<(int Stride, double Score)>();
        for (int stride = 16; stride <= 8192; stride++)
        {
            // Zero runs repeat at every offset; only non-zero pixels say where a row starts again.
            int match = 0, total = 0;
            for (int index = 0; index < SampleSpan && index + stride < dwords.Length; index += SampleStep)
            {
                if (dwords[index] == 0) continue;
                total++;
                if (dwords[index] == dwords[index + stride]) match++;
            }
            if (total < 64) continue;
            double score = (double)match / total;
            scores.Add((stride, score));
            if (score > best) best = score;
        }
        if (best < 0.6) return 0;
        foreach ((int stride, double score) in scores)
            if (score >= best - 0.005) return stride * 4;
        return 0;
    }

    private static bool Read(long address, byte[] buffer, int length) =>
        ReadProcessMemory(GetCurrentProcess(), new IntPtr(address), buffer, (UIntPtr)length, out UIntPtr read)
        && (long)read == length;

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 2);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessHeapEntry
    {
        public IntPtr lpData;
        public uint cbData;
        public byte cbOverhead;
        public byte iRegionIndex;
        public ushort wFlags;
        public uint dwCommittedSize;
        public uint dwUnCommittedSize;
        public IntPtr lpFirstBlock;
        public IntPtr lpLastBlock;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSetExInformation
    {
        public IntPtr VirtualAddress;
        public UIntPtr VirtualAttributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32QueryWorkingSetEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSetEx(IntPtr process, [In, Out] WorkingSetExInformation[] info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapSummaryInfo
    {
        public uint cb;
        public UIntPtr cbAllocated;
        public UIntPtr cbCommitted;
        public UIntPtr cbReserved;
        public UIntPtr cbMaxReserve;
    }

    [DllImport("kernel32.dll")]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessHeaps(uint numberOfHeaps, [Out] IntPtr[]? heaps);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcessHeap();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapSummary(IntPtr heap, uint flags, ref HeapSummaryInfo summary);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapLock(IntPtr heap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapUnlock(IntPtr heap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapWalk(IntPtr heap, ref ProcessHeapEntry entry);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size,
        out UIntPtr read);
}
