using System.IO;
using Xunit;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;

namespace MyCapture.App.Tests;

/// <summary>Selection only; no corpus generation, image decode, OCR or environment mutation.</summary>
public sealed class OcrCommercialCorpusSelectionTests
{
    [Fact]
    public void UnsetSplitPreservesThePreviousFullOrderAndSmokeExamples()
    {
        Data.Manifest manifest = Fixture();
        string split = OcrCommercialCorpusTests.ResolveSplit(null, smoke: false);
        Assert.Equal("all", split);
        Data.Sample[] full = OcrCommercialCorpusTests.SelectSamples(manifest, false, split);
        Assert.Equal(300, full.Length);
        Assert.Equal(Data.Categories.Select(category => category + "-00-0"), full.Take(5).Select(sample => sample.Id));
        Assert.Equal(Data.Categories.Select(category => category + "-09-5"), full.TakeLast(5).Select(sample => sample.Id));
        Data.Sample[] smoke = OcrCommercialCorpusTests.SelectSamples(manifest, true, "all");
        Assert.Equal(full.Take(5), smoke);
    }

    [Theory]
    [InlineData("development", 180, 30)]
    [InlineData("held-out", 120, 20)]
    public void ExplicitSplitKeepsEveryVariantAndOriginalRelativeOrder(string split, int count, int scenes)
    {
        Data.Manifest manifest = Fixture();
        Data.Sample[] all = OcrCommercialCorpusTests.SelectSamples(manifest, false, "all");
        Data.Sample[] actual = OcrCommercialCorpusTests.SelectSamples(manifest, false, split);
        Assert.Equal(count, actual.Length);
        Assert.Equal(scenes, actual.Select(sample => sample.SceneId).Distinct().Count());
        Assert.Equal(all.Where(sample => sample.Split == split), actual);
        Assert.All(actual, sample => Assert.Equal(split, sample.Split));
        Assert.All(actual.GroupBy(sample => sample.SceneId), scene => Assert.Equal(6, scene.Count()));
        Assert.Equal(300, manifest.Samples.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("heldout")]
    [InlineData("Development")]
    [InlineData("all ")]
    public void UnknownExplicitSplitFailsInsteadOfBecomingAnEmptySuccess(string value) =>
        Assert.Throws<InvalidDataException>(() => OcrCommercialCorpusTests.ResolveSplit(value, false));

    [Theory]
    [InlineData("development")]
    [InlineData("held-out")]
    public void SmokeCannotPretendToMeasureAnExplicitSplit(string value) =>
        Assert.Throws<InvalidDataException>(() => OcrCommercialCorpusTests.ResolveSplit(value, true));

    [Fact]
    public void ReassignedIdentityAndIncompleteSceneFailSelection()
    {
        Data.Manifest original = Fixture();
        Data.Sample[] changed = [.. original.Samples];
        changed[0] = changed[0] with { Split = "held-out" };
        Assert.Throws<InvalidDataException>(() => OcrCommercialCorpusTests.SelectSamples(original with { Samples = changed }, false, "development"));
        changed = [.. original.Samples];
        changed[0] = changed[0] with { SceneId = "wrong-scene" };
        Assert.Throws<InvalidDataException>(() => OcrCommercialCorpusTests.SelectSamples(original with { Samples = changed }, false, "development"));
        changed = [.. original.Samples];
        changed[0] = changed[0] with { Id = changed[1].Id };
        Assert.Throws<InvalidDataException>(() => OcrCommercialCorpusTests.SelectSamples(original with { Samples = changed }, false, "development"));
    }

    private static Data.Manifest Fixture()
    {
        Data.Sample[] samples = Data.Categories.SelectMany(category => Enumerable.Range(0, 10).SelectMany(scene =>
            Enumerable.Range(0, 6).Select(variant => new Data.Sample($"{category}-{scene:00}-{variant}", $"{category}-{scene:00}",
                category, scene < 6 ? "development" : "held-out", ["ko-KR"], 1, "light", 20, "synthetic", false,
                1, 1, "text", [], [], "unused.png", "unused", "unused", "unused")))).ToArray();
        return new Data.Manifest(1, Data.CorpusId, Data.Seed, 50, "Selection-only metadata fixture", samples, []);
    }
}
