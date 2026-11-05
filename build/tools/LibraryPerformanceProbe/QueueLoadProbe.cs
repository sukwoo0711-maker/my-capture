using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;

namespace MyCapture.LibraryPerformanceProbe;

internal static partial class Program
{
    private const int QueueLoadWarmups = 2;
    private const int QueueLoadMeasured = 20;
    private const long QueueLoadFixtureCap = 64L * 1024 * 1024;

    private static int RunQueueLoad(string[] args, bool smallInputs = false)
    {
        string? output = null;
        try
        {
            Require(args.Length == 6 && args[0] == "--output" && args[2] == "--label" && args[4] == "--core-sha256",
                "Usage: (--queue-load|--queue-load-small) --output <new artifacts run> --label <A1|B1|B2|A2> --core-sha256 <expected hash>");
            Require(new[] { "A1", "B1", "B2", "A2" }.Contains(args[3]), "Unknown ABBA label.");
            string corePath = typeof(CaptureQueue).Assembly.Location;
            string coreHash = HashFile(corePath);
            Require(coreHash.Equals(args[5], StringComparison.OrdinalIgnoreCase), "Loaded Core differs from the pinned arm hash.");
            output = PrepareOutput(args[1]);
            var report = new QueueLoadReport
            {
                Label = args[3], StartedUtc = DateTimeOffset.UtcNow,
                Mode = smallInputs ? "--queue-load-small" : "--queue-load",
                CorePath = corePath, CoreSha256 = coreHash,
                CoreModuleId = typeof(CaptureQueue).Module.ModuleVersionId,
                ProbeSha256 = HashFile(typeof(Program).Assembly.Location),
                ProbeModuleId = typeof(Program).Module.ModuleVersionId,
                Runtime = RuntimeInformation.FrameworkDescription, Os = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                LogicalProcessors = Environment.ProcessorCount, ServerGc = GCSettings.IsServerGC,
                StopwatchFrequency = Stopwatch.Frequency,
            };
            long fixtureBytes = 0;
            // Each immutable input is created once, before any measurement. No user paths,
            // recovery sidecars, backups, pending files, publication or purge APIs are used.
            int[] recordCounts = smallInputs ? [0, 100] : [0, 100, 1000, 5000];
            QueueLoadFixture[] fixtures = recordCounts
                .Select(count => PrepareQueueLoadFixture(output, count, ref fixtureBytes)).ToArray();
            report.FixtureBytes = fixtureBytes;
            WriteJson(Path.Combine(output, "queue-load-inputs.json"), new
            {
                mode = report.Mode, fixtureBytes, fixtureCapBytes = QueueLoadFixtureCap,
                cases = fixtures.Select(fixture => new
                {
                    records = fixture.ExpectedRecords.Length,
                    inputs = fixture.Before,
                    expectedMetadataSha256 = Convert.ToHexString(SHA256.HashData(fixture.ExpectedMetadata)),
                }),
            });
            foreach (QueueLoadFixture fixture in fixtures)
            {
                int warmups = smallInputs ? 200 : QueueLoadWarmups;
                int measured = smallInputs ? (fixture.ExpectedRecords.Length == 0 ? 20_000 : 2_000) : QueueLoadMeasured;
                QueueLoadCaseResult result = MeasureQueueLoad(fixture, warmups, measured);
                report.Cases.Add(result);
                WriteJson(Path.Combine(output, $"queue-load-r{result.Records}.json"), result);
                Console.WriteLine($"COMPLETE {report.Label} records={result.Records}: {result.Measured} loads, CPU={result.ProcessCpuSeconds:F6}s, allocation={result.ProcessAllocatedBytes} bytes.");
            }
            // Also check earlier cases after the last case, including paths/timestamps.
            foreach (QueueLoadFixture fixture in fixtures) VerifyQueueLoadInputs(fixture);
            report.FinishedUtc = DateTimeOffset.UtcNow;
            WriteJson(Path.Combine(output, "queue-load-report.json"), report);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (output is not null) WriteJson(Path.Combine(output, "queue-load-failure.json"), new { error = error.ToString() });
            return 1;
        }
    }

    private static QueueLoadFixture PrepareQueueLoadFixture(string output, int count, ref long totalBytes)
    {
        AppPaths paths = AppPaths.CreateForRoot(Path.Combine(output, $"input-r{count}"));
        byte[] png = QueueLoadPng();
        string pngHash = Convert.ToHexString(SHA256.HashData(png));
        CaptureRecord[] records = Enumerable.Range(0, count).Select(index => new CaptureRecord
        {
            Id = Id(index, 7), CreatedAt = FixtureDate.AddSeconds(index), UpdatedAt = FixtureDate.AddSeconds(index),
            Width = 1, Height = 1, DpiScale = 1, TotalBytes = png.Length,
            Title = $"synthetic-load-{index:D5}", Tags = $"synthetic,group-{index % 7}",
            OcrText = $"synthetic OCR {index:D5}", OcrLanguage = "en", OcrContentRevision = 0,
            OriginalContentHash = pngHash, SourceMonitor = "synthetic-monitor",
            SourceWindowTitle = "synthetic fixture", SourceAppName = "synthetic app",
            SourcePageUrl = "https://example.invalid/synthetic", RelativeDirectory = "synthetic-shared",
        }).ToArray();
        byte[] indexBytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, records }, JsonDefaults.Compact);
        long bytes = checked(indexBytes.LongLength + (count == 0 ? 0 : png.LongLength));
        Require(checked(totalBytes + bytes) <= QueueLoadFixtureCap, "Queue-load fixture byte cap exceeded before writing.");
        totalBytes += bytes;
        paths.EnsureCreated();
        WriteQueueLoadBytes(paths.IndexFile, indexBytes);
        if (count != 0)
        {
            string directory = Path.Combine(paths.CapturesRoot, "synthetic-shared");
            Directory.CreateDirectory(directory);
            WriteQueueLoadBytes(Path.Combine(directory, CaptureFileNames.Original), png);
        }
        var queue = new CaptureQueue(paths, new QueueSettings
        {
            MaxItems = 10_000, MaxBytes = 1024L * 1024 * 1024, ImageRetentionHours = 0,
        }, NullLogger<CaptureQueue>.Instance);
        queue.Evicted += (_, _) => throw new InvalidOperationException("Unexpected queue-load fixture eviction.");
        CaptureRecord[] expected = records.Reverse().ToArray();
        return new(paths, queue, expected, JsonSerializer.SerializeToUtf8Bytes(expected, JsonDefaults.Compact),
            FingerprintQueueLoadInputs(paths.DataRoot));
    }

    private static QueueLoadCaseResult MeasureQueueLoad(QueueLoadFixture fixture, int warmups, int measured)
    {
        var preparation = Stopwatch.StartNew();
        for (int i = 0; i < warmups; i++)
        {
            fixture.Queue.Load();
            VerifyQueueLoadRecords(fixture);
        }
        VerifyQueueLoadInputs(fixture);
        preparation.Stop();
        var samples = new double[measured];
        FullCpuGc();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int[] collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        CpuProcessStamp before = CpuProcessStamp.Read();
        long blockStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            fixture.Queue.Load();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        double blockWall = Stopwatch.GetElapsedTime(blockStart).TotalSeconds;
        CpuProcessStamp after = CpuProcessStamp.Read();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int[] collections = [GC.CollectionCount(0) - collectionsBefore[0], GC.CollectionCount(1) - collectionsBefore[1],
            GC.CollectionCount(2) - collectionsBefore[2]];
        CpuProcessStamp tailBefore = CpuProcessStamp.Read();
        long tailStart = Stopwatch.GetTimestamp();
        FullCpuGc();
        double tailWall = Stopwatch.GetElapsedTime(tailStart).TotalSeconds;
        CpuProcessStamp tailAfter = CpuProcessStamp.Read();
        // Avoid retaining each deserialized generation or running serialization/hash checks
        // inside timing. Check all records after warmups and the final measured Load.
        VerifyQueueLoadRecords(fixture);
        QueueLoadFileStamp[] afterInputs = VerifyQueueLoadInputs(fixture);
        return new QueueLoadCaseResult
        {
            Records = fixture.ExpectedRecords.Length, Warmups = warmups, Measured = measured,
            LoadWallMilliseconds = samples,
            WarmupAndValidationWallSeconds = preparation.Elapsed.TotalSeconds,
            BlockWallSeconds = blockWall, ProcessCpuSeconds = after.Total - before.Total,
            UserCpuSeconds = after.User - before.User, KernelCpuSeconds = after.Kernel - before.Kernel,
            ProcessAllocatedBytes = allocated, NaturalGcCounts = collections,
            PostBlockGcCpuSeconds = tailAfter.Total - tailBefore.Total, PostBlockGcWallSeconds = tailWall,
            InputFilesAfter = afterInputs, AllFinalRecordIdsOrderAndMetadataExact = true,
            InputPathsBytesAndLastWriteTimesUnchanged = true,
        };
    }

    private static void VerifyQueueLoadRecords(QueueLoadFixture fixture)
    {
        CaptureQueue queue = fixture.Queue;
        Require(queue.Count == fixture.ExpectedRecords.Length, "Queue-load record count changed.");
        for (int i = 0; i < fixture.ExpectedRecords.Length; i++)
            Require(queue.Records[i].Id == fixture.ExpectedRecords[i].Id, $"Queue-load ID/order differs at {i}.");
        byte[] actual = JsonSerializer.SerializeToUtf8Bytes(queue.Records, JsonDefaults.Compact);
        Require(actual.AsSpan().SequenceEqual(fixture.ExpectedMetadata), "Queue-load record metadata differs.");
        Require(queue.TotalBytes == fixture.ExpectedRecords.Sum(record => record.TotalBytes), "Queue-load byte accounting differs.");
        Require(queue.Folders.Count == 0 && queue.SavedSearches.Count == 0, "Unexpected organization appeared.");
    }

    private static QueueLoadFileStamp[] VerifyQueueLoadInputs(QueueLoadFixture fixture)
    {
        QueueLoadFileStamp[] after = FingerprintQueueLoadInputs(fixture.Paths.DataRoot);
        Require(fixture.Before.SequenceEqual(after), "Queue-load input paths, bytes or write timestamps changed.");
        return after;
    }

    private static QueueLoadFileStamp[] FingerprintQueueLoadInputs(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path =>
            {
                Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Fixture input became a reparse point.");
                var info = new FileInfo(path);
                return new QueueLoadFileStamp(Path.GetRelativePath(root, path), info.Length,
                    info.LastWriteTimeUtc.Ticks, HashFile(path));
            }).ToArray();

    private static void WriteQueueLoadBytes(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    // A deterministic, valid 1x1 RGBA PNG, constructed entirely in memory. Load only
    // checks existence; this fixture does not exercise image decoding or cold disk I/O.
    private static byte[] QueueLoadPng()
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8; header[9] = 6;
        WriteChunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(new byte[] { 0, 0x35, 0x72, 0xA9, 0xFF });
        WriteChunk("IDAT", compressed.ToArray());
        WriteChunk("IEND", []);
        return png.ToArray();

        void WriteChunk(string name, byte[] content)
        {
            byte[] type = Encoding.ASCII.GetBytes(name);
            Span<byte> integer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(integer, checked((uint)content.Length));
            png.Write(integer); png.Write(type); png.Write(content);
            uint crc = uint.MaxValue;
            foreach (byte value in type.Concat(content))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
            png.Write(integer);
        }
    }

    private sealed record QueueLoadFixture(AppPaths Paths, CaptureQueue Queue, CaptureRecord[] ExpectedRecords,
        byte[] ExpectedMetadata, QueueLoadFileStamp[] Before);
    private sealed record QueueLoadFileStamp(string RelativePath, long Bytes, long LastWriteUtcTicks, string Sha256);
    private sealed class QueueLoadCaseResult
    {
        public int Records { get; init; }
        public int Warmups { get; init; }
        public int Measured { get; init; }
        public double[] LoadWallMilliseconds { get; init; } = [];
        public double MeanLoadWallMilliseconds => LoadWallMilliseconds.Average();
        public double WarmupAndValidationWallSeconds { get; init; }
        public double BlockWallSeconds { get; init; }
        public double ProcessCpuSeconds { get; init; }
        public double UserCpuSeconds { get; init; }
        public double KernelCpuSeconds { get; init; }
        public double CpuSecondsPerLoad => ProcessCpuSeconds / Measured;
        public long ProcessAllocatedBytes { get; init; }
        public double ProcessAllocatedBytesPerLoad => (double)ProcessAllocatedBytes / Measured;
        public int[] NaturalGcCounts { get; init; } = [];
        public double PostBlockGcCpuSeconds { get; init; }
        public double PostBlockGcWallSeconds { get; init; }
        public double CpuIncludingGcTailPerLoad => (ProcessCpuSeconds + PostBlockGcCpuSeconds) / Measured;
        public QueueLoadFileStamp[] InputFilesAfter { get; init; } = [];
        public bool AllFinalRecordIdsOrderAndMetadataExact { get; init; }
        public bool InputPathsBytesAndLastWriteTimesUnchanged { get; init; }
    }
    private sealed class QueueLoadReport
    {
        public string Status => "COMPLETE_CORRECTNESS_VALIDATED";
        public string Mode { get; init; } = "--queue-load";
        public string Label { get; init; } = "";
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset FinishedUtc { get; set; }
        public string CorePath { get; init; } = "";
        public string CoreSha256 { get; init; } = "";
        public Guid CoreModuleId { get; init; }
        public string ProbeSha256 { get; init; } = "";
        public Guid ProbeModuleId { get; init; }
        public string Runtime { get; init; } = "";
        public string Os { get; init; } = "";
        public string Architecture { get; init; } = "";
        public int LogicalProcessors { get; init; }
        public bool ServerGc { get; init; }
        public long StopwatchFrequency { get; init; }
        public long FixtureBytes { get; set; }
        public long FixtureByteCap => QueueLoadFixtureCap;
        public double SmallInputRegressionBudgetPercent => 5;
        public int[] SmallInputRecordCounts => [0, 100];
        public double ConservativeProcessCpuQuantumMilliseconds => 15.625;
        public string Scope => (Mode == "--queue-load-small" ? "Fixed small-input batches: 200 warmups, 20000 loads at 0 records, 2000 loads at 100 records. " : "Fixed four-case matrix: 2 warmups and 20 measured loads per case. ")
            + "Public Load; one shared PNG per nonempty case with warm cached existence checks; one queue per case; CPU includes bounded timing bookkeeping; no publication, image decoding, independent per-record directories or cold first-open I/O.";
        public string ValidationScope => "Every ID/order/serialized metadata after each warmup and final measured load; all input paths/lengths/SHA256/write timestamps before/after; intermediate generations not retained.";
        public string BudgetStatus => Mode == "--queue-load-small"
            ? "DIAGNOSTIC_ONLY; fixed larger batches for small-input CPU precision; compare AB/BA and quantization before budget decisions."
            : "DIAGNOSTIC_ONLY; compare AB/BA and process-time precision before adoption; 20 samples do not establish a p95 gate.";
        public List<QueueLoadCaseResult> Cases { get; } = [];
    }
}
