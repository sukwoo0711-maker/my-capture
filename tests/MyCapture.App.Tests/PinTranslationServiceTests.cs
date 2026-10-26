using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationServiceTests
{
    [Theory]
    [InlineData("https://example.com/English/document")]
    [InlineData("www.example.com/English/document")]
    [InlineData(@"C:\Program Files\MyCapture\readme.txt")]
    [InlineData("/usr/local/share/readme.txt")]
    [InlineData("hello@example.com")]
    [InlineData("123.5 MHz 42 2026-10-27")]
    [InlineData("이미 한국어로 작성한 문장입니다.")]
    [InlineData("console.log(\"Hello world\");")]
    public async Task NonProse_IsNotSentToTheTranslator(string text)
    {
        PinContent content = await CreateContent(text);
        var translator = new FakeTranslator();
        var service = new PinTranslationService(translator);

        Assert.False(PinTranslationService.CanTranslate(content));
        Assert.Null(await service.TranslateAsync(content, CancellationToken.None));
        Assert.Empty(translator.Calls);
    }

    [Fact]
    public async Task TablesAreEligibleAndPicturesAreDecidedByTheirRecognizedText()
    {
        PinContent text = await CreateContent("Hello world");
        PinContent image = PinContent.FromImage(text.Image);
        PinContent table = PinContent.FromText(text.Image, "English text\tAnother column", true);
        Assert.True(PinTranslationService.CanTranslate(table));
        // A picture's language is known only after its text is read: the command stays on until then.
        Assert.Equal(PinTranslationAvailability.Translatable, PinTranslationService.Evaluate(image, true).Availability);
        Assert.Equal(PinTranslationAvailability.AlreadyTargetLanguage, PinTranslationService.Evaluate("이미 한국어로 작성한 문장입니다.", true).Availability);
        Assert.Equal(PinTranslationAvailability.UnsupportedLanguage, PinTranslationService.Evaluate("今日は天気がいいので散歩します。", true).Availability);
        Assert.Equal(PinTranslationDirection.KoreanToEnglish, PinTranslationService.Evaluate("창을 닫기 전에 문서를 저장합니다.", false).Direction);
    }

    [Fact]
    public async Task Json_TranslatesOnlyStringValuesAndStaysValidJson()
    {
        const string source = "{\"message\": \"Hello world\", \"count\": 3, \"url\": \"https://example.com/a\", \"items\": [\"Read this guide\", true, null]}";
        var translator = new FakeTranslator(text => text == "Hello world" ? "안녕 \"세상\"" : "안내를 읽으십시오.");
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(source), CancellationToken.None));
        Assert.Equal(["Hello world", "Read this guide"], Assert.Single(translator.Calls));
        Assert.Equal("{\"message\": \"안녕 \\\"세상\\\"\", \"count\": 3, \"url\": \"https://example.com/a\", \"items\": [\"안내를 읽으십시오.\", true, null]}", result.Content.OriginalText);
        using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(result.Content.OriginalText!);
        Assert.Equal("안녕 \"세상\"", parsed.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Code_TranslatesCommentsOnlyAndNeverStringsOrStatements()
    {
        const string source = "var text = \"Hello world\"; // Read the documentation first\nvar url = \"http://example.com/a//b\";\n// Save the document before closing\nint count = 3;";
        var translator = new FakeTranslator(text => text.StartsWith("Read", StringComparison.Ordinal) ? "먼저 문서를 읽으십시오." : "닫기 전에 문서를 저장하십시오.");
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(source), CancellationToken.None));
        Assert.Equal("var text = \"Hello world\"; // 먼저 문서를 읽으십시오.\nvar url = \"http://example.com/a//b\";\n// 닫기 전에 문서를 저장하십시오.\nint count = 3;", result.Content.OriginalText);
    }

    [Fact]
    public async Task Prose_JoinsHardWrappedPdfLinesAndHyphenatedWordsBeforeTranslating()
    {
        const string source = "The configuration of the system requires careful at-\r\ntention to the settings and the con-\r\nfiguration files used by the service.\r\n\r\nA second paragraph stays separate.";
        var translator = new FakeTranslator(text => "번역:" + text);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(source), CancellationToken.None));
        Assert.Equal(["The configuration of the system requires careful attention to the settings and the configuration files used by the service.",
            "A second paragraph stays separate."], Assert.Single(translator.Calls));
        Assert.Equal("번역:The configuration of the system requires careful attention to the settings and the configuration files used by the service.\r\n\r\n번역:A second paragraph stays separate.", result.Content.OriginalText);
    }

    [Fact]
    public async Task KoreanToEnglish_TranslatesKoreanTextAndKeepsEnglishAndNumbers()
    {
        const string source = "서버 응답 시간이 3.5초를 초과했습니다.\n업데이트는 약 2분 정도 걸립니다.";
        var translator = new FakeTranslator(text => text.Contains("3.5", StringComparison.Ordinal) ? "The server response time exceeded 3.5 seconds." : "The update takes about 2 minutes.");
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator, PinTranslationDirection.KoreanToEnglish)
            .TranslateAsync(await CreateContent(source), CancellationToken.None));
        Assert.Equal("The server response time exceeded 3.5 seconds.\nThe update takes about 2 minutes.", result.Content.OriginalText);
    }

    [Fact]
    public async Task PlainText_PreservesExactWhitespaceAndKeepsTheSourceSeparate()
    {
        const string source = "  Hello world.\r\n\r\nSecond paragraph.\t";
        PinContent content = await CreateContent(source);
        var translator = new FakeTranslator(text => text == "Hello world." ? "안녕하세요." : "두 번째 문단입니다.");

        PinTranslationResult result = Assert.IsType<PinTranslationResult>(
            await new PinTranslationService(translator).TranslateAsync(content, CancellationToken.None));

        Assert.Equal(source, content.OriginalText);
        Assert.Equal("  안녕하세요.\r\n\r\n두 번째 문단입니다.\t", result.Content.OriginalText);
        Assert.Equal(["Hello world.", "Second paragraph."], Assert.Single(translator.Calls));
        Assert.NotSame(content, result.Content);
        Assert.NotSame(content.Image, result.Content.Image);
        Assert.True(result.Content.Image.IsFrozen);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public async Task Markdown_KeepsWholeInlineSentencesAndPreservesSyntaxWithoutPlaceholders()
    {
        const string source = "# Welcome aboard\r\n\r\n- Read **the guide** and `config.json` before [opening settings](https://example.com/a_(b)?x=1).\r\n\r\n```cs\r\nvar text = \"Do not translate\";\r\n```\r\n\r\n| Column name | Details |\r\n| :--- | ---: |\r\n| First value | Use 42 MHz |\r\n";
        PinContent content = await CreateContent(source, markdown: true);
        var translator = new FakeTranslator(text => text);

        PinTranslationResult result = Assert.IsType<PinTranslationResult>(
            await new PinTranslationService(translator).TranslateAsync(content, CancellationToken.None));

        Assert.Equal(source, result.Content.OriginalText);
        Assert.True(result.Content.IsMarkdown);
        string[] calls = Assert.Single(translator.Calls);
        Assert.Contains("Welcome aboard", calls);
        Assert.Contains("Read **the guide** and `config.json` before [opening settings](https://example.com/a_(b)?x=1).", calls);
        // Table cells are translated one by one; pipes and the alignment row never reach the model.
        Assert.Contains("Column name", calls);
        Assert.Contains("Details", calls);
        Assert.Contains("First value", calls);
        Assert.Contains("Use 42 MHz", calls);
        Assert.DoesNotContain(calls, text => text.Contains("Do not translate", StringComparison.Ordinal)
            || text.Contains("---", StringComparison.Ordinal));
        Assert.True(result.Content.Image.IsFrozen);
    }

    [Fact]
    public async Task Markdown_QuotedFencesLongBackticksImagesAndReferenceTargetsRemainLiteral()
    {
        const string source = "> ```text\n> Do not translate this code\n> ```\n\nRead ``code ` with ticks`` and ![Keep this image](https://example.com/picture.png).\nRead [the guide][source].\n[source]: https://example.com/a \"Keep title\"\n";
        var translator = new FakeTranslator(text => text);

        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(source, markdown: true), CancellationToken.None));

        Assert.Equal(source, result.Content.OriginalText);
        Assert.DoesNotContain(Assert.Single(translator.Calls), text => text.Contains("Do not translate", StringComparison.Ordinal)
            || text.Contains("Keep title", StringComparison.Ordinal));
        Assert.Contains("Read ``code ` with ticks`` and ![Keep this image](https://example.com/picture.png).",
            Assert.Single(translator.Calls));
    }

    [Fact]
    public async Task Markdown_UnclosedFenceIsNeverTranslated()
    {
        PinContent content = await CreateContent("```text\nDo not translate this code", markdown: true);
        Assert.False(PinTranslationService.CanTranslate(content));
    }

    [Fact]
    public async Task Markdown_ModelOutputCannotIntroduceNewStructure()
    {
        var translator = new FakeTranslator(_ => "# 제목\n[링크](주소) *강조* | 열");
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent("- Read the guide", markdown: true), CancellationToken.None));
        Assert.Equal("- Read the guide", result.Content.OriginalText);
        Assert.True(result.NeedsReview);
    }

    [Theory]
    [InlineData("- ")]
    [InlineData("+ ")]
    [InlineData("* ")]
    [InlineData("1. ")]
    [InlineData("2) ")]
    [InlineData("- [x] ")]
    [InlineData("## ")]
    [InlineData("> ")]
    [InlineData("> - ")]
    public async Task Markdown_BlockPrefixIsPreservedOutsideTheModelInput(string prefix)
    {
        var translator = new FakeTranslator(_ => "안내서 읽기");
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(prefix + "Read the guide", markdown: true), CancellationToken.None));

        Assert.Equal(["Read the guide"], Assert.Single(translator.Calls));
        Assert.Equal(prefix + "안내서 읽기", result.Content.OriginalText);
    }

    [Fact]
    public async Task ProtectedValues_KeepTheWholeSentenceContextAndRemainExact()
    {
        const string source = "Read https://example.com/guide using config.json at 42 MHz and C:\\tools\\file.txt.";
        var translator = new FakeTranslator(text => text);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(source), CancellationToken.None));
        Assert.Equal(source, result.Content.OriginalText);
        Assert.Equal([source], Assert.Single(translator.Calls));
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData("The update takes about 2 minutes.", "업데이트는 약 2분 정도 걸립니다.")]
    [InlineData("Do not change the timeout value from 5000 to 30000.", "시간 제한 값을 5000에서 30000으로 변경하지 마십시오.")]
    public async Task NumericalSentences_AreTranslatedWholeWithObservedModelResults(string source, string translated)
    {
        var translator = new FakeTranslator(_ => translated);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(source), CancellationToken.None));

        Assert.Equal([source], Assert.Single(translator.Calls));
        Assert.Equal(translated, result.Content.OriginalText);
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData("Save **your work** before closing.", "* SAVE * YOUR WORK * 마감하기 전에")]
    [InlineData("Read [the guide](https://example.com/guide) before continuing.", "계속하기 전에 [가이 (https://example.com/guide)를 읽으십시오.")]
    public async Task ObservedModelMarkupDamage_PreservesTheWholeSourceAndRequestsReview(string source, string translated)
    {
        var translator = new FakeTranslator(_ => translated);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(source, markdown: true), CancellationToken.None));

        Assert.Equal([source], Assert.Single(translator.Calls));
        Assert.Equal(source, result.Content.OriginalText);
        Assert.True(result.NeedsReview);
    }

    [Theory]
    [InlineData("Run `npm install` before starting.", "시작하기 전에 `npm install`을 실행한다.")]
    [InlineData("Read the guide and `config.json` before [opening settings](https://example.com/a_(b)?x=1).", "[설정을 열기](https://example.com/a_(b)?x=1) 전에 가이드와 `config.json`을 읽으십시오.")]
    [InlineData("Save **your work** before closing.", "닫기 전에 **작업**을 저장하십시오.")]
    public async Task ValidMarkdown_KeepsKoreanWordOrderAndExactInlineSyntax(string source, string translated)
    {
        var translator = new FakeTranslator(_ => translated);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(source, markdown: true), CancellationToken.None));

        Assert.Equal(translated, result.Content.OriginalText);
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData("The update takes about 2 minutes.", "업데이트는 약 3분 정도 걸립니다.")]
    [InlineData("The update takes about 2 minutes.", "업데이트는 약 2분, 2분 정도 걸립니다.")]
    [InlineData("Do not change the timeout value from 5000 to 30000.", "시간 제한 값을 5000에서 3000으로 변경하지 마십시오.")]
    [InlineData("Read www.example.com/English/document for details.", "자세한 내용은 www.example.com을 읽으십시오.")]
    public async Task ChangedOrDuplicatedValues_PreserveTheSourceEvenWithoutEngineWarnings(string source, string translated)
    {
        var translator = new FakeTranslator(_ => translated);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(await CreateContent(source), CancellationToken.None));

        Assert.Equal(source, result.Content.OriginalText);
        Assert.True(result.NeedsReview);
    }

    [Fact]
    public async Task DelayedResult_IsNotPublishedAfterCancellationEvenIfEngineIgnoresIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var translator = new FakeTranslator
        {
            Translate = async sources =>
            {
                entered.SetResult();
                await release.Task;
                return sources.Select(text => new TranslatedText(text, "번역된 문장", [], [])).ToArray();
            },
        };
        using var cancellation = new CancellationTokenSource();
        PinContent content = await CreateContent("Read the documentation.");
        Task<PinTranslationResult?> pending = new PinTranslationService(translator).TranslateAsync(content, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("Read the documentation.", content.OriginalText);
    }

    [Fact]
    public async Task Failure_PropagatesAndDoesNotChangeTheOriginal()
    {
        var translator = new FakeTranslator
        {
            Translate = _ => Task.FromException<IReadOnlyList<TranslatedText>>(new InvalidOperationException("model failure")),
        };
        PinContent content = await CreateContent("Read the documentation.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PinTranslationService(translator).TranslateAsync(content, CancellationToken.None));
        Assert.Equal("Read the documentation.", content.OriginalText);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("mismatched")]
    public async Task InvalidEngineResults_CannotDropOrReorderSourceText(string failure)
    {
        var translator = new FakeTranslator
        {
            Translate = sources => Task.FromResult<IReadOnlyList<TranslatedText>>(failure switch
            {
                "empty" => [],
                "missing" => [new TranslatedText(sources[0], " ", [], [])],
                _ => [new TranslatedText("a different source", "번역된 문장", [], [])],
            }),
        };
        PinContent content = await CreateContent("Read the documentation.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PinTranslationService(translator)
            .TranslateAsync(content, CancellationToken.None));
    }

    [Fact]
    public async Task ReviewStatusAndProvenanceFollowTheTranslatedResult()
    {
        var translator = new FakeTranslator
        {
            Translate = sources => Task.FromResult<IReadOnlyList<TranslatedText>>(
                [new TranslatedText(sources[0], "검토할 문장", ["missing value"], [])]),
        };
        PinContent content = await CreateContent("Read the documentation.");
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateAsync(content, CancellationToken.None));
        Assert.True(result.NeedsReview);
        Assert.Equal(content.Provenance, result.Content.Provenance);
    }

    private static Task<PinContent> CreateContent(string text, bool markdown = false) => StaThreadTask.RunAsync(() =>
    {
        BitmapSource image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return markdown ? PinContent.FromMarkdown(image, text) : PinContent.FromText(image, text, false);
    });

    private sealed class FakeTranslator(Func<string, string>? convert = null) : ITextTranslator
    {
        internal List<string[]> Calls { get; } = [];
        internal Func<IReadOnlyList<string>, Task<IReadOnlyList<TranslatedText>>>? Translate { get; init; }
        public long ModelDownloadBytes => 0;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Model preparation belongs to the pin controller.");
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The adapter must not download a model.");
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add(sources.ToArray());
            return Translate?.Invoke(sources) ?? Task.FromResult<IReadOnlyList<TranslatedText>>(
                sources.Select(text => new TranslatedText(text, convert?.Invoke(text) ?? "한국어 문장", [], [])).ToArray());
        }
    }
}
