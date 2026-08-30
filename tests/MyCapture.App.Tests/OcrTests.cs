using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Deterministic OCR tests: the pure planner (language selection/fallback, dimension scaling,
/// normalisation) and the full <see cref="WindowsOcrService"/> pipeline driven through a fake
/// recognizer, so coordinate unscaling, outcome mapping and cancellation are verified without
/// invoking real OS OCR.
/// </summary>
public sealed class OcrTests
{
    // ---- OcrPlanner: language selection / fallback --------------------------------

    [Fact]
    public void SelectLanguage_PicksFirstSupportedPreferred()
    {
        string? chosen = OcrPlanner.SelectLanguage(
            ["ko-KR", "en-US"],
            ["en-US", "ja-JP"]);

        // ko-KR is not supported; en-US is, and is preferred over ja-JP.
        Assert.Equal("en-US", chosen);
    }

    [Fact]
    public void SelectLanguage_IsCaseInsensitive()
    {
        string? chosen = OcrPlanner.SelectLanguage(["EN-us"], ["en-US"]);
        Assert.Equal("en-US", chosen);
    }

    [Fact]
    public void SelectLanguage_LanguageOnlyPreferenceMatchesRegionalVariant()
    {
        string? chosen = OcrPlanner.SelectLanguage(["en"], ["en-GB", "fr-FR"]);
        Assert.Equal("en-GB", chosen);
    }

    [Fact]
    public void SelectLanguage_NoMatch_ReturnsNullForProfileFallback()
    {
        string? chosen = OcrPlanner.SelectLanguage(["de-DE"], ["en-US", "ko-KR"]);
        Assert.Null(chosen);
    }

    [Fact]
    public void SelectLanguage_NoSupported_ReturnsNull()
    {
        Assert.Null(OcrPlanner.SelectLanguage(["en-US"], []));
    }

    // ---- OcrPlanner: dimension scaling --------------------------------------------

    [Fact]
    public void ResolveScale_UpscalesSmallImageWithinRange()
    {
        double scale = OcrPlanner.ResolveScale(100, 80, requestedFactor: 2.0, maxDimension: 4000);
        Assert.Equal(2.0, scale, 6);
    }

    [Fact]
    public void ResolveScale_ClampsRequestedToFourX()
    {
        double scale = OcrPlanner.ResolveScale(50, 50, requestedFactor: 10.0, maxDimension: 4000);
        Assert.Equal(4.0, scale, 6);
    }

    [Fact]
    public void ResolveScale_CapsUpscaleAtMaxDimensionPreservingAspect()
    {
        // Long edge 1000 * 4 = 4000 would exceed a 3000 cap; the scale is capped to 3.0.
        double scale = OcrPlanner.ResolveScale(1000, 500, requestedFactor: 4.0, maxDimension: 3000);
        Assert.Equal(3.0, scale, 6);
        Assert.True(1000 * scale <= 3000);
    }

    [Fact]
    public void ResolveScale_DownscalesOversizedSourceToFit()
    {
        // Source already exceeds the cap: must shrink below 1.0 to be recognisable.
        double scale = OcrPlanner.ResolveScale(8000, 4000, requestedFactor: 1.0, maxDimension: 4000);
        Assert.Equal(0.5, scale, 6);
        Assert.True(8000 * scale <= 4000);
    }

    // ---- OcrPlanner: normalisation ------------------------------------------------

    [Fact]
    public void NormalizeWord_TrimsAndCollapsesWhitespace()
    {
        Assert.Equal("hello world", OcrPlanner.NormalizeWord("  hello   world  "));
        Assert.Equal(string.Empty, OcrPlanner.NormalizeWord("   "));
    }

    [Fact]
    public void BuildLineText_JoinsWordsWithSingleSpaces()
    {
        Assert.Equal("the quick fox", OcrPlanner.BuildLineText(["the", "  quick ", "fox"]));
    }

    [Fact]
    public void BuildBlockText_JoinsNonEmptyLinesWithNewlines()
    {
        string block = OcrPlanner.BuildBlockText(["line one", "  ", "line two"]);
        Assert.Equal("line one\nline two", block);
    }

    // ---- OcrRect: coordinate unscale ----------------------------------------------

    [Fact]
    public void OcrRect_Unscale_DividesByScale()
    {
        var rect = new OcrRect(20, 40, 100, 60);
        OcrRect original = rect.Unscale(2.0);
        Assert.Equal(10, original.X, 6);
        Assert.Equal(20, original.Y, 6);
        Assert.Equal(50, original.Width, 6);
        Assert.Equal(30, original.Height, 6);
    }

    [Fact]
    public void OcrRect_Unscale_IdentityForUnitScale()
    {
        var rect = new OcrRect(5, 5, 10, 10);
        Assert.Equal(rect, rect.Unscale(1.0));
    }

    [Fact]
    public void OcrRect_Union_TakesBoundingBox()
    {
        OcrRect union = OcrRect.Union(new OcrRect(0, 0, 10, 10), new OcrRect(20, 5, 10, 30));
        Assert.Equal(0, union.X, 6);
        Assert.Equal(0, union.Y, 6);
        Assert.Equal(30, union.Right, 6);
        Assert.Equal(35, union.Bottom, 6);
    }

    // ---- WindowsOcrService pipeline via fake recognizer ---------------------------

    private static BitmapSource Solid(int w = 100, int h = 60)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = 0xFF;
        }

        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    private static WindowsOcrService NewService(FakeRecognizer recognizer) =>
        new(recognizer, _ => Solid(), _ => Solid(), NullLogger.Instance);

    [Fact]
    public async Task Recognize_Unavailable_ReturnsUnavailableOutcome()
    {
        var recognizer = new FakeRecognizer { Available = false };
        WindowsOcrService service = NewService(recognizer);

        OcrResult result = await service.RecognizeAsync(OcrRequest.FromBitmap(Solid()));

        Assert.Equal(OcrStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Recognize_NoLines_ReturnsNoText()
    {
        var recognizer = new FakeRecognizer
        {
            Available = true,
            Supported = ["en-US"],
            Result = new RecognizedText("en-US", []),
        };
        WindowsOcrService service = NewService(recognizer);

        OcrResult result = await service.RecognizeAsync(
            OcrRequest.FromBitmap(Solid(), 1.0, ["en-US"]));

        Assert.Equal(OcrStatus.NoText, result.Status);
        Assert.Equal("en-US", result.LanguageTag);
    }

    [Fact]
    public async Task Recognize_Success_UnscalesBoxesToOriginalPixels()
    {
        // The service upscales 2x before recognition; the fake returns a box in prepared
        // (scaled) coordinates, and the service must divide it back to original pixels.
        var recognizer = new FakeRecognizer
        {
            Available = true,
            Supported = ["en-US"],
            MaxDimension = 4000,
            Result = new RecognizedText("en-US",
            [
                new RecognizedLine("Hello", [new RecognizedWord("Hello", new OcrRect(20, 40, 100, 30))]),
            ]),
        };
        WindowsOcrService service = NewService(recognizer);

        OcrResult result = await service.RecognizeAsync(
            OcrRequest.FromBitmap(Solid(100, 60), upscaleFactor: 2.0, preferredLanguages: ["en-US"]));

        Assert.Equal(OcrStatus.Success, result.Status);
        Assert.Equal("Hello", result.Text);
        Assert.Equal("en-US", result.LanguageTag);

        // The recognizer was handed a 2x bitmap, so the reported 20,40,100,30 box maps back to
        // 10,20,50,15 in original pixels.
        OcrWord word = result.Lines[0].Words[0];
        Assert.Equal(10, word.Bounds.X, 3);
        Assert.Equal(20, word.Bounds.Y, 3);
        Assert.Equal(50, word.Bounds.Width, 3);
        Assert.Equal(15, word.Bounds.Height, 3);

        // The fake records the prepared bitmap dimensions it received.
        Assert.Equal(200, recognizer.LastWidth);
        Assert.Equal(120, recognizer.LastHeight);
    }

    [Fact]
    public async Task Recognize_FallsBackToProfileWhenPreferredUnsupported()
    {
        var recognizer = new FakeRecognizer
        {
            Available = true,
            Supported = ["ko-KR"],
            Result = new RecognizedText("ko-KR",
            [
                new RecognizedLine("안녕", [new RecognizedWord("안녕", new OcrRect(0, 0, 10, 10))]),
            ]),
        };
        WindowsOcrService service = NewService(recognizer);

        // Prefer de-DE (unsupported): SelectLanguage returns null, so the service passes null and
        // the recognizer uses its profile default.
        OcrResult result = await service.RecognizeAsync(
            OcrRequest.FromBitmap(Solid(), 1.0, ["de-DE"]));

        Assert.Equal(OcrStatus.Success, result.Status);
        Assert.Null(recognizer.LastLanguageTag);
    }

    [Fact]
    public async Task Recognize_Cancelled_ReturnsCancelled()
    {
        var recognizer = new FakeRecognizer { Available = true, Supported = ["en-US"] };
        WindowsOcrService service = NewService(recognizer);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OcrResult result = await service.RecognizeAsync(
            OcrRequest.FromBitmap(Solid()), cts.Token);

        Assert.Equal(OcrStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Recognize_RecognizerReturnsNull_ReportsUnavailable()
    {
        var recognizer = new FakeRecognizer
        {
            Available = true,
            Supported = ["en-US"],
            Result = null, // engine could not be created for the request
        };
        WindowsOcrService service = NewService(recognizer);

        OcrResult result = await service.RecognizeAsync(
            OcrRequest.FromBitmap(Solid(), 1.0, ["en-US"]));

        Assert.Equal(OcrStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Recognize_UndecodableBytes_ReportsFailed()
    {
        var recognizer = new FakeRecognizer { Available = true, Supported = ["en-US"] };
        // Decoder returns null for the bytes.
        var service = new WindowsOcrService(recognizer, _ => null, _ => null, NullLogger.Instance);

        OcrResult result = await service.RecognizeAsync(OcrRequest.FromBytes([1, 2, 3]));

        Assert.Equal(OcrStatus.Failed, result.Status);
    }

    /// <summary>A recognizer that records what it was asked and returns a scripted result.</summary>
    private sealed class FakeRecognizer : IOcrRecognizer
    {
        public bool Available { get; set; }

        public IReadOnlyList<string> Supported { get; set; } = [];

        public int MaxDimension { get; set; } = 4000;

        public RecognizedText? Result { get; set; }

        public string? LastLanguageTag { get; private set; }

        public int LastWidth { get; private set; }

        public int LastHeight { get; private set; }

        public bool IsAvailable => Available;

        public IReadOnlyList<string> SupportedLanguages => Supported;

        public int MaxImageDimension => MaxDimension;

        public Task<RecognizedText?> RecognizeAsync(
            BitmapSource preparedBitmap,
            string? languageTag,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastLanguageTag = languageTag;
            LastWidth = preparedBitmap.PixelWidth;
            LastHeight = preparedBitmap.PixelHeight;
            return Task.FromResult(Result);
        }
    }
}
