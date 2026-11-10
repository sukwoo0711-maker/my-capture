using System.Text;
using System.Text.RegularExpressions;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class TranslationPreprocessingOracleTests(ITestOutputHelper output)
{
    private const int Samples = 20_000;
    private static readonly string[] Pieces =
    [
        "Dr. ", "Mr. ", "Fig. ", "e.g. ", "i.e. ", "J. ", "A", "z", "a", "Z", "1.8 V", "1", "0", "9", ".", "!", "?",
        "\"", "'", "[", "]", "(", ")", "_", "()", "[1:0]", "0x", "0b", "--", "Ctrl+", "v1.2", "http://", "C:\\", "%", "`",
        "mV", "MHz", "value", "aBC", "ABC", "FooBar", "1abc.def()", "abc.def()", "가나다", "한글", " ", "\t", "\n", "\r\n",
        "\u0085", "\u00A0", "\u2028", "\u3000", "\u200B", "\uFEFF",
    ];

    private static IEnumerable<string> RandomTexts(int seed, int maxLength)
    {
        var random = new Random(seed);
        while (true)
        {
            int length = random.Next(maxLength + 1);
            var text = new StringBuilder(length);
            while (text.Length < length)
            {
                string piece = Pieces[random.Next(Pieces.Length)];
                text.Append(piece.AsSpan(0, Math.Min(piece.Length, length - text.Length)));
            }
            yield return text.ToString();
        }
    }

    [Fact]
    public void SentencesMatchTheReference()
    {
        int skipped = 0;
        foreach (string text in TranslationHardLimitTests.FixtureTexts().Concat(RandomTexts(24401, 256).Take(Samples)))
        {
            try { Assert.Equal(TranslationReferenceSegmenter.Sentences(text), TranslationSegmenter.Sentences(text)); }
            catch (RegexMatchTimeoutException) { skipped++; }
        }
        CheckSkips("Sentences", skipped);
    }

    [Fact]
    public void ProtectedSpansMatchTheReference()
    {
        int skipped = 0;
        foreach (string text in TranslationHardLimitTests.FixtureTexts().Concat(RandomTexts(24402, 48).Take(Samples)))
        {
            try { Assert.Equal(TranslationReferenceProtectedTokens.Find(text), ProtectedTokens.Find(text)); }
            catch (RegexMatchTimeoutException) { skipped++; }
        }
        CheckSkips("Find", skipped);
    }

    [Fact]
    public void TranslatableWordsMatchTheReference()
    {
        int skipped = 0;
        foreach (string text in TranslationHardLimitTests.FixtureTexts().Concat(RandomTexts(24403, 48).Take(Samples)))
        {
            try { Assert.Equal(TranslationReferenceProtectedTokens.HasTranslatableWords(text), ProtectedTokens.HasTranslatableWords(text)); }
            catch (RegexMatchTimeoutException) { skipped++; }
        }
        CheckSkips("HasTranslatableWords", skipped);
    }

    [Theory]
    [InlineData(64, 256, false)]
    [InlineData(30, 256, false)]
    [InlineData(8, 11, false)]
    [InlineData(5, 16, false)]
    [InlineData(64, 256, true)]
    [InlineData(30, 256, true)]
    [InlineData(8, 11, true)]
    [InlineData(5, 16, true)]
    public void PackingMatchesWhenEveryWordFits(int maxTokens, int hardLimit, bool realTokenizer)
    {
        Func<string, int> count = realTokenizer ? TranslationHardLimitTests.Count : Words;
        bool Eligible(string text) => text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => count(word) <= hardLimit);
        int skipped = 0, randomChecked = 0;
        void Compare(string text)
        {
            Assert.All(text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries), word => Assert.True(count(word) <= hardLimit));
            try
            {
                Assert.Equal(TranslationReferenceSegmenter.Split(text, count, maxTokens, hardLimit),
                    TranslationSegmenter.Split(text, count, maxTokens, hardLimit));
            }
            catch (RegexMatchTimeoutException) { skipped++; }
        }
        foreach (string text in TranslationHardLimitTests.FixtureTexts().Where(Eligible)) Compare(text);
        foreach (string text in RandomTexts(24501, 64).Where(Eligible).Take(Samples))
        {
            Compare(text);
            randomChecked++;
        }
        Assert.Equal(Samples, randomChecked);
        CheckSkips($"Split/{maxTokens}/{hardLimit}/real={realTokenizer}", skipped);
    }

    private static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1;

    private void CheckSkips(string function, int skipped)
    {
        output.WriteLine($"{function}: {Samples} seeded random inputs plus both fixtures; regex timeouts={skipped}");
        Assert.InRange(skipped, 0, 2);
    }
}
