using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Xml.Linq;
using MyCapture.App.Gallery;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryProvenanceTests
{
    [Fact]
    public void BadgeProjectionIsCachedUntilMetadataChangesWithoutLoadingThumbnail()
    {
        var record = new CaptureRecord
        {
            SourcePageUrl = "https://www.github.com/owner/repo?private=value#fragment",
            SourceAppName = "Browser",
        };
        var tile = Tile(record);
        CaptureProvenance initial = tile.SourceProvenance;
        Assert.True(tile.HasSourceBadge);
        Assert.Equal("github.com", tile.SourceBadgeText);
        Assert.Equal("url:github.com", tile.SourceSearchQuery);
        Assert.Contains(record.SourcePageUrl, tile.SourceBadgeToolTip);
        Assert.Equal("https://www.github.com/owner/repo", tile.SourcePageDisplayText);
        Assert.DoesNotContain("private", tile.SourcePageUrlAutomationName);

        record.SourcePageUrl = string.Empty;
        record.SourceDocumentPath = @"C:\Work\Project specs\사양서.xlsx";
        record.SourceAppName = "Excel";
        record.SourceAppPath = @"C:\Office\EXCEL.EXE";
        Assert.Same(initial, tile.SourceProvenance);
        Assert.Equal("github.com", tile.SourceBadgeText);

        var changed = new HashSet<string>();
        tile.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        tile.RaiseMetaChanged();
        Assert.NotSame(initial, tile.SourceProvenance);
        Assert.Equal("사양서.xlsx", tile.SourceBadgeText);
        Assert.Contains(record.SourceDocumentPath, tile.SourceBadgeToolTip);
        Assert.Empty(tile.SourcePageDisplayText);
        Assert.Contains(nameof(GalleryItemViewModel.SourceProvenance), changed);
        Assert.Contains(nameof(GalleryItemViewModel.SourceBadgeText), changed);
        Assert.Contains(nameof(GalleryItemViewModel.SourceBadgeToolTip), changed);
        Assert.Contains(nameof(GalleryItemViewModel.SourceSearchQuery), changed);
        Assert.Contains(nameof(GalleryItemViewModel.SourcePageDisplayText), changed);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("document")]
    [InlineData("app")]
    public void SameSourceQueryUsesSupportedFieldAndMatchesOriginalRecord(string kind)
    {
        CaptureRecord record = kind switch
        {
            "web" => new() { SourcePageUrl = "https://www.github.com/owner/repo?q=value#part" },
            "document" => new() { SourceDocumentPath = @"C:\Work\Project specs\사양서.xlsx", SourceAppName = "Excel" },
            _ => new() { SourceAppName = "Visual Studio Code" },
        };
        GalleryItemViewModel tile = Tile(record);
        CaptureSearchQuery query = CaptureSearchQuery.Parse(tile.SourceSearchQuery);
        CaptureSearchToken token = Assert.Single(query.Tokens);
        Assert.Equal(kind switch
        {
            "web" => CaptureSearchField.Url,
            "document" => CaptureSearchField.Folder,
            _ => CaptureSearchField.App,
        }, token.Field);
        Assert.Equal(kind switch
        {
            "web" => "github.com",
            "document" => record.SourceDocumentPath,
            _ => record.SourceAppName,
        }, token.Value);
        Assert.True(query.IsMatch(record));
        Assert.False(query.IsMatch(new CaptureRecord { SourcePageUrl = "https://different.example/", SourceAppName = "Other" }));
    }

    [Fact]
    public void EmptyAndWindowTitleOnlyRecordsHaveNoActionableBadge()
    {
        foreach (CaptureRecord record in new[] { new CaptureRecord(), new CaptureRecord { SourceWindowTitle = "Old window title" } })
        {
            GalleryItemViewModel tile = Tile(record);
            Assert.False(tile.HasSourceBadge);
            Assert.Empty(tile.SourceBadgeText);
            Assert.Empty(tile.SourceSearchQuery);
        }
    }

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void RecycledBadgeUpdatesVisibleTextTargetAndSameSourceMenu(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var button = new Button();
        CaptureProvenance web = CaptureProvenance.Create(url: "https://www.github.com/owner/repo?private=value#part");
        GallerySourceBadge.SetProvenance(button, web);
        Assert.Equal(web.Tooltip, button.ToolTip);
        Assert.Contains("github.com", AutomationProperties.GetName(button));
        Assert.DoesNotContain("private", AutomationProperties.GetName(button));
        Assert.True(button.Focusable && button.IsTabStop);
        Assert.Equal("github.com", Assert.IsType<TextBlock>(Assert.IsType<Grid>(button.Content).Children[1]).Text);
        Assert.Equal(new[] { UiText.Get("Source.Open"), UiText.Get("Source.CopyUrl"), UiText.Get("Source.ShowSame") },
            button.ContextMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header));

        CaptureProvenance document = CaptureProvenance.Create(documentPath: @"C:\Work\Project specs\사양서.xlsx", appName: "Excel");
        GallerySourceBadge.SetProvenance(button, document);
        Assert.Same(document, GallerySourceBadge.GetProvenance(button));
        Assert.Equal("사양서.xlsx", Assert.IsType<TextBlock>(Assert.IsType<Grid>(button.Content).Children[1]).Text);
        Assert.Equal(document.Tooltip, button.ToolTip);
        Assert.Equal(new[] { UiText.Get("Source.Open"), UiText.Get("Source.CopyPath"), UiText.Get("Source.OpenLocation"), UiText.Get("Source.ShowSame") },
            button.ContextMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header));

        string? requestedQuery = null;
        GallerySourceBadge.Configure(button, document, status: null, query => requestedQuery = query);
        MenuItem sameSource = button.ContextMenu.Items.OfType<MenuItem>().Last();
        sameSource.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("folder:\"C:\\Work\\Project specs\\사양서.xlsx\"", requestedQuery);

        GallerySourceBadge.SetProvenance(button, CaptureProvenance.None);
        Assert.Empty(button.ContextMenu.Items);
        Assert.Empty(Assert.IsType<TextBlock>(Assert.IsType<Grid>(button.Content).Children[1]).Text);
    });

    [Fact]
    public void BothCardTemplatesBindTheCachedBadgeAtTheThumbnailBottomLeft()
    {
        using Stream stream = typeof(GalleryProvenanceTests).Assembly.GetManifestResourceStream("GalleryFixture.Window.xaml")!;
        XDocument document = XDocument.Load(stream);
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace gallery = "clr-namespace:MyCapture.App.Gallery";
        foreach (string key in new[] { "Gallery.TileTemplate", "Gallery.CompactTileTemplate" })
        {
            XElement template = Assert.Single(document.Descendants(p + "DataTemplate"), node => (string?)node.Attribute(x + "Key") == key);
            XElement badge = Assert.Single(template.Descendants(p + "Button"), node => (string?)node.Attribute("AutomationProperties.AutomationId") == "LibrarySourceBadge");
            Assert.Equal("{Binding SourceProvenance}", (string?)badge.Attribute(gallery + "GallerySourceBadge.Provenance"));
            Assert.Equal("Left", (string?)badge.Attribute("HorizontalAlignment"));
            Assert.Equal("Bottom", (string?)badge.Attribute("VerticalAlignment"));
            Assert.StartsWith("{Binding HasSourceBadge,", (string?)badge.Attribute("Visibility"));
            Assert.Contains(badge.Ancestors(p + "Border"), border => (string?)border.Attribute(x + "Name") == "TilePreview");
        }
    }

    private static GalleryItemViewModel Tile(CaptureRecord record) => new(record,
        _ => throw new InvalidOperationException("Source presentation must not resolve thumbnails."), 320);
}
