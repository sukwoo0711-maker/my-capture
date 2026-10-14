using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureSearchQueryTests
{
    [Theory]
    [InlineData("app", "앱", CaptureSearchField.App)]
    [InlineData("title", "제목", CaptureSearchField.Title)]
    [InlineData("tag", "태그", CaptureSearchField.Tag)]
    [InlineData("text", "글자", CaptureSearchField.Text)]
    [InlineData("url", "주소", CaptureSearchField.Url)]
    [InlineData("folder", "폴더", CaptureSearchField.Folder)]
    [InlineData("type", "종류", CaptureSearchField.Type)]
    [InlineData("pinned", "보관", CaptureSearchField.Pinned)]
    [InlineData("date", "날짜", CaptureSearchField.Date)]
    public void ParsesEveryKeyAndAlias(string key, string alias, CaptureSearchField field)
    {
        foreach (string name in new[] { key, key.ToUpperInvariant(), alias })
        {
            CaptureSearchToken token = Assert.Single(CaptureSearchQuery.Parse($"-{name}:\"two words\"").Tokens);
            Assert.Equal(field, token.Field);
            Assert.Equal("two words", token.Value);
            Assert.True(token.IsNegated);
        }
    }

    [Theory]
    [InlineData("app:EDGE", true)]
    [InlineData("앱:msedge.exe", true)]
    [InlineData("app:매출", false)]
    [InlineData("title:회의", true)]
    [InlineData("제목:브라우저", true)]
    [InlineData("tag:임시", true)]
    [InlineData("태그:회의", false)]
    [InlineData("text:\"월 매출\"", true)]
    [InlineData("글자:매출", true)]
    [InlineData("text:\"매출 월\"", false)]
    [InlineData("url:example.test", true)]
    [InlineData("주소:EXAMPLE", true)]
    [InlineData("folder:reports", true)]
    [InlineData("폴더:보고서", true)]
    [InlineData("type:image", true)]
    [InlineData("종류:이미지", true)]
    [InlineData("type:video", false)]
    [InlineData("종류:동영상", false)]
    [InlineData("type:im", false)]
    [InlineData("pinned:YES", true)]
    [InlineData("보관:예", true)]
    [InlineData("pinned:no", false)]
    [InlineData("pinned:invalid", false)]
    [InlineData("date:2026-10", true)]
    [InlineData("날짜:2026-10-14", true)]
    [InlineData("date:2026-10-13", false)]
    [InlineData("date:2026-1", false)]
    [InlineData("date:2026-10-32", false)]
    [InlineData("date:2026-13", false)]
    [InlineData("date:2026", false)]
    [InlineData("회의 매출 edge", true)]
    [InlineData("app:edge text:매출 -tag:완료", true)]
    [InlineData("app:edge -tag:임시", false)]
    [InlineData("-text:없음 -type:video -pinned:no", true)]
    [InlineData("-date:2026-10", false)]
    [InlineData("-folder:보고서", false)]
    [InlineData("unknown:value", true)]
    [InlineData("-unknown:value", false)]
    [InlineData("\"title:literal\"", true)]
    [InlineData("title:", false)]
    [InlineData("\"월 매출", true)]
    [InlineData("", true)]
    public void MatchesFieldsPhrasesNegationAndCaptureLocalDate(string text, bool expected)
    {
        var record = new CaptureRecord
        {
            Title = "회의 unknown:value title:literal", SourceWindowTitle = "브라우저",
            Tags = "임시", OcrText = "월 매출 보고", SourceAppName = "Edge",
            SourceAppPath = @"C:\Program Files\Edge\msedge.exe",
            SourcePageUrl = "https://example.test", SourceDocumentPath = @"C:\Reports\October",
            CreatedAt = new DateTimeOffset(2026, 10, 14, 0, 30, 0, TimeSpan.FromHours(9)), IsPinned = true,
        };
        CaptureSearchQuery query = CaptureSearchQuery.Parse(text);
        Assert.Equal(expected, query.IsMatch(record, "업무/보고서"));
        Assert.Equal(expected, query.TryMatch(record, out _, "업무/보고서"));
    }

    [Fact]
    public void ManualPinAndMutableMetadataAreReadAtMatchTime()
    {
        var record = new CaptureRecord { FolderId = Guid.NewGuid(), IsPinned = true, FolderPinBeforeAssignment = false };
        CaptureSearchQuery query = CaptureSearchQuery.Parse("보관:no -종류:동영상");
        Assert.True(query.IsMatch(record));
        record.FolderPinBeforeAssignment = true;
        Assert.False(query.IsMatch(record));
        record.FolderPinBeforeAssignment = false;
        record.MediaKind = CaptureMediaKind.Video;
        Assert.False(query.IsMatch(record));
        Assert.True(CaptureSearchQuery.Parse("종류:동영상").IsMatch(record));
    }

    [Fact]
    public void AttributionContainsOnlyPositiveMatchingFieldsAndFailureClearsIt()
    {
        var record = new CaptureRecord { Title = "needle", OcrText = "needle", Tags = "needle" };
        Assert.True(CaptureSearchQuery.Parse("text:needle -title:absent").TryMatch(record, out CaptureMatchField fields));
        Assert.Equal(CaptureMatchField.OcrText, fields);
        Assert.True(CaptureSearchQuery.Parse("-text:absent").TryMatch(record, out fields));
        Assert.Equal(CaptureMatchField.None, fields);
        Assert.False(CaptureSearchQuery.Parse("text:needle title:absent").TryMatch(record, out fields));
        Assert.Equal(CaptureMatchField.None, fields);
        Assert.Equal(CaptureMatchField.Title | CaptureMatchField.OcrText | CaptureMatchField.Tags,
            Assert.Single(CaptureTextSearch.Search([record], "needle")).Fields);
    }

    [Theory]
    [InlineData(0, "-태그:임시  text:\"월 매출\"")]
    [InlineData(1, "앱:Edge    text:\"월 매출\"")]
    [InlineData(2, "앱:Edge  -태그:임시")]
    public void RemovingOneChipPreservesOtherSourceTokens(int index, string expected)
    {
        CaptureSearchQuery query = CaptureSearchQuery.Parse("  앱:Edge  -태그:임시  text:\"월 매출\"  ");
        string rewritten = query.RemoveToken(index);
        Assert.Equal(expected, rewritten);
        Assert.Equal(query.Tokens.Where((_, i) => i != index).Select(t => (t.Field, t.Value, t.IsNegated)),
            CaptureSearchQuery.Parse(rewritten).Tokens.Select(t => (t.Field, t.Value, t.IsNegated)));
    }

    [Fact]
    public void DuplicateChipsAndEmptyInputsRemainIndividuallyRemovable()
    {
        Assert.Empty(CaptureSearchQuery.Parse(null).Tokens);
        Assert.Empty(CaptureSearchQuery.Parse(" \t\n").Tokens);
        Assert.Equal("tag:a", CaptureSearchQuery.Parse("tag:a tag:a").RemoveToken(1));
        Assert.Equal("", CaptureSearchQuery.Parse("\"\"").RemoveToken(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureSearchQuery.Parse("a").RemoveToken(1));
        Assert.Throws<ArgumentNullException>(() => CaptureSearchQuery.Parse("").IsMatch(null!));
    }
}
