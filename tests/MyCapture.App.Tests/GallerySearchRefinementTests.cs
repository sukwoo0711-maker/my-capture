using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GallerySearchRefinementTests
{
    [Fact]
    public void ChipsRewriteTheQueryAndAttributionClearsOnCachedCards()
    {
        var (queue, controller, vm) = Create();
        var record = new CaptureRecord { Title = "Report", SourceAppName = "Edge", OcrText = "월 매출", Tags = "회의" };
        queue.Add(record);
        vm.Refresh();
        GalleryItemViewModel tile = Assert.Single(vm.Groups.SelectMany(group => group.Items));
        Assert.False(tile.HasSearchMatch);

        vm.SearchQuery = "앱:Edge 글자:\"월 매출\" -태그:임시";
        Assert.Equal(3, vm.SearchConditions.Count);
        Assert.Contains(UiText.Get("Library.Search.Field.App"), vm.SearchConditions[0].Label);
        Assert.Contains(UiText.Get("Library.Search.MatchField.OcrText"), tile.SearchMatchText);
        Assert.True(tile.HasSearchMatch);
        Assert.Equal(1, vm.VisibleCount);
        vm.RemoveSearchCondition(1);
        Assert.Equal("앱:Edge  -태그:임시", vm.SearchQuery);
        Assert.Equal(2, vm.SearchConditions.Count);
        Assert.DoesNotContain(UiText.Get("Library.Search.MatchField.OcrText"), tile.SearchMatchText);
        vm.SearchQuery = "-tag:임시";
        Assert.Equal(UiText.Get("Library.Search.ExclusionsMatched"), tile.SearchMatchText);
        record.Tags = "임시";
        vm.Refresh();
        Assert.True(vm.IsEmpty);
        vm.SearchQuery = "";
        Assert.Same(tile, Assert.Single(vm.Groups.SelectMany(group => group.Items)));
        Assert.False(tile.HasSearchMatch);
        Assert.False(vm.HasSearchQuery);
        Assert.Empty(vm.SearchConditions);
        Assert.Equal(1, controller.Count);
    }

    [Fact]
    public void EmptyQueryResultsCountOnlyCandidatesInTheCurrentDateAndMediaView()
    {
        var (queue, _, vm) = Create();
        DateTimeOffset date = new(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(9));
        queue.Add(new CaptureRecord { CreatedAt = date, Title = "One" });
        queue.Add(new CaptureRecord { CreatedAt = date, Title = "Two" });
        queue.Add(new CaptureRecord { CreatedAt = date, MediaKind = CaptureMediaKind.Video });
        queue.Add(new CaptureRecord { CreatedAt = date.AddDays(-1) });
        vm.SelectScope(CaptureFolderScope.ForDate(new DateOnly(2026, 10, 14)));
        vm.Filter = GalleryFilter.Images;
        vm.SearchQuery = "text:absent";
        Assert.True(vm.IsEmpty);
        Assert.Equal(2, vm.SearchHiddenCount);
        Assert.Equal(UiText.Format("Library.Search.HiddenCount", 2), vm.EmptyStateText);
        Assert.True(vm.CanResetEmptySearch);
        vm.SearchQuery = "title:One";
        Assert.Equal(1, vm.VisibleCount);
        Assert.Equal(1, vm.SearchHiddenCount);
    }

    [Fact]
    public void EmptyLibraryKeepsItsExistingMessageDuringSearch()
    {
        var (_, _, vm) = Create();
        vm.SearchQuery = "app:Edge";
        Assert.Equal(UiText.Get("Library.Empty.NoCaptures"), vm.EmptyStateTitle);
        Assert.Equal(UiText.Get("Library.Empty.Hint"), vm.EmptyStateText);
        Assert.False(vm.CanResetEmptySearch);
    }

    [Theory]
    [InlineData("ko", "사이트")]
    [InlineData("en", "Site")]
    public void SourceGeneratedHostQueryFiltersTheGalleryAndShowsALocalizedCondition(string language, string fieldLabel)
    {
        using var culture = UiText.UseLanguage(language);
        var (queue, _, vm) = Create();
        var source = new CaptureRecord { SourcePageUrl = "https://www.GitHub.com/owner/repo?token=private#anchor" };
        var sameHost = new CaptureRecord { SourcePageUrl = "https://github.com/other" };
        queue.Add(source);
        queue.Add(sameHost);
        foreach (string url in new[]
        {
            "https://notgithub.com/", "https://other.example/?redirect=https://github.com",
            "https://other.example/github.com", "https://docs.github.com/",
        }) queue.Add(new CaptureRecord { SourcePageUrl = url });

        vm.SearchQuery = CaptureProvenance.FromRecord(source).SearchQuery;

        Assert.Equal("host:github.com", vm.SearchQuery);
        Assert.Equal(fieldLabel, UiText.Get("Library.Search.Field.Host"));
        GallerySearchCondition condition = Assert.Single(vm.SearchConditions);
        Assert.Equal(UiText.Format("Library.Search.Condition", fieldLabel, "github.com"), condition.Label);
        GalleryItemViewModel[] matches = vm.Groups.SelectMany(group => group.Items).ToArray();
        Assert.Equal(new[] { source.Id, sameHost.Id }.Order(), matches.Select(item => item.Id).Order());
        Assert.All(matches, item => Assert.Equal(GallerySearchMatchText.Describe(CaptureMatchField.SourcePageUrl), item.SearchMatchText));
        Assert.Equal(2, vm.VisibleCount);
        Assert.Equal(4, vm.SearchHiddenCount);

        vm.RemoveSearchCondition(condition.Index);
        Assert.Empty(vm.SearchConditions);
        Assert.Equal(6, vm.VisibleCount);
    }

    [Fact]
    public void FilenameMatchIsAttributedAndCannotBypassFieldOrNegationConditions()
    {
        var (queue, controller, vm) = Create();
        var record = new CaptureRecord { CreatedAt = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero) };
        queue.Add(record);
        vm.SearchQuery = "mycapture_20261014_120000.PNG";
        GalleryItemViewModel tile = Assert.Single(vm.Groups.SelectMany(group => group.Items));
        Assert.Equal(GallerySearchMatchText.Describe(CaptureMatchField.ExportFileName), tile.SearchMatchText);
        record.Title = "tag:absent";
        Assert.Empty(controller.BuildGroups("tag:absent", DateTimeOffset.Now));
        record.Title = "-text:forbidden";
        record.OcrText = "forbidden";
        Assert.Empty(controller.BuildGroups("-text:forbidden", DateTimeOffset.Now));
    }

    [Theory]
    [InlineData("mycapture_20261014_120000.PNG", true, CaptureMatchField.ExportFileName)]
    [InlineData("mycapture_20261014_120000.PNG type:image", true, CaptureMatchField.ExportFileName | CaptureMatchField.MediaType)]
    [InlineData("\"mycapture_20261014_120000.PNG\"", true, CaptureMatchField.ExportFileName)]
    [InlineData("-mycapture_20261014_120000.PNG", false, CaptureMatchField.None)]
    [InlineData("-\"mycapture_20261014_120000.PNG\" type:image", false, CaptureMatchField.None)]
    [InlineData("mycapture_20261014_120000.PNG type:video", false, CaptureMatchField.None)]
    [InlineData("mycapture_20261014_120000.PNG absent", false, CaptureMatchField.None)]
    [InlineData("mycapture_ 20261014_120000.PNG", true, CaptureMatchField.ExportFileName)]
    [InlineData("title:mycapture_20261014_120000.PNG", false, CaptureMatchField.None)]
    [InlineData("-title:mycapture_20261014_120000.PNG", true, CaptureMatchField.None)]
    public void FilenameTokensRespectRefinementNegationAndQuotes(string query, bool expected, CaptureMatchField expectedFields)
    {
        var (queue, controller, _) = Create();
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var record = new CaptureRecord { CreatedAt = now };
        queue.Add(record);

        foreach (bool collectFields in new[] { false, true })
        {
            var fields = new Dictionary<Guid, CaptureMatchField>();
            CaptureRecord[] matches = controller.BuildGroups(query, now,
                searchMatchFields: collectFields ? fields : null).SelectMany(group => group.Records).ToArray();
            if (expected) Assert.Same(record, Assert.Single(matches));
            else Assert.Empty(matches);
            if (expected && collectFields) Assert.Equal(expectedFields, fields[record.Id]);
            else Assert.Empty(fields);
        }
    }

    [Fact]
    public async Task LibraryFolderPathsAndEmptyFolderStateArePreserved()
    {
        // This fixture stays inside the build output and is retained for inspection.
        var (queue, _, vm) = Create();
        CaptureFolder folder = await queue.CreateFolderAsync("Reports");
        vm.SelectScope(CaptureFolderScope.ForFolder(folder.Id));
        vm.SearchQuery = "text:absent";
        Assert.Equal(UiText.Get("Folder410.EmptyFolder"), vm.EmptyStateTitle);
        Assert.False(vm.CanResetEmptySearch);

        var record = new CaptureRecord { FolderId = folder.Id, Title = "Invoice" };
        queue.Add(record);
        // Queue.Add may assign the active destination; either way the stored folder is Reports.
        vm.SearchQuery = "folder:reports";
        GalleryItemViewModel tile = Assert.Single(vm.Groups.SelectMany(group => group.Items));
        Assert.Contains(UiText.Get("Library.Search.MatchField.Folder"), tile.SearchMatchText);
        vm.SearchQuery = "-폴더:Reports";
        Assert.True(vm.IsEmpty);
        Assert.Equal(1, vm.SearchHiddenCount);
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void AllSearchLabelsAndAttributionAreLocalized(string language)
    {
        using var culture = UiText.UseLanguage(language);
        var (_, _, vm) = Create();
        vm.SearchQuery = "app:a title:a tag:a text:a url:a host:a folder:a type:image pinned:yes date:2026-10 -a";
        Assert.Equal(11, vm.SearchConditions.Count);
        foreach (GallerySearchCondition chip in vm.SearchConditions) Assert.NotEmpty(chip.RemoveToolTip);
        foreach (CaptureMatchField field in Enum.GetValues<CaptureMatchField>())
            Assert.NotEmpty(GallerySearchMatchText.Describe(field));
        Assert.Contains(language == "en" ? "app:" : "앱:", vm.SearchToolTip);
    }

    private static (CaptureQueue Queue, GalleryController Controller, GalleryViewModel ViewModel) Create()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "search-fixtures", Guid.NewGuid().ToString("N"));
        var queue = new CaptureQueue(AppPaths.CreateForRoot(root), new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var vm = new GalleryViewModel(controller, _ => "", 96);
        vm.SetThumbnailLoadingEnabled(false);
        return (queue, controller, vm);
    }
}
