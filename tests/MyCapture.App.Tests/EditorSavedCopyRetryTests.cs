using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorSavedCopyRetryTests
{
    [Theory]
    [InlineData((int)EditorCommitAction.SaveAs)]
    [InlineData((int)EditorCommitAction.QuickSave)]
    public void SavedFileSurvivesFailedCopyAndCopyRetryDoesNotExportAgain(int actionValue) => StaTestHost.Run(() =>
    {
        var fixture = new Fixture();
        AnnotationEditingResult saving = Result((EditorCommitAction)actionValue);

        Assert.False(fixture.Service.CommitAsync(null, saving).GetAwaiter().GetResult());
        Assert.True(saving.SaveCompleted);
        Assert.True(saving.ClipboardCopyFailed);
        Assert.Equal(1, fixture.CopyCalls);
        string savedPath = Assert.Single(Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories));
        byte[] savedBytes = File.ReadAllBytes(savedPath);
        using (var stream = new MemoryStream(savedBytes))
        {
            BitmapSource decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(48, decoded.PixelWidth);
            Assert.Equal(32, decoded.PixelHeight);
        }

        fixture.CopySucceeds = true;
        fixture.Service.SaveAsPrompt = _ => throw new InvalidOperationException("Copy retry must not ask for another file.");
        AnnotationEditingResult copying = Result(EditorCommitAction.CopyToClipboard);
        Assert.True(fixture.Service.CommitAsync(null, copying).GetAwaiter().GetResult());

        Assert.False(copying.SaveCompleted);
        Assert.False(copying.ClipboardCopyFailed);
        Assert.Equal(2, fixture.CopyCalls);
        Assert.Equal(savedPath, Assert.Single(Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories)));
        Assert.Equal(savedBytes, File.ReadAllBytes(savedPath));
    });

    [Fact]
    public void ThrowingClipboardPreservesCompletedExportFacts() => StaTestHost.Run(() =>
    {
        var fixture = new Fixture { CopyThrows = true };
        AnnotationEditingResult result = Result(EditorCommitAction.SaveAs);

        Assert.Throws<COMException>(() => fixture.Service.CommitAsync(null, result).GetAwaiter().GetResult());

        Assert.True(result.SaveCompleted);
        Assert.True(result.ClipboardCopyFailed);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "saved.png")));
        Assert.Equal(1, fixture.CopyCalls);
    });

    [Theory]
    [InlineData((int)EditorCommitAction.Done)]
    [InlineData((int)EditorCommitAction.Pin)]
    public void LibrarySaveRemainsCompletedWhenItsCopyFails(int actionValue) => StaTestHost.Run(() =>
    {
        var fixture = new Fixture();
        CaptureRecord record = fixture.SeedPublishedCapture();
        long before = record.ContentRevision;
        AnnotationEditingResult result = Result((EditorCommitAction)actionValue);

        Assert.False(fixture.Service.CommitAsync(record, result).GetAwaiter().GetResult());

        Assert.True(result.SaveCompleted);
        Assert.True(result.ClipboardCopyFailed);
        Assert.True(record.ContentRevision > before);
        Assert.Equal(1, fixture.CopyCalls);
        var reopened = new CaptureQueue(fixture.Paths, fixture.Settings.Queue, NullLogger<CaptureQueue>.Instance);
        reopened.Load();
        Assert.Equal(record.ContentRevision, Assert.Single(reopened.Records).ContentRevision);
    });

    [Fact]
    public void CancelledExportDoesNotReportACompletedSaveOrCopyFailure() => StaTestHost.Run(() =>
    {
        var fixture = new Fixture();
        fixture.Service.SaveAsPrompt = _ => null;
        AnnotationEditingResult result = Result(EditorCommitAction.SaveAs);

        Assert.False(fixture.Service.CommitAsync(null, result).GetAwaiter().GetResult());

        Assert.False(result.SaveCompleted);
        Assert.False(result.ClipboardCopyFailed);
        Assert.Equal(0, fixture.CopyCalls);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories));
    });

    [Fact]
    public void SuccessfulSaveAndCopyHaveSeparateCompletionFacts() => StaTestHost.Run(() =>
    {
        var fixture = new Fixture { CopySucceeds = true };
        AnnotationEditingResult result = Result(EditorCommitAction.SaveAs);

        Assert.True(fixture.Service.CommitAsync(null, result).GetAwaiter().GetResult());

        Assert.True(result.SaveCompleted);
        Assert.False(result.ClipboardCopyFailed);
        Assert.Equal(1, fixture.CopyCalls);
    });

    private static AnnotationEditingResult Result(EditorCommitAction action)
    {
        BitmapSource image = Image();
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        return new AnnotationEditingResult(new FrozenFrame(image, region, null, 0), region, image,
            AnnotationDocument.CreateFor(image.PixelWidth, image.PixelHeight), action,
            new Dictionary<string, BitmapSource>(), new Dictionary<string, string>());
    }

    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(48, 32, 96, 96, PixelFormats.Bgra32, null, new byte[48 * 32 * 4], 48 * 4);
        image.Freeze();
        return image;
    }

    private sealed class Fixture
    {
        internal string Root { get; } = ExportTestDirectory.Create();
        internal AppPaths Paths { get; }
        internal AppSettings Settings { get; } = new();
        internal CapturePersistenceService Persistence { get; }
        internal CaptureCommitService Service { get; }
        private CaptureQueue Queue { get; }
        internal bool CopySucceeds { get; set; }
        internal bool CopyThrows { get; set; }
        internal int CopyCalls { get; private set; }

        internal Fixture()
        {
            Paths = AppPaths.CreateForRoot(Root);
            Settings.Export.QuickSaveDirectoryOverride = Path.Combine(Root, "exports");
            Queue = new CaptureQueue(Paths, Settings.Queue, NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, Paths, () => Settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            Service = new CaptureCommitService(Persistence, () => Settings, () => Paths, NullLogger<CaptureCommitService>.Instance,
                (_, _) =>
                {
                    CopyCalls++;
                    return CopyThrows ? Task.FromException<bool>(new COMException("Synthetic clipboard failure")) : Task.FromResult(CopySucceeds);
                });
            Service.SaveAsPrompt = _ => Path.Combine(Root, "saved.png");
        }

        internal CaptureRecord SeedPublishedCapture()
        {
            // Start from a valid, already-published isolated library record. Publishing a
            // new original has its own pending-marker retirement, outside this copy test.
            BitmapSource image = Image();
            byte[] png = ImageCodec.EncodePng(image);
            var record = new CaptureRecord { Width = image.PixelWidth, Height = image.PixelHeight, TotalBytes = png.LongLength * 3 };
            Queue.Add(record);
            string directory = Queue.GetDirectory(record);
            Directory.CreateDirectory(directory);
            foreach (string name in new[] { CaptureFileNames.Original, CaptureFileNames.Rendered, CaptureFileNames.Thumbnail })
                File.WriteAllBytes(Path.Combine(directory, name), png);
            Queue.SaveRecordMetaOrThrow(record);
            Queue.Save();
            return record;
        }
    }
}
