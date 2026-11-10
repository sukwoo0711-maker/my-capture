using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Storage;

namespace MyCapture.LibraryPerformanceProbe;

internal static partial class Program
{
    private const long CpuPayloadCap = 2L * 1024 * 1024 * 1024;
    // Frozen before measuring either arm. Small-input CPU is measured around an entire
    // block; individual Windows process-time deltas are too coarse for these operations.
    private static readonly CpuCase[] CpuCases =
    [
        new(2, 0, 20, 2000), new(2, 4096, 20, 2000),
        new(1000, 0, 2, 5), new(1000, 65536, 2, 5),
    ];

    private static int RunPublicationCpu(string[] args)
    {
        string? output = null;
        try
        {
            Require(args.Length == 6 && args[0] == "--output" && args[2] == "--label" && args[4] == "--core-sha256",
                "Usage: --publication-cpu --output <new artifacts run> --label <A1|B1|B2|A2> --core-sha256 <expected hash>");
            string label = args[3];
            Require(new[] { "A1", "B1", "B2", "A2" }.Contains(label), "Unknown ABBA label.");
            string corePath = typeof(CaptureQueue).Assembly.Location;
            string coreHash = HashFile(corePath);
            Require(coreHash.Equals(args[5], StringComparison.OrdinalIgnoreCase), "Loaded Core binary differs from the pinned arm hash.");
            output = PrepareOutput(args[1]);
            using var ledger = new CpuPayloadLedger(Path.Combine(Path.GetDirectoryName(output)!, "cpu-budget.jsonl"));
            var report = new CpuReport
            {
                Label = label, StartedUtc = DateTimeOffset.UtcNow,
                CorePath = corePath, CoreSha256 = coreHash,
                ProbeSha256 = HashFile(typeof(Program).Assembly.Location),
                Runtime = RuntimeInformation.FrameworkDescription, Os = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
                ServerGc = GCSettings.IsServerGC, StopwatchFrequency = Stopwatch.Frequency,
            };
            foreach (CpuCase specification in CpuCases)
                report.Cases.Add(MeasurePublicationCpu(output, label, specification, ledger));
            report.ReservedPayloadBytesAcrossRuns = ledger.ReservedBytes;
            report.FinishedUtc = DateTimeOffset.UtcNow;
            WriteJson(Path.Combine(output, "cpu-report.json"), report);
            Console.WriteLine($"COMPLETE {label}: {report.Cases.Count} CPU cases; {ledger.ReservedBytes:N0}/{CpuPayloadCap:N0} payload bytes reserved across runs.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (output is not null) File.WriteAllText(Path.Combine(output, "cpu-failure.txt"), error.ToString());
            return 1;
        }
    }

    private static CpuCaseResult MeasurePublicationCpu(string output, string label, CpuCase specification,
        CpuPayloadLedger ledger)
    {
        var preparation = Stopwatch.StartNew();
        string name = $"r{specification.Records}-ocr{specification.OcrBytes}";
        AppPaths paths = AppPaths.CreateForRoot(Path.Combine(output, name));
        paths.EnsureCreated();
        CaptureQueue queue = NewQueue(paths);
        string ocr = new('x', specification.OcrBytes);
        for (int i = 0; i < specification.Records; i++) queue.Add(Record(i, ocr));
        Require(queue.Count == specification.Records, "CPU fixture record count differs.");
        CaptureRecord target = queue.Records[0];
        target.Title = "synthetic repeated publication";
        // The public API oracle uses the existing string serializer and the schema-2 shape.
        // No internal access, reflection, test hook or modified product assembly is required.
        byte[] expectedIndex = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 2, records = queue.Records,
        }, JsonDefaults.Compact));
        byte[] expectedMetadata = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(target, JsonDefaults.Readable));
        long payloadBytes = checked(expectedIndex.LongLength + expectedMetadata.LongLength);
        long planned = checked(payloadBytes * (specification.Warmups + specification.Measured));
        ledger.Reserve(label, name, planned);
        string indexHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedIndex));
        string metadataHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedMetadata));
        // Retain only hashes/lengths in the fixture receipt; avoid duplicate 66-MB oracle files.
        WriteJson(Path.Combine(paths.DataRoot, "cpu-fixture.json"), new
        {
            specification, payloadBytes, planned, indexHash, metadataHash,
            values = "deterministic ASCII OCR; fixed records/dates/title; no media; no concurrent writer",
        });
        for (int i = 0; i < specification.Warmups; i++)
        {
            using CaptureWriteReservation slot = queue.ReservePublicationAsync().GetAwaiter().GetResult();
            queue.PublishRecordAsync(target, slot).GetAwaiter().GetResult();
        }
        ValidateCpuFiles(paths, queue, target, expectedIndex, expectedMetadata);
        preparation.Stop();
        // Oracle arrays survive the block equally in both arms. Full GC is outside measured
        // intervals. Subsequent natural GC remains part of both CPU and latency observations.
        FullCpuGc();
        var samples = new CpuLatencySample[specification.Measured];
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        int[] collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        CpuProcessStamp before = CpuProcessStamp.Read();
        long blockStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples.Length; i++)
        {
            using CaptureWriteReservation slot = queue.ReservePublicationAsync().GetAwaiter().GetResult();
            long callerBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            Task publication = queue.PublishRecordAsync(target, slot);
            double returned = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long callerAllocated = GC.GetAllocatedBytesForCurrentThread() - callerBefore;
            publication.GetAwaiter().GetResult();
            samples[i] = new(i, returned, Stopwatch.GetElapsedTime(start).TotalMilliseconds, callerAllocated);
        }
        double blockWall = Stopwatch.GetElapsedTime(blockStart).TotalSeconds;
        CpuProcessStamp after = CpuProcessStamp.Read();
        long processAllocation = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        int[] naturalCollections = [GC.CollectionCount(0) - collectionsBefore[0], GC.CollectionCount(1) - collectionsBefore[1],
            GC.CollectionCount(2) - collectionsBefore[2]];
        CpuProcessStamp tailBefore = CpuProcessStamp.Read();
        FullCpuGc();
        double tailCpu = CpuProcessStamp.Read().Total - tailBefore.Total;
        // Hashing, file reads and JSON assertions are outside the CPU/latency block.
        ValidateCpuFiles(paths, queue, target, expectedIndex, expectedMetadata);
        using (JsonDocument index = JsonDocument.Parse(File.ReadAllBytes(paths.IndexFile)))
        {
            Require(index.RootElement.GetProperty("schemaVersion").GetInt32() == 2, "CPU fixture schema changed.");
            JsonElement records = index.RootElement.GetProperty("records");
            Require(records.GetArrayLength() == queue.Count, "CPU fixture lost records.");
            int i = 0;
            foreach (JsonElement saved in records.EnumerateArray())
                Require(JsonElement.DeepEquals(saved, JsonSerializer.SerializeToElement(queue.Records[i++], JsonDefaults.Compact)),
                    "CPU fixture record content/order changed.");
        }
        Require(indexHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { schemaVersion = 2, records = queue.Records }, JsonDefaults.Compact)))),
            "Publication mutated the input fixture.");
        ledger.Complete(label, name, planned);
        var result = new CpuCaseResult
        {
            Name = name, Specification = specification, Samples = samples,
            PreparationWallSeconds = preparation.Elapsed.TotalSeconds,
            BlockWallSeconds = blockWall, ProcessCpuSeconds = after.Total - before.Total,
            UserCpuSeconds = after.User - before.User, KernelCpuSeconds = after.Kernel - before.Kernel,
            ProcessAllocatedBytes = processAllocation, NaturalGcCounts = naturalCollections,
            PostBlockGcCpuSeconds = tailCpu, CompletedPayloadBytes = planned, PayloadBytesPerPublication = payloadBytes,
            IndexSha256 = indexHash, MetadataSha256 = metadataHash,
        };
        WriteJson(Path.Combine(paths.DataRoot, "cpu-case.json"), result);
        Console.WriteLine($"{label} {name}: n={samples.Length}, blockCPU={result.ProcessCpuSeconds:F6}s, CPU/op={result.CpuSecondsPerOperation:F8}s, completion p95={result.CompletionWallMs.P95:F4}ms");
        return result;
    }

    private static void ValidateCpuFiles(AppPaths paths, CaptureQueue queue, CaptureRecord target, byte[] index, byte[] metadata)
    {
        foreach (string suffix in new[] { "", AtomicFile.BackupSuffix })
        {
            Require(File.ReadAllBytes(paths.IndexFile + suffix).AsSpan().SequenceEqual(index), "Index bytes differ from string oracle.");
            Require(File.ReadAllBytes(queue.GetFilePath(target, CaptureFileNames.Meta) + suffix).AsSpan().SequenceEqual(metadata),
                "Metadata bytes differ from string oracle.");
        }
    }

    private static void FullCpuGc() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private sealed record CpuCase(int Records, int OcrBytes, int Warmups, int Measured);
    private readonly record struct CpuLatencySample(int Trial, double ReturnWallMs, double CompletionWallMs, long CallerAllocatedBytes);
    private readonly record struct CpuProcessStamp(double Total, double User, double Kernel)
    {
        internal static CpuProcessStamp Read()
        {
            Environment.ProcessCpuUsage usage = Environment.CpuUsage;
            return new(usage.TotalTime.TotalSeconds, usage.UserTime.TotalSeconds, usage.PrivilegedTime.TotalSeconds);
        }
    }
    private sealed record CpuDistribution(double P50, double P95, double Max)
    {
        internal static CpuDistribution From(IEnumerable<double> values)
        {
            double[] ordered = values.Order().ToArray();
            return new(ordered[(int)Math.Ceiling(ordered.Length * .5) - 1],
                ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], ordered[^1]);
        }
    }
    private sealed class CpuCaseResult
    {
        public string Name { get; init; } = "";
        public CpuCase Specification { get; init; } = null!;
        public CpuLatencySample[] Samples { get; init; } = [];
        public double PreparationWallSeconds { get; init; }
        public double BlockWallSeconds { get; init; }
        public double ProcessCpuSeconds { get; init; }
        public double UserCpuSeconds { get; init; }
        public double KernelCpuSeconds { get; init; }
        public double CpuSecondsPerOperation => ProcessCpuSeconds / Specification.Measured;
        public double PostBlockGcCpuSeconds { get; init; }
        public double CpuIncludingGcTailPerOperation => (ProcessCpuSeconds + PostBlockGcCpuSeconds) / Specification.Measured;
        public long ProcessAllocatedBytes { get; init; }
        public double ProcessAllocatedBytesPerOperation => (double)ProcessAllocatedBytes / Specification.Measured;
        public int[] NaturalGcCounts { get; init; } = [];
        public long PayloadBytesPerPublication { get; init; }
        public long CompletedPayloadBytes { get; init; }
        public string IndexSha256 { get; init; } = "";
        public string MetadataSha256 { get; init; } = "";
        public CpuDistribution ReturnWallMs => CpuDistribution.From(Samples.Select(s => s.ReturnWallMs));
        public CpuDistribution CompletionWallMs => CpuDistribution.From(Samples.Select(s => s.CompletionWallMs));
    }
    private sealed class CpuReport
    {
        public string Label { get; init; } = "";
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset FinishedUtc { get; set; }
        public string CorePath { get; init; } = "";
        public string CoreSha256 { get; init; } = "";
        public string ProbeSha256 { get; init; } = "";
        public string Runtime { get; init; } = "";
        public string Os { get; init; } = "";
        public string Architecture { get; init; } = "";
        public int LogicalProcessors { get; init; }
        public bool ServerGc { get; init; }
        public long StopwatchFrequency { get; init; }
        public long ReservedPayloadBytesAcrossRuns { get; set; }
        public long PayloadCapBytes => CpuPayloadCap;
        public double CpuCounterConservativeQuantumSeconds => 0.015625;
        public string CpuScope => "One process CPU interval per case: admission + sequential awaited publication + bounded per-operation timing/allocation bookkeeping; no fixture creation, hashes or file reads.";
        public string Allocations => "Process block allocation includes reservation and measurement bookkeeping; caller samples cover synchronous PublishRecordAsync return only.";
        public string CacheAndGc => "Warmed code/file cache; full GC before and after block only; natural GC included; post-block GC reported separately.";
        public string Correctness => "All calls awaited successfully; repeated identical deterministic payload; exact string-oracle bytes checked before/after each block for current+backup, all final records/order/metadata and unchanged input. Earlier overwritten generations not independently hashed.";
        public string Statistics => "Per-arm small n=4000 and stress n=10 after all ABBA blocks; descriptive quantiles; stress p95=max. Completion does not certify the +5% adoption budgets.";
        public List<CpuCaseResult> Cases { get; } = [];
    }

    private sealed class CpuPayloadLedger : IDisposable
    {
        private readonly FileStream _file;
        internal long ReservedBytes { get; private set; }
        internal CpuPayloadLedger(string path)
        {
            _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using (var reader = new StreamReader(_file, Encoding.UTF8, false, 4096, leaveOpen: true))
                while (reader.ReadLine() is { } line)
                {
                    using JsonDocument row = JsonDocument.Parse(line);
                    if (row.RootElement.GetProperty("kind").GetString() == "reserve")
                        ReservedBytes = checked(ReservedBytes + row.RootElement.GetProperty("bytes").GetInt64());
                }
            Require(ReservedBytes <= CpuPayloadCap, "Existing ledger exceeds the fixed payload cap.");
            _file.Position = _file.Length;
        }
        internal void Reserve(string label, string name, long bytes)
        {
            Require(bytes > 0 && checked(ReservedBytes + bytes) <= CpuPayloadCap, "2 GiB logical publication payload budget exhausted.");
            Append("reserve", label, name, bytes);
            ReservedBytes += bytes;
        }
        internal void Complete(string label, string name, long bytes) => Append("complete", label, name, bytes);
        private void Append(string kind, string label, string name, long bytes)
        {
            byte[] row = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind, label, name, bytes }, JsonDefaults.Compact) + "\n");
            _file.Write(row);
            _file.Flush(flushToDisk: true);
        }
        public void Dispose() => _file.Dispose();
    }
}
