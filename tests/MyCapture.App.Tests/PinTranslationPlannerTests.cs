using System.IO;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Samples and assertions shared by the planner tests.</summary>
internal static class PlannerSamples
{
    internal const string SampleDirectory = @"C:\GitRepositories\my-capture\work\pin-content-20261027\test-samples";

    internal static readonly string[] SampleFiles =
    [
        "T1-pdf-copied-lines.txt", "T4-json-nested.json", "T5-comments.cs", "T5-comments.py", "T5-comments.js",
        "A5-excel-table.tsv", "T10-mixed-ko-en.txt", "T14-protected-tokens.txt", "T15-long-english-2000.txt", "T13-japanese.txt",
    ];

    internal static readonly Dictionary<string, string> Inline = new()
    {
        ["empty"] = "",
        ["blank"] = " \r\n\t\r\n  ",
        ["one-char"] = "x",
        ["wrapped-pdf"] = "The quick brown fox jumps over the lazy dog and keeps running through the\r\n"
            + "forest until it reaches the river, where it stops to drink some water from\r\n"
            + "Page 2 of 5\r\n"
            + "the clear stream before moving on to the next valley with its friends.\r\n"
            + "\r\n"
            + "A short note.\r\n",
        ["list"] = "Shopping list\n- Open the window\n- Close the door\n1. First step here\n2) Second step here\n  a) Indented option\n",
        ["poem"] = "Roses are red\nViolets are blue\nSugar is sweet\nAnd so are you\n",
        ["mixed-ko-en"] = "오늘 회의에서 We need to finalize the budget by Friday 라고 했다\n서버 API 재시작 필요\nPlease check the 회의 일정 tomorrow morning\n",
        ["korean"] = "이것은 한국어로 작성된 문장입니다. 번역이 필요하지 않습니다.\n두 번째 줄도 한국어입니다.\n",
        ["urls-paths"] = "https://example.com/a//b\nC:\\Users\\me\\file.txt\nuser@example.com\n12,345.67\nint x = 5;\n",
        ["json"] = "{\n  \"title\": \"Hello world, this is a test\",\n  \"count\": 12,\n  \"ratio\": 1.50e+2,\n  \"enabled\": true,\n  \"nothing\": null,\n"
            + "  \"tags\": [\"Settings\", \"active\", \"https://example.com/a\", \"snake_case_value\", \"Save the file now\"],\n"
            + "  \"nested\": { \"message\": \"Line one\\nLine \\\"two\\\" with \\\\ backslash\", \"ko\": \"한국어 문장입니다\" },\n"
            + "  \"k\\u00e9y\": \"Another sentence here\",\n  \"emoji\": \"Smile \\ud83d\\ude00 happily today\"\n}",
        ["json-numbers"] = "[1, 2.5, 3e10, true, false, null]",
        ["json-broken"] = "{\"a\": \"hello wor",
        ["csharp"] = "using System;\r\n// Calculates the total price of all items in the cart.\r\nnamespace Demo\r\n{\r\n"
            + "    /// <summary>\r\n    /// Represents a shopping cart with several items.\r\n    /// </summary>\r\n"
            + "    /// <param name=\"items\">The list of items to add.</param>\r\n    public class Cart\r\n    {\r\n"
            + "        private string url = \"http://example.com/a//b\"; // the endpoint address\r\n"
            + "        private string note = \"// not a comment\";\r\n"
            + "        /* The first line of a block comment\r\n         * continues on the second line\r\n         */\r\n"
            + "        public int Total() { return 1; } // returns the running total\r\n"
            + "        // int x = 5;\r\n        // TODO(sw): refactor this method later\r\n"
            + "        var verbatim = @\"C:\\temp\\ // still a string \"\" with quote // still\";\r\n"
            + "        var raw = \"\"\"\r\n            // inside a raw string\r\n            \"\"\";\r\n    }\r\n}\r\n",
        ["python"] = "#!/usr/bin/env python\n# -*- coding: utf-8 -*-\n\"\"\"Module docstring that must stay untouched even though it is English text.\"\"\"\n"
            + "import os  # noqa\n# Load the configuration file from the disk.\ndef load(path):\n"
            + "    '''Another docstring # not a comment inside triple quotes.'''\n    text = \"value # not a comment\"\n"
            + "    return text  # return the loaded text\n# type: ignore\nx = 1 # it's the number one\n",
        ["javascript"] = "// Fetch the user profile from the server.\nconst url = `http://example.com/${id}//x`; // template literal with slashes\n"
            + "/**\n * Adds two numbers together.\n * @param {number} a The first number.\n * @returns {number} The sum of both numbers.\n */\n"
            + "function add(a, b) { return a + b; } // eslint-disable-line\n// eslint-disable-next-line no-console\n// @ts-ignore\nconst re = /ab+c/; // matches one or more letters\n",
        ["sql"] = "-- Select all active users from the table\nSELECT * FROM users WHERE active = 1; -- only active ones here\n",
        ["yaml"] = "# Server settings for the production environment\nhost: example.com   # the public host name of the server\nport: 8080\n",
        ["shell"] = "#!/bin/bash\n# Install the dependencies first before building\napt-get update && apt-get install -y curl  # quiet install of curl\n",
        ["table"] = "Name\tStatus\tPrice\tNote\r\nWidget A\tIn stock\t$12.50\tShips within two days\r\n"
            + "Widget B\tBack ordered\t1,200\t\"Multi-line\nnote with \"\"quotes\"\"\"\r\nID-1234\tactive\t2025-01-05\thttps://example.com\r\n",
        ["table-ragged"] = "a\tb\tc\n1\t2\n\n\tx\t\n",
        ["korean-table"] = "이름\t상태\t메모\n위젯\t재고 있음\t이틀 안에 배송됩니다\n",
        ["mermaid"] = "flowchart LR\n  A[Start here] --> B{Is it ready?}\n  B -->|Yes| C[Ship it]\n",
        ["japanese"] = "これは日本語の文章です。翻訳は対象外です。\n",
        ["stray-marks"] = "Please visit http://example.com for details. Use # for headings // and more.\n",
        ["tabs-in-prose"] = "A line with\ta tab inside it and more words after it\nSecond line without any tab at all\n",
        ["surrogates"] = "Emoji \ud83d\ude00 sentence here with enough words to translate\n\ud83d\ude00\n",
        ["unbalanced"] = "\"quote never closed and some words follow here\n/* block never closed with words inside\n`tick never closed",
        ["cr-only"] = "First long line of text that keeps going and going until the end\rSecond long line of text that keeps going and going until the end\r",
    };

    internal static string? Load(string name)
    {
        if (Inline.TryGetValue(name, out string? text)) return text;
        string path = Path.Combine(SampleDirectory, name);
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static PinTranslationDirection Direction(bool toKorean) =>
        toKorean ? PinTranslationDirection.EnglishToKorean : PinTranslationDirection.KoreanToEnglish;

    internal static IReadOnlyList<string?> Nulls(PinTranslationPlan plan) => [.. plan.Sources.Select(static _ => (string?)null)];

    /// <summary>Invariants that hold for every plan: exact round trip, null translations restore the source, literals untouched.</summary>
    internal static void AssertInvariants(string source, PinTranslationPlan plan)
    {
        Assert.Equal(source, plan.Original);
        Assert.Equal(source, plan.Assemble(Nulls(plan)));

        IReadOnlyList<string?> upper = [.. plan.Sources.Select(static text => (string?)text.ToUpperInvariant())];
        string output = plan.Assemble(upper);
        int position = 0, translated = 0;
        foreach (PlanPiece piece in plan.Pieces)
        {
            Assert.NotEqual(string.Empty, piece.Raw);
            if (piece.Translate)
            {
                Assert.False(string.IsNullOrWhiteSpace(piece.Source));
                string emitted = piece.Emit(upper[translated++]!);
                Assert.Equal(emitted, output.Substring(position, emitted.Length));
                position += emitted.Length;
            }
            else
            {
                Assert.Equal(PlanKind.Literal, piece.Kind);
                Assert.True(string.CompareOrdinal(output, position, piece.Raw, 0, piece.Raw.Length) == 0, "A literal piece changed: " + piece.Raw);
                position += piece.Raw.Length;
            }
        }
        Assert.Equal(output.Length, position);
        Assert.Equal(plan.Sources.Count, translated);
    }

    internal static PinTranslationPlan Plan(string source, PinTranslationDirection direction, PinTextFormat? format = null) =>
        PinTranslationPlanner.Create(source, format ?? PinTranslationPlanner.Classify(source, false, false), direction);

    internal static string[] Sources(string source, PinTranslationDirection direction, PinTextFormat? format = null) =>
        [.. Plan(source, direction, format).Sources];
}

public sealed class PinTranslationPlannerTests
{
    public static IEnumerable<object[]> SampleNames() =>
        PlannerSamples.Inline.Keys.Concat(PlannerSamples.SampleFiles).Select(static name => new object[] { name });

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void EveryFormatAndDirectionKeepsTheInvariants(string name)
    {
        if (PlannerSamples.Load(name) is not { } source) return;
        PinTextFormat[] formats = [PinTextFormat.Prose, PinTextFormat.Json, PinTextFormat.Code, PinTextFormat.Table, PinTranslationPlanner.Classify(source, false, false)];
        foreach (PinTextFormat format in formats)
        {
            foreach (PinTranslationDirection direction in Enum.GetValues<PinTranslationDirection>())
            {
                PlannerSamples.AssertInvariants(source, PinTranslationPlanner.Create(source, format, direction));
            }
        }
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void GridAndMarkdownFlagsChooseTheStructure(string name)
    {
        if (PlannerSamples.Load(name) is not { } source) return;
        Assert.Equal(PinTextFormat.Table, PinTranslationPlanner.Classify(source, false, true));
        Assert.Equal(PinTextFormat.Markdown, PinTranslationPlanner.Classify(source, true, false));
    }

    [Fact]
    public void NullAndEmptyInputNeverThrow()
    {
        foreach (PinTextFormat format in new[] { PinTextFormat.Prose, PinTextFormat.Json, PinTextFormat.Code, PinTextFormat.Table })
        {
            PinTranslationPlan plan = PinTranslationPlanner.Create(null!, format, PinTranslationDirection.EnglishToKorean);
            Assert.Equal(string.Empty, plan.Original);
            Assert.False(plan.HasTranslatable);
        }
        Assert.Equal(PinTextFormat.Prose, PinTranslationPlanner.Classify(null!, false, false));
    }

    [Fact]
    public void LargeInputStaysLinear()
    {
        string line = string.Concat(Enumerable.Repeat("The report explains why the new design is faster than before. ", 400));
        string source = string.Join('\n', Enumerable.Repeat(line, 40));
        PinTranslationPlan plan = PinTranslationPlanner.Create(source, PinTextFormat.Prose, PinTranslationDirection.EnglishToKorean);
        Assert.Equal(source, plan.Original);
        Assert.Equal(40, plan.Sources.Count);
    }

    [Theory]
    [InlineData("{\"a\": \"hello world there\"}", "Json")]
    [InlineData("[\"one\", \"two\"]", "Json")]
    [InlineData("flowchart LR\n  A --> B\n", "Mermaid")]
    [InlineData("%% note\nsequenceDiagram\n  Alice->>Bob: Hello\n", "Mermaid")]
    [InlineData("a\tb\nc\td\n", "Table")]
    [InlineData("int x = 1;\nif (x) { y(); }\n// comment here\n", "Code")]
    [InlineData("def f(x):\n    return x  # keep it\n", "Code")]
    [InlineData("Plain sentence with some words in it.\nAnother plain line.\n", "Prose")]
    [InlineData("Visit http://example.com for details # and more\n", "Prose")]
    [InlineData("Use // to start a comment in C and see http://x.org\n", "Prose")]
    [InlineData("\tfoo();\n\tbar();\n", "Prose")]
    [InlineData("{not json at all}", "Prose")]
    public void ClassifyDetectsTheStructure(string source, string expected)
    {
        PinTextFormat actual = PinTranslationPlanner.Classify(source, false, false);
        // A tab-indented statement pair is code or prose, never a table.
        if (source.StartsWith("\tfoo", StringComparison.Ordinal)) Assert.NotEqual(PinTextFormat.Table, actual);
        else Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public void FencedMermaidTextIsHandedToTheMarkdownPlanner()
    {
        Assert.Equal(PinTextFormat.Markdown, PinTranslationPlanner.Classify("```mermaid\nflowchart LR\n  A --> B\n```\n", false, false));
    }

    [Fact]
    public void WrappedParagraphBecomesOnePieceWithJoinedContent()
    {
        const string source = "This is the first line of a wrapped paragraph that goes on and on\r\n"
            + "and continues here with another line that is nearly as long as it\r\n"
            + "can be before it finally ends here.\r\n";
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        PlanPiece piece = Assert.Single(plan.Pieces, static piece => piece.Translate);
        Assert.Equal(PlanKind.Prose, piece.Kind);
        Assert.Contains("\r\n", piece.Raw, StringComparison.Ordinal);
        Assert.Equal("This is the first line of a wrapped paragraph that goes on and on and continues here with another line that is nearly as long as it can be before it finally ends here.", piece.Content);
        Assert.Equal("번역입니다\r\n", plan.Assemble(["번역입니다"]));
        PlannerSamples.AssertInvariants(source, plan);
    }

    [Fact]
    public void HyphenatedWordIsRejoinedWithoutTheHyphen()
    {
        const string source = "The system administrator changed the config-\n"
            + "uration of the network after the COVID-\n"
            + "19 outage happened last week in the office\n";
        string content = Assert.Single(PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean));
        Assert.Contains("configuration of", content, StringComparison.Ordinal);
        Assert.Contains("COVID-19 outage", content, StringComparison.Ordinal);
        Assert.DoesNotContain("config-", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterLineIsBridgedWhenTheParagraphContinues()
    {
        string source = PlannerSamples.Inline["wrapped-pdf"];
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        string[] sources = [.. plan.Sources];
        Assert.Equal(2, sources.Length);
        Assert.DoesNotContain("Page 2", sources[0], StringComparison.Ordinal);
        Assert.StartsWith("The quick brown fox", sources[0], StringComparison.Ordinal);
        Assert.EndsWith("with its friends.", sources[0], StringComparison.Ordinal);
        Assert.Equal("A short note.", sources[1]);
        PlannerSamples.AssertInvariants(source, plan);
    }

    [Fact]
    public void FooterLineEndsTheParagraphWhenTheNextTextStartsAnUppercaseSentence()
    {
        const string source = "The first page ends with a line that is nearly as long as the others\n"
            + "and a second line that is also nearly as long as the first one is\n"
            + "Page 3 of 9\n"
            + "The second page begins a completely new section of this document\n"
            + "and keeps going with one more line that is nearly as long as that\n";
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        string[] sources = [.. plan.Sources];
        Assert.Equal(2, sources.Length);
        Assert.All(sources, static text => Assert.DoesNotContain("Page 3", text, StringComparison.Ordinal));
        string output = plan.Assemble([.. sources.Select(static _ => (string?)"X")]);
        Assert.Equal("X\nPage 3 of 9\nX\n", output);
    }

    [Fact]
    public void ListAndShortLinesAreTranslatedLineByLineWithMarkersKept()
    {
        string source = PlannerSamples.Inline["list"];
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        Assert.Equal(["Shopping list", "Open the window", "Close the door", "First step here", "Second step here", "Indented option"], plan.Sources);
        string output = plan.Assemble([.. plan.Sources.Select(static _ => (string?)"X")]);
        Assert.Equal("X\n- X\n- X\n1. X\n2) X\n  a) X\n", output);
    }

    [Fact]
    public void PoemLinesAreNotJoined()
    {
        PinTranslationPlan plan = PlannerSamples.Plan(PlannerSamples.Inline["poem"], PinTranslationDirection.EnglishToKorean);
        Assert.Equal(["Roses are red", "Violets are blue", "Sugar is sweet", "And so are you"], plan.Sources);
    }

    [Fact]
    public void ParagraphsAreSeparatedByBlankLines()
    {
        const string source = "First paragraph line one is rather long and keeps going on\n"
            + "and line two of the first paragraph is just as long and goes on\n\n"
            + "Second paragraph line one is also rather long and keeps going\n"
            + "and line two of the second paragraph is as long as the other ok\n";
        string[] sources = PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean);
        Assert.Equal(2, sources.Length);
        Assert.StartsWith("First paragraph", sources[0], StringComparison.Ordinal);
        Assert.StartsWith("Second paragraph", sources[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ListItemContinuationLinesAreJoinedIntoTheItem()
    {
        const string source = "Practices:\n1. Parse and validate the complete document before touching any\nlive state.\n2. Publish the new snapshot with a single atomic reference swap.\n"
            + "3. Emit a structured log entry and a metric for every accepted or\nrejected reload.\n";
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        Assert.Equal(
        [
            "Practices:",
            "Parse and validate the complete document before touching any live state.",
            "Publish the new snapshot with a single atomic reference swap.",
            "Emit a structured log entry and a metric for every accepted or rejected reload.",
        ], plan.Sources);
        Assert.Equal("X\n1. X\n2. X\n3. X\n", plan.Assemble([.. plan.Sources.Select(static _ => (string?)"X")]));
    }

    [Fact]
    public void LinesThatEachStartWithACapitalAreAListNotAWrappedParagraph()
    {
        const string source = "Quiet study rooms that can be reserved for up to two hours\nFree Wi-Fi and 24 public computers\nA weekly homework help session for middle school students\nPrinting and scanning services for a small fee\n";
        Assert.Equal(4, PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean).Length);
    }

    [Fact]
    public void UnicodeHyphenFromPdfTextIsRejoined()
    {
        const string source = "The system administrator changed the config‐\nuration of the network after the outage happened\nlast week in the office and nobody noticed it\n";
        string content = Assert.Single(PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean));
        Assert.Contains("configuration of", content, StringComparison.Ordinal);
    }

    [Fact]
    public void KoreanRunInsideAQuotationDoesNotTakeTheClosingQuoteWithIt()
    {
        const string source = "QA 팀은 \"Looks good to me, but please double-check the error messages on Windows 10.\"라는 의견을 남겼습니다. 해당 문구는 번역 담당자에게 확인을 요청하였습니다.\n";
        string run = Assert.Single(PlannerSamples.Sources(source, PinTranslationDirection.KoreanToEnglish));
        Assert.StartsWith("라는 의견을", run, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfSampleFileIsUnwrappedBeforeTranslation()
    {
        if (PlannerSamples.Load("T1-pdf-copied-lines.txt") is not { } source) return;
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        string all = string.Join("\n", plan.Sources);
        foreach (string word in new[] { "handling", "background", "reference", "continue", "configuration", "automatically", "practices" })
        {
            Assert.Contains(word, all, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("han-", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Page 2 of 3", all, StringComparison.Ordinal);
        Assert.All(plan.Sources, static text => Assert.DoesNotContain('\n', text));
        PlannerSamples.AssertInvariants(source, plan);
    }

    [Fact]
    public void MixedSampleFileTranslatesTheRightLanguageRuns()
    {
        if (PlannerSamples.Load("T10-mixed-ko-en.txt") is not { } source) return;
        string[] english = PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean);
        Assert.Contains("We should freeze the build by Friday so that the store review can start on Monday.", english);
        Assert.DoesNotContain(english, static text => text.Any(static c => c is >= '가' and <= '힣'));
        string[] korean = PlannerSamples.Sources(source, PinTranslationDirection.KoreanToEnglish);
        Assert.DoesNotContain(korean, static text => text.StartsWith("We should", StringComparison.Ordinal));
        Assert.True(korean.Length >= 4);
    }

    [Fact]
    public void RandomTextNeverBreaksTheInvariants()
    {
        string[] atoms = ["\"", "'", "/", "//", "/*", "*/", "#", "--", "\n", "\r\n", "\r", "\t", "{", "}", "[", "]", ":", ",", "\\", " ", "abc", "Hello", "world", "the", "한글", "문장",
            "is", "- ", "1. ", "x = 1;", "`", "\"\"\"", "'''", "@", "<b>", "///", "TODO(x): ", "page 2", "https://a.b/c", "\\u00e9", "-\n", "end.", "and"];
        var random = new Random(20261027);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            string source = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => atoms[random.Next(atoms.Length)]));
            foreach (PinTextFormat format in new[] { PinTextFormat.Prose, PinTextFormat.Json, PinTextFormat.Code, PinTextFormat.Table })
            {
                foreach (PinTranslationDirection direction in Enum.GetValues<PinTranslationDirection>())
                {
                    PlannerSamples.AssertInvariants(source, PinTranslationPlanner.Create(source, format, direction));
                }
            }
            _ = PinTextLanguageDecision.Decide(source, true, out _);
        }
    }

    [Fact]
    public void KoreanParagraphIsJoinedForKoreanToEnglish()
    {
        const string source = "회의에서 논의한 내용은 다음 분기 예산과 인력 계획을 모두 포함하고 있으며\n"
            + "특히 신규 채용 일정과 외부 협력사 계약 조건에 대한 검토가 필요하다는 의견이 많았고\n"
            + "최종 결정은 다음 주 월요일 임원 회의에서 하기로 했습니다";
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.KoreanToEnglish);
        string content = Assert.Single(plan.Sources);
        Assert.DoesNotContain('\n', content);
        Assert.Contains("있으며 특히", content, StringComparison.Ordinal);
        Assert.Empty(PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean));
    }

    [Fact]
    public void MixedLineTranslatesOnlyEnglishPhrasesInsideKoreanText()
    {
        string source = PlannerSamples.Inline["mixed-ko-en"];
        string[] english = PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean);
        Assert.Equal(["We need to finalize the budget by Friday", "Please check the 회의 일정 tomorrow morning"], english);
    }

    [Fact]
    public void SingleEnglishWordsAndAcronymsInsideKoreanTextStayUntouched()
    {
        Assert.Empty(PlannerSamples.Sources("서버 API 재시작 필요\n오늘은 Docker 와 Kubernetes 설정을 확인했다\n", PinTranslationDirection.EnglishToKorean));
    }

    [Fact]
    public void MixedLineTranslatesOnlyKoreanRunsInsideEnglishText()
    {
        const string source = "Please check the 회의 일정 tomorrow morning at the office\nOnly one 단어 here and then plain english words follow\n";
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.KoreanToEnglish);
        Assert.Equal(["회의 일정"], plan.Sources);
        Assert.Equal("Please check the MEETING tomorrow morning at the office\nOnly one 단어 here and then plain english words follow\n",
            plan.Assemble(["MEETING"]));
        PlannerSamples.AssertInvariants(source, plan);
    }

    [Fact]
    public void KoreanLineWithEnglishAcronymIsTranslatedWholeForKoreanToEnglish()
    {
        string[] sources = PlannerSamples.Sources("API 서버를 재시작하세요\n", PinTranslationDirection.KoreanToEnglish);
        Assert.Equal(["API 서버를 재시작하세요"], sources);
    }

    [Fact]
    public void CodeLikeLinesUrlsPathsEmailsAndNumbersStayLiteral()
    {
        PinTranslationPlan plan = PlannerSamples.Plan(PlannerSamples.Inline["urls-paths"], PinTranslationDirection.EnglishToKorean, PinTextFormat.Prose);
        Assert.False(plan.HasTranslatable);
    }

    [Fact]
    public void ProtectedTokensInsideASentenceStayInTheTranslatedLine()
    {
        const string source = "Please open https://example.com/a and run `npm install` for {name} in C:\\Temp\\x now\n";
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        Assert.Equal([source.TrimEnd('\n')], plan.Sources);
    }

    [Fact]
    public void NullTranslationKeepsOnlyThatPieceOriginal()
    {
        string source = PlannerSamples.Inline["list"];
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        string output = plan.Assemble(["A", null, "", "  ", "B", "C"]);
        Assert.Equal("A\n- Open the window\n- Close the door\n1. First step here\n2) B\n  a) C\n", output);
    }
}
