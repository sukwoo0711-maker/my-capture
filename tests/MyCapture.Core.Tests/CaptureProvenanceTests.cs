using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureProvenanceTests
{
    [Fact]
    public void WebDisplayAndTooltipKeepHostAndPathWhileOnlyActionUrlContainsQueryAndFragment()
    {
        const string url = "https://www.github.com/owner/repo?token=private#anchor";
        CaptureProvenance source = CaptureProvenance.Create(url, appName: "Browser");
        Assert.Equal(CaptureProvenanceKind.Web, source.Kind);
        Assert.Equal("github.com/owner/repo", source.DisplayText);
        Assert.Equal("github.com", source.BadgeText);
        Assert.Equal("host:github.com", source.SearchQuery);
        Assert.Equal(url, source.Url);
        Assert.Equal("https://www.github.com/owner/repo" + Environment.NewLine + "Browser", source.Tooltip);
        foreach (string text in new[] { source.DisplayText, source.BadgeText, source.Tooltip })
        {
            Assert.DoesNotContain("token=private", text);
            Assert.DoesNotContain("anchor", text);
        }
        Assert.True(source.CanOpen);
    }

    [Fact]
    public void UserInfoIsRejectedBeforeItCanReachSourceDisplayOrActions()
    {
        CaptureProvenance source = CaptureProvenance.Create("https://private-user:private-password@example.com/path?secret=1#private");
        Assert.Same(CaptureProvenance.None, source);
        Assert.Empty(source.Url);
        Assert.Empty(source.DisplayText);
        Assert.Empty(source.BadgeText);
        Assert.Empty(source.Tooltip);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", true)]
    [InlineData("https://www.github.com/other", true)]
    [InlineData("https://GitHub.com/other", true)]
    [InlineData("https://notgithub.com/", false)]
    [InlineData("https://other.example/?redirect=https://github.com", false)]
    [InlineData("https://other.example/github.com", false)]
    [InlineData("https://docs.github.com/", false)]
    public void WebSourceActionMatchesOnlyTheNormalizedHost(string url, bool expected)
    {
        CaptureProvenance source = CaptureProvenance.Create("https://www.GitHub.com/owner/repo?token=private#anchor");
        Assert.Equal("host:github.com", source.SearchQuery);
        Assert.Equal(expected, CaptureSearchQuery.Parse(source.SearchQuery).IsMatch(new CaptureRecord { SourcePageUrl = url }));
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
    public void InternationalDomainSearchMatchesUnicodeAndAsciiAddresses()
    {
        var record = new CaptureRecord { SourcePageUrl = "https://bücher.example/spec" };
        CaptureProvenance source = CaptureProvenance.FromRecord(record);
        Assert.Equal("xn--bcher-kva.example", source.BadgeText);
        Assert.Equal("host:xn--bcher-kva.example", source.SearchQuery);
        Assert.True(CaptureSearchQuery.Parse(source.SearchQuery).IsMatch(record));
        Assert.True(CaptureSearchQuery.Parse(source.SearchQuery).IsMatch(new CaptureRecord { SourcePageUrl = "https://www.xn--bcher-kva.example/other" }));
        Assert.Equal(source.SearchQuery, CaptureProvenance.Create("https://xn--bcher-kva.example/spec").SearchQuery);
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
