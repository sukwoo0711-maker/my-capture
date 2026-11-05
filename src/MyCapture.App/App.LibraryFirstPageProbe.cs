using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;

namespace MyCapture.App;

public partial class App
{
    private long _libraryFirstPageShellStarted, _libraryFirstPageShellFinished;
    private long _libraryFirstPageQueueLoadStarted, _libraryFirstPageQueueLoadFinished;
    private long _libraryFirstPageInputStarted, _libraryFirstPageInputFinished;
    private LibraryFirstPageInput? _libraryFirstPageInput;
    private LibraryFirstPageTiming? _libraryFirstPageTiming;

    private sealed record LibraryFirstPageInput(string Schema, int Count, DateTimeOffset PreparedUtc,
        DateTimeOffset ValidUntilUtc, Guid[] RecordIds, Dictionary<string, string> Files);

    // Called before settings, DI or queue loading. The outer owned runner verifies all copied
    // bytes; here the routing files are rehashed and every manifest path is confined again.
    private void ValidateLibraryFirstPageInput(ResidentProbeOptions options, string[] args)
    {
        _libraryFirstPageInputStarted = Stopwatch.GetTimestamp();
        string manifest = DiagnosticOutputPaths.Child(options.Output, "library-input.json");
        string expected = GalleryFirstPageProbe.ReadSingle(args, GalleryFirstPageProbe.InputShaSwitch);
        if (!string.Equals(HashLibraryInput(manifest), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Library fixture manifest SHA256 differs.");
        LibraryFirstPageInput input = JsonSerializer.Deserialize<LibraryFirstPageInput>(File.ReadAllText(manifest))
            ?? throw new InvalidOperationException("Missing library fixture manifest.");
        if (input.Schema != "library-first-page-preindexed-v1" || input.Count != options.LibraryFirstPageCount
            || input.Count is not (0 or 1000) || input.RecordIds.Length != input.Count
            || input.RecordIds.Distinct().Count() != input.Count
            || DateTimeOffset.UtcNow < input.PreparedUtc || DateTimeOffset.UtcNow > input.ValidUntilUtc
            || input.ValidUntilUtc - input.PreparedUtc != TimeSpan.FromHours(12)
            || input.Files.Count is < 2 or > 5000)
            throw new InvalidOperationException("Invalid or expired synthetic library input.");
        foreach ((string relative, string digest) in input.Files)
        {
            string[] parts = relative.Split('/');
            if (parts.Length < 2 || parts[0] != "profile" || digest.Length != 64 || !digest.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Invalid synthetic input file.");
            string path = options.Output;
            foreach (string part in parts) path = DiagnosticOutputPaths.Child(path, part);
            if (!File.Exists(path)) throw new InvalidOperationException("A synthetic input file is missing.");
        }
        foreach (string name in new[] { "settings.json", "index.json" })
        {
            if (!input.Files.TryGetValue("profile/" + name, out string? digest)
                || !string.Equals(HashLibraryInput(DiagnosticOutputPaths.Child(options.DataRoot, name)), digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Synthetic settings or index changed before startup.");
        }
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(DiagnosticOutputPaths.Child(options.DataRoot, "settings.json")));
        JsonElement queue = Property(settings.RootElement, "queue");
        JsonElement moved = Property(queue, "capturesDirectoryOverride");
        if (moved.ValueKind != JsonValueKind.Null && (moved.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(moved.GetString())))
            throw new InvalidOperationException("A library probe cannot redirect the synthetic captures root.");
        if (Property(queue, "maxItems").GetInt32() < input.Count + 1000
            || Property(queue, "maxBytes").GetInt64() != (512L << 30))
            throw new InvalidOperationException("Synthetic retention capacity is insufficient.");
        _libraryFirstPageInput = input;
        _libraryFirstPageInputFinished = Stopwatch.GetTimestamp();
        string? timing = Environment.GetEnvironmentVariable(LibraryFirstPageTiming.Variable);
        if (timing is not null && timing != "1")
            throw new InvalidOperationException("The optional first-page timing value must be exactly 1.");
        if (timing == "1") _libraryFirstPageTiming = new LibraryFirstPageTiming();
        string? firstEditor = Environment.GetEnvironmentVariable(LibraryFirstEditorVariable);
        if (firstEditor is not null && (firstEditor != "1" || input.Count != 1000 || timing != "1"))
            throw new InvalidOperationException("First-editor observation requires exact opt-in, 1000 records and timing.");
        _libraryFirstEditorRequested = firstEditor == "1";
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        foreach (JsonProperty property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        // Optional null properties may be omitted by the production settings serializer.
        if (name == "capturesDirectoryOverride") return JsonSerializer.SerializeToElement<string?>(null);
        throw new InvalidOperationException("Missing synthetic setting: " + name);
    }

    private static string HashLibraryInput(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private async Task RunLibraryFirstPageProbeAsync(ResidentProbeOptions options)
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        int exitCode = 1;
        object? failure = null;
        GalleryFirstPageProbe.Measurement? measurement = null;
        LibraryFirstPageTiming.Observation? firstPageTiming = null;
        long recoveryWaitStarted = Stopwatch.GetTimestamp(), recoveryWaitFinished = 0;
        try
        {
            await Task.WhenAll(_initialLibraryRecoveryTask, _initialEditRecoveryTask);
            recoveryWaitFinished = Stopwatch.GetTimestamp();
            LibraryFirstPageInput input = _libraryFirstPageInput ?? throw new InvalidOperationException("The synthetic input was not validated before startup.");
            var queue = _queue ?? throw new InvalidOperationException("Queue unavailable.");
            if (queue.Count != input.Count || !queue.Records.Select(record => record.Id).Order().SequenceEqual(input.RecordIds.Order())
                || queue.Records.Any(record => !record.IsImage || !record.HasCurrentOcrIndex))
                throw new InvalidOperationException("The preindexed synthetic queue differs from its manifest.");
            if (_galleryWindow is not null || _libraryFirstPageQueueLoadStarted <= 0
                || _libraryFirstPageQueueLoadFinished < _libraryFirstPageQueueLoadStarted
                || _libraryFirstPageShellFinished < _libraryFirstPageQueueLoadFinished || s_longRunPrivacy is null)
                throw new InvalidOperationException("Cold gallery, startup timing or privacy prerequisite missing.");
            measurement = await GalleryFirstPageProbe.MeasureAsync(Dispatcher, () => _galleryWindow, () => queue.Count, HandleGalleryRequested);
            if (queue.Count != input.Count || queue.Records.Any(record => !record.HasCurrentOcrIndex))
                throw new InvalidOperationException("Synthetic queue changed during first-page observation.");
            if (_libraryFirstEditorRequested)
            {
                firstPageTiming = _libraryFirstPageTiming?.Snapshot()
                    ?? throw new InvalidOperationException("First-editor timing unavailable.");
                (_galleryWindow ?? throw new InvalidOperationException("Measured gallery missing.")).OpenTimingObserverForTest = null;
                await RunLibraryFirstEditorProbeAsync(options, input, firstPageTiming);
            }
            exitCode = 0;
        }
        catch (Exception error) { failure = new { Type = error.GetType().FullName, error.HResult, error.Message }; }
        finally
        {
            // Completion evidence precedes closing the diagnostic window. Product close/Shutdown
            // handles its normal resources; no profile, image, index or other file is deleted.
            if (_libraryFirstPageTiming is not null && _galleryWindow is not null)
                _galleryWindow.OpenTimingObserverForTest = null;
            LibraryFirstPageTiming.Observation? timing = firstPageTiming ?? _libraryFirstPageTiming?.Snapshot();
            var report = new
            {
                Schema = GalleryFirstPageProbe.Revision, Status = exitCode == 0 ? "MEASURED" : "FAILED",
                StartedUtc = startedUtc, FinishedUtc = DateTimeOffset.UtcNow, ProcessId = Environment.ProcessId,
                StopwatchFrequency = Stopwatch.Frequency, Count = options.LibraryFirstPageCount,
                Workload = "Synthetic fully preindexed queue; real App/OCR availability/indexer, production idle GC; synthetic input suppliers",
                HistoricalTesthostWorkload = "AvailableOcr fake service and only one-third cached OCR; timings are not a matched workload",
                InputValidationStarted = _libraryFirstPageInputStarted, InputValidationFinished = _libraryFirstPageInputFinished,
                ShellStarted = _libraryFirstPageShellStarted, ShellFinished = _libraryFirstPageShellFinished,
                QueueLoadStarted = _libraryFirstPageQueueLoadStarted, QueueLoadFinished = _libraryFirstPageQueueLoadFinished,
                RecoveryWaitStarted = recoveryWaitStarted, RecoveryWaitFinished = recoveryWaitFinished,
                Measurement = measurement, Privacy = s_longRunPrivacy?.Snapshot(), Failure = failure,
                ThresholdGate = "NOT_EVALUATED", ClipboardWrites = "INTERCEPTED", NativeInputSent = 0,
            };
            try
            {
                using var stream = new FileStream(DiagnosticOutputPaths.Child(options.Output, "library-first-page.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
                if (timing is not null)
                {
                    using var timingStream = new FileStream(DiagnosticOutputPaths.Child(options.Output,
                        "library-first-page-timing.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    JsonSerializer.Serialize(timingStream, timing, new JsonSerializerOptions { WriteIndented = true });
                    timingStream.Flush(true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { exitCode = 2; }
            _galleryWindow?.Close();
            Shutdown(exitCode);
        }
    }
}
