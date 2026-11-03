using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationRecoveryR7Tests
{
    private const string Source = "Save the document before leaving this page, then close the window after the operation has finished.";
    private const string First = "페이지를 떠나기 전에 문서를 저장합니다,";
    private const string Second = "작업이 완료된 후 창을 닫습니다.";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LostSentenceRetriesEveryClauseInBothPaths(bool overlay, bool repetition)
    {
        var translator = new FakeTranslator(text => text == Source
            ? repetition ? "반복 문장 반복 문장 반복 문장 반복 문장" : "저장"
            : text.StartsWith("Save", StringComparison.Ordinal) ? First : Second);
        var decisions = new List<PinTranslationRecoveryDecision>();
        var service = new PinTranslationService(translator) { ObserveRecovery = decisions.Add };
        PinTranslationResult result = await Translate(service, Source, overlay);
        Assert.Equal(First + " " + Second, Text(result));
        Assert.False(result.NeedsReview);
        Assert.Equal(2, translator.Calls.Count);
        Assert.Equal(["Save the document before leaving this page,", "then close the window after the operation has finished."], translator.Calls[1]);
        PinTranslationRecoveryDecision decision = Assert.Single(decisions);
        Assert.Equal(repetition ? "repetition" : "truncated", decision.Issue);
        Assert.True(decision.Recovered);
        Assert.Equal(2, decision.PieceCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedRecoveryPieceKeepsTheEntireSentence(bool overlay)
    {
        const string source = "Save the document before leaving this page, then keep the original value 42 while closing the window.";
        var translator = new FakeTranslator(text => text == source ? "저장" : text.StartsWith("Save", StringComparison.Ordinal) ? First : "창을 닫습니다.");
        PinTranslationResult result = await Translate(new PinTranslationService(translator), source, overlay);
        Assert.Equal(source, Text(result));
        Assert.True(result.NeedsReview);
        Assert.Equal(2, translator.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedRecoveryOutputAlsoKeepsTheSource(bool overlay)
    {
        var translator = new FakeTranslator(_ => "반복 문장 반복 문장 반복 문장 반복 문장");
        PinTranslationResult result = await Translate(new PinTranslationService(translator), Source, overlay);
        Assert.Equal(Source, Text(result));
        Assert.True(result.NeedsReview);
        Assert.Equal(2, translator.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KoreanConnectiveRecoversTruncation(bool overlay)
    {
        const string source = "원본 화면을 캡처한 내용을 파일에 저장하며 다른 프로그램에서도 읽을 수 있도록 정보를 함께 보관합니다.";
        var translator = new FakeTranslator(text => text == source ? "Save" : text.EndsWith("저장하며", StringComparison.Ordinal)
            ? "Save the captured original screen to a file" : "and keep the information together so that other programs can read it.");
        PinTranslationResult result = await Translate(new PinTranslationService(translator, PinTranslationDirection.KoreanToEnglish), source, overlay);
        Assert.Equal("Save the captured original screen to a file and keep the information together so that other programs can read it.", Text(result));
        Assert.False(result.NeedsReview);
        Assert.Equal(2, translator.Calls.Count);
    }

    [Fact]
    public async Task OcrLineBoundariesRecoverASentenceWithoutClauses()
    {
        const string first = "Save the document before leaving this page";
        const string second = "and then close the window after the operation has finished.";
        var translator = new FakeTranslator(text => text == first + " " + second ? "저장"
            : text == first ? First : Second);
        var recognition = PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line(first, 20, 20, 700, 25),
            PinTranslationOverlayTests.Line(second, 20, 53, 700, 25));
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator).TranslateRecognizedAsync(
            recognition, PinContent.FromImage(PinTranslationOverlayTests.Bitmap(1000, 300)), CancellationToken.None));
        Assert.Equal(First + " " + Second, Text(result));
        Assert.Equal([first, second], translator.Calls[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JoinedOcrParagraphKeepsRejectedSentenceSpellingWhileItsNeighborTranslates(bool hyphenated)
    {
        string source = hyphenated
            ? "SeIect the original document before completing the next con-\r\nfiguration step and preserve the required value 42, because\r\nthe report needs to stay unchanged. Save the document."
            : "SeIect the original document before completing the next\r\nstep and preserve the required value 42, because\r\nthe report needs to stay unchanged. Save the document.";
        string expected = hyphenated
            ? "SeIect the original document before completing the next configuration step and preserve the required value 42, because the report needs to stay unchanged. 문서를 저장합니다."
            : "SeIect the original document before completing the next step and preserve the required value 42, because the report needs to stay unchanged. 문서를 저장합니다.";
        var translator = new FakeTranslator(text => text == "Save the document." ? "문서를 저장합니다." : "저장");
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateRecognizedAsync(source, PinContent.FromImage(PinTranslationOverlayTests.Bitmap(300, 100)), CancellationToken.None));
        Assert.Equal(expected, result.Content.OriginalText);
        Assert.True(result.NeedsReview);
        Assert.StartsWith("Select the original", translator.Calls[0][0]);
        Assert.Equal(2, translator.Calls.Count);
    }

    [Fact]
    public void LengthBoundsRetainObservedConciseEnglishTranslations()
    {
        Assert.Equal(string.Empty, PinTranslationService.ContentLossReason("The type and amount of income that qualifies for the exemption", "면제 대상 소득의 종류 및 금액", PinTranslationDirection.EnglishToKorean));
        Assert.Equal(string.Empty, PinTranslationService.ContentLossReason("For the latest information about developments", "개발에 대한 최신 정보", PinTranslationDirection.EnglishToKorean));
        Assert.Equal("truncated", PinTranslationService.ContentLossReason(Source, "저장", PinTranslationDirection.EnglishToKorean));
    }

    [Fact]
    public void NovelNgramsCatchSpacedAndUnspacedLoopsButRetainSourceRepetition()
    {
        Assert.True(PinTranslationService.HasNovelRepetition(Source, "Form99-Form99-Form99-Form99-"));
        Assert.True(PinTranslationService.HasNovelRepetition(Source, "repeat the phrase; repeat the phrase, repeat the phrase."));
        Assert.True(PinTranslationService.HasNovelRepetition(Source, "반복되는문장반복되는문장반복되는문장"));
        const string literal = "repeat the phrase; repeat the phrase; repeat the phrase.";
        Assert.False(PinTranslationService.HasNovelRepetition(literal, literal));
        Assert.False(PinTranslationService.HasNovelRepetition(Source, First + " " + Second));
    }

    [Fact]
    public void ClausePiecesPreserveEveryCharacterApartFromBoundaryWhitespaceAndKeepValuesWhole()
    {
        const string source = "Read `first, second` before continuing, then keep https://example.com/a,b and value 1,000; finish the work.";
        string[] pieces = PinTranslationService.RecoveryPieces(source);
        Assert.Equal(source, string.Join(" ", pieces));
        Assert.Contains("`first, second`", pieces[0]);
        Assert.Equal(3, pieces.Length);
    }

    private static string? Text(PinTranslationResult result) => result.Document?.OriginalText ?? result.Content.OriginalText;

    private static async Task<PinTranslationResult> Translate(PinTranslationService service, string text, bool overlay)
    {
        if (overlay)
            return Assert.IsType<PinTranslationResult>(await service.TranslateRecognizedAsync(
                PinTranslationOverlayTests.Recognition(PinTranslationOverlayTests.Line(text, 20, 20, 850, 28)),
                PinContent.FromImage(PinTranslationOverlayTests.Bitmap(1000, 240)), CancellationToken.None));
        PinContent content = await StaThreadTask.RunAsync(() => PinContent.FromText(PinTranslationOverlayTests.Bitmap(300, 100), text, false));
        return Assert.IsType<PinTranslationResult>(await service.TranslateAsync(content, CancellationToken.None));
    }

    private sealed class FakeTranslator(Func<string, string> translate) : ITextTranslator
    {
        internal List<string[]> Calls { get; } = [];
        public long ModelDownloadBytes => 0;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(sources.ToArray());
            return Task.FromResult<IReadOnlyList<TranslatedText>>(sources.Select(source => new TranslatedText(source, translate(source), [], [])).ToArray());
        }
    }
}
