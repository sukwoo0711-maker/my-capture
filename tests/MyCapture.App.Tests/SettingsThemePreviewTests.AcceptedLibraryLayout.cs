using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using MyCapture.App.Gallery;
using MyCapture.App.Sources;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task AcceptedLibraryLayoutUsesCompactFoldersAndStableSelectionActions() =>
        RunIsolated(nameof(AcceptedLibraryLayoutUsesCompactFoldersAndStableSelectionActions), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                using var language = UiText.UseLanguage("ko-KR");
                foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight, AppTheme.HighContrast })
                {
                    ThemeService.Apply(theme);
                    var fixture = CreateLibraryUxFixture("mc-accepted-library-");
                    GalleryWindow window = fixture.Window;
                    GalleryViewModel model = fixture.ViewModel;
                    try
                    {
                        window.Width = 1100;
                        window.Height = 760;
                        window.Show();
                        model.Refresh();
                        model.ClearSelection();
                        SettleGalleryLayout(window);
                        var rows = (ListBox)window.FindName("RowsList");
                        PumpLibraryUntil(window, () => FolderCardButtons(rows).Length == 2, "compact folders");
                        PumpLibraryUntil(window, () => Descendants(rows).OfType<GalleryThumbnailImage>().Count(image => image.Source is not null && !image.IsFading) >= 4, "loaded thumbnails");
                        Assert.False(window.IsDetailsPaneOpen);
                        Assert.Equal(3, model.ColumnCount);
                        foreach (Button folder in FolderCardButtons(rows))
                        {
                            Assert.InRange(folder.ActualHeight, 32, 56);
                            Assert.Empty(Descendants(folder).OfType<Image>());
                        }
                        var commands = (FrameworkElement)window.FindName("SelectionCommands");
                        var edit = (Button)window.FindName("EditSelectedButton");
                        var preview = (Button)window.FindName("PreviewSelectedButton");
                        var keep = (Button)window.FindName("KeepSelectedButton");
                        double before = rows.TranslatePoint(new Point(), window).Y;
                        Assert.True(commands.IsVisible);
                        Assert.False(edit.IsEnabled);
                        Assert.False(preview.IsEnabled);
                        Assert.False(keep.IsEnabled);
                        var sort = (ComboBox)window.FindName("SortBox");
                        Assert.True(sort.IsVisible);
                        Assert.Equal(0, sort.SelectedIndex);
                        Assert.Contains(sort, Descendants((FrameworkElement)window.FindName("LibraryContentCommandBar")));
                        sort.SelectedIndex = 1;
                        SettleGalleryLayout(window);
                        Assert.Equal(1, model.SortIndex);
                        sort.SelectedIndex = 0;
                        SettleGalleryLayout(window);
                        SaveLibraryUxEvidence(window, theme, $"implemented-library-{theme}.png");
                        model.Select(fixture.Records[0].Id);
                        SettleGalleryLayout(window);
                        Assert.True(edit.IsEnabled);
                        Assert.True(preview.IsEnabled);
                        Assert.True(keep.IsEnabled);
                        Assert.Equal(before, rows.TranslatePoint(new Point(), window).Y);
                        CheckBox secondCheck = Descendants(rows).OfType<CheckBox>().Single(check =>
                            check.DataContext is GalleryItemViewModel tile && tile.Id == fixture.Records[1].Id);
                        secondCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        SettleGalleryLayout(window);
                        Assert.False(edit.IsEnabled);
                        Assert.False(preview.IsEnabled);
                        Assert.True(keep.IsEnabled);
                        Assert.Equal(before, rows.TranslatePoint(new Point(), window).Y);
                        Assert.Equal(2, model.SelectedTiles.Count);
                        foreach (var record in fixture.Records.Take(2))
                        {
                            ListBoxItem selected = Assert.Single(Descendants(rows).OfType<ListBoxItem>(),
                                item => item.DataContext is GalleryItemViewModel tile && tile.Id == record.Id);
                            Assert.True(((GalleryItemViewModel)selected.DataContext).IsSelected);
                            CheckBox check = Assert.Single(Descendants(selected).OfType<CheckBox>());
                            Assert.True(check.IsChecked);
                            var box = (Border)check.Template.FindName("SelectionBox", check);
                            var mark = (Path)check.Template.FindName("SelectionCheck", check);
                            Assert.Equal(((SolidColorBrush)window.FindResource("Accent.Default")).Color, ((SolidColorBrush)box.Background).Color);
                            Assert.Equal(((SolidColorBrush)window.FindResource("Text.OnAccent")).Color, ((SolidColorBrush)mark.Stroke).Color);
                            Assert.True(mark.IsVisible);
                            var edge = (Border)selected.Template.FindName("SelectedLayer", selected);
                            PumpLibraryUntil(window, () => edge.Opacity >= 0.99, "selected card outline");
                            Assert.Equal(((SolidColorBrush)box.Background).Color, ((SolidColorBrush)edge.BorderBrush).Color);
                            Assert.Equal(new Thickness(2), edge.BorderThickness);
                            AssertInsideClient(selected, (FrameworkElement)window.Content, theme.ToString());
                            TextBlock title = Descendants(selected).OfType<TextBlock>().Single(text => text.Name == "TileTitle");
                            TextBlock time = Descendants(selected).OfType<TextBlock>().Single(text => text.Name == "TileMetadataText");
                            Assert.Equal(title.TranslatePoint(new Point(), selected).X, time.TranslatePoint(new Point(), selected).X, 1);
                        }
                        SaveLibraryUxEvidence(window, theme, $"implemented-library-selection-{theme}.png");
                        fixture.Records[0].SourcePageUrl = "https://example.com/library-source";
                        model.Refresh();
                        SettleGalleryLayout(window);
                        Button sourceBadge = Descendants(rows).OfType<Button>().Single(button => button.Name == "SourceBadge"
                            && button.DataContext is GalleryItemViewModel tile && tile.Id == fixture.Records[0].Id);
                        Assert.True(sourceBadge.IsVisible);
                        int externalLaunches = 0;
                        GallerySourceBadge.Configure(sourceBadge, GallerySourceBadge.GetProvenance(sourceBadge), null, null,
                            new CaptureSourceActions(launch: _ => externalLaunches++));
                        sourceBadge.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        SettleGalleryLayout(window);
                        Assert.Equal(0, externalLaunches);
                        Assert.True(window.IsDetailsPaneOpen);
                        Assert.True(model.HasSingleSelection);
                        Assert.Same(model.FindTile(fixture.Records[0].Id), model.SingleSelectedTile);
                        PumpLibraryUntil(window, () => ((FrameworkElement)window.FindName("GalleryDetailsPanel")).Opacity >= 0.99, "details entrance");
                        SaveLibraryUxEvidence(window, theme, $"implemented-library-details-{theme}.png");
                        using (UiText.UseLanguage("en-US"))
                        {
                            window.Width = 980;
                            SettleGalleryLayout(window);
                            var commandBar = (FrameworkElement)window.FindName("LibraryContentCommandBar");
                            Rect selectionBounds = commands.TransformToAncestor(commandBar).TransformBounds(new Rect(commands.RenderSize));
                            Rect sortBounds = sort.TransformToAncestor(commandBar).TransformBounds(new Rect(sort.RenderSize));
                            Assert.False(selectionBounds.IntersectsWith(sortBounds));
                            AssertInsideClient(commands, commandBar, "narrow English selection");
                            AssertInsideClient(sort, commandBar, "narrow English sort");
                        }
                        window.Width = 1100;
                        SettleGalleryLayout(window);
                        model.NavigateTo(CaptureFolderScope.ForFolder(fixture.Design.Id));
                        model.Select(fixture.Records[4].Id);
                        SettleGalleryLayout(window);
                        Assert.False(fixture.Records[4].IsManuallyPinned);
                        keep.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        PumpLibraryUntil(window, () => fixture.Records[4].IsManuallyPinned && keep.IsEnabled, "kept folder capture");
                        keep.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        SettleGalleryLayout(window);
                        Assert.True(fixture.Records[4].IsManuallyPinned);
                        preview.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        SettleGalleryLayout(window);
                        Window imagePreview = Assert.Single(window.OwnedWindows.Cast<Window>());
                        Assert.NotNull(Assert.IsType<Image>(imagePreview.Content).Source);
                        imagePreview.Close();
                        Assert.True(window.IsVisible);
                        model.NavigateTo(CaptureFolderScope.All);
                        var created = PumpUiTask(window, () => model.CreateFolderAsync("선택 상태", CaptureFolderScope.All));
                        SettleGalleryLayout(window);
                        Button selectedFolder = FolderCardButtons(rows).Single(button => ((GalleryFolderCardViewModel)button.DataContext).FolderId == created.Id);
                        Assert.Equal(new Thickness(2), selectedFolder.BorderThickness);
                        Assert.Equal(((SolidColorBrush)window.FindResource("Accent.Default")).Color, ((SolidColorBrush)selectedFolder.BorderBrush).Color);
                        AssertVisibleSelectionMark(selectedFolder, window);
                        TreeViewItem selectedNode = Descendants((TreeView)window.FindName("FolderTree")).OfType<TreeViewItem>()
                            .Single(item => item.DataContext is GalleryFolderNode node && node.FolderId == created.Id);
                        AssertSelectedNavigationOutline(selectedNode, window);
                        SaveLibraryUxEvidence(window, theme, $"selection-folder-{theme}.png");
                        ((Expander)window.FindName("DateSection")).IsExpanded = true;
                        model.NavigateTo(model.FolderNodes[0].Scope);
                        SettleGalleryLayout(window);
                        ListBoxItem selectedDate = Descendants((ListBox)window.FindName("DateList")).OfType<ListBoxItem>().Single(item => item.IsSelected);
                        AssertSelectedNavigationOutline(selectedDate, window);
                        PumpLibraryUntil(window, () => Descendants((TreeView)window.FindName("FolderTree")).OfType<TreeViewItem>()
                            .All(item => !item.IsSelected && ((Border)item.Template.FindName("SelectedLayer", item)).Opacity <= 0.01), "previous folder selection cleared");
                        SaveLibraryUxEvidence(window, theme, $"selection-date-{theme}.png");
                    }
                    finally { window.CloseForExit(); }
                }
            }
            finally { application.Shutdown(); }
        });

    private static void AssertSelectedNavigationOutline(Control selected, Window window)
    {
        var border = (Border)selected.Template.FindName("SelectedLayer", selected);
        PumpLibraryUntil(window, () => border.Opacity >= 0.99, "selected navigation outline");
        Assert.Equal(new Thickness(2), border.BorderThickness);
        Assert.Equal(((SolidColorBrush)window.FindResource("Accent.Default")).Color, ((SolidColorBrush)border.BorderBrush).Color);
        AssertVisibleSelectionMark(selected, window);
    }

    private static void AssertVisibleSelectionMark(DependencyObject selected, Window window)
    {
        CheckBox check = Descendants(selected).OfType<CheckBox>().Single(control => control.IsVisible);
        Assert.True(check.IsChecked);
        var box = (Border)check.Template.FindName("SelectionBox", check);
        var mark = (Path)check.Template.FindName("SelectionCheck", check);
        Assert.True(mark.IsVisible);
        Assert.Equal(((SolidColorBrush)window.FindResource("Accent.Default")).Color, ((SolidColorBrush)box.Background).Color);
        Assert.Equal(((SolidColorBrush)window.FindResource("Text.OnAccent")).Color, ((SolidColorBrush)mark.Stroke).Color);
    }
}
