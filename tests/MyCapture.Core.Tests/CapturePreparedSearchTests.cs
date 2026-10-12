using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CapturePreparedSearchTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(" \t\r\n", true)]
    [InlineData("DEVELOPER", true)]
    [InlineData("developer needle", true)]
    [InlineData("needle tag-target", true)]
    [InlineData("example.test 한글", true)]
    [InlineData("editor.exe", true)]
    [InlineData("Friendly editor.exe", true)]
    [InlineData("project\\source.cs", true)]
    [InlineData("developer absent", false)]
    [InlineData("program files", false)]
    public void PreparedMatcherPreservesAndFieldsCaseWhitespaceAndAttribution(string? query, bool expected)
    {
        var record = new CaptureRecord
        {
            Title = "Developer", Tags = "tag-target", SourceWindowTitle = "한글 IDE",
            SourcePageUrl = "https://example.test/page", SourceAppName = "Friendly",
            SourceAppPath = @"C:\Program Files\editor.exe", SourceDocumentPath = @"C:\project\source.cs",
            OcrText = new string('x', 65536) + " needle", MediaKind = CaptureMediaKind.Image,
        };
        Func<CaptureRecord, bool> matcher = CaptureTextSearch.CreateMatcher(query);
        Assert.Equal(expected, matcher(record));
        Assert.Equal(expected, CaptureTextSearch.IsMatch(record, query));
        Assert.Equal(expected, CaptureTextSearch.Search([record], query).Count == 1);
        // Attribution is retained even when the boolean path stops on a title match.
        record.Title = "needle";
        Assert.Equal(CaptureMatchField.Title | CaptureMatchField.OcrText,
            Assert.Single(CaptureTextSearch.Search([record], "needle")).Fields);
    }

    [Fact]
    public void PreparedMatcherReadsLaterMutationsAndRejectsNullRecords()
    {
        var record = new CaptureRecord { Title = "before" };
        Func<CaptureRecord, bool> matcher = CaptureTextSearch.CreateMatcher("after");
        Assert.False(matcher(record));
        record.OcrText = "after";
        Assert.True(matcher(record));
        record.OcrText = null;
        record.Tags = "after";
        Assert.True(matcher(record));
        record.Tags = "";
        Assert.False(matcher(record));
        Assert.Throws<ArgumentNullException>(() => matcher(null!));
        Assert.Throws<ArgumentNullException>(() => CaptureTextSearch.CreateMatcher(null)(null!));
    }

    [Fact]
    public void PreparedTitleQueryDoesNotAllocatePerRecordWithLargeOcr()
    {
        Func<CaptureRecord, bool> matcher = CaptureTextSearch.CreateMatcher("developer");
        var record = new CaptureRecord { Title = "developer", OcrText = new string('x', 65536),
            SourceAppName = "IDE", SourceAppPath = @"C:\tools\editor.exe" };
        for (int i = 0; i < 100; i++) Assert.True(matcher(record));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int hits = 0;
        for (int i = 0; i < 10000; i++) if (matcher(record)) hits++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10000, hits);
        Assert.True(allocated < 16384, $"Prepared filtering allocated {allocated} bytes.");
    }
}
