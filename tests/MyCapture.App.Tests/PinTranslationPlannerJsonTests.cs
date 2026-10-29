using System.IO;
using System.Text.Json;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationPlannerJsonTests
{
    private static string Json => PlannerSamples.Inline["json"];

    [Fact]
    public void ClassifyDetectsJsonAndNumbersOnlyJsonHasNothingToTranslate()
    {
        Assert.Equal(PinTextFormat.Json, PinTranslationPlanner.Classify(Json, false, false));
        Assert.Equal(PinTextFormat.Json, PinTranslationPlanner.Classify(PlannerSamples.Inline["json-numbers"], false, false));
        Assert.NotEqual(PinTextFormat.Json, PinTranslationPlanner.Classify(PlannerSamples.Inline["json-broken"], false, false));
        Assert.False(PlannerSamples.Plan(PlannerSamples.Inline["json-numbers"], PinTranslationDirection.EnglishToKorean).HasTranslatable);
    }

    [Fact]
    public void OnlyProseValuesAreTranslatedAndKeysNever()
    {
        PinTranslationPlan plan = PlannerSamples.Plan(Json, PinTranslationDirection.EnglishToKorean);
        Assert.All(plan.Pieces.Where(static piece => piece.Translate), static piece => Assert.Equal(PlanKind.JsonString, piece.Kind));
        Assert.Equal(
        [
            "Hello world, this is a test",
            "Settings",
            "Save the file now",
            "Line one\nLine \"two\" with \\ backslash",
            "Another sentence here",
            "Smile \U0001F600 happily today",
        ], plan.Sources);
    }

    [Fact]
    public void KoreanDirectionTranslatesOnlyKoreanValues()
    {
        Assert.Equal(["한국어 문장입니다"], PlannerSamples.Sources(Json, PinTranslationDirection.KoreanToEnglish));
    }

    [Fact]
    public void RawIsTheEscapedSourceBetweenTheQuotesAndContentIsDecoded()
    {
        PinTranslationPlan plan = PlannerSamples.Plan(Json, PinTranslationDirection.EnglishToKorean);
        PlanPiece message = Assert.Single(plan.Pieces, static piece => piece.Translate && piece.Raw.StartsWith("Line one", StringComparison.Ordinal));
        Assert.Equal("Line one\\nLine \\\"two\\\" with \\\\ backslash", message.Raw);
        Assert.Equal("Line one\nLine \"two\" with \\ backslash", message.Content);
        PlanPiece plain = Assert.Single(plan.Pieces, static piece => piece.Translate && piece.Raw == "Settings");
        Assert.Null(plain.Content);
        // The quotes around a value stay literal.
        int index = plan.Pieces.ToList().IndexOf(plain);
        Assert.EndsWith("\"", plan.Pieces[index - 1].Raw, StringComparison.Ordinal);
        Assert.StartsWith("\"", plan.Pieces[index + 1].Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void AssembledJsonParsesAndKeepsKeysNumbersAndStructure()
    {
        PinTranslationPlan plan = PlannerSamples.Plan(Json, PinTranslationDirection.EnglishToKorean);
        string[] translated = ["한글 \"번역\"", "설정", "저장\\완료", "첫 줄\n둘째 줄", "다른 문장", "웃음"];
        string output = plan.Assemble(translated);

        using JsonDocument before = JsonDocument.Parse(Json);
        using JsonDocument after = JsonDocument.Parse(output);
        AssertSameShape(before.RootElement, after.RootElement, translatedValues: new HashSet<string>(translated.Select(static text => text.ReplaceLineEndings(" ")), StringComparer.Ordinal));
        Assert.Contains("1.50e+2", output, StringComparison.Ordinal);
        Assert.Contains("\"k\\u00e9y\"", output, StringComparison.Ordinal);
        Assert.Contains("  \"count\": 12,\n", output, StringComparison.Ordinal);
        Assert.Contains("한글", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\ud55c", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("한글 \"번역\"", after.RootElement.GetProperty("title").GetString());
        Assert.Equal("첫 줄 둘째 줄", after.RootElement.GetProperty("nested").GetProperty("message").GetString());
    }

    [Fact]
    public void EscapesInTranslationsAreWrittenBackAsJsonEscapes()
    {
        PinTranslationPlan plan = PlannerSamples.Plan("{\"a\": \"Hello there my friend\"}", PinTranslationDirection.EnglishToKorean);
        string output = plan.Assemble(["He said \"hi\" \\ bye\nnow"]);
        Assert.Equal("{\"a\": \"He said \\\"hi\\\" \\\\ bye now\"}", output);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("He said \"hi\" \\ bye now", document.RootElement.GetProperty("a").GetString());
    }

    [Fact]
    public void ValuesThatAreDataAreNotTranslated()
    {
        const string source = "{\"url\": \"https://example.com/some/long/path\", \"mail\": \"someone@example.com\", \"path\": \"/usr/local/bin/tool\","
            + " \"win\": \"C:\\\\Program Files\\\\App\", \"id\": \"user_profile_id\", \"camel\": \"userProfileId\", \"kebab\": \"user-profile-id\","
            + " \"ver\": \"1.2.3-beta\", \"date\": \"2025-01-05T10:20:30Z\", \"time\": \"10:20\", \"ph\": \"{0}\", \"ph2\": \"{name}\", \"ph3\": \"%s\","
            + " \"enum\": \"active\", \"code\": \"x = foo(1);\", \"num\": \"12,345\", \"json\": \"{\\\"a\\\":1}\", \"empty\": \"\", \"cur\": \"$12.50\"}";
        Assert.Equal(PinTextFormat.Json, PinTranslationPlanner.Classify(source, false, false));
        Assert.Empty(PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean));
    }

    [Fact]
    public void ValuesWithEmbeddedPlaceholdersAreTranslatable()
    {
        const string source = "{\"m\": \"Hello {name}, you have {count} new messages\", \"n\": \"Deleted %s items from the list\"}";
        Assert.Equal(["Hello {name}, you have {count} new messages", "Deleted %s items from the list"], PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean));
    }

    [Fact]
    public void ArrayOfStringsAndNestedArraysAreScanned()
    {
        const string source = "[[\"First sentence goes here\", 1], [\"Second sentence goes here\"], {\"k\": [\"Third sentence goes here\"]}]";
        Assert.Equal(["First sentence goes here", "Second sentence goes here", "Third sentence goes here"], PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean));
    }

    [Fact]
    public void KeyThatLooksLikeAValueIsStillAKey()
    {
        const string source = "{\"Hello there my friend\": \"Hello there my friend\", \"b\": {\"Another key phrase here\": 1}}";
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        PlanPiece piece = Assert.Single(plan.Pieces, static piece => piece.Translate);
        int index = plan.Pieces.ToList().IndexOf(piece);
        Assert.Contains(":", plan.Pieces[index - 1].Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenJsonHasNoTranslatablePiece()
    {
        foreach (string source in new[] { "{\"a\": \"hello wor", "{\"a\": \"bad \\q escape here\"}", "{\"a\": \"truncated \\u12", "]\"stray\"[" })
        {
            PinTranslationPlan plan = PinTranslationPlanner.Create(source, PinTextFormat.Json, PinTranslationDirection.EnglishToKorean);
            Assert.False(plan.HasTranslatable);
            Assert.Equal(source, plan.Original);
        }
    }

    [Fact]
    public void SampleFileKeepsItsStructure()
    {
        if (PlannerSamples.Load("T4-json-nested.json") is not { } source) return;
        Assert.Equal(PinTextFormat.Json, PinTranslationPlanner.Classify(source, false, false));
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        string output = plan.Assemble([.. plan.Sources.Select(static text => (string?)("번역 \"" + text.Length + "\""))]);
        using JsonDocument before = JsonDocument.Parse(source);
        using JsonDocument after = JsonDocument.Parse(output);
        AssertSameShape(before.RootElement, after.RootElement, translatedValues: null);
    }

    /// <summary>Same property names and order, value kinds, number spellings and array lengths; strings may differ when translated.</summary>
    private static void AssertSameShape(JsonElement expected, JsonElement actual, HashSet<string>? translatedValues)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.Equal(expected.EnumerateObject().Select(static property => property.Name), actual.EnumerateObject().Select(static property => property.Name));
                foreach (JsonProperty property in expected.EnumerateObject())
                {
                    AssertSameShape(property.Value, actual.GetProperty(property.Name), translatedValues);
                }
                break;
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (int index = 0; index < expected.GetArrayLength(); index++)
                {
                    AssertSameShape(expected[index], actual[index], translatedValues);
                }
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
            case JsonValueKind.String:
                if (translatedValues is not null && !string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
                    Assert.Contains(actual.GetString()!, translatedValues);
                break;
        }
    }
}
