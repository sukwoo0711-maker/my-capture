using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class KoreanWordWrapTests
{
    private const char Joiner = KoreanWordWrap.WordJoiner;

    [Fact]
    public void KoreanWords_KeepTheirLettersTogether_AndSpacesStayBreakable()
    {
        string wrapped = KoreanWordWrap.KeepWords("필요한 항목은 보관하십시오.");

        Assert.Equal($"필{Joiner}요{Joiner}한 항{Joiner}목{Joiner}은 보{Joiner}관{Joiner}하{Joiner}십{Joiner}시{Joiner}오{Joiner}.", wrapped);
        Assert.Equal("필요한 항목은 보관하십시오.", wrapped.Replace(Joiner.ToString(), string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void CountersStayAttachedToTheirNumbers()
    {
        // Digits never break between themselves; only the digit-to-Hangul edge needs a joiner.
        Assert.Equal($"전{Joiner}체 24{Joiner}개 중 10{Joiner}개", KoreanWordWrap.KeepWords("전체 24개 중 10개"));
    }

    [Theory]
    [InlineData("Images are cleaned up after their retention period.")]
    [InlineData("24 of 24 items")]
    [InlineData("")]
    public void TextWithoutHangul_IsUnchanged(string text) => Assert.Equal(text, KoreanWordWrap.KeepWords(text));
}
