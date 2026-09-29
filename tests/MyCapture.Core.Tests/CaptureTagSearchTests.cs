using System;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureTagSearchTests
{
    [Theory]
    [InlineData("alpha")]
    [InlineData("ALPHA")]
    [InlineData("개발")]
    [InlineData("ALPHA 개발")]
    [InlineData("\t개발\r\nalpha alpha")]
    [InlineData("lph 개발")]
    public void TagsOnly_MixedLanguageTermsUseExistingSubstringAndRules(string query)
    {
        var record = new CaptureRecord { Tags = "Alpha, 개발 검토" };
        CaptureSearchHit hit = Assert.Single(CaptureTextSearch.Search([record], query));
        Assert.Same(record, hit.Record);
        Assert.Equal(CaptureMatchField.Tags, hit.Fields);
        Assert.False(hit.MatchedOcr);
        Assert.True(CaptureTextSearch.IsMatch(record, query));
        Assert.Empty(CaptureTextSearch.Search([record], query + " missing"));
        Assert.False(CaptureTextSearch.IsMatch(record, query + " missing"));
    }

    [Fact]
    public void MultiTerm_MatchesAcrossAllFields_WithExactAttribution()
    {
        var record = new CaptureRecord
        {
            Title = "계약 검토", Tags = "Client, Alpha", SourceWindowTitle = "Edge",
            OcrText = "invoice total", ContentRevision = 7, OcrContentRevision = 7,
        };
        CaptureSearchHit hit = Assert.Single(CaptureTextSearch.Search([record], "계약 ALPHA edge invoice png"));
        Assert.Equal(CaptureMatchField.Title | CaptureMatchField.Tags | CaptureMatchField.WindowTitle
            | CaptureMatchField.OcrText | CaptureMatchField.MediaType, hit.Fields);
        Assert.True(hit.MatchedOcr);
        Assert.Equal(7, record.ContentRevision);
        Assert.Equal(7, record.OcrContentRevision);
        Assert.True(CaptureTextSearch.IsMatch(record, "계약 ALPHA edge invoice png"));
    }

    [Fact]
    public void SameTerm_InSeveralFields_UnionsRatherThanReplacingAttribution()
    {
        var record = new CaptureRecord
        { Title = "image", Tags = "image", SourceWindowTitle = "image", OcrText = "image" };
        CaptureSearchHit hit = Assert.Single(CaptureTextSearch.Search([record], "IMAGE image"));
        Assert.Equal(CaptureMatchField.Title | CaptureMatchField.Tags | CaptureMatchField.WindowTitle
            | CaptureMatchField.OcrText | CaptureMatchField.MediaType, hit.Fields);
        Assert.True(hit.MatchedOcr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void EmptyQuery_WithTagsKeepsNewestFirstAndNoAttribution(string? query)
    {
        var older = new CaptureRecord { Tags = "alpha", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var newer = new CaptureRecord { Tags = "alpha", CreatedAt = older.CreatedAt.AddMinutes(1) };
        var hits = CaptureTextSearch.Search([older, newer], query);
        Assert.Equal(2, hits.Count);
        Assert.Same(newer, hits[0].Record);
        Assert.All(hits, hit => Assert.Equal(CaptureMatchField.None, hit.Fields));
    }

    [Fact]
    public void LegacyNullTags_KeepExistingMatchingAndAttribution()
    {
        var record = new CaptureRecord { Title = "계약", Tags = null!, OcrText = "invoice" };
        CaptureSearchHit hit = Assert.Single(CaptureTextSearch.Search([record], "계약 invoice"));
        Assert.Equal(CaptureMatchField.Title | CaptureMatchField.OcrText, hit.Fields);
        Assert.False(CaptureTextSearch.IsMatch(record, "missing"));
    }

    [Fact]
    public void TagsDoNotPretendToProvideOcrCoverage()
    {
        var record = new CaptureRecord { Tags = "invoice", ContentRevision = 7 };
        OcrCoverage before = CaptureTextSearch.MeasureCoverage([record]);
        Assert.Single(CaptureTextSearch.Search([record], "invoice"));
        Assert.Equal(before, CaptureTextSearch.MeasureCoverage([record]));
        Assert.Equal(0, before.WithOcrText);
        Assert.Equal(7, record.ContentRevision);
    }

    [Fact]
    public void TagHits_KeepNewestFirst_AndExcludePartialAndMatches()
    {
        var older = new CaptureRecord { Tags = "alpha 개발", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var newer = new CaptureRecord { Tags = "ALPHA 개발", CreatedAt = older.CreatedAt.AddMinutes(1) };
        var partial = new CaptureRecord { Tags = "alpha", CreatedAt = newer.CreatedAt.AddMinutes(1) };
        var hits = CaptureTextSearch.Search([older, partial, newer], "alpha 개발");
        Assert.Equal(2, hits.Count);
        Assert.Same(newer, hits[0].Record);
        Assert.Same(older, hits[1].Record);
    }
}
