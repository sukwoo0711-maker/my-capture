using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureProvenanceTests
{
    [Fact]
    public void WebDisplayKeepsHostAndPathButOnlyTooltipContainsQueryAndFragment()
    {
        const string url = "https://www.github.com/owner/repo?token=private#anchor";
        CaptureProvenance source = CaptureProvenance.Create(url, appName: "Browser");
        Assert.Equal(CaptureProvenanceKind.Web, source.Kind);
        Assert.Equal("github.com/owner/repo", source.DisplayText);
        Assert.Equal("github.com", source.BadgeText);
        Assert.Equal("url:github.com", source.SearchQuery);
        Assert.Equal(url, source.Url);
        Assert.Equal(url + Environment.NewLine + "Browser", source.Tooltip);
        Assert.True(source.CanOpen);
    }

    [Fact]
    public void LongWebDisplayIsBoundedAndRootPathIsOmitted()
    {
        CaptureProvenance source = CaptureProvenance.Create("https://www.example.com/" + new string('x', 150));
        Assert.Equal(80, source.DisplayText.Length);
        Assert.EndsWith("…", source.DisplayText);
        Assert.Equal("example.com", CaptureProvenance.Create("https://example.com/?secret=1#hidden").DisplayText);
    }

    [Fact]
    public void InternationalDomainSearchMatchesTheStoredUnicodeAddress()
    {
        var record = new CaptureRecord { SourcePageUrl = "https://bücher.example/spec" };
        CaptureProvenance source = CaptureProvenance.FromRecord(record);
        Assert.Equal("xn--bcher-kva.example", source.BadgeText);
        Assert.Equal("url:bücher.example", source.SearchQuery);
        Assert.True(CaptureSearchQuery.Parse(source.SearchQuery).IsMatch(record));
    }

    [Fact]
    public void DocumentShowsOnlyItsFilenameAndSearchUsesTheExistingFolderGrammar()
    {
        const string path = @"C:\Users\someone\Private Projects\사양서.xlsx";
        CaptureProvenance source = CaptureProvenance.Create(documentPath: path, appName: "Excel", windowTitle: "사양서 - Excel");
        Assert.Equal(CaptureProvenanceKind.Document, source.Kind);
        Assert.Equal("사양서.xlsx", source.DisplayText);
        Assert.Equal(source.DisplayText, source.BadgeText);
        Assert.Equal(path + Environment.NewLine + "Excel", source.Tooltip);
        CaptureSearchToken token = Assert.Single(CaptureSearchQuery.Parse(source.SearchQuery).Tokens);
        Assert.Equal(CaptureSearchField.Folder, token.Field);
        Assert.Equal(path, token.Value);
        Assert.True(CaptureSearchQuery.Parse(source.SearchQuery).IsMatch(new CaptureRecord { SourceDocumentPath = path }));
    }

    [Fact]
    public void LongDocumentNameKeepsExtensionWithoutShowingParentFolders()
    {
        string file = new string('x', 120) + "사양서.xlsx";
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\Private\" + file);
        Assert.True(source.DisplayText.Length <= 80);
        Assert.DoesNotContain("Private", source.DisplayText);
        Assert.EndsWith("사양서.xlsx", source.DisplayText);
        Assert.Contains("…", source.DisplayText);
    }

    [Fact]
    public void AppOnlySourceDoesNotInventAnOpenTargetAndHandlesQuotedSearchNames()
    {
        CaptureProvenance source = CaptureProvenance.Create(appName: "Editor \"Preview\"");
        Assert.Equal(CaptureProvenanceKind.App, source.Kind);
        Assert.True(source.HasSource);
        Assert.False(source.CanOpen);
        Assert.True(CaptureSearchQuery.Parse(source.SearchQuery).IsMatch(new CaptureRecord { SourceAppName = source.AppName }));
        CaptureProvenance executable = CaptureProvenance.Create(appExecutablePath: @"C:\Apps\editor.exe");
        Assert.Equal("editor", executable.DisplayText);
        Assert.True(executable.CanOpen);
        Assert.False(CaptureProvenance.Create(appExecutablePath: @"C:\Apps\script.cmd").CanOpen);
    }

    [Fact]
    public void MissingAndUnsafeValuesDoNotProduceAnAction()
    {
        Assert.Same(CaptureProvenance.None, CaptureProvenance.Create());
        Assert.Same(CaptureProvenance.None, CaptureProvenance.FromRecord(null));
        CaptureProvenance invalid = CaptureProvenance.Create("javascript:alert(1)", "relative.xlsx", appExecutablePath: "shell:apps");
        Assert.False(invalid.HasSource);
        Assert.False(invalid.CanOpen);
        Assert.Empty(invalid.DisplayText);
        Assert.False(CaptureProvenance.Create(windowTitle: "Window title alone").HasSource);
    }

    [Fact]
    public void RecordAndSourceFactoriesPreserveTheSameMetadataWithWebPriority()
    {
        var record = new CaptureRecord
        {
            SourcePageUrl = "https://example.com/page", SourceDocumentPath = @"C:\Work\spec.pdf",
            SourceAppName = "Browser", SourceAppPath = @"C:\Apps\browser.exe", SourceWindowTitle = new string('a', 240),
        };
        CaptureProvenance provenance = CaptureProvenance.FromRecord(record);
        Assert.Equal(CaptureProvenance.FromSource(record.SourceDetails, record.SourcePageUrl, record.SourceWindowTitle), provenance);
        Assert.Equal(CaptureProvenanceKind.Web, provenance.Kind);
        Assert.Equal(record.SourceDocumentPath, provenance.DocumentPath);
        Assert.Equal(record.SourceWindowTitle, provenance.WindowTitle);
    }
}
