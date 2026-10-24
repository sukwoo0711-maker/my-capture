using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TranslationSegmentationTests
{
    private static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1;

    [Fact]
    public void ShortBlockStaysWholeEvenWithSeveralSentences()
    {
        const string Text = "Bit 0 EN: Counter enable. 0: Counter disabled. 1: Counter enabled.";

        Assert.Equal([Text], TranslationSegmenter.Split(Text, Words, maxTokens: 64, hardLimit: 256));
    }

    [Fact]
    public void LongParagraphSplitsAtSentenceEndsAndPacksUpToTheBudget()
    {
        string sentence = "The counter stops at the next update event and the CEN bit is cleared.";
        string text = string.Join(" ", Enumerable.Repeat(sentence, 6));

        IReadOnlyList<string> segments = TranslationSegmenter.Split(text, Words, maxTokens: 30, hardLimit: 256);

        // Fourteen words per sentence: two sentences fit a 30-token budget, three do not.
        Assert.Equal(3, segments.Count);
        Assert.All(segments, segment => Assert.Equal(sentence + " " + sentence, segment));
    }

    [Theory]
    [InlineData("Use a pull-up, e.g. 10 kΩ. Then enable it.", 2)]
    [InlineData("Designed by J. Smith in 2021. It is stable.", 2)]
    [InlineData("The supply is 1.8 V. The current is 2.1 µA.", 2)]
    [InlineData("Is it set? Yes! Then clear it.", 3)]
    [InlineData("see Fig. 3 for details", 1)]
    public void SentencesIgnoreAbbreviationsInitialsAndDecimals(string text, int expected) =>
        Assert.Equal(expected, TranslationSegmenter.Sentences(text).Count());

    [Fact]
    public void OverlongSentenceSplitsAtWordsWithinTheHardLimit()
    {
        string text = string.Join(" ", Enumerable.Range(0, 40).Select(index => "word" + index));

        IReadOnlyList<string> segments = TranslationSegmenter.Split(text, Words, maxTokens: 8, hardLimit: 11);

        Assert.All(segments, segment => Assert.True(Words(segment) <= 11, segment));
        Assert.Equal(text, string.Join(" ", segments));
    }

    [Fact]
    public void BlocksUseTableCellsInPlaceOfParagraphsInsideTheTable()
    {
        OcrResult result = null!;
        Ocr410Fixture.RunSta(() => result = TranslationFixture.RegisterTable());

        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
        IReadOnlyList<TranslationBlock> blocks = TranslationBlocks.Build(extraction);

        Assert.True(extraction.Table.HasStructure);
        Assert.Equal("Table 31. CTRL1 register bit fields", blocks[0].Text);
        Assert.Contains(blocks, block => block.Text == "Auto-reload preload enable.");
        Assert.Contains(blocks, block => block.Text == "Counter enable. 0: Counter disabled. 1: Counter enabled.");
        // Bit numbers, register names and access codes keep their original text.
        Assert.DoesNotContain(blocks, block => block.Text is "ARPE" or "CEN" or "rw" or "7" or "0");
        Assert.Equal(Enumerable.Range(1, blocks.Count), blocks.Select(block => block.Number));
        Assert.All(blocks, block => Assert.True(OcrGeometry.IsValid(block.Region)));
    }

    [Theory]
    [InlineData("캡처 결과 창의 OCR 보기", false)]
    [InlineData("内部高速时钟 IRC8M 的频率精度", false)]
    [InlineData("Reset value 0x0000", true)]
    [InlineData("0x0000 rw 15:10", false)]
    public void OnlyEnglishTextIsTranslated(string text, bool expected) =>
        Assert.Equal(expected, TranslationBlocks.IsEnglishToTranslate(text));
}
