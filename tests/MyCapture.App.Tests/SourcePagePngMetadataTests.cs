using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The source page URL stays on the PNG bytes: library files, a later edit, Save As
/// before the record is published, and a clipboard read that becomes an F9 upload.
/// </summary>
public sealed class SourcePagePngMetadataTests
{
    private const string Page = "https://example.com/docs/page";

    [Fact]
    public void RoundTrip_KeepsALatin1UrlAndLeavesThePngDecodable() => RunSta(() =>
    {
        byte[] png = ImageCodec.EncodePng(Solid(4, 3), Page);
        string path = TestRecycleBin.CreateFilePath("mc-srcurl-", ".png");
        try
        {
            File.WriteAllBytes(path, png);
            Assert.Equal(Page, PngSourceUrlMetadata.TryReadSourceUrl(png));
            Assert.Equal(Page, PngSourceUrlMetadata.TryReadSourceUrl(path));
            BitmapSource? loaded = ImageCodec.TryLoad(path);
            Assert.NotNull(loaded);
            Assert.Equal(4, loaded!.PixelWidth);
            Assert.Equal(3, loaded.PixelHeight);
        }
        finally
        {
            TestRecycleBin.DeleteFile(path);
        }
    });

    [Fact]
    public void WithSourceUrl_SkipsEmptyAndNonLatin1Values()
    {
        byte[] png = MinimalPng();
        Assert.Same(png, PngSourceUrlMetadata.WithSourceUrl(png, null));
        Assert.Same(png, PngSourceUrlMetadata.WithSourceUrl(png, "  "));
        Assert.Same(png, PngSourceUrlMetadata.WithSourceUrl(png, "https://example.com/\u0100"));
        Assert.Equal(string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(png));
    }

    [Fact]
    public void TryRead_RejectsAChunkLengthThatWouldWrapTheBoundsCheck()
    {
        byte[] png = new byte[16];
        png[0] = 0x89;
        png[1] = 0x50;
        png[2] = 0x4E;
        png[3] = 0x47;
        png[4] = 0x0D;
        png[5] = 0x0A;
        png[6] = 0x1A;
        png[7] = 0x0A;
        png[8] = 0xFF;
        png[9] = 0xFF;
        png[10] = 0xFF;
        png[11] = 0xF0;
        png[12] = (byte)'t';
        png[13] = (byte)'E';
        png[14] = (byte)'X';
        png[15] = (byte)'t';

        Assert.Equal(string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(png));
    }

    [Fact]
    public void TryRead_ReturnsEmptyForMalformedBytesAndUnreadablePaths()
    {
        Assert.Equal(string.Empty, PngSourceUrlMetadata.TryReadSourceUrl([]));
        Assert.Equal(string.Empty, PngSourceUrlMetadata.TryReadSourceUrl([0x89, 0x50, 0x4E, 0x47, 0, 1]));
        Assert.Equal(string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(Path.Combine(Path.GetTempPath(), "missing-source-url.png")));

        string directory = TestRecycleBin.CreateTempSubdirectory("mc-srcurl-dir-").FullName;
        try
        {
            Assert.Equal(string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(directory));
        }
        finally
        {
            TestRecycleBin.DeleteDirectory(directory, recursive: true);
        }
    }

    [Fact]
    public void CapturedFiles_KeepTheUrlThroughTheOriginalCopyAndALaterEdit() => RunSta(() =>
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-srcurl-persist-").FullName;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var settings = new AppSettings();
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            var persistence = new CapturePersistenceService(
                queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            BitmapSource bitmap = Solid(8, 6);
            string? copiedUrl = null;
            var commit = new CaptureCommitService(
                persistence,
                () => settings,
                () => paths,
                NullLogger<CaptureCommitService>.Instance,
                (_, sourceUrl) =>
                {
                    copiedUrl = sourceUrl;
                    return Task.FromResult(true);
                });

            CaptureRecord record = CapturePersistenceService.CreatePendingRecord(
                bitmap, 1, "Docs", "DISPLAY1", sourcePageUrl: Page);
            persistence.PersistPendingOriginalAsync(record, bitmap).GetAwaiter().GetResult();

            string expected = SourcePageUrl.Normalize(Page);
            Assert.Equal(expected, PngSourceUrlMetadata.TryReadSourceUrl(queue.GetFilePath(record, CaptureFileNames.Original)));
            Assert.Equal(expected, PngSourceUrlMetadata.TryReadSourceUrl(queue.GetFilePath(record, CaptureFileNames.Rendered)));

            Assert.True(commit.CommitAsync(record, Result(bitmap, EditorCommitAction.Done)).GetAwaiter().GetResult());
            Assert.Equal(expected, PngSourceUrlMetadata.TryReadSourceUrl(queue.GetFilePath(record, CaptureFileNames.Rendered)));
            Assert.Equal(expected, PngSourceUrlMetadata.TryReadSourceUrl(queue.GetFilePath(record, CaptureFileNames.Original)));
            Assert.Equal(expected, copiedUrl);

            string exportDir = Path.Combine(root, "exports");
            Directory.CreateDirectory(exportDir);
            settings.Export.QuickSaveDirectoryOverride = exportDir;
            copiedUrl = null;
            Assert.True(commit.CommitAsync(record, Result(bitmap, EditorCommitAction.QuickSave)).GetAwaiter().GetResult());
            string exported = Assert.Single(Directory.EnumerateFiles(exportDir, "*.png"));
            Assert.Equal(expected, PngSourceUrlMetadata.TryReadSourceUrl(exported));
            Assert.Equal(expected, copiedUrl);

            string saveAsPath = Path.Combine(root, "named-before-publish.png");
            commit.SaveAsPrompt = _ => saveAsPath;
            copiedUrl = null;
            Assert.True(commit.CommitAsync(
                null,
                Result(bitmap, EditorCommitAction.SaveAs),
                namingRecord: record).GetAwaiter().GetResult());
            Assert.Equal(expected, PngSourceUrlMetadata.TryReadSourceUrl(saveAsPath));
            Assert.Equal(expected, copiedUrl);
        }
        finally
        {
            TestRecycleBin.DeleteDirectory(root, recursive: true);
        }
    });

    [Fact]
    public async Task ClipboardRead_RecoversTheSourceUrlFromThePngChunk()
    {
        BitmapSource image = Solid(2, 1);
        byte[] png = ImageCodec.EncodePng(image, Page);

        ClipboardImageReader.PinReadAttempt pin = await ClipboardImageReader.CompletePinAttemptAsync(
            ClipboardImageReader.CapturedAttempt.Content(png, fallbackImage: null),
            ClipboardImageReader.TryDecodePngPayload,
            (_, _) => Task.FromException<BitmapSource>(new InvalidOperationException("text renderer")),
            _ => Task.FromException<ClipboardImageReader.CapturedAttempt>(new InvalidOperationException("fallback")));

        Assert.Equal(ClipboardImageStatus.Success, pin.Outcome.Status);
        Assert.NotNull(pin.Content);
        Assert.Equal(Page, pin.Content!.SourcePageUrl);
        Assert.Equal(2, pin.Content.Image.PixelWidth);
    }

    private static AnnotationEditingResult Result(BitmapSource bitmap, EditorCommitAction action)
    {
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var frame = new FrozenFrame(bitmap, region, null, 0);
        return new AnnotationEditingResult(
            frame,
            region,
            bitmap,
            AnnotationDocument.CreateFor(bitmap.PixelWidth, bitmap.PixelHeight),
            action,
            new Dictionary<string, BitmapSource>(),
            new Dictionary<string, string>());
    }

    private static BitmapSource Solid(int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(
            new System.Windows.Int32Rect(0, 0, width, height),
            new byte[width * height * 4],
            width * 4,
            0);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Signature plus a 13-byte IHDR, enough for the writer to accept and the reader to stop.</summary>
    private static byte[] MinimalPng()
    {
        byte[] png = new byte[8 + 8 + 13 + 4];
        png[0] = 0x89;
        png[1] = 0x50;
        png[2] = 0x4E;
        png[3] = 0x47;
        png[4] = 0x0D;
        png[5] = 0x0A;
        png[6] = 0x1A;
        png[7] = 0x0A;
        png[11] = 13;
        png[12] = (byte)'I';
        png[13] = (byte)'H';
        png[14] = (byte)'D';
        png[15] = (byte)'R';
        return png;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }
}
