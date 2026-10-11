using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The library's single command bar, navigation pane, location header, folder cards and capture
/// drops, rendered from the real GalleryWindow BAML. Set MYCAPTURE_LIBRARY_UX_EVIDENCE to a
/// directory to also save PNG evidence; the caption strip on those images is drawn with the
/// colour ModernWindowChrome.ResolveChrome hands to DWM, because RenderTargetBitmap cannot
/// capture the native title bar.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryUx_CommandBarNavigationFolderCardsAndCaptureDrops() =>
        RunIsolated(nameof(LibraryUx_CommandBarNavigationFolderCardsAndCaptureDrops), () =>
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
                foreach ((string language, AppTheme theme) in new[]
                         {
                             ("ko-KR", AppTheme.Glass), ("ko-KR", AppTheme.HighContrast), ("ko-KR", AppTheme.Daylight),
                             ("en-US", AppTheme.Glass),
                         })
                {
                    using var scope = UiText.UseLanguage(language);
                    VerifyLibraryUx(language, theme);
                }
            }
            finally
            {
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
        });

    private sealed record LibraryUxFixture(GalleryWindow Window, GalleryViewModel ViewModel, CaptureQueue Queue,
        CaptureFolder Empty, CaptureFolder Design, IReadOnlyList<CaptureRecord> Records);

    private static LibraryUxFixture CreateLibraryUxFixture(string prefix)
    {
        string root = TestRecycleBin.CreateTempSubdirectory(prefix).FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        DateTimeOffset today = new DateTimeOffset(DateTime.Today).AddHours(12);
        CaptureFolder empty = queue.CreateFolderAsync("제품리뷰", createdAt: today.AddHours(-3)).GetAwaiter().GetResult();
        CaptureFolder design = queue.CreateFolderAsync("디자인 시안", createdAt: today.AddHours(-4)).GetAwaiter().GetResult();
        Color[] colors = [Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0xF5, 0x9E, 0x0B),
            Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x06, 0xB6, 0xD4)];
        var records = new List<CaptureRecord>();
        for (int index = 0; index < colors.Length; index++)
            records.Add(AddLibraryFolderCapture(queue, today.AddMinutes(-index * 40 - (index >= 4 ? 60 * 24 : 0)),
                $"Library sample {index + 1}", colors[index]));
        queue.MoveCapturesAsync([records[4].Id, records[5].Id], design.Id).GetAwaiter().GetResult();
        queue.TogglePin(records[1].Id);

        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var ocr = new GalleryCompactOcr();
        var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
            NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
        var vm = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96,
            clock: () => today);
        var window = new GalleryWindow(vm, controller,
            new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
            new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
            NullLoggerFactory.Instance, presenter, () => settings.Ocr, indexing,
            new GalleryMarkupPrivacy(), NullLogger.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
        };
        window.Closed += (_, _) => presenter.Dispose();
        FluidMotion.SetWindowEntrance(window, false);
        return new LibraryUxFixture(window, vm, queue, empty, design, records);
    }

    private static void VerifyLibraryUx(string language, AppTheme theme)
    {
        string label = $"{language}/{theme}";
        string file = $"{AppThemeNames.ToSetting(theme)}-{language}";
        ThemeService.Apply(theme);
        LibraryUxFixture f = CreateLibraryUxFixture("mc-library-ux-");
        GalleryWindow window = f.Window;
        GalleryViewModel vm = f.ViewModel;
        try
        {
            window.Show();
            var rowsList = (ListBox)window.FindName("RowsList");
            Button[] cards = PumpLibraryUntil(window, () => FolderCardButtons(rowsList).Length == 2
                && FolderCardButtons(rowsList).All(card => !((GalleryFolderCardViewModel)card.DataContext).HasPreview
                    || Descendants(card).OfType<Image>().Single().Source is not null), label + ": folder cards");
            var client = (FrameworkElement)window.Content;
            int rootColumns = vm.ColumnCount;

            // The title is the native caption only: no second title or description in the body.
            Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.IsVisible
                && (text.Text == UiText.Get("Text_37AD4F28D422") || text.Text == UiText.Get("Text_13036A090EBA")));
            var commandBar = (FrameworkElement)window.FindName("LibraryCommandBar");
            Assert.Equal(48, commandBar.ActualHeight, 1);
            Button capture = VisibleButtonNamed(window, UiText.Get("Text_2C20ADDA2018"));
            Assert.Same(window.FindResource("Button.Primary"), capture.Style);
            Assert.Contains("Ctrl+Shift", Assert.IsType<string>(capture.ToolTip), StringComparison.Ordinal);
            var search = (TextBox)window.FindName("SearchBox");
            TextBlock placeholder = Descendants(window).OfType<TextBlock>().Single(text => text.Text == vm.SearchPlaceholder);
            foreach (FrameworkElement element in new FrameworkElement[]
                     { capture, search, placeholder, (FrameworkElement)window.FindName("NewFolderButton"),
                       (FrameworkElement)window.FindName("SortBox"), (FrameworkElement)window.FindName("MoreCommandsButton") })
                AssertInsideClient(element, client, label);
            AssertNotTrimmed(placeholder, label);
            AssertSameRow(commandBar, capture, search, (FrameworkElement)window.FindName("SortBox"));

            // Navigation pane: icon items with counts instead of radio buttons and sentences.
            RadioButton[] views = Descendants(window).OfType<RadioButton>().Where(button => button.IsVisible).ToArray();
            Assert.Equal(["All", "Images", "Videos", "Pinned"], views.Select(view => (string)view.Tag));
            Assert.True(views[0].IsChecked);
            Assert.Equal(UiText.Format("LibraryUx_NavItemName", UiText.Get("LibraryUx_NavAll"), 6),
                AutomationProperties.GetName(views[0]));
            Assert.Equal(UiText.Format("LibraryUx_NavItemName", UiText.Get("LibraryUx_NavKept"), 1),
                AutomationProperties.GetName(views[3]));
            Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.IsVisible
                && (text.Text == vm.ResultCountText || text.Text == UiText.Get("Folder410.RetentionHint")));
            var tree = (TreeView)window.FindName("FolderTree");
            Assert.Equal(["디자인 시안", "제품리뷰"], vm.FolderRoots.Select(node => node.Name));
            Assert.True(tree.ActualHeight >= 120, label + ": folder list height");
            var dates = (Expander)window.FindName("DateSection");
            Assert.False(dates.IsExpanded);
            foreach (TextBlock text in Descendants((DependencyObject)window.FindName("GalleryFilterRail")).OfType<TextBlock>()
                         .Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)))
                AssertNotTrimmed(text, label);

            // Folder cards: the capture card frame, a different surface, and a readable name.
            Button designCard = cards.Single(card => ((GalleryFolderCardViewModel)card.DataContext).FolderId == f.Design.Id);
            Button emptyCard = cards.Single(card => ((GalleryFolderCardViewModel)card.DataContext).FolderId == f.Empty.Id);
            ListBoxItem firstTile = Descendants(rowsList).OfType<ListBoxItem>()
                .First(item => item.DataContext is GalleryItemViewModel tile && tile.Id == f.Records[0].Id);
            Border tileCard = Descendants(firstTile).OfType<Border>().First(border => border.Width == 244);
            Assert.Equal(tileCard.ActualWidth, designCard.ActualWidth, 1);
            Assert.Equal(tileCard.ActualHeight, designCard.ActualHeight, 1);
            Assert.Same(window.FindResource("Surface.Folder"), designCard.Background);
            Assert.NotEqual(((SolidColorBrush)window.FindResource("Surface.Raised")).Color,
                ((SolidColorBrush)window.FindResource("Surface.Folder")).Color);
            Assert.Equal(firstTile.TranslatePoint(new Point(), rowsList).X + 8,
                cards.Min(card => card.TranslatePoint(new Point(), rowsList).X), 1);
            foreach (Button card in cards)
                foreach (TextBlock text in Descendants(card).OfType<TextBlock>().Where(text => text.IsVisible))
                    AssertNotTrimmed(text, label);
            Assert.Equal(language == "ko-KR" ? "폴더 디자인 시안, 캡처 2개" : "Folder 디자인 시안, Captures: 2",
                AutomationProperties.GetName(designCard));
            SaveLibraryUxEvidence(window, theme, $"after-library-{file}.png");

            // Selecting swaps the right side of the same bar; nothing below moves.
            var workspace = (FrameworkElement)window.FindName("GalleryWorkspace");
            double workspaceTop = workspace.TranslatePoint(new Point(), client).Y;
            ScrollViewer scroll = Descendants(rowsList).OfType<ScrollViewer>().First();
            double viewport = scroll.ViewportHeight;
            vm.Select(f.Records[0].Id);
            SettleGalleryLayout(window);
            Assert.Equal(48, commandBar.ActualHeight, 1);
            Assert.Equal(workspaceTop, workspace.TranslatePoint(new Point(), client).Y, 1);
            Assert.Equal(viewport, scroll.ViewportHeight, 1);
            var selection = (FrameworkElement)window.FindName("SelectionCommands");
            Assert.True(selection.IsVisible);
            Assert.False(((FrameworkElement)window.FindName("LibraryCommands")).IsVisible);
            // Capture stays one click away as an icon while the selection owns the bar.
            Assert.Equal("CaptureRegionIconButton", VisibleButtonNamed(window, UiText.Get("Text_2C20ADDA2018")).Name);
            // One filled button: the selected capture's Edit in the details panel.
            Assert.Single(Descendants(window).OfType<Button>(), button => button.IsVisible
                && ReferenceEquals(button.Style, window.FindResource("Button.Primary")));
            foreach (Button button in Descendants(selection).OfType<Button>().Where(button => button.IsVisible))
                AssertInsideClient(button, client, label);
            AssertNotTrimmed((TextBlock)window.FindName("SelectionCountLabel"), label);
            SaveLibraryUxEvidence(window, theme, $"after-selection-{file}.png");
            var clear = (Button)window.FindName("ClearSelectionButton");
            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, clear));
            SettleGalleryLayout(window);
            Assert.False(vm.HasSelection);
            Assert.True(((FrameworkElement)window.FindName("LibraryCommands")).IsVisible);
            // Escape from a capture clears the selection the same way.
            vm.Select(f.Records[0].Id);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            };
            firstTile.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.False(vm.HasSelection);
            SettleGalleryLayout(window);

            // Dragging captures over a folder card highlights it and names the move.
            GalleryFolderDragPayload payload = window.CreateCaptureDragPayload([f.Records[0].Id, f.Records[1].Id]);
            vm.BeginCaptureDrag(payload.Captures);
            SettleGalleryLayout(window);
            Assert.All(cards, card => Assert.True(((GalleryFolderCardViewModel)card.DataContext).IsDropAvailable));
            TextBlock emptyName = Descendants(emptyCard).OfType<TextBlock>().First(text => text.Text == "제품리뷰");
            var hintLayer = (Canvas)window.FindName("DragHintLayer");
            Point pointer = emptyName.TranslatePoint(new Point(10, 8), hintLayer);
            GalleryDropPlan plan = window.PreviewFolderDrop(emptyName, payload, pointer);
            SettleGalleryLayout(window);
            Assert.Equal(GalleryDropAction.MoveCaptures, plan.Action);
            Assert.Equal(2, plan.Count);
            Assert.True(((GalleryFolderCardViewModel)emptyCard.DataContext).IsDropTarget);
            var hint = (Border)window.FindName("DragHint");
            Assert.True(hint.IsVisible);
            Assert.Equal(UiText.Format("LibraryUx_DropMoveTo", "제품리뷰", 2), ((TextBlock)window.FindName("DragHintText")).Text);
            AssertInsideClient(hint, client, label);
            SaveLibraryUxEvidence(window, theme, $"after-drag-{file}.png");
            // A tile is not a drop target: the highlight and hint go away.
            Assert.False(window.PreviewFolderDrop(firstTile, payload, pointer).CanDrop);
            Assert.False(((GalleryFolderCardViewModel)emptyCard.DataContext).IsDropTarget);
            Assert.False(hint.IsVisible);
            Assert.True(PumpUiTask(window, () => window.DropAsync(emptyName, payload)));
            // The rows are rebuilt because the dropped captures leave this view; the cards' view
            // models are kept, so read them rather than the recycled buttons.
            PumpLibraryUntil(window, () => vm.FindFolderCard(f.Empty.Id)!.CaptureCount == 2, label + ": moved into folder");
            Assert.All(f.Records.Take(2), record => Assert.Equal(f.Empty.Id, record.FolderId));
            Assert.Equal(UiText.Format("LibraryUx_MovedTo", "제품리뷰", 2), ((TextBlock)window.FindName("LibraryActionStatus")).Text);
            Assert.All(new[] { f.Empty.Id, f.Design.Id }, id => Assert.False(vm.FindFolderCard(id)!.IsDropAvailable));
            // Like File Explorer, the dropped captures leave the library root at once; the root
            // still reaches them through the card, so it reports no partial result.
            Guid[] dropped = [f.Records[0].Id, f.Records[1].Id];
            Assert.DoesNotContain(vm.Groups.SelectMany(group => group.Items), tile => dropped.Contains(tile.Id));
            Assert.DoesNotContain(Descendants(rowsList).OfType<ListBoxItem>(),
                item => item.DataContext is GalleryItemViewModel tile && dropped.Contains(tile.Id));
            Assert.False(vm.ShowResultCount);
            SaveLibraryUxEvidence(window, theme, $"after-drop-{file}.png");
            // The result clears itself; it never lingers in the command area.
            window.ExpireActionStatus();
            Assert.Equal(string.Empty, ((TextBlock)window.FindName("LibraryActionStatus")).Text);

            // "모든 항목" takes captures back out of their folder.
            var all = (RadioButton)window.FindName("AllLibraryFilter");
            GalleryDropPlan back = window.PreviewFolderDrop(all, payload, all.TranslatePoint(new Point(20, 10), hintLayer));
            Assert.Equal(GalleryDropAction.RemoveFromFolder, back.Action);
            Assert.True(vm.IsAllItemsDropTarget);
            Assert.Same(window.FindResource("Accent.Subtle"), all.Background);
            Assert.True(PumpUiTask(window, () => window.DropAsync(all, payload)));
            PumpLibraryUntil(window, () => f.Records[0].FolderId is null, label + ": out of folder");
            Assert.False(vm.IsAllItemsDropTarget);
            Assert.Contains(vm.Groups.SelectMany(group => group.Items), tile => tile.Id == f.Records[0].Id);

            // A subfolder in the folder about to open, shown there as a compact folder card.
            CaptureFolder final = PumpUiTask(window, () => f.Queue.CreateFolderAsync("최종안", f.Design.Id));
            vm.Refresh();
            SettleGalleryLayout(window);

            // Opening a folder: location header with the way back and the save-here chip.
            cards = FolderCardButtons(rowsList);
            designCard = cards.Single(card => ((GalleryFolderCardViewModel)card.DataContext).FolderId == f.Design.Id);
            designCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, designCard));
            var chip = (CheckBox)window.FindName("CurrentFolderCaptureCheck");
            PumpLibraryUntil(window, () => vm.SelectedScope == CaptureFolderScope.ForFolder(f.Design.Id) && chip.IsChecked == true,
                label + ": opened folder");
            Assert.True(((FrameworkElement)window.FindName("LocationHeader")).IsVisible);
            Assert.Equal([UiText.Get("LibraryUx_NavAll"), "디자인 시안"], vm.Breadcrumb.Select(step => step.Name));
            Assert.Equal(UiText.Get("LibraryUx_SaveHere"),
                Descendants(chip).OfType<TextBlock>().Single(text => text.Text == UiText.Get("LibraryUx_SaveHere")).Text);
            AssertNotTrimmed(Descendants(chip).OfType<TextBlock>().Single(text => text.Text == UiText.Get("LibraryUx_SaveHere")), label);
            AssertInsideClient(chip, client, label);
            Assert.Equal(UiText.Format("LibraryUx_SaveHereOn", "디자인 시안"), chip.ToolTip);
            Assert.All(views, view => Assert.False(view.IsChecked));
            Assert.Contains(FlattenFolderNodes(vm.FolderNodes), node => node.FolderId == f.Design.Id && node.IsSelected);
            // Inside a folder the captures use the compact card, in more columns than the root.
            ListBoxItem compactTile = PumpLibraryUntil(window, () => Descendants(rowsList).OfType<ListBoxItem>()
                    .FirstOrDefault(item => item.DataContext is GalleryItemViewModel tile && tile.Id == f.Records[4].Id),
                item => item is not null, label + ": compact tile");
            Border compactCard = Descendants(compactTile).OfType<Border>().First(border => border.Width == 148);
            Assert.Equal(180, compactCard.ActualHeight, 1);
            Assert.True(vm.ColumnCount > rootColumns, $"{label}: {vm.ColumnCount} compact columns, {rootColumns} at the root");
            Assert.All(vm.Rows.Skip(1), row => Assert.True(row.IsCompact));
            AssertInsideClient(compactTile, client, label);
            Assert.Equal(((GalleryItemViewModel)compactTile.DataContext).AccessibleName, AutomationProperties.GetName(compactTile));
            Button compactMore = Descendants(compactTile).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == UiText.Get("Text_5E8959D18390"));
            Assert.NotNull(compactMore.ContextMenu);
            AssertInsideClient(compactMore, client, label);
            Assert.False(Descendants(compactTile).OfType<Border>().Single(border => border.Name == "FolderBadge").IsVisible);
            // The subfolder card takes the same compact frame and column grid as the captures.
            Button subfolder = PumpLibraryUntil(window, () => FolderCardButtons(rowsList)
                    .SingleOrDefault(card => ((GalleryFolderCardViewModel)card.DataContext).FolderId == final.Id),
                card => card is not null, label + ": compact subfolder card");
            Assert.Equal(148, subfolder.ActualWidth, 1);
            Assert.Equal(180, subfolder.ActualHeight, 1);
            Assert.Equal(compactTile.TranslatePoint(new Point(), rowsList).X + 8, subfolder.TranslatePoint(new Point(), rowsList).X, 1);
            foreach (TextBlock text in Descendants(subfolder).OfType<TextBlock>().Where(text => text.IsVisible))
                AssertNotTrimmed(text, label);
            Assert.Equal(UiText.Format("LibraryFolder_CardAccessibleName", "최종안", UiText.Format("LibraryFolder_CardCount", 0)),
                AutomationProperties.GetName(subfolder));
            SaveLibraryUxEvidence(window, theme, $"after-folder-{file}.png");
            // The breadcrumb's first step returns to all items.
            Button home = Descendants((DependencyObject)window.FindName("LocationBreadcrumb")).OfType<Button>()
                .First(button => button.IsVisible);
            home.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, home));
            PumpLibraryUntil(window, () => vm.SelectedScope.IsAll, label + ": back to all");
            Assert.False(((FrameworkElement)window.FindName("LocationHeader")).IsVisible);
            Assert.True(views[0].IsChecked);
            Assert.Equal(rootColumns, vm.ColumnCount);

            // A search from the root still finds captures filed in folders and names their folder.
            search.Text = "Library sample 5";
            ListBoxItem found = PumpLibraryUntil(window, () => Descendants(rowsList).OfType<ListBoxItem>()
                    .FirstOrDefault(item => item.DataContext is GalleryItemViewModel tile && tile.Id == f.Records[4].Id),
                item => item is not null, label + ": search result");
            Assert.Contains(Descendants(found).OfType<Border>(), border => border.Width == 244);
            var badge = Descendants(found).OfType<Border>().Single(border => border.Name == "FolderBadge");
            Assert.True(badge.IsVisible);
            TextBlock badgeText = Descendants(badge).OfType<TextBlock>().Single();
            Assert.Equal("디자인 시안", badgeText.Text);
            AssertNotTrimmed(badgeText, label);
            Assert.EndsWith(UiText.Format("LibraryUx_InFolderAccessible", "디자인 시안"), AutomationProperties.GetName(found),
                StringComparison.Ordinal);
            SaveLibraryUxEvidence(window, theme, $"after-search-{file}.png");
            search.Text = string.Empty;
            PumpLibraryUntil(window, () => FolderCardButtons(rowsList).Length == 2, label + ": search cleared");

            // The collapsed "날짜" section opens on demand and selects a date like any other location.
            dates.IsExpanded = true;
            SettleGalleryLayout(window);
            var dateList = (ListBox)window.FindName("DateList");
            Assert.True(dateList.IsVisible);
            Assert.Equal(vm.FolderNodes.Count, dateList.Items.Count);
            dateList.SelectedIndex = 0;
            PumpLibraryUntil(window, () => vm.SelectedScope == vm.FolderNodes[0].Scope, label + ": date selected");
            Assert.True(((FrameworkElement)window.FindName("LocationHeader")).IsVisible);
            Assert.False(((FrameworkElement)window.FindName("CurrentFolderCaptureCheck")).IsVisible);
            Assert.All(views, view => Assert.False(view.IsChecked));
            dates.IsExpanded = false;

            // A media view applies to the whole library and leaves an open folder.
            vm.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
            SettleGalleryLayout(window);
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(views[1])).Select();
            PumpLibraryUntil(window, () => vm.SelectedScope.IsAll && vm.Filter == GalleryFilter.Images, label + ": images view");
            Assert.True(views[1].IsChecked);
            Assert.False(views[0].IsChecked);
            Assert.All(FlattenFolderNodes(vm.FolderNodes), node => Assert.False(node.IsSelected));
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(views[0])).Select();
            PumpLibraryUntil(window, () => vm.Filter == GalleryFilter.All, label + ": all items view");
            Assert.True(views[0].IsChecked);
        }
        finally
        {
            window.CloseForExit();
        }
    }

    /// <summary>Runs a window task with WPF's synchronization context, as a real drop event does.</summary>
    private static T PumpUiTask<T>(Window window, Func<Task<T>> start)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            Task<T> task = start();
            return PumpLibraryUntil(window, () => task, candidate => candidate!.IsCompleted, "UI task").GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static Button VisibleButtonNamed(DependencyObject root, string name) =>
        Descendants(root).OfType<Button>().Single(button => button.IsVisible && AutomationProperties.GetName(button) == name);

    private static void AssertSameRow(FrameworkElement bar, params FrameworkElement[] elements)
    {
        foreach (FrameworkElement element in elements)
        {
            double top = element.TranslatePoint(new Point(), bar).Y;
            Assert.InRange(top, 0, bar.ActualHeight);
            Assert.InRange(top + element.ActualHeight, 0, bar.ActualHeight + 0.5);
        }
    }

    private static void SaveLibraryUxEvidence(Window window, AppTheme theme, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_UX_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Drain(window);
        const double caption = 32;
        // The client area: the window's root visual, without the non-client frame.
        var clientRoot = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        double width = clientRoot.ActualWidth, height = clientRoot.ActualHeight;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale),
            (int)Math.Ceiling((height + caption) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        WindowChromePlan plan = ModernWindowChrome.ResolveChrome(theme, WorkspaceRole.Gallery, systemHighContrast: false);
        static Color FromColorRef(int value) => Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            // The native caption as DWM paints it, then WPF's black clear under the window background.
            context.DrawRectangle(new SolidColorBrush(FromColorRef(plan.CaptionColor!.Value)), null,
                new Rect(0, 0, width, caption));
            context.DrawText(new FormattedText(window.Title, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, new SolidColorBrush(FromColorRef(plan.TextColor!.Value)), scale),
                new Point(12, 9));
            context.DrawRectangle(Brushes.Black, null, new Rect(0, caption, width, height));
        }
        bitmap.Render(backdrop);
        var content = new DrawingVisual();
        using (DrawingContext context = content.RenderOpen())
            context.DrawRectangle(new VisualBrush(clientRoot)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.Fill,
            }, null, new Rect(0, caption, width, height));
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
