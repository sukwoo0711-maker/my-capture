using System.Text.Json;
using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class SourcePageUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("github.com/owner/repo/pull/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/test.txt")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("https://example.com/\npage")]
    [InlineData("https://")]
    [InlineData("https://example.com\\evil")]
    public void InvalidOrUnprovenSchemeIsNeverActionable(string? input) => Assert.Empty(SourcePageUrl.Normalize(input));

    [Fact]
    public void ActualUrlPreservesPathQueryAndFragmentThroughJsonAndSearch()
    {
        const string url = "https://github.com/org/repo/pull/313?diff=split#discussion_r42";
        Assert.Equal(url, SourcePageUrl.Normalize(url));
        var record = new CaptureRecord { SourcePageUrl = url, Tags = "웹페이지" };
        CaptureRecord loaded = JsonSerializer.Deserialize<CaptureRecord>(JsonSerializer.Serialize(record, JsonDefaults.Compact), JsonDefaults.Compact)!;
        Assert.Equal(url, loaded.SourcePageUrl);
        CaptureSearchHit hit = Assert.Single(CaptureTextSearch.Search([loaded], "pull/313 discussion_r42"));
        Assert.Equal(CaptureMatchField.SourcePageUrl, hit.Fields);
        Assert.Contains(url, loaded.SearchHaystack);
        Assert.Empty(JsonSerializer.Deserialize<CaptureRecord>("{}", JsonDefaults.Compact)!.SourcePageUrl);
    }
}
