using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlayServiceTests
{
    [Theory]
    [InlineData("숨김", " \t숨김\r\n ")]
    [InlineData("원본 보관", " 원본\t\r\n보관 ")]
    public async Task IdentityOutputPreservesImageAndDoesNotRequestReview(string source, string translated)
    {
        BitmapSource image = PinTranslationOverlayTests.Bitmap(300, 100);
        var translator = new PinTranslationOverlayTests.FakeTranslator(_ => translated);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator,
            PinTranslationDirection.KoreanToEnglish).TranslateRecognizedAsync(
                PinTranslationOverlayTests.Recognition(PinTranslationOverlayTests.Line(source, 20, 20, 80)),
                PinContent.FromImage(image), CancellationToken.None));

        Assert.Equal(1, translator.Calls);
        Assert.Same(image, result.Content.Image);
        Assert.False(result.NeedsReview);
        Assert.NotNull(result.Document);
    }

    [Theory]
    [InlineData("Select the area you need.", false)]
    [InlineData("Select_area is the area you need.", true)]
    public async Task ProtectedIdentifierKeepsOnlyItsSentenceAndTheColumnStillTranslates(string selection, bool rejected)
    {
        BitmapSource image = PinTranslationOverlayTests.Bitmap(1100, 680);
        OcrResult recognized = PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line("Create a new document.", 40, 130, 370, 26),
            PinTranslationOverlayTests.Line("Save your work regularly.", 40, 168, 370, 26),
            PinTranslationOverlayTests.Line("Add pictures t0 your library.", 590, 130, 340, 26),
            PinTranslationOverlayTests.Line(selection, 590, 168, 340, 26),
            PinTranslationOverlayTests.Line("Keep the original picture safe.", 590, 206, 340, 26),
            PinTranslationOverlayTests.Line("Open it again later.", 590, 244, 340, 26));
        var translator = new PinTranslationOverlayTests.FakeTranslator(text => text switch
        {
            "Create a new document." => "새 문서를 만듭니다.",
            "Save your work regularly." => "작업을 정기적으로 저장합니다.",
            "Add pictures to your library." => "라이브러리에 사진을 추가합니다.",
            "Keep the original picture safe." => "원본 사진을 안전하게 보관하십시오.",
            "Open it again later." => "나중에 다시 열 수 있습니다.",
            _ => "필요한 영역을 선택합니다."
        });
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateRecognizedAsync(recognized, PinContent.FromImage(image), CancellationToken.None));

        Assert.Equal(2, PinTranslationOverlay.BuildBlocks(recognized).Count);
        Assert.Equal(1, translator.Calls);
        Assert.Equal(6, translator.Sources.Count);
        Assert.Equal(rejected, result.NeedsReview);
        Assert.Contains("새 문서를 만듭니다.", result.Content.OriginalText);
        var right = new Int32Rect(590, 130, 340, 140);
        // The column is redrawn either way: only the rejected sentence stays in the source language.
        Assert.NotEqual(Pixels(image, right), Pixels(result.Content.Image, right));
        Assert.Contains("원본 사진을 안전하게 보관하십시오.", result.Content.OriginalText);
        if (rejected)
        {
            Assert.Contains(selection, result.Content.OriginalText);
            Assert.DoesNotContain("필요한 영역을 선택합니다.", result.Content.OriginalText);
        }
        else Assert.Contains("필요한 영역을 선택합니다.", result.Content.OriginalText);
    }

    [Fact]
    public async Task BlockWithNoAcceptedSentenceKeepsItsPixels()
    {
        BitmapSource image = PinTranslationOverlayTests.Bitmap(600, 200);
        OcrResult recognized = PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line("Select_area is the area you need.", 40, 60, 340, 26));
        var translator = new PinTranslationOverlayTests.FakeTranslator(_ => "필요한 영역을 선택합니다.");
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateRecognizedAsync(recognized, PinContent.FromImage(image), CancellationToken.None));

        Assert.True(result.NeedsReview);
        var block = new Int32Rect(30, 50, 360, 46);
        Assert.Equal(Pixels(image, block), Pixels(result.Content.Image, block));
    }

    private static byte[] Pixels(BitmapSource image, Int32Rect rect)
    {
        var pixels = new byte[rect.Width * rect.Height * 4];
        image.CopyPixels(rect, pixels, rect.Width * 4, 0);
        return pixels;
    }
}
