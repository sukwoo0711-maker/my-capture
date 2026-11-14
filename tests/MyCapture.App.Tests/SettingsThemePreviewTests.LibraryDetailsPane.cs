using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The library's details pane is a fixed part of the layout; only the user's fold button changes it.</summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryDetailsPane_StaysWithSameWidthAndColumnsForNoneSingleAndMultiSelection_Korean() =>
        VerifyLibraryDetailsPaneAlwaysShown(nameof(LibraryDetailsPane_StaysWithSameWidthAndColumnsForNoneSingleAndMultiSelection_Korean), "ko-KR");

    [Fact]
    public Task LibraryDetailsPane_StaysWithSameWidthAndColumnsForNoneSingleAndMultiSelection_English() =>
        VerifyLibraryDetailsPaneAlwaysShown(nameof(LibraryDetailsPane_StaysWithSameWidthAndColumnsForNoneSingleAndMultiSelection_English), "en-US");

    private Task VerifyLibraryDetailsPaneAlwaysShown(string testName, string language) =>
        RunIsolated(testName, () =>
        {
            using var culture = UiText.UseLanguage(language);
            Application application = CreateLibraryMotionApplication();
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-details-pane-");
            try
            {
                GalleryWindow window = fixture.Window;
                GalleryViewModel vm = fixture.ViewModel;
                window.Show();
                window.Width = window.MinWidth;
                window.Height = 760;
                SettleGalleryLayout(window);
                var panel = Assert.IsType<Border>(window.FindName("GalleryDetailsPanel"));
                var rail = Assert.IsType<Border>(window.FindName("GalleryDetailsRail"));
                var empty = Assert.IsType<StackPanel>(window.FindName("GalleryDetailsEmpty"));
                var title = Assert.IsType<TextBlock>(window.FindName("GalleryDetailsEmptyTitle"));
                var hint = Assert.IsType<TextBlock>(window.FindName("GalleryDetailsEmptyHint"));

                // Open by default, with nothing selected: the empty state, not a missing pane.
                Assert.True(window.IsDetailsPaneOpen);
                Assert.True(panel.IsVisible);
                Assert.False(rail.IsVisible);
                Assert.True(empty.IsVisible);
                Assert.Equal(UiText.Get("Workspace.NoSelection"), title.Text);
                Assert.Equal(UiText.Get("Workspace.SelectHint"), MyCapture.App.Themes.KoreanWordWrap.Strip(hint.Text));
                Assert.True(Assert.IsType<Button>(window.FindName("HideDetailsButton")).IsVisible);
                Assert.Equal(UiText.Get("Workspace.Details"), Assert.IsType<TextBlock>(window.FindName("GalleryDetailsHeading")).Text);
                double paneWidth = panel.ActualWidth;
                int columns = vm.ColumnCount;
                // The minimum window keeps two columns beside the pane (the layout's floor).
                Assert.Equal(2, columns);
                SaveLibraryDetailsPaneEvidence(window, "after", "none-min");

                void AssertStable(string stage)
                {
                    SettleGalleryLayout(window);
                    Assert.True(window.IsDetailsPaneOpen, stage);
                    Assert.True(panel.IsVisible && !rail.IsVisible, stage);
                    Assert.Equal(paneWidth, panel.ActualWidth);
                    Assert.True(columns == vm.ColumnCount, $"{stage}: columns {vm.ColumnCount} instead of {columns}");
                }

                vm.Select(fixture.Records[0].Id);
                AssertStable("single");
                Assert.False(empty.IsVisible);
                Assert.True(Assert.IsType<Grid>(window.FindName("GalleryDetailsContent")).IsVisible);
                SaveLibraryDetailsPaneEvidence(window, "after", "single-min");

                vm.Select(fixture.Records[1].Id, control: true);
                AssertStable("multiple");
                Assert.True(empty.IsVisible);
                Assert.Equal(vm.SelectionText, title.Text);
                Assert.Equal(UiText.Format("LibrarySelection_Count", 2), title.Text);
                Assert.Equal(UiText.Get("Workspace.SelectOneHint"), MyCapture.App.Themes.KoreanWordWrap.Strip(hint.Text));
                Assert.False(Assert.IsType<Grid>(window.FindName("GalleryDetailsContent")).IsVisible);
                SaveLibraryDetailsPaneEvidence(window, "after", "multi-min");

                vm.ClearSelection();
                AssertStable("cleared");
                Assert.Equal(UiText.Get("Workspace.NoSelection"), title.Text);
                // Releasing the selection lets go of the preview image the pane showed.
                Assert.Null(Assert.IsType<Image>(window.FindName("SelectedDetailThumbnail")).Source);

                // Searching, filing and leaving the folder never move the pane.
                vm.SearchQuery = "no such capture anywhere";
                AssertStable("empty search result");
                Assert.True(empty.IsVisible);
                vm.SearchQuery = string.Empty;
                vm.NavigateTo(MyCapture.Core.Queue.CaptureFolderScope.ForFolder(fixture.Empty.Id));
                AssertStable("empty folder");
                vm.NavigateTo(MyCapture.Core.Queue.CaptureFolderScope.All);
                AssertStable("all captures");

                // A library with nothing in it still shows the pane.
                foreach (var record in fixture.Records) Assert.True(fixture.Queue.Remove(record.Id));
                vm.Refresh();
                AssertStable("empty library");
                Assert.True(vm.QueueIsEmpty);
                Assert.Equal(UiText.Get("Workspace.NoSelection"), title.Text);
                SaveLibraryDetailsPaneEvidence(window, "after", "library-empty-min");
            }
            finally { fixture.Window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryDetailsPane_FoldsOnlyByTheUsersChoiceAndRemembersIt() =>
        RunIsolated(nameof(LibraryDetailsPane_FoldsOnlyByTheUsersChoiceAndRemembersIt), () =>
        {
            using var culture = UiText.UseLanguage("ko-KR");
            Application application = CreateLibraryMotionApplication();
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-details-fold-");
            try
            {
                GalleryWindow window = fixture.Window;
                GalleryViewModel vm = fixture.ViewModel;
                var saved = new List<bool>();
                // A saved fold choice is restored at once and without motion.
                window.ConfigureDetailsPanePersistence(collapsed: true, saved.Add);
                window.Show();
                window.Width = window.MinWidth;
                window.Height = 760;
                SettleGalleryLayout(window);
                var panel = Assert.IsType<Border>(window.FindName("GalleryDetailsPanel"));
                var rail = Assert.IsType<Border>(window.FindName("GalleryDetailsRail"));
                var hide = Assert.IsType<Button>(window.FindName("HideDetailsButton"));
                var show = Assert.IsType<Button>(window.FindName("ShowDetailsButton"));
                Assert.False(window.IsDetailsPaneOpen);
                Assert.True(rail.IsVisible && !panel.IsVisible);
                Assert.True(show.IsVisible);
                Assert.Empty(saved);
                int foldedColumns = vm.ColumnCount;
                Assert.Equal(3, foldedColumns);
                SaveLibraryDetailsPaneEvidence(window, "after", "folded-min");

                // Selecting, clearing, searching and an empty library never unfold it.
                vm.Select(fixture.Records[0].Id);
                SettleGalleryLayout(window);
                Assert.False(window.IsDetailsPaneOpen);
                Assert.True(rail.IsVisible && !panel.IsVisible);
                vm.ClearSelection();
                vm.SearchQuery = "no such capture anywhere";
                SettleGalleryLayout(window);
                Assert.False(window.IsDetailsPaneOpen);
                vm.SearchQuery = string.Empty;
                Assert.Equal(foldedColumns, vm.ColumnCount);
                Assert.Empty(saved);

                // The rail's button unfolds it and the choice is saved.
                show.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, show));
                SettleGalleryLayout(window);
                Assert.True(window.IsDetailsPaneOpen && panel.IsVisible && !rail.IsVisible);
                Assert.Equal(new[] { false }, saved);
                Assert.Equal(2, vm.ColumnCount);

                // Selecting and clearing leave it open; the fold button folds it and the choice is saved.
                vm.Select(fixture.Records[0].Id);
                vm.ClearSelection();
                SettleGalleryLayout(window);
                Assert.True(window.IsDetailsPaneOpen);
                hide.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, hide));
                SettleGalleryLayout(window);
                Assert.False(window.IsDetailsPaneOpen);
                Assert.Equal(new[] { false, true }, saved);

                // A later run restores whatever was saved last.
                window.ConfigureDetailsPanePersistence(collapsed: false, saved.Add);
                SettleGalleryLayout(window);
                Assert.True(window.IsDetailsPaneOpen && panel.IsVisible);
                Assert.Equal(2, saved.Count);
            }
            finally { fixture.Window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryDetailsPane_MinimumWidthToolbarShowsEveryTextWhole() =>
        RunIsolated(nameof(LibraryDetailsPane_MinimumWidthToolbarShowsEveryTextWhole), () =>
        {
            Application application = CreateLibraryMotionApplication();
            var problems = new List<string>();
            try
            {
                foreach (string language in new[] { "ko-KR", "en-US" })
                {
                    using var culture = UiText.UseLanguage(language);
                    LibraryUxFixture fixture = CreateLibraryUxFixture("mc-details-toolbar-");
                    try
                    {
                        GalleryWindow window = fixture.Window;
                        GalleryViewModel vm = fixture.ViewModel;
                        window.Show();
                        window.Height = 760;
                        window.Width = window.MinWidth;
                        SettleGalleryLayout(window);
                        Assert.True(window.IsDetailsPaneOpen);
                        var count = Assert.IsType<TextBlock>(window.FindName("SelectionCountLabel"));
                        var bar = (FrameworkElement)window.FindName("LibraryContentCommandBar");
                        foreach (bool selected in new[] { false, true })
                        {
                            if (selected) vm.Select(fixture.Records[0].Id); else vm.ClearSelection();
                            SettleGalleryLayout(window);
                            string stage = $"{language}, selected {selected}, width {window.ActualWidth:0.#}";
                            // Where the toolbar first fits, for the report when it does not at the minimum.
                            double fitsFrom = double.NaN;
                            if (window.ContentToolbarShortfall > 0.5)
                            {
                                double original = window.Width;
                                for (double width = original; width <= 1044; width++)
                                {
                                    window.MinWidth = Math.Min(window.MinWidth, original);
                                    window.Width = width;
                                    SettleGalleryLayout(window);
                                    if (window.ContentToolbarShortfall <= 0.5) { fitsFrom = width; break; }
                                }
                                window.Width = original;
                                SettleGalleryLayout(window);
                            }
                            if (window.ContentToolbarShortfall > 0.5)
                            {
                                problems.Add($"{stage}: the toolbar overflows by {window.ContentToolbarShortfall:0.#} DIP; it first fits at {fitsFrom} DIP");
                                continue;
                            }
                            Assert.Equal(44, bar.ActualHeight, 1);
                            AssertNotTrimmed(count, stage);
                            AssertSortChoicesFit(window, stage);
                            foreach (Button button in ((Panel)window.FindName("SelectionCommands")).Children.OfType<Button>().Where(button => button.IsVisible))
                                AssertInsideClient(button, bar, stage);
                            SaveLibraryDetailsPaneEvidence(window, "after", $"toolbar-{language}-{(selected ? "selected" : "none")}-min");
                        }
                    }
                    finally { fixture.Window.CloseForExit(); }
                }
            }
            finally { application.Shutdown(); }
            Assert.True(problems.Count == 0, string.Join(" | ", problems));
        });

    /// <summary>Evidence only: the earlier behaviour (rail by default, no pane for an empty library) from the same window.</summary>
    [Fact]
    public Task LibraryDetailsPane_BeforeEvidence() =>
        RunIsolated(nameof(LibraryDetailsPane_BeforeEvidence), () =>
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_LIBPANE_EVIDENCE"))) return;
            using var culture = UiText.UseLanguage("ko-KR");
            Application application = CreateLibraryMotionApplication();
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-details-before-");
            try
            {
                GalleryWindow window = fixture.Window;
                GalleryViewModel vm = fixture.ViewModel;
                window.Show();
                window.Width = window.MinWidth;
                window.Height = 760;
                window.SetDetailsPaneOpen(false);
                SettleGalleryLayout(window);
                SaveLibraryDetailsPaneEvidence(window, "before", "none-min");
                vm.Select(fixture.Records[0].Id);
                SettleGalleryLayout(window);
                SaveLibraryDetailsPaneEvidence(window, "before", "single-min");
                window.SetDetailsPaneOpen(true);
                vm.Select(fixture.Records[1].Id, control: true);
                SettleGalleryLayout(window);
                SaveLibraryDetailsPaneEvidence(window, "before", "multi-min");
                vm.ClearSelection();
                foreach (var record in fixture.Records) fixture.Queue.Remove(record.Id);
                vm.Refresh();
                ((FrameworkElement)window.FindName("GalleryDetailsPanel")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("GalleryDetailsRail")).Visibility = Visibility.Collapsed;
                SettleGalleryLayout(window);
                SaveLibraryDetailsPaneEvidence(window, "before", "library-empty-min");
            }
            finally { fixture.Window.CloseForExit(); application.Shutdown(); }
        });

    /// <summary>
    /// Saves a picture when MYCAPTURE_LIBPANE_EVIDENCE names a folder: the "after" scenes go to its after folder.
    /// The "before" scenes of the earlier behaviour (folded rail by default, nothing for an empty library) are
    /// taken from the same window with the pane folded, by the "before" caller.
    /// </summary>
    private static void SaveLibraryDetailsPaneEvidence(GalleryWindow window, string state, string name)
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_LIBPANE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        string? previous = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_UX_EVIDENCE");
        Environment.SetEnvironmentVariable("MYCAPTURE_LIBRARY_UX_EVIDENCE", System.IO.Path.Combine(root, state));
        try { SaveLibraryUxEvidence(window, MyCapture.Core.Themes.AppTheme.Glass, $"{name}.png"); }
        finally { Environment.SetEnvironmentVariable("MYCAPTURE_LIBRARY_UX_EVIDENCE", previous); }
    }
}
