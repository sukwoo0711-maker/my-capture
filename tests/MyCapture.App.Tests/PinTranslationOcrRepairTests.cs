using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOcrRepairTests
{
    [Theory]
    [InlineData("tO", "to")]
    [InlineData("dO", "do")]
    [InlineData("j0bs", "jobs")]
    [InlineData("SeIect", "Select")]
    [InlineData("NOtify", "Notify")]
    [InlineData("t0", "to")]
    [InlineData("Ab0ut", "About")]
    [InlineData("SeIect.", "Select.")]
    [InlineData("(SeIect), NOtify; j0bs!", "(Select), Notify; jobs!")]
    [InlineData("SeIect: dO-it", "Select: do-it")]
    [InlineData("SeIect 항목", "Select 항목")]
    [InlineData("iOS", "iOS")]
    [InlineData("macOS", "macOS")]
    [InlineData("IO", "IO")]
    [InlineData("B2B", "B2B")]
    [InlineData("0x1F", "0x1F")]
    [InlineData("10 100 2026 3.0", "10 100 2026 3.0")]
    [InlineData("W1th", "W1th")]
    [InlineData("f00d", "f00d")]
    [InlineData("bOOks", "bOOks")]
    [InlineData("SeIect_area", "SeIect_area")]
    [InlineData("SeIect.exe", "SeIect.exe")]
    [InlineData("SeIect/file", "SeIect/file")]
    [InlineData("SeIect@example.com", "SeIect@example.com")]
    [InlineData("SeIect-user@example.com", "SeIect-user@example.com")]
    [InlineData("SeIect+tag@example.com", "SeIect+tag@example.com")]
    [InlineData("https://example.com/a-SeIect", "https://example.com/a-SeIect")]
    [InlineData(@"C:\folder-SeIect", @"C:\folder-SeIect")]
    [InlineData("/SeIect", "/SeIect")]
    [InlineData(@"C:\SeIect", @"C:\SeIect")]
    [InlineData(".SeIect", ".SeIect")]
    [InlineData("initial Original IOI a I", "initial Original IOI a I")]
    [InlineData("abI aI Ia", "abI aI Ia")]
    public void RepairsOnlyTheSpecifiedLatinWordConfusions(string source, string expected)
    {
        Assert.Equal(expected, OcrLatinTextRepair.Repair(source));
        Assert.Equal(expected, OcrLatinTextRepair.Repair(expected));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BothOcrPathsRepairBeforeTranslationAndValueChecks(bool koreanSource, bool overlay)
    {
        string source = koreanSource ? "SeIect 항목을 선택한 다음 문서를 저장하고 창을 닫습니다."
            : "SeIect the area and NOtify customers tO check j0bs.";
        string expected = OcrLatinTextRepair.Repair(source);
        string translated = koreanSource ? "Select the item, then save the document and close the window."
            : "영역을 선택하고 고객에게 작업을 확인하도록 알립니다.";
        var translator = new PinTranslationOverlayTests.FakeTranslator(_ => translated);
        var service = new PinTranslationService(translator, koreanSource
            ? PinTranslationDirection.KoreanToEnglish : PinTranslationDirection.EnglishToKorean);
        PinContent picture = PinContent.FromImage(PinTranslationOverlayTests.Bitmap(1100, 220));
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(overlay
            ? await service.TranslateRecognizedAsync(PinTranslationOverlayTests.Recognition(
                PinTranslationOverlayTests.Line(source, 30, 40, 1000, 32)), picture, CancellationToken.None)
            : await service.TranslateRecognizedAsync(source, picture, CancellationToken.None));

        Assert.Equal(expected, Assert.Single(translator.Sources));
        Assert.False(result.NeedsReview);
        Assert.Equal(translated, result.Content.OriginalText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneOcrLabelBecomesEligibleAfterRepair(bool overlay)
    {
        var translator = new PinTranslationOverlayTests.FakeTranslator(_ => "선택");
        var service = new PinTranslationService(translator);
        PinContent picture = PinContent.FromImage(PinTranslationOverlayTests.Bitmap(500, 160));
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(overlay
            ? await service.TranslateRecognizedAsync(PinTranslationOverlayTests.Recognition(
                PinTranslationOverlayTests.Line("SeIect", 30, 40, 150, 26)), picture, CancellationToken.None)
            : await service.TranslateRecognizedAsync("SeIect", picture, CancellationToken.None));

        Assert.Equal("Select", Assert.Single(translator.Sources));
        Assert.Equal("선택", result.Content.OriginalText);
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairRetainsExistingEligibilityForShortWords(bool overlay)
    {
        var translator = new PinTranslationOverlayTests.FakeTranslator(text => text);
        var service = new PinTranslationService(translator);
        PinContent picture = PinContent.FromImage(PinTranslationOverlayTests.Bitmap(500, 160));
        PinTranslationResult? result = overlay
            ? await service.TranslateRecognizedAsync(PinTranslationOverlayTests.Recognition(
                PinTranslationOverlayTests.Line("tO", 30, 40, 50, 26)), picture, CancellationToken.None)
            : await service.TranslateRecognizedAsync("tO", picture, CancellationToken.None);

        Assert.Equal(PinTranslationAvailability.NothingToTranslate,
            PinTranslationService.Evaluate("to", true).Availability);
        Assert.Null(result);
        Assert.Empty(translator.Sources);
    }

    [Fact]
    public async Task DocumentOcrRepairsCommentTextAndPreservesTheOriginalCode()
    {
        const string source = "int tO = 3; // SeIect the area before saving\nreturn tO;";
        var translator = new PinTranslationOverlayTests.FakeTranslator(_ => "저장하기 전에 영역을 선택합니다.");
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateRecognizedAsync(source, PinContent.FromImage(PinTranslationOverlayTests.Bitmap(500, 160)), CancellationToken.None));

        Assert.Equal("Select the area before saving", Assert.Single(translator.Sources));
        Assert.Equal("int tO = 3; // 저장하기 전에 영역을 선택합니다.\nreturn tO;", result.Content.OriginalText);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public async Task ManuallyEnteredTextDoesNotUseOcrRepair()
    {
        const string source = "SeIect the area and NOtify customers tO check j0bs.";
        var translator = new PinTranslationOverlayTests.FakeTranslator(text => text);
        PinContent textPin = PinContent.FromText(PinTranslationOverlayTests.Bitmap(500, 160), source, false);
        _ = await new PinTranslationService(translator).TranslateAsync(textPin, CancellationToken.None);

        Assert.Equal(source, Assert.Single(translator.Sources));
    }
}
