using System;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureTextSearchTests
{
    private static CaptureRecord Rec(
        string title = "",
        string window = "",
        string? ocr = null,
        DateTimeOffset? createdAt = null) =>
        new()
        {
            Title = title,
            SourceWindowTitle = window,
            OcrText = ocr,
            CreatedAt = createdAt ?? DateTimeOffset.Now,
        };

    [Fact]
    public void EmptyQuery_ReturnsAllRecords_NewestFirst()
    {
        var older = Rec(title: "a", createdAt: DateTimeOffset.Now.AddMinutes(-10));
        var newer = Rec(title: "b", createdAt: DateTimeOffset.Now);

        var hits = CaptureTextSearch.Search([older, newer], "   ");

        Assert.Equal(2, hits.Count);
        Assert.Equal("b", hits[0].Record.Title); // newest first
        Assert.Equal("a", hits[1].Record.Title);
    }

    [Fact]
    public void Match_FromOcrText_IsFound_AndAttributed()
    {
        // The whole point of the lock-in feature: find a capture by the words inside the image.
        var record = Rec(title: "무제", window: "Chrome", ocr: "송장 번호 INV-2026-0830 결제 완료");

        var hits = CaptureTextSearch.Search([record], "INV-2026-0830");

        Assert.Single(hits);
        Assert.True(hits[0].MatchedOcr);
        Assert.True((hits[0].Fields & CaptureMatchField.OcrText) != 0);
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var record = Rec(ocr: "Total Amount Due");

        Assert.Single(CaptureTextSearch.Search([record], "total amount"));
        Assert.True(CaptureTextSearch.IsMatch(record, "AMOUNT"));
    }

    [Fact]
    public void MultiTerm_IsAnd_AcrossFields()
    {
        // "chrome" from window title, "인보이스" from OCR — both terms present in different
        // fields still satisfies AND.
        var record = Rec(window: "Chrome - 결제", ocr: "인보이스 합계 12,000원");

        Assert.Single(CaptureTextSearch.Search([record], "chrome 인보이스"));

        // A term present nowhere fails the whole record.
        Assert.Empty(CaptureTextSearch.Search([record], "chrome 없는단어"));
    }

    [Fact]
    public void FieldAttribution_UnionsAcrossTerms()
    {
        var record = Rec(title: "영수증", window: "Edge", ocr: "결제 완료");

        var hits = CaptureTextSearch.Search([record], "영수증 결제");

        Assert.Single(hits);
        CaptureMatchField f = hits[0].Fields;
        Assert.True((f & CaptureMatchField.Title) != 0);
        Assert.True((f & CaptureMatchField.OcrText) != 0);
    }

    [Fact]
    public void NoOcrText_StillMatchesByTitleOrWindow()
    {
        var record = Rec(title: "회의 캡처", window: "Zoom", ocr: null);

        Assert.Single(CaptureTextSearch.Search([record], "회의"));
        Assert.Single(CaptureTextSearch.Search([record], "zoom"));
        Assert.Empty(CaptureTextSearch.Search([record], "존재하지않는텍스트"));
    }

    [Fact]
    public void MeasureCoverage_CountsRecordsWithOcrText()
    {
        var records = new[]
        {
            Rec(ocr: "has text"),
            Rec(ocr: null),
            Rec(ocr: "   "),   // whitespace-only counts as no text
            Rec(ocr: "more text"),
        };

        OcrCoverage coverage = CaptureTextSearch.MeasureCoverage(records);

        Assert.Equal(4, coverage.Total);
        Assert.Equal(2, coverage.WithOcrText);
        Assert.Equal(2, coverage.Missing);
        Assert.False(coverage.IsComplete);
        Assert.Equal(0.5, coverage.Fraction, 3);
    }

    [Fact]
    public void MeasureCoverage_EmptyQueue_IsComplete()
    {
        OcrCoverage coverage = CaptureTextSearch.MeasureCoverage([]);

        Assert.Equal(0, coverage.Total);
        Assert.True(coverage.IsComplete);
        Assert.Equal(1.0, coverage.Fraction, 3);
    }

    [Fact]
    public void Search_OrdersHitsNewestFirst()
    {
        var a = Rec(ocr: "match", createdAt: DateTimeOffset.Now.AddHours(-2));
        var b = Rec(ocr: "match", createdAt: DateTimeOffset.Now.AddHours(-1));
        var c = Rec(ocr: "match", createdAt: DateTimeOffset.Now);

        var hits = CaptureTextSearch.Search([a, c, b], "match");

        Assert.Equal(3, hits.Count);
        Assert.True(hits[0].Record.CreatedAt >= hits[1].Record.CreatedAt);
        Assert.True(hits[1].Record.CreatedAt >= hits[2].Record.CreatedAt);
    }

    [Fact]
    public void DuplicateTerms_DoNotBreakMatching()
    {
        var record = Rec(ocr: "결제 완료");

        // Repeated identical terms must still match (AND of the same term is trivially true).
        Assert.Single(CaptureTextSearch.Search([record], "결제 결제 결제"));
    }

    [Fact]
    public void NullTitleAndWindow_WithOcrOnly_StillSearchable()
    {
        // Records default Title/Window to empty; explicitly exercise null OcrText vs present.
        var record = new CaptureRecord { Title = null!, SourceWindowTitle = null!, OcrText = "invoice total" };

        Assert.Single(CaptureTextSearch.Search([record], "invoice"));
        Assert.True(CaptureTextSearch.IsMatch(record, "total"));
        Assert.Empty(CaptureTextSearch.Search([record], "absent"));
    }

    [Fact]
    public void MeasureCoverage_AllWithText_IsComplete()
    {
        var records = new[] { Rec(ocr: "a"), Rec(ocr: "b"), Rec(ocr: "c") };

        OcrCoverage coverage = CaptureTextSearch.MeasureCoverage(records);

        Assert.Equal(3, coverage.Total);
        Assert.Equal(3, coverage.WithOcrText);
        Assert.Equal(0, coverage.Missing);
        Assert.True(coverage.IsComplete);
        Assert.Equal(1.0, coverage.Fraction, 3);
    }

    [Fact]
    public void WhitespaceOnlyOcr_CountsAsMissing()
    {
        // HasOcrText uses IsNullOrWhiteSpace, so a whitespace-only OCR value is "not searchable".
        var record = Rec(ocr: "\t  \n");
        Assert.Equal(1, CaptureTextSearch.MeasureCoverage([record]).Missing);
        Assert.Empty(CaptureTextSearch.Search([record], "anything"));
    }

    [Fact]
    public void NoTextAttemptForCurrentGeneration_CompletesCoverageWithoutSearchableWords()
    {
        var record = new CaptureRecord
        {
            ContentRevision = 4,
            OcrContentRevision = 4,
            OcrText = string.Empty,
        };

        OcrCoverage coverage = CaptureTextSearch.MeasureCoverage([record]);

        Assert.Equal(1, coverage.Indexed);
        Assert.Equal(0, coverage.WithOcrText);
        Assert.Equal(0, coverage.Missing);
        Assert.True(coverage.IsComplete);
        Assert.Empty(CaptureTextSearch.Search([record], "anything"));
    }
}
