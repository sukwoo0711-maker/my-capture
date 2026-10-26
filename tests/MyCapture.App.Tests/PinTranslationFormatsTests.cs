using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationFormatsTests
{
    [Theory]
    [InlineData("이미 한국어 문장입니다.", "ko", false, "AlreadyTarget")]
    [InlineData("Already in English.", "en-US", false, "AlreadyTarget")]
    [InlineData("한국어 문장입니다.", "en", true, "Available")]
    [InlineData("Read the guide.", "ko-KR", true, "Available")]
    [InlineData("日本語の文章です。", "ko", false, "Unsupported")]
    [InlineData("中文句子。", "en", false, "Unsupported")]
    [InlineData("123 2026-10-27 https://example.com/guide", "ko", false, "NoText")]
    public void LanguagePolicy_ExplainsEligibility(string text, string target, bool eligible, string reason)
    {
        PinLanguageAssessment assessment = PinLanguagePolicy.Assess(text, target);
        Assert.Equal(eligible, assessment.CanTranslate);
        Assert.Equal(reason, assessment.Reason.ToString());
    }

    [Fact]
    public async Task PdfLines_JoinWordsAndSentencesWhileKeepingParagraphBreaks()
    {
        const string source = "The docu-\r\nment has two\r\nphysical lines.\r\n\r\nAnother paragraph.";
        var translator = new FakeTranslator(text => text.StartsWith("The", StringComparison.Ordinal)
            ? "문서는 두 줄로 구성됩니다." : "다른 문단입니다.");
        PinContent content = CreateContent(source);
        PinTranslationResult result = await Translate(content, translator);
        Assert.Equal(["The document has two physical lines.", "Another paragraph."], translator.Sources);
        Assert.Equal("문서는 두 줄로 구성됩니다.\r\n\r\n다른 문단입니다.", result.Content.OriginalText);
        Assert.Equal(source, content.OriginalText);
    }

    [Fact]
    public async Task OcrLines_PreserveOrderAndPhysicalSeparators()
    {
        const string source = "First heading\r\nSecond heading\nThird heading";
        var translator = new FakeTranslator(text => text switch
        {
            "First heading" => "첫 번째 제목", "Second heading" => "두 번째 제목", _ => "세 번째 제목",
        });
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator,
            "ko", preserveLineBreaks: true).TranslateAsync(CreateContent(source), CancellationToken.None));
        Assert.Equal(["First heading", "Second heading", "Third heading"], translator.Sources);
        Assert.Equal("첫 번째 제목\r\n두 번째 제목\n세 번째 제목", result.Content.OriginalText);
    }

    [Fact]
    public async Task Json_TranslatesOnlyValuesAndReparsesWithAllOtherTokensIntact()
    {
        const string source = "{\r\n  \"English key\": \"Hello world\", \"nested\": [\"Save \\\"your work\\\"\", 42, true, null], \"url\": \"https://example.com/English\", \"date\": \"2026-10-27\"\r\n}";
        var translator = new FakeTranslator(text => text == "Hello world" ? "안녕하세요" : "\"작업\"을 저장하십시오");
        PinTranslationResult result = await Translate(CreateContent(source), translator);
        Assert.Equal(["Hello world", "Save \"your work\""], translator.Sources);
        using JsonDocument document = JsonDocument.Parse(result.Content.OriginalText!);
        Assert.Equal("안녕하세요", document.RootElement.GetProperty("English key").GetString());
        Assert.Contains("\"English key\": \"안녕하세요\"", result.Content.OriginalText);
        Assert.Contains("작업", result.Content.OriginalText);
        Assert.DoesNotContain("\\uC548", result.Content.OriginalText, StringComparison.OrdinalIgnoreCase);
        JsonElement array = document.RootElement.GetProperty("nested");
        Assert.Equal("\"작업\"을 저장하십시오", array[0].GetString());
        Assert.Equal(42, array[1].GetInt32());
        Assert.True(array[2].GetBoolean());
        Assert.Equal(JsonValueKind.Null, array[3].ValueKind);
        Assert.Contains("\r\n  \"English key\": ", result.Content.OriginalText);
        Assert.Contains(", 42, true, null], \"url\": \"https://example.com/English\", \"date\": \"2026-10-27\"", result.Content.OriginalText);
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData("var text = \"Do not translate // this literal\"; // Save your work\r\n/* Read the guide */", "var text = \"Do not translate // this literal\"; // 작업을 저장하십시오\r\n/* 안내서를 읽으십시오 */")]
    [InlineData("message = \"Do not translate # this literal\"\n# Save your work\nvalue = '''Keep # this text''' # Read the guide", "message = \"Do not translate # this literal\"\n# 작업을 저장하십시오\nvalue = '''Keep # this text''' # 안내서를 읽으십시오")]
    public async Task Code_TranslatesOnlyCommentsAndRetainsThemeAndValidRuns(string source, string expected)
    {
        PinContent content = CreateContent(source);
        var code = new PinCodeDocument(source, [new PinCodeRun(0, 3, "#569CD6"),
            new PinCodeRun(3, source.Length - 3, "#6A9955")], "#123456", "#DCDCDC");
        content = PinContent.FromCode(content.Image, source, code);
        var translator = new FakeTranslator(text => text == "Save your work" ? "작업을 저장하십시오" : "안내서를 읽으십시오");
        PinTranslationResult result = await Translate(content, translator);
        Assert.Equal(["Save your work", "Read the guide"], translator.Sources);
        Assert.Equal(expected, result.Content.OriginalText);
        PinCodeDocument translated = Assert.IsType<PinCodeDocument>(result.Content.CodeDocument);
        Assert.Equal("#123456", translated.Background);
        Assert.Equal("#DCDCDC", translated.Foreground);
        Assert.Equal(expected, translated.DisplayText);
        Assert.Equal("#569CD6", translated.Runs[0].Color);
        Assert.Equal(3, translated.Runs[0].Length);
        Assert.True(translated.MatchesOriginalText(expected));
        Assert.All(translated.Runs, run => Assert.InRange(run.Start + run.Length, 1, expected.Length));
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData("""var s = $"{Format("// Keep this text")}"; // Save before closing.""")]
    [InlineData("""var s = $@"{Format(@"// Keep this text")}"; // Save before closing.""")]
    [InlineData("""var s = @$"{Format("// Keep this text")}"; // Save before closing.""")]
    [InlineData("""var s = $"{Format($"{Read("// Keep this text")}")}"; // Save before closing.""")]
    [InlineData("""var s = $"{{Literal braces}} {new { Message = "/* Keep this text */" }}"; // Save before closing.""")]
    public async Task CSharpInterpolatedLiterals_NeverExposeNestedStringsAsComments(string source)
    {
        PinContent content = CreateContent(source);
        content = PinContent.FromCode(content.Image, source, new PinCodeDocument(source, [], "#123456", "#DCDCDC"));
        var translator = new FakeTranslator(_ => "닫기 전에 저장하십시오.");
        PinTranslationResult result = await Translate(content, translator);
        Assert.Equal(["Save before closing."], translator.Sources);
        Assert.Equal(source.Replace("Save before closing.", "닫기 전에 저장하십시오.", StringComparison.Ordinal), result.Content.OriginalText);
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData("#region Feature\n// Save before closing.\n#endregion")]
    [InlineData("#undef FEATURE\n// Save before closing.\n#error Keep this diagnostic")]
    [InlineData("#if FEATURE\n// Save before closing.\n#elif OTHER\n#else\n#endif")]
    [InlineData("#warning Keep this text // Also keep\n// Save before closing.\n#nullable enable")]
    public async Task CSharpDirectives_AreNeverMistakenForPythonComments(string source)
    {
        PinContent content = CreateContent(source);
        content = PinContent.FromCode(content.Image, source, new PinCodeDocument(source, [], "#123456", "#DCDCDC"));
        var translator = new FakeTranslator(_ => "닫기 전에 저장하십시오.");
        PinTranslationResult result = await Translate(content, translator);
        Assert.Equal(["Save before closing."], translator.Sources);
        Assert.Equal(source.Replace("Save before closing.", "닫기 전에 저장하십시오.", StringComparison.Ordinal), result.Content.OriginalText);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public async Task Table_PreservesCellLayoutNumbersDatesAndEmptyCells()
    {
        const string source = "Item\tStatus\tDate\r\nFirst item\t42\t2026-10-27\r\n\tReady\tOctober 27, 2026\r\n";
        PinContent content = CreateContent(source);
        content = PinContent.FromText(content.Image, source, true);
        var translator = new FakeTranslator(text => text switch
        {
            "Item" => "항목", "Status" => "상태", "Date" => "날짜", "First item" => "첫 항목", _ => "준비",
        });
        PinTranslationResult result = await Translate(content, translator);
        Assert.Equal(["Item", "Status", "Date", "First item", "Ready"], translator.Sources);
        Assert.Equal("항목\t상태\t날짜\r\n첫 항목\t42\t2026-10-27\r\n\t준비\tOctober 27, 2026\r\n", result.Content.OriginalText);
        Assert.Equal(PinContentKind.Table, result.Content.Kind);
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData("이미 한국어입니다. Read the guide.", "ko", "Read the guide.", "안내서를 읽으십시오.", "이미 한국어입니다. 안내서를 읽으십시오.")]
    [InlineData("Already English. 안내서를 읽으십시오.", "en", "안내서를 읽으십시오.", "Read the guide.", "Already English. Read the guide.")]
    public async Task MixedLanguages_TranslateOnlyTheForeignPortion(string source, string target, string input, string translated, string expected)
    {
        var translator = new FakeTranslator(_ => translated);
        PinTranslationResult result = await Translate(CreateContent(source), translator, target);
        Assert.Equal([input], translator.Sources);
        Assert.Equal(expected, result.Content.OriginalText);
    }

    [Fact]
    public async Task Mermaid_TranslatesLabelsButRetainsIdentifiersSyntaxAndCodeFences()
    {
        const string source = "# Release notes\n\n```mermaid\nflowchart TD\n  A[Start process] -->|Ready now| B{Save work}\n```\n\n```cs\nvar message = \"Never translate\";\n```\n";
        var translator = new FakeTranslator(text => text switch
        {
            "Release notes" => "배포 안내", "Start process" => "작업 시작", "Ready now" => "준비 완료", _ => "작업 저장",
        });
        PinContent content = CreateContent(source, true) with
        {
            MermaidResults = new Dictionary<int, MermaidRenderResult> { [0] = new(CreateContent("image").Image, null) },
        };
        PinTranslationResult result = await Translate(content, translator);
        Assert.Equal(["Release notes", "Start process", "Ready now", "Save work"], translator.Sources);
        Assert.Equal("# 배포 안내\n\n```mermaid\nflowchart TD\n  A[작업 시작] -->|준비 완료| B{작업 저장}\n```\n\n```cs\nvar message = \"Never translate\";\n```\n", result.Content.OriginalText);
        Assert.Null(result.Content.MermaidResults);
        Assert.True(result.Content.IsMarkdown);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public async Task PureMermaid_TranslatesNodeLabelsWithoutSendingDirectionOrIds()
    {
        const string source = "flowchart LR\n  Begin[Start process] --> End[Save work]";
        var translator = new FakeTranslator(text => text == "Start process" ? "작업 시작" : "작업 저장");
        PinTranslationResult result = await Translate(CreateContent(source, true), translator);
        Assert.Equal(["Start process", "Save work"], translator.Sources);
        Assert.Equal("flowchart LR\n  Begin[작업 시작] --> End[작업 저장]", result.Content.OriginalText);
    }

    [Theory]
    [InlineData("[Read guide][]")]
    [InlineData("[Read guide]")]
    public async Task MarkdownReferenceIds_CannotBeTranslatedAwayFromTheirDefinitions(string reference)
    {
        string source = reference + "\n\n[Read guide]: https://example.com";
        var translator = new FakeTranslator(text => text.Replace("Read guide", "안내서 읽기", StringComparison.Ordinal));
        PinTranslationResult result = await Translate(CreateContent(source, true), translator);
        Assert.Equal([reference], translator.Sources);
        Assert.Equal(source, result.Content.OriginalText);
        Assert.True(result.NeedsReview);
    }

    [Fact]
    public async Task MarkdownFullReference_StillAllowsVisibleLabelTranslation()
    {
        const string source = "[Read guide][guide]\n\n[guide]: https://example.com";
        var translator = new FakeTranslator(_ => "[안내서 읽기][guide]");
        PinTranslationResult result = await Translate(CreateContent(source, true), translator);
        Assert.Equal("[안내서 읽기][guide]\n\n[guide]: https://example.com", result.Content.OriginalText);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public async Task MermaidEntities_AreEncodedOnceAfterTranslation()
    {
        const string source = "flowchart TD\n  A[\"Read &amp; save\"]";
        var translator = new FakeTranslator(_ => "읽기 &amp; 저장");
        PinTranslationResult result = await Translate(CreateContent(source, true), translator);
        Assert.Equal(["Read &amp; save"], translator.Sources);
        Assert.Equal("flowchart TD\n  A[\"읽기 &amp; 저장\"]", result.Content.OriginalText);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public async Task UnsafeMermaidResult_RetainsExactOriginalEntityText()
    {
        const string source = "flowchart TD\n  A[\"Read &amp; save 42\"]";
        var translator = new FakeTranslator(_ => "읽기 &amp; 저장 43");
        PinTranslationResult result = await Translate(CreateContent(source, true), translator);
        Assert.Equal(source, result.Content.OriginalText);
        Assert.True(result.NeedsReview);
    }

    [Fact]
    public async Task DamagedSentence_DoesNotDiscardOtherTranslatedSentencesOrParagraphSpacing()
    {
        const string source = "Save before closing.  Keep 42 records.\r\n\r\nRead the guide.";
        var translator = new FakeTranslator(text => text switch
        {
            "Save before closing." => "닫기 전에 저장하십시오.", "Keep 42 records." => "43개 기록을 유지하십시오.", _ => "안내서를 읽으십시오.",
        });
        PinTranslationResult result = await Translate(CreateContent(source), translator);
        Assert.Equal(["Save before closing.", "Keep 42 records.", "Read the guide."], translator.Sources);
        Assert.Equal("닫기 전에 저장하십시오.  Keep 42 records.\r\n\r\n안내서를 읽으십시오.", result.Content.OriginalText);
        Assert.True(result.NeedsReview);
    }

    [Fact]
    public async Task LongProse_IsPassedInFullAndRetainsTheLastSentence()
    {
        string source = string.Concat(Enumerable.Repeat("Read the documentation carefully. ", 80)) + "The final sentence.";
        var translator = new FakeTranslator(text => text);
        PinTranslationResult result = await Translate(CreateContent(source), translator);
        Assert.True(source.Length > 2000);
        Assert.Equal(81, translator.Sources.Length);
        Assert.All(translator.Sources[..80], sentence => Assert.Equal("Read the documentation carefully.", sentence));
        Assert.Equal("The final sentence.", translator.Sources[^1]);
        Assert.Equal(source, string.Join(" ", translator.Sources));
        Assert.Equal(source, result.Content.OriginalText);
        Assert.EndsWith("The final sentence.", result.Content.OriginalText);
    }

    [Fact]
    public async Task ProtectedValuesCannotBeAlteredInEitherDirection()
    {
        const string source = "Save {0} on 2026-10-27 to C:\\tools\\file.txt and contact hello@example.com.";
        var translator = new FakeTranslator(_ => "{1}을 2026-10-28에 C:\\tools\\other.txt에 저장하고 other@example.com에 연락하십시오.");
        PinTranslationResult result = await Translate(CreateContent(source), translator);
        Assert.Equal(source, result.Content.OriginalText);
        Assert.True(result.NeedsReview);
    }

    private static PinContent CreateContent(string source, bool markdown = false)
    {
        BitmapSource image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return markdown ? PinContent.FromMarkdown(image, source) : PinContent.FromText(image, source, false);
    }

    private static async Task<PinTranslationResult> Translate(PinContent content, FakeTranslator translator, string target = "ko") =>
        Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator, target).TranslateAsync(content, CancellationToken.None));

    private sealed class FakeTranslator(Func<string, string> translate) : ITextTranslator
    {
        internal string[] Sources { get; private set; } = [];
        public long ModelDownloadBytes => 0;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected model preparation.");
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected download.");
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Sources = sources.ToArray();
            return Task.FromResult<IReadOnlyList<TranslatedText>>(sources.Select(text => new TranslatedText(text, translate(text), [], [])).ToArray());
        }
    }
}
