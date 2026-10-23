using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.Core.Privacy;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using RapidOcrOptions = RapidOcrNet.RapidOcrOptions;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrAnglePolicyTests
{
    [Fact]
    public void NeuralOptions_KeepRotationCorrectionAtTheMeasuredConfidence()
    {
        RapidOcrOptions options = NeuralOcrEngine.CreateOptions();

        Assert.True(options.DoAngle);
        Assert.Equal(.99f, options.ClsThresh);
        Assert.True(options.ReturnWordBox);
        Assert.Equal(Math.Clamp(Environment.ProcessorCount, 1, 8), options.RecMaxDegreeOfParallelism);
    }

    [Theory]
    [InlineData(OcrQuality.Accurate)]
    [InlineData(OcrQuality.Enhanced)]
    public async Task TextCaptureAndEditedQuickAccess_UseTheSharedNeuralPolicy(OcrQuality quality)
    {
        OcrSettings settings = Settings(quality);
        BitmapSource image = Image();
        var ocr = new RecordingOcr();
        int copies = 0;
        var service = new TextCaptureService(ocr, () => settings,
            _ => { copies++; return Task.FromResult(true); }, NullLogger.Instance);

        TextCaptureOutcome outcome = await service.RecognizeAndCopyAsync(image, CancellationToken.None);

        Assert.Equal(TextCaptureStatus.Cancelled, outcome.Status);
        Assert.Equal(0, copies);
        AssertBitmapRequest(Assert.Single(ocr.Requests), image);
    }

    [Theory]
    [InlineData(OcrQuality.Accurate)]
    [InlineData(OcrQuality.Enhanced)]
    public async Task PinCopy_WithCaptureOrPastedPixels_UsesTheSharedNeuralPolicy(OcrQuality quality)
    {
        BitmapSource image = Image();
        var ocr = new RecordingOcr();
        int copies = 0;

        bool copied = await PinTextCopyService.CopyAsync(image, ocr,
            _ => { copies++; return Task.FromResult(true); }, Settings(quality));

        Assert.False(copied);
        Assert.Equal(0, copies);
        AssertBitmapRequest(Assert.Single(ocr.Requests), image);
    }

    [Theory]
    [InlineData(OcrQuality.Accurate)]
    [InlineData(OcrQuality.Enhanced)]
    public async Task PrivacyDetection_WithCaptureOrImportedPixels_UsesTheSharedNeuralPolicy(OcrQuality quality)
    {
        BitmapSource image = Image();
        var ocr = new RecordingOcr();
        var service = new PrivacyRedactionService(ocr, new PrivacyDetector(), () => Settings(quality));

        PrivacyRedactionResult result = await service.FindAsync(image);

        Assert.Equal(PrivacyRedactionStatus.Cancelled, result.Status);
        AssertBitmapRequest(Assert.Single(ocr.Requests), image);
    }

    [Theory]
    [InlineData(OcrQuality.Accurate, false)]
    [InlineData(OcrQuality.Accurate, true)]
    [InlineData(OcrQuality.Enhanced, false)]
    [InlineData(OcrQuality.Enhanced, true)]
    public async Task IndexingCaptureAndImport_DisablesWindowsSearchButKeepsNeuralRotation(
        OcrQuality quality,
        bool imported)
    {
        // TMP/TEMP may redirect retained fixtures without accepting a separate path override.
        string root = Path.Combine(Path.GetTempPath(), "mc-ocr-angle-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var record = new CaptureRecord
        {
            ImportedFileName = imported ? "rotated-scan.png" : null,
            IsPinned = true,
        };
        queue.Add(record);
        Directory.CreateDirectory(queue.GetDirectory(record));
        string originalPath = queue.GetFilePath(record, CaptureFileNames.Original);
        await File.WriteAllBytesAsync(originalPath, [1]);
        var ocr = new RecordingOcr();
        var gallery = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var service = new OcrIndexingService(gallery, ocr, queue.GetDirectory, () => Settings(quality),
            NullLogger<OcrIndexingService>.Instance);

        OcrIndexingOutcome outcome = await service.IndexMissingAsync();

        Assert.Equal(OcrIndexingOutcome.Cancelled, outcome);
        OcrRequest request = Assert.Single(ocr.Requests);
        Assert.Equal(originalPath, request.FilePath);
        Assert.Null(request.Bitmap);
        Assert.False(request.SearchRotatedOrientations);
        Assert.True(request.UseNeuralModel);
        AssertMeasuredAnglePolicy();
        Assert.Null(record.OcrText);
        Assert.False(File.Exists(paths.IndexFile));
    }

    [Theory]
    [InlineData("bitmap", false)]
    [InlineData("bitmap", true)]
    [InlineData("file", false)]
    [InlineData("file", true)]
    [InlineData("bytes", false)]
    [InlineData("bytes", true)]
    public void InputStorageAndWindowsSearch_DoNotDetermineNeuralAnglePolicy(string source, bool search)
    {
        OcrRequest request = source switch
        {
            "bitmap" => OcrRequest.FromBitmap(Image(), searchRotatedOrientations: search, useNeuralModel: true),
            "file" => OcrRequest.FromFile("rotated-scan.png", searchRotatedOrientations: search, useNeuralModel: true),
            _ => OcrRequest.FromBytes([1], searchRotatedOrientations: search, useNeuralModel: true),
        };

        Assert.Equal(search, request.SearchRotatedOrientations);
        Assert.True(request.UseNeuralModel);
        AssertMeasuredAnglePolicy();
    }

    private static void AssertBitmapRequest(OcrRequest request, BitmapSource expectedImage)
    {
        BitmapSource bitmap = Assert.IsAssignableFrom<BitmapSource>(request.Bitmap);
        Assert.Same(expectedImage, bitmap);
        Assert.True(bitmap.IsFrozen);
        Assert.True(request.UseNeuralModel);
        Assert.True(request.SearchRotatedOrientations);
        AssertMeasuredAnglePolicy();
    }

    private static void AssertMeasuredAnglePolicy()
    {
        // The PP-OCR helper is shared by all callers. Windows' whole-image orientation
        // search flag is independent of its per-line classifier.
        RapidOcrOptions options = NeuralOcrEngine.CreateOptions();
        Assert.True(options.DoAngle);
        Assert.Equal(.99f, options.ClsThresh);
    }

    private static OcrSettings Settings(OcrQuality quality)
    {
        var settings = new OcrSettings { Quality = quality, PreferredLanguages = ["ko-KR", "en-US"] };
        OcrQualityProfile.Synchronize(settings);
        return settings;
    }

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16);
        image.Freeze();
        return image;
    }

    private sealed class RecordingOcr : IOcrService
    {
        internal List<OcrRequest> Requests { get; } = [];
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko-KR", "en-US"];

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(OcrResult.Cancelled());
        }
    }
}
