using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;

namespace MyCapture.LibraryPerformanceProbe;

internal static partial class Program
{
    private const int ScopeRecordCount = 1_000;
    private const int ScopeWarmups = 3;
    private const int ScopeRepetitions = 20;
    private const int PublicationWarmups = 1;
    private const int PublicationRepetitions = 5;
    private const long PublicationPayloadBudget = 768L * 1024 * 1024;
    private static readonly DateTimeOffset FixtureDate = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static long _completedPublicationBytes;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--publication-cpu")
            return RunPublicationCpu(args[1..]);
        if (args.Length > 0 && args[0] == "--queue-load")
            return RunQueueLoad(args[1..]);
        if (args.Length > 0 && args[0] == "--queue-load-small")
            return RunQueueLoad(args[1..], smallInputs: true);
        string? output = null;
        try
        {
            if (args.Length != 2 || args[0] != "--output")
                throw new ArgumentException("Usage: LibraryPerformanceProbe --output <new directory under repository artifacts>");
            output = PrepareOutput(args[1]);
            var report = new ProbeReport
            {
                StartedUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                ServerGc = GCSettings.IsServerGC,
                StopwatchFrequency = Stopwatch.Frequency,
                CoreModuleId = typeof(CaptureQueue).Module.ModuleVersionId,
                CoreAssemblySha256 = HashFile(typeof(CaptureQueue).Assembly.Location),
            };
            foreach (int folders in new[] { 100, 500, 1_000 })
                foreach (int depth in new[] { 1, 5 })
                    MeasureScopes(output, folders, depth, report);
            foreach (int records in new[] { 300, 1_000 })
                foreach (int ocrBytes in new[] { 0, 4 * 1024, 64 * 1024 })
                    MeasurePublication(output, records, ocrBytes, report);
            report.CompletedPublicationPayloadBytes = _completedPublicationBytes;
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.RetainedFileBytesBeforeReport = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            WriteJson(Path.Combine(output, "report.json"), report);
            Console.WriteLine($"PASS: {report.Cases.Count} cases; {_completedPublicationBytes:N0} completed publication payload bytes.");
            Console.WriteLine($"Report and synthetic fixtures preserved: {output}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            // This directory is created by this run; all partial evidence remains available.
            if (output is not null)
                File.WriteAllText(Path.Combine(output, "failure.txt"), exception.ToString());
            return 1;
        }
    }

    private static string PrepareOutput(string requested)
    {
        string repository = Directory.GetCurrentDirectory();
        Require(File.Exists(Path.Combine(repository, "src", "MyCapture.Core", "MyCapture.Core.csproj")),
            "Run from the repository root.");
        string artifacts = Path.GetFullPath(Path.Combine(repository, "artifacts"));
        string output = Path.GetFullPath(requested);
        Require(output.StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Output must be a new directory strictly below this repository's artifacts directory.");
        Require(!Directory.Exists(output) && !File.Exists(output), "Output already exists; use a new run directory.");
        for (string? current = Path.GetDirectoryName(output); current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Output ancestors must not be reparse points.");
        Directory.CreateDirectory(output);
        return output;
    }

    private static CaptureQueue NewQueue(AppPaths paths) => new(paths,
        new QueueSettings { MaxItems = 5_000, MaxBytes = 1024L * 1024 * 1024 }, NullLogger<CaptureQueue>.Instance);

    private static void MeasureScopes(string output, int folderCount, int depth, ProbeReport report)
    {
        string fixture = Path.Combine(output, $"scope-f{folderCount}-d{depth}");
        AppPaths paths = AppPaths.CreateForRoot(fixture);
        paths.EnsureCreated();
        var folders = new List<CaptureFolder>(folderCount);
        var rootByFolder = new Dictionary<Guid, Guid>();
        for (int index = 0; index < folderCount; index++)
        {
            int chain = index / depth;
            var folder = new CaptureFolder
            {
                Id = Id(index, 1), Name = $"synthetic-{index:D4}",
                ParentId = index % depth == 0 ? null : folders[index - 1].Id,
                CreatedAt = FixtureDate.AddDays(chain % 2),
            };
            folders.Add(folder);
            rootByFolder.Add(folder.Id, folders[chain * depth].Id);
        }
        // Loading an empty index exercises the public folder parser without per-folder saves.
        WriteJson(paths.IndexFile, new
        {
            schemaVersion = 2, records = Array.Empty<CaptureRecord>(),
            folderState = new { version = 1, folders, assignments = Array.Empty<object>() },
        });
        CaptureQueue queue = NewQueue(paths);
        queue.Load();
        Require(queue.Folders.Count == folderCount, "Folder fixture did not load intact.");
        for (int index = 0; index < ScopeRecordCount; index++)
        {
            CaptureRecord record = Record(index, string.Empty);
            // Every tenth record is unassigned; the rest are evenly distributed.
            record.FolderId = index % 10 == 0 ? null : folders[index % folderCount].Id;
            record.CreatedAt = FixtureDate.AddDays(index % 2);
            queue.Add(record);
        }
        Require(queue.Count == ScopeRecordCount, "Scope fixture lost records.");
        WriteJson(Path.Combine(fixture, "synthetic-records.json"), queue.Records);
        string before = Fingerprint(queue);
        Dictionary<Guid, DateOnly> rootDates = folders.Where(folder => folder.ParentId is null)
            .ToDictionary(folder => folder.Id, folder => folder.CreatedDate);
        foreach ((string name, CaptureFolderScope scope) in new[]
        {
            ("date", CaptureFolderScope.ForDate(DateOnly.FromDateTime(FixtureDate.DateTime))),
            // The second chain contains assigned records at both tested depths. At depth 1,
            // folder zero only receives every-tenth records, which are deliberately unassigned.
            ("subtree", CaptureFolderScope.ForFolder(folders[depth].Id)),
        })
        {
            // The oracle uses the construction map, independently of GetFolderPath.
            HashSet<Guid> expectedFolders = folders.Where(folder => scope.FolderId is { } id
                    ? rootByFolder[folder.Id] == id
                    : rootDates[rootByFolder[folder.Id]] == scope.Date)
                .Select(folder => folder.Id).ToHashSet();
            HashSet<Guid> expectedRecords = queue.Records.Where(record => record.FolderId is { } id
                    ? expectedFolders.Contains(id)
                    : scope.FolderId is null && DateOnly.FromDateTime(record.CreatedAt.DateTime) == scope.Date)
                .Select(record => record.Id).ToHashSet();
            Require(expectedFolders.Count > 0 && expectedRecords.Count > 0,
                $"Scope fixture must exercise nonempty membership: {name}/f{folderCount}/d{depth}");
            MeasureScopeCase(report, $"FoldersInScope/{name}/f{folderCount}/d{depth}", folderCount, depth,
                () => queue.FoldersInScope(scope), folder => folder.Id, expectedFolders);
            MeasureScopeCase(report, $"RecordsInScope/{name}/f{folderCount}/d{depth}", folderCount, depth,
                () => queue.RecordsInScope(scope), record => record.Id, expectedRecords);
        }
        Require(before == Fingerprint(queue), "Read-only scope calls changed the fixture.");
    }

    private static void MeasureScopeCase<T>(ProbeReport report, string name, int folderCount, int depth,
        Func<IReadOnlyList<T>> operation, Func<T, Guid> id, HashSet<Guid> expected)
    {
        var samples = new List<Sample>(ScopeRepetitions);
        for (int repetition = -ScopeWarmups; repetition < ScopeRepetitions; repetition++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            IReadOnlyList<T> result = operation();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Require(result.Count == expected.Count && expected.SetEquals(result.Select(id)), $"Scope membership changed: {name}");
            if (repetition >= 0) samples.Add(new Sample(elapsed, elapsed, bytes, null, 0));
        }
        AddCase(report, new CaseResult(name, folderCount, depth, ScopeRecordCount, 0,
            expected.Count, ScopeWarmups, samples));
    }

    private static void MeasurePublication(string output, int recordCount, int ocrBytes, ProbeReport report)
    {
        // ASCII means byte count equals character count before JSON framing; one shared input
        // string bounds fixture memory, while serialization writes it for every record.
        string ocr = new('x', ocrBytes);
        string name = $"PublishRecordAsync/r{recordCount}/ocr{ocrBytes}";
        AppPaths paths = AppPaths.CreateForRoot(Path.Combine(output, $"publish-r{recordCount}-ocr{ocrBytes}"));
        paths.EnsureCreated();
        CaptureQueue queue = NewQueue(paths);
        for (int index = 0; index < recordCount; index++) queue.Add(Record(index, ocr));
        Require(queue.Count == recordCount, "Publication fixture lost records.");
        CaptureRecord target = queue.Records[0];
        WriteJson(Path.Combine(paths.DataRoot, "fixture-description.json"), new
        {
            recordCount, ocrAsciiBytesPerRecord = ocrBytes, targetId = target.Id,
            mediaPayloads = "None; this probe measures metadata publication only.",
        });
        var samples = new List<Sample>(PublicationRepetitions);
        long caseBytes = 0;
        for (int repetition = -PublicationWarmups; repetition < PublicationRepetitions; repetition++)
        {
            target.Title = $"synthetic-publication-{repetition + PublicationWarmups:D2}";
            string before = Fingerprint(queue);
            using CaptureWriteReservation reservation = queue.ReservePublicationAsync().GetAwaiter().GetResult();
            // Collect only between trials; this is an isolated warm-code probe, not a UI trace.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long processBefore = GC.GetTotalAllocatedBytes(precise: true);
            long threadBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            Task publication = queue.PublishRecordAsync(target, reservation);
            double returned = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long callerBytes = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
            publication.GetAwaiter().GetResult();
            double completed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long processBytes = GC.GetTotalAllocatedBytes(precise: true) - processBefore;
            // With folder state inactive, the current API writes exactly metadata and index.
            // Successful completion is required before counting either file's bytes.
            long writtenBytes = new FileInfo(paths.IndexFile).Length
                + new FileInfo(queue.GetFilePath(target, CaptureFileNames.Meta)).Length;
            caseBytes += writtenBytes;
            _completedPublicationBytes += writtenBytes;
            Require(_completedPublicationBytes <= PublicationPayloadBudget, "Publication payload budget exceeded.");
            AssertPublication(queue, paths, target);
            Require(before == Fingerprint(queue), "Publication changed its source records or folder state.");
            if (repetition >= 0) samples.Add(new Sample(returned, completed, callerBytes, processBytes, writtenBytes));
        }
        var result = new CaseResult(name, 0, 0, recordCount, ocrBytes, recordCount,
            PublicationWarmups, samples) { CompletedPayloadBytesIncludingWarmup = caseBytes };
        AddCase(report, result);
    }

    private static void AssertPublication(CaptureQueue queue, AppPaths paths, CaptureRecord target)
    {
        using (FileStream stream = File.OpenRead(paths.IndexFile))
        using (JsonDocument index = JsonDocument.Parse(stream))
        {
            JsonElement records = index.RootElement.GetProperty("records");
            Require(records.GetArrayLength() == queue.Count, "Published index record count changed.");
            int position = 0;
            foreach (JsonElement saved in records.EnumerateArray())
            {
                JsonElement expected = JsonSerializer.SerializeToElement(queue.Records[position++], JsonDefaults.Compact);
                Require(JsonElement.DeepEquals(saved, expected), "Published record data or ordering changed.");
            }
        }
        using FileStream metadataStream = File.OpenRead(queue.GetFilePath(target, CaptureFileNames.Meta));
        using JsonDocument metadata = JsonDocument.Parse(metadataStream);
        Require(JsonElement.DeepEquals(metadata.RootElement, JsonSerializer.SerializeToElement(target, JsonDefaults.Compact)),
            "Published metadata differs from the target record.");
    }

    private static CaptureRecord Record(int index, string ocr) => new()
    {
        Id = Id(index, 2), CreatedAt = FixtureDate.AddSeconds(index), UpdatedAt = FixtureDate.AddSeconds(index),
        Width = 2, Height = 2, TotalBytes = 0, OcrText = ocr, Title = $"synthetic-{index:D4}",
    };

    private static Guid Id(int index, byte category)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = category;
        return new Guid(bytes);
    }

    private static string Fingerprint(CaptureQueue queue) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { folders = queue.Folders, records = queue.Records }, JsonDefaults.Compact)));

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteJson<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, JsonDefaults.Readable);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AddCase(ProbeReport report, CaseResult result)
    {
        report.Cases.Add(result);
        Console.WriteLine($"{result.Name}: return p50={result.ReturnMilliseconds.P50:F3} ms, "
            + $"p95={result.ReturnMilliseconds.P95:F3} ms; completion p50={result.CompletionMilliseconds.P50:F3} ms");
    }

    private sealed record Sample(double ReturnMilliseconds, double CompletionMilliseconds,
        long CallerAllocatedBytes, long? ProcessAllocatedBytes, long CompletedPayloadBytes);

    private sealed record Distribution(double P50, double P95, double Max)
    {
        internal static Distribution From(IEnumerable<double> values)
        {
            double[] sorted = values.Order().ToArray();
            return new(sorted[(int)Math.Ceiling(sorted.Length * 0.50) - 1],
                sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1]);
        }
    }

    private sealed record CaseResult(string Name, int FolderCount, int MaximumDepth, int RecordCount,
        int OcrAsciiBytesPerRecord, int ResultCount, int Warmups, List<Sample> Samples)
    {
        public long CompletedPayloadBytesIncludingWarmup { get; init; }
        public Distribution ReturnMilliseconds => Distribution.From(Samples.Select(sample => sample.ReturnMilliseconds));
        public Distribution CompletionMilliseconds => Distribution.From(Samples.Select(sample => sample.CompletionMilliseconds));
        public Distribution CallerAllocatedBytes => Distribution.From(Samples.Select(sample => (double)sample.CallerAllocatedBytes));
        public Distribution? ProcessAllocatedBytes => Samples.All(sample => sample.ProcessAllocatedBytes.HasValue)
            ? Distribution.From(Samples.Select(sample => (double)sample.ProcessAllocatedBytes!.Value)) : null;
    }

    private sealed class ProbeReport
    {
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset FinishedUtc { get; set; }
        public string Runtime { get; init; } = string.Empty;
        public string OperatingSystem { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public int ProcessorCount { get; init; }
        public bool ServerGc { get; init; }
        public long StopwatchFrequency { get; init; }
        public Guid CoreModuleId { get; init; }
        public string CoreAssemblySha256 { get; init; } = string.Empty;
        public long CompletedPublicationPayloadBytes { get; set; }
        public long RetainedFileBytesBeforeReport { get; set; }
        public List<CaseResult> Cases { get; } = [];
    }
}
