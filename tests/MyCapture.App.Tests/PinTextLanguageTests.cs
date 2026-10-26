using System.IO;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTextLanguageTests
{
    [Theory]
    [InlineData("", "None")]
    [InlineData("   \r\n", "None")]
    [InlineData("12345 67.89 -- ++", "None")]
    [InlineData("https://example.com/path user@example.com C:\\Temp\\file.txt", "None")]
    [InlineData("The quick brown fox jumps over the lazy dog.", "English")]
    [InlineData("이것은 한국어로 작성된 문장입니다.", "Korean")]
    [InlineData("오늘 회의는 API 서버 점검과 배포 일정 그리고 담당자 지정에 관한 내용입니다 https://example.com", "Korean")]
    [InlineData("The meeting about 서버 점검 is on 금요일 morning", "Mixed")]
    [InlineData("これは日本語の文章です。", "Japanese")]
    [InlineData("日本語のtext and more", "Japanese")]
    [InlineData("这是一个中文句子。", "Chinese")]
    [InlineData("Это русское предложение.", "Other")]
    [InlineData("هذه جملة عربية", "Other")]
    public void DetectFindsTheDominantScript(string text, string expected) =>
        Assert.Equal(expected, PinTextLanguageDecision.Detect(text).ToString());

    [Theory]
    [InlineData("The report explains why the new design is faster.", true, "Translatable", true)]
    [InlineData("이 보고서는 새 설계가 더 빠른 이유를 설명합니다.", true, "AlreadyTargetLanguage", true)]
    [InlineData("これは日本語の文章です。", true, "UnsupportedLanguage", true)]
    [InlineData("这是一个中文句子。", true, "UnsupportedLanguage", true)]
    [InlineData("Это русское предложение.", true, "UnsupportedLanguage", true)]
    [InlineData("12345", true, "NothingToTranslate", true)]
    [InlineData("", true, "NothingToTranslate", true)]
    [InlineData("API SDK URL", true, "NothingToTranslate", true)]
    [InlineData("https://example.com/a/b", true, "NothingToTranslate", true)]
    [InlineData("int count = 5;", true, "NothingToTranslate", true)]
    [InlineData("이 보고서는 새 설계가 더 빠른 이유를 설명합니다.", false, "Translatable", false)]
    [InlineData("The report explains why the new design is faster.", false, "AlreadyTargetLanguage", false)]
    [InlineData("これは日本語の文章です。", false, "UnsupportedLanguage", false)]
    [InlineData("你好，世界", false, "UnsupportedLanguage", false)]
    [InlineData("12345", false, "NothingToTranslate", false)]
    public void DecideChoosesTheAvailabilityAndDirection(string text, bool targetIsKorean, string expected, bool englishToKorean)
    {
        PinTranslationAvailability actual = PinTextLanguageDecision.Decide(text, targetIsKorean, out PinTranslationDirection direction);
        Assert.Equal(expected, actual.ToString());
        Assert.Equal(englishToKorean ? PinTranslationDirection.EnglishToKorean : PinTranslationDirection.KoreanToEnglish, direction);
    }

    [Fact]
    public void MixedTextIsTranslatableOnlyWhenAPlanFindsAPhrase()
    {
        // Korean app: an English phrase of three or more words is translatable; single English words are not.
        Assert.Equal(PinTranslationAvailability.Translatable,
            PinTextLanguageDecision.Decide("오늘 회의에서 We need to finalize the budget by Friday 라고 했다", true, out _));
        Assert.Equal(PinTranslationAvailability.AlreadyTargetLanguage,
            PinTextLanguageDecision.Decide("서버 API 와 Docker 설정을 확인했다 그리고 Kubernetes 도 확인했다", true, out _));
        // English app: a Korean run of two or more words is translatable.
        Assert.Equal(PinTranslationAvailability.Translatable,
            PinTextLanguageDecision.Decide("Please check the 회의 일정 tomorrow morning at the office", false, out _));
        Assert.Equal(PinTranslationAvailability.AlreadyTargetLanguage,
            PinTextLanguageDecision.Decide("The meeting is on 금요일 morning at the office today", false, out _));
    }

    [Fact]
    public void StructuredTextUsesItsHumanTextOnly()
    {
        // JSON whose values are all numbers or data has nothing to translate.
        Assert.Equal(PinTranslationAvailability.NothingToTranslate,
            PinTextLanguageDecision.Decide("{\"id\": 5, \"ratio\": 1.5, \"ok\": true, \"url\": \"https://example.com\"}", true, out _));
        Assert.Equal(PinTranslationAvailability.Translatable,
            PinTextLanguageDecision.Decide("{\"message\": \"Your changes were saved successfully\"}", true, out _));
        Assert.Equal(PinTranslationAvailability.AlreadyTargetLanguage,
            PinTextLanguageDecision.Decide("{\"message\": \"변경 사항이 저장되었습니다\", \"id\": 7}", true, out _));
        Assert.Equal(PinTranslationAvailability.UnsupportedLanguage,
            PinTextLanguageDecision.Decide("{\"message\": \"変更が保存されました\"}", true, out _));
        // English comments inside code are translatable; code without comments is not.
        Assert.Equal(PinTranslationAvailability.Translatable,
            PinTextLanguageDecision.Decide("int Total() { return 1; } // returns the running total\nint x = 2;\n", true, out _));
        Assert.Equal(PinTranslationAvailability.NothingToTranslate,
            PinTextLanguageDecision.Decide("int Total() { return 1; }\nint x = 2;\nvar y = Total();\n", true, out _));
        Assert.Equal(PinTranslationAvailability.Translatable,
            PinTextLanguageDecision.Decide("Name\tStatus\nWidget\tIn stock\n", true, out _));
        Assert.Equal(PinTranslationAvailability.NothingToTranslate,
            PinTextLanguageDecision.Decide("1\t2\t3\n4\t5\t6\n", true, out _));
    }

    [Fact]
    public void SampleFilesAreDecidedWithoutThrowing()
    {
        foreach (string name in PlannerSamples.SampleFiles)
        {
            if (PlannerSamples.Load(name) is not { } text) continue;
            foreach (bool targetIsKorean in new[] { true, false })
            {
                _ = PinTextLanguageDecision.Decide(text, targetIsKorean, out _);
            }
        }
        if (PlannerSamples.Load("T13-japanese.txt") is { } japanese)
        {
            Assert.Equal(PinTranslationAvailability.UnsupportedLanguage, PinTextLanguageDecision.Decide(japanese, true, out _));
            Assert.Equal(PinTranslationAvailability.UnsupportedLanguage, PinTextLanguageDecision.Decide(japanese, false, out _));
        }
    }
}
