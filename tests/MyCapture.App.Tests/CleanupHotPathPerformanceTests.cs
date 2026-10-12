using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in timing of the hot paths that clean up app-owned files: capture publication before
/// the editor opens, annotation save, video publish and edit commit, export size calculation
/// and draft retirement at shutdown. Set <c>MYCAPTURE_CLEANUP_PERF</c> to an output directory
/// and <c>MYCAPTURE_CLEANUP_PERF_LABEL</c> to name the result set. Ordinary runs return at once.
/// </summary>
public sealed class CleanupHotPathPerformanceTests
{
    private const string OutputVariable = "MYCAPTURE_CLEANUP_PERF";
    private const string LabelVariable = "MYCAPTURE_CLEANUP_PERF_LABEL";
    private const string RoundsVariable = "MYCAPTURE_CLEANUP_PERF_ROUNDS";

    [Fact]
    public void AppOwnedCleanupHotPaths()
    {
        string? output = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(output)) return;
        string label = Environment.GetEnvironmentVariable(LabelVariable) is { Length: > 0 } named ? named : "run";
        int rounds = int.TryParse(Environment.GetEnvironmentVariable(RoundsVariable), out int parsed) ? parsed : 5;
        var samples = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        void Add(string name, double ms) => (samples.TryGetValue(name, out var list) ? list : samples[name] = []).Add(ms);

        StaTestHost.Run(() =>
        {
            for (int round = 0; round <= rounds; round++)
            {
                bool record = round > 0; // round 0 warms JIT, codecs and the Shell
                string root = Directory.CreateTempSubdirectory("mc-cleanup-perf-").FullName;
                try
                {
                    var result = RunRound(root);
                    if (record) foreach (var (name, ms) in result) Add(name, ms);
                }
                finally { DeleteOwnedRoot(root); }
            }
        });

        Directory.CreateDirectory(output);
        var summary = samples.ToDictionary(pair => pair.Key, pair => new
        {
            Median = Median(pair.Value),
            Samples = pair.Value.Select(value => Math.Round(value, 2)).ToArray(),
        });
        File.WriteAllText(Path.Combine(output, $"cleanup-perf-{label}.json"),
            JsonSerializer.Serialize(new { Label = label, Rounds = rounds, Environment.ProcessorCount, Results = summary },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static List<(string, double)> RunRound(string root)
    {
        var results = new List<(string, double)>();
        AppPaths paths = AppPaths.CreateForRoot(root);
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));

        // 1. Region capture -> editor open: original persistence, and its publication tail
        // (index publication + pending-marker cleanup) that runs before the editor appears.
        BitmapSource screen = Screen(1920, 1080);
        CaptureRecord pending = CapturePersistenceService.CreatePendingRecord(screen, 1, "Window title", "monitor");
        var tail = new Stopwatch();
        persistence.BeforeRecordMetadataCommit = _ => tail.Restart();
        persistence.ImagePersisted += (_, _) => tail.Stop();
        var total = Stopwatch.StartNew();
        CaptureRecord record = persistence.PersistPendingOriginalAsync(pending, screen).GetAwaiter().GetResult();
        total.Stop();
        persistence.BeforeRecordMetadataCommit = null;
        results.Add(("capture-publish-total", total.Elapsed.TotalMilliseconds));
        results.Add(("capture-publish-tail", tail.Elapsed.TotalMilliseconds));

        // 2. Annotation save (Done) on that record, then the editor's draft retirement.
        var document = AnnotationDocument.CreateFor(1920, 1080);
        document.Items.Add(new RectangleAnnotation { Rect = new RectD(100, 100, 400, 300) });
        var frame = new MyCapture.Platform.Capture.FrozenFrame(screen, new RectD(0, 0, 1920, 1080), null, 0);
        var editing = new AnnotationEditingResult(frame, new RectD(0, 0, 1920, 1080), screen, document,
            EditorCommitAction.Done, new Dictionary<string, BitmapSource>(), new Dictionary<string, string>());
        using (CaptureEditSession session = commit.BeginEditSession(record))
        {
            var save = Stopwatch.StartNew();
            Assert.True(commit.CommitAsync(record, editing, session).GetAwaiter().GetResult());
            save.Stop();
            results.Add(("annotation-save-commit", save.Elapsed.TotalMilliseconds));
        }

        byte[] png = AnnotationEditorControl.EncodeDraftBitmap(screen);
        var drafts = CreateDraftStore(Path.Combine(root, "edit-drafts"));
        Guid saved = Guid.NewGuid();
        drafts.WriteAsync(saved, 1, "image", document.ToJson(), [], new Dictionary<string, byte[]>
            { ["base.png"] = png, ["original.png"] = png }, () => true).GetAwaiter().GetResult();
        var retire = Stopwatch.StartNew();
        drafts.RetireAsync(saved).GetAwaiter().GetResult();
        retire.Stop();
        results.Add(("annotation-save-draft-retire", retire.Elapsed.TotalMilliseconds));

        // 3. Video publish and edit commit.
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        using (VideoCaptureWriteSession capture = library.BeginCapture())
        {
            RecordingResult clip = EncodeClip(capture.StagingOutputPath, 320, 180, 10, 10);
            var publish = Stopwatch.StartNew();
            VideoLibraryItem item = library.CompleteCaptureAsync(capture, clip).GetAwaiter().GetResult();
            publish.Stop();
            results.Add(("video-publish", publish.Elapsed.TotalMilliseconds));
            using VideoEditSession edit = library.BeginEdit(item.Record);
            string stage = library.CreateRenderStagingPath(item.Record);
            File.Copy(library.SourcePath(item.Record), stage);
            VideoEditDocument videoDocument = item.EditDocument.Clone();
            videoDocument.TextOverlays.Add(new TimedTextOverlay { Text = "timing", StartMs = 100, EndMs = 800 });
            var commitEdit = Stopwatch.StartNew();
            library.CommitEditAsync(item.Record, edit, videoDocument, stage).GetAwaiter().GetResult();
            commitEdit.Stop();
            results.Add(("video-edit-commit", commitEdit.Elapsed.TotalMilliseconds));
        }

        // 4. Export dialog open: the size calculation renders two owned files, then disposes them.
        byte[] synthetic = new byte[256 * 1024];
        new Random(7).NextBytes(synthetic);
        var export = Stopwatch.StartNew();
        using (VideoExportCalculation.Calculate(30, 1000, 1_000_000,
                   (path, _, baseline, _) => File.WriteAllBytes(path, baseline ? synthetic : synthetic[..(synthetic.Length / 2)]),
                   CancellationToken.None))
        {
        }
        export.Stop();
        results.Add(("export-calculation-open-close", export.Elapsed.TotalMilliseconds));

        // 5. Shutdown with three open editors whose drafts retire.
        Guid[] sessions = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        foreach (Guid session in sessions)
            drafts.WriteAsync(session, 1, "image", document.ToJson(), [], new Dictionary<string, byte[]>
                { ["base.png"] = png, ["original.png"] = png }, () => true).GetAwaiter().GetResult();
        var shutdown = Stopwatch.StartNew();
        Task.WhenAll(sessions.Select(session => Task.Run(() => drafts.RetireAsync(session)))).GetAwaiter().GetResult();
        shutdown.Stop();
        results.Add(("shutdown-retire-3-drafts", shutdown.Elapsed.TotalMilliseconds));
        return results;
    }

    // The production configuration: retired sessions are deleted directly.
    private static EditDraftStore CreateDraftStore(string root) => new(root);

    private static BitmapSource Screen(int width, int height)
    {
        // Text-like rows compress like a real screenshot instead of a solid or noise frame.
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * stride + x * 4;
            bool ink = (y % 24) < 12 && ((x / 7 + y / 24) % 5) != 0 && ((x * 13 + y * 7) % 11) < 4;
            byte shade = ink ? (byte)40 : (byte)(235 + (x / 240));
            pixels[i] = shade; pixels[i + 1] = shade; pixels[i + 2] = (byte)(shade - (ink ? 0 : 10)); pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static RecordingResult EncodeClip(string path, int width, int height, int fps, int frameCount)
    {
        var options = new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps));
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];
            double frameMs = 1000.0 / fps;
            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int offset = 0; offset < pixels.Length; offset += 4)
                {
                    pixels[offset] = (byte)(frame * 20);
                    pixels[offset + 1] = 90;
                    pixels[offset + 2] = 160;
                    pixels[offset + 3] = 255;
                }
                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, frame * frameMs));
            }
            encoder.Complete();
        }
        return new RecordingResult(path, frameCount * 1000.0 / fps, fps, frameCount, width, height);
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1
            ? Math.Round(sorted[sorted.Length / 2], 2)
            : Math.Round((sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2, 2);
    }

    private static void DeleteOwnedRoot(string root)
    {
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var directory = new DirectoryInfo(root);
        if (!string.Equals(directory.Parent?.FullName, temp, StringComparison.OrdinalIgnoreCase)
            || !directory.Name.StartsWith("mc-cleanup-perf-", StringComparison.Ordinal)
            || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to delete a directory this benchmark did not create.");
        directory.Delete(recursive: true);
    }
}
