using System.Globalization;
using System.IO;
using System.Text.Json;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TranslationHardLimitTests
{
    internal static readonly Lazy<SentencePieceModel> Model = new(() => SentencePieceModel.Load(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "opus-mt-en-ko-source.spm")));

    internal static int Count(string text) => Model.Value.EncodeToIds(text).Length + 1;
    internal static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));
    internal static string Squash(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c))]);

    public static IEnumerable<object[]> Inputs()
    {
        yield return ["ascii", new string('a', 4096)];
        yield return ["hangul", Repeat("가나다라마바사", 512)];
        yield return ["chinese", Repeat("中华人民共和国汉字", 256)];
        yield return ["japanese", Repeat("日本語の文字を翻訳する", 256)];
        yield return ["url", "https://example.com/" + Repeat("long-path/", 512) + "?query=" + Repeat("value123", 128)];
        yield return ["digits", Repeat("1234567890", 512)];
        yield return ["emoji", Repeat("👩🏽‍💻", 512)];
        yield return ["combining", Repeat("a\u0301\u0327", 1024)];
        yield return ["one-element", "a" + new string('\u0301', 2000)];
        yield return ["astral", Repeat("\U00010400\U00010428", 1024)];
        foreach (string separator in new[] { "\t", "\n", "\u00A0", "\u3000" })
            yield return ["whitespace-" + ((int)separator[0]).ToString("X4"), Repeat("word" + separator, 1024).Trim()];
        yield return ["mixed", "Start with ordinary words " + new string('a', 4096) + " and finish here."];
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void SplitsWithoutLosingCharactersOrCrossingUnicodeBoundaries(string family, string input)
    {
        IReadOnlyList<string> segments = TranslationSegmenter.Split(input, Count, 64, 256);
        Assert.NotEmpty(segments);
        foreach (string segment in segments)
        {
            Assert.False(string.IsNullOrWhiteSpace(segment), family);
            Assert.True(Count(segment) <= 256, $"{family}: {Count(segment)} tokens");
            Assert.False(char.IsLowSurrogate(segment[0]), family);
            Assert.False(char.IsHighSurrogate(segment[^1]), family);
        }
        Assert.Equal(Squash(input.Trim()), Squash(string.Concat(segments)));
        if (!input.Any(char.IsWhiteSpace))
        {
            Assert.Equal(input, string.Concat(segments));
            int[] starts = StringInfo.ParseCombiningCharacters(input);
            bool elementsFit = starts.Select((start, i) => input[start..(i + 1 < starts.Length ? starts[i + 1] : input.Length)])
                .All(element => Count(element) <= 256);
            if (elementsFit)
            {
                int offset = 0;
                foreach (string segment in segments.SkipLast(1))
                {
                    offset += segment.Length;
                    Assert.Contains(offset, starts);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void ConsumerEncodesEveryPieceAndEos(string family, string input)
    {
        foreach (string segment in TranslationSegmenter.Split(input, Count, 64, 256))
        {
            Assert.True(Count(segment) <= MarianTranslator.MaxSourceTokens, family);
            Assert.Equal([.. Model.Value.EncodeToIds(segment), Model.Value.EndOfSentenceId], Encode(segment));
        }
    }

    [Fact]
    public void ConsumerRejectsAnOversizedSource() => Assert.Throws<ArgumentException>(() => Encode(new string('a', 4096)));

    private static int[] Encode(string text) => MarianTranslator.EncodeSource(Model.Value, text);

    [Fact]
    public void NonmonotoneCountsOnlyReturnCheckedPiecesAndNeverInsertSpaces()
    {
        static int CountNonmonotone(string text) => text.Length is 2 or 5 ? 20 : text.Length + 1;
        const string Source = "abcdefghijklmnop";
        IReadOnlyList<string> segments = TranslationSegmenter.Split(Source, CountNonmonotone, 7, 7);
        Assert.Equal(Source, string.Concat(segments));
        Assert.All(segments, segment => Assert.InRange(CountNonmonotone(segment), 1, 7));
    }

    [Fact]
    public void AnIndivisibleCodePointCanExceedTheBudgetWithoutSplittingItsSurrogates()
    {
        const string Scalar = "\U00010400";
        IReadOnlyList<string> segments = TranslationSegmenter.Split(Repeat(Scalar, 3), _ => 2, 1, 1);
        Assert.Equal([Scalar, Scalar, Scalar], segments);
    }

    [Fact]
    public void OrdinaryParagraphKeepsItsPacking()
    {
        IReadOnlyList<string> segments = TranslationSegmenter.Split(Repeat("Alpha beta gamma delta. ", 256), Count, 64, 256);
        Assert.Equal(22, segments.Count);
        Assert.Equal(61, segments.Max(Count));
    }

    [Theory]
    [InlineData(0, 256)]
    [InlineData(-1, 256)]
    [InlineData(257, 256)]
    [InlineData(1, 0)]
    public void RejectsInvalidBudgetsEvenForEmptyText(int maxTokens, int hardLimit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TranslationSegmenter.Split("", Count, maxTokens, hardLimit));

    [Fact]
    public void ProbeInputsFitTheConsumer()
    {
        foreach (TranslationLiveModelTests.ProbeTranslation probe in TranslationLiveModelTests.ProbeTranslations())
        {
            Assert.InRange(Count(probe.Text), 1, MarianTranslator.MaxSourceTokens);
            Assert.Equal([.. Model.Value.EncodeToIds(probe.Text), Model.Value.EndOfSentenceId], Encode(probe.Text));
        }
    }

    internal static IEnumerable<string> FixtureTexts()
    {
        foreach (string file in new[] { "opus-mt-en-ko-source-spm-cases.json", "opus-mt-en-ko-probe-translations.json" })
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
            foreach (JsonElement item in document.RootElement.EnumerateArray()) yield return item.GetProperty("text").GetString()!;
        }
    }
}
