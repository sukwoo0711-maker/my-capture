using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
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
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    // Same requirement as the --selftest-ux-review compact gallery check: a 292-DIP tile
    // plus its 6-DIP margins must fit in the library viewport at the minimum window size.
    private const double GalleryCompactFullCardHeight = 304;

    [Fact]
    public Task GalleryCompact_MinimumWindowFitsFullCardWithFolderToolbarAndSelection() =>
        RunIsolated(nameof(GalleryCompact_MinimumWindowFitsFullCardWithFolderToolbarAndSelection), () =>
        {
            var application = CreateGalleryTestApplication();
            try
            {
                // English is the longest copy for the folder option, sort and selection actions.
                foreach (string language in new[] { "ko-KR", "en-US" })
                {
                    using var scope = UiText.UseLanguage(language);
                    VerifyCompactGallery(language);
                }
            }
            finally { application.Shutdown(); }
        });

    [Fact]
    public Task GalleryNavigation_CreationRevealsVirtualizedFolderAndAncestor() =>
        RunIsolated(nameof(GalleryNavigation_CreationRevealsVirtualizedFolderAndAncestor), () =>
        {
            var application = CreateGalleryTestApplication();
            try { VerifyCompactGallery("en-US", virtualizedRevealOnly: true); }
            finally { application.Shutdown(); }
        });

    private static Application CreateGalleryTestApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
        application.Resources["PinLabel"] = new PinLabelConverter();
        application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
        return application;
    }

    private static void VerifyCompactGallery(string language, bool virtualizedRevealOnly = false)
    {
        ThemeService.Apply(AppTheme.Midnight);
        string root = TestRecycleBin.CreateTempSubdirectory("mc-gallery-compact-").FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        CaptureFolder review = queue.CreateFolderAsync("Review").GetAwaiter().GetResult();
        BitmapSource sample = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 3 * 4], 4 * 4);
        byte[] png = ImageCodec.EncodePng(sample);
        DateTimeOffset now = DateTimeOffset.Now;
        for (int index = 0; index < 12; index++)
        {
            var record = new CaptureRecord
            {
                Width = sample.PixelWidth, Height = sample.PixelHeight, Title = $"Compact sample {index + 1}",
                CreatedAt = now.AddHours(-index * 7), OcrText = "Compact fixture", OcrContentRevision = 0,
            };
            queue.Add(record);
            string directory = queue.GetDirectory(record);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Thumbnail), png);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Rendered), png);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), png);
        }
        // Half of them are filed, so the folder view below lays out real compact cards.
        queue.MoveCapturesAsync(queue.Records.Take(6).Select(record => record.Id).ToArray(), review.Id).GetAwaiter().GetResult();
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var ocr = new GalleryCompactOcr();
        using var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
            NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
        var vm = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96);
        var window = new GalleryWindow(vm, controller,
            new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
            new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
            NullLoggerFactory.Instance, presenter, () => settings.Ocr, indexing,
            new GalleryMarkupPrivacy(), NullLogger.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
        };
        try
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            FluidMotion.SetWindowEntrance(window, false);
            window.Show();
            SettleGalleryLayout(window);
            if (virtualizedRevealOnly)
            {
                VerifyVirtualizedFolderReveal(window, queue, vm);
                return;
            }
            var client = (FrameworkElement)window.Content;
            var banner = (FrameworkElement)window.FindName("OcrCoverageBanner");
            Assert.False(banner.IsVisible, language + ": the complete-coverage fixture must not show the OCR banner");

            // Global capture/search and content sort/more occupy two stable command rows.
            var bar = (FrameworkElement)window.FindName("LibraryCommandBar");
            var contentBar = (FrameworkElement)window.FindName("LibraryContentCommandBar");
            var edit = (Button)window.FindName("EditSelectedButton");
            var sort = (ComboBox)window.FindName("SortBox");
            var searchBox = (TextBox)window.FindName("SearchBox");
            var searchField = (FrameworkElement)window.FindName("SearchField");
            var placeholder = (TextBlock)window.FindName("SearchPlaceholderText");
            var capture = (Button)window.FindName("CaptureRegionButton");
            var record = (Button)window.FindName("RecordRegionButton");
            Assert.Null(window.FindName("NewFolderButton"));
            var newFolder = (Button)window.FindName("NewFolderPaneButton");
            AssertInsideClient(newFolder, client, language);
            var more = (Button)window.FindName("MoreCommandsButton");
            foreach (FrameworkElement element in new FrameworkElement[] { capture, record, searchBox, placeholder, edit, more })
                AssertInsideClient(element, client, language);
            AssertSameRow(bar, capture, record, searchField);
            AssertSameRow(contentBar, edit, more);
            AssertSameRow(contentBar, sort, more);
            Assert.True(contentBar.TranslatePoint(new Point(), client).Y >= bar.TranslatePoint(new Point(), client).Y + bar.ActualHeight);
            // The root hint switches to compact text at minimum width; folder hints may still ellipsize.
            Assert.Equal(TextTrimming.CharacterEllipsis, placeholder.TextTrimming);
            AssertNotTrimmed(placeholder, language);
            Assert.Equal(vm.SearchToolTip, searchBox.ToolTip);
            foreach (TextBlock label in new[] { capture, record }.SelectMany(button => Descendants(button).OfType<TextBlock>()))
                AssertNotTrimmed(label, language);
            // Tab order follows the visual order: capture, search, then the library commands.
            AssertLeftToRight(client, record, searchField);
            AssertLeftToRight(client, edit, more);
            AssertLeftToRight(client, sort, more);
            double barHeight = bar.ActualHeight;
            double contentBarHeight = contentBar.ActualHeight;
            Rect searchBounds = searchField.TransformToAncestor(client).TransformBounds(new Rect(searchField.RenderSize));
            string searchHint = placeholder.Text;
            var workspace = (FrameworkElement)window.FindName("GalleryWorkspace");
            double workspaceTop = workspace.TranslatePoint(new Point(), client).Y;

            // A single selected image: the selection's commands replace the library commands in place.
            vm.Select(vm.Groups.SelectMany(group => group.Items).First().Id);
            SettleGalleryLayout(window);
            var selection = (Panel)window.FindName("SelectionCommands");
            Button[] visibleActions = selection.Children.OfType<Button>().Where(button => button.IsVisible).ToArray();
            SaveLibraryUxEvidence(window, AppTheme.Midnight, $"minimum-window-selection-{language}.png");
            AssertSelectionActionsReachable(window, compareVisible: false);
            var compare = (MenuItem)window.FindName("CompareSelectedMenu");
            Assert.False(compare.IsEnabled);
            Assert.Same(window.FindName("ClearSelectionButton"), visibleActions[0]);
            Assert.Same(window.FindName("KeepSelectedButton"), visibleActions[^1]);
            // Apps must reach the toolbar button's own menu, not the canvas menu.
            // WPF opens keyboard context menus during InputManager.PostProcessInput;
            // RaiseEvent alone only visits the routed handlers and cannot open that menu.
            more.Focus();
            bool contextMenuOpeningRaised = false;
            more.ContextMenuOpening += (_, _) => contextMenuOpeningRaised = true;
            var menuKeyDown = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Apps)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = more };
            InputManager.Current.ProcessInput(menuKeyDown);
            Assert.False(menuKeyDown.Handled);
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Apps)
                { RoutedEvent = Keyboard.KeyUpEvent, Source = more });
            SettleGalleryLayout(window);
            Assert.True(contextMenuOpeningRaised);
            Assert.True(more.ContextMenu!.IsOpen);
            Assert.Same(more, more.ContextMenu.PlacementTarget);
            Assert.Same(vm, more.ContextMenu.DataContext);
            more.ContextMenu.IsOpen = false;
            window.Dispatcher.Invoke(() => more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            SettleGalleryLayout(window);
            var overflow = more.ContextMenu!;
            Assert.True(overflow.IsOpen);
            Assert.Same(vm, overflow.DataContext);
            foreach (string name in new[] { "ContactSheetMenu", "BatchTagsMenu", "ReduceSelectedMenu" })
            {
                var item = (MenuItem)window.FindName(name);
                Assert.True(item.IsVisible, name);
                Assert.True(item.Focusable, name);
            }
            Assert.Same(vm.SingleSelectedTile, ((MenuItem)window.FindName("ReduceSelectedMenu")).CommandParameter);
            Assert.Equal(Visibility.Collapsed, ((MenuItem)window.FindName("GifSelectedMenu")).Visibility);
            overflow.IsOpen = false;
            Assert.Equal(searchHint, placeholder.Text);
            // More stays after them, so restoring deleted captures is reachable with a selection.
            foreach (FrameworkElement element in visibleActions.Append<FrameworkElement>(placeholder).Append(more))
                AssertInsideClient(element, client, language);
            AssertLeftToRight(client, visibleActions[^1], more);
            AssertSameRow(contentBar, visibleActions.Cast<FrameworkElement>().Append(more).ToArray());
            AssertSameRow(bar, capture, record, searchField);
            Assert.True(capture.IsVisible && record.IsVisible);
            Assert.Null(window.FindName("CaptureRegionIconButton"));
            Assert.Null(window.FindName("RecordRegionIconButton"));
            Assert.Equal(searchBounds, searchField.TransformToAncestor(client).TransformBounds(new Rect(searchField.RenderSize)));
            AssertLeftToRight(client, record, searchField);
            AssertNotTrimmed(placeholder, language);
            AssertNotTrimmed((TextBlock)window.FindName("SelectionCountLabel"), language);
            foreach (Button button in visibleActions.Skip(1))
                AssertNotTrimmed(Descendants(button).OfType<TextBlock>().Single(), language);
            Assert.Equal(barHeight, bar.ActualHeight, 1);
            Assert.Equal(contentBarHeight, contentBar.ActualHeight, 1);
            Assert.Equal(workspaceTop, workspace.TranslatePoint(new Point(), client).Y, 1);

            // Comparing two images shares the selection with PDF export and the earlier lanes.
            GalleryItemViewModel second = vm.Groups.SelectMany(group => group.Items).Skip(1).First();
            vm.Select(second.Id, control: true);
            SettleGalleryLayout(window);
            Assert.True(compare.IsEnabled);
            Assert.True(GalleryWindow.CompareCommand.CanExecute(null, window));
            AssertSelectionActionsReachable(window, compareVisible: true);
            foreach (Button button in selection.Children.OfType<Button>().Where(button => button.IsVisible))
                AssertInsideClient(button, client, language);
            vm.Select(second.Id, control: true);
            SettleGalleryLayout(window);
            Assert.False(compare.IsEnabled);
            Assert.False(GalleryWindow.CompareCommand.CanExecute(null, window));

            ListBox rows = (ListBox)window.FindName("RowsList");
            ScrollViewer scroll = Descendants(rows).OfType<ScrollViewer>().First();
            foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            {
                ThemeService.Apply(theme);
                SettleGalleryLayout(window);
                Assert.True(scroll.ViewportHeight >= GalleryCompactFullCardHeight,
                    $"{language}/{theme}: compact library viewport {scroll.ViewportHeight:0.##} DIP must fit a full {GalleryCompactFullCardHeight}-DIP card");
            }
            ThemeService.Apply(AppTheme.Glass);
            SaveLibraryUxEvidence(window, AppTheme.Glass, $"after-narrow-selection-glass-{language}.png");
            vm.ClearSelection();
            SettleGalleryLayout(window);
            AssertSearchPlaceholderFit(window, language);
            // Clearing selection restores the root hint; enough space restores its full form.
            WithHostedSearchCommandBar(window, host =>
            {
                ArrangeSearchHost(host, 1280 - 40 - 24);
                Assert.Equal(vm.SearchPlaceholder, placeholder.Text);
                AssertNotTrimmed(placeholder, language);
            });
            AssertSearchPlaceholderFit(window, language);
            int rootCardsInView = CardsFullyInView(rows, scroll);

            // Inside a folder the location header joins the content column. A date shows the same
            // header above full cards, so a full card must still fit under it.
            vm.NavigateTo(CaptureFolderScope.ForFolder(review.Id));
            SettleGalleryLayout(window);
            var check = (CheckBox)window.FindName("CurrentFolderCaptureCheck");
            AssertInsideClient(check, client, language);
            AssertNotTrimmed(Descendants(check).OfType<TextBlock>().Single(text => text.Text == UiText.Get("LibraryUx_SaveHere")), language);
            Assert.True(scroll.ViewportHeight >= GalleryCompactFullCardHeight,
                $"{language}: compact folder viewport {scroll.ViewportHeight:0.##} DIP must fit a full {GalleryCompactFullCardHeight}-DIP card");
            // The folder's own captures use the compact card: every card of the first row sits
            // inside the viewport with its overflow menu, and more of the folder is in view at once
            // than full cards show at the library root.
            Assert.True(vm.IsCompactLayout);
            GalleryTileRow compactRow = vm.Rows.OfType<GalleryTileRow>().First();
            Assert.True(compactRow.IsCompact);
            ListBoxItem[] compactTiles = Descendants(rows).OfType<ListBoxItem>()
                .Where(item => item.DataContext is GalleryItemViewModel tile && compactRow.Tiles.Contains(tile)).ToArray();
            Assert.Equal(compactRow.Tiles.Count, compactTiles.Length);
            foreach (ListBoxItem item in compactTiles)
            {
                double left = item.TranslatePoint(new Point(), scroll).X;
                Assert.True(left >= -0.5 && left + item.ActualWidth <= scroll.ViewportWidth + 0.5,
                    $"{language}: a compact card spans {left:0.#}..{left + item.ActualWidth:0.#} in a {scroll.ViewportWidth:0.#}-DIP viewport");
                Button[] tileActions = Descendants(item).OfType<Button>().Where(button => button.IsVisible).ToArray();
                Assert.Single(tileActions);
                Assert.Equal(UiText.Get("Text_5E8959D18390"), AutomationProperties.GetName(tileActions[0]));
                foreach (Button action in tileActions)
                    AssertInsideClient(action, client, language);
            }
            int folderCardsInView = CardsFullyInView(rows, scroll);
            Assert.True(folderCardsInView > rootCardsInView,
                $"{language}: {folderCardsInView} compact cards in view must exceed {rootCardsInView} full cards ({vm.ColumnCount} columns)");
            SaveLibraryUxEvidence(window, AppTheme.Glass, $"after-narrow-folder-glass-{language}.png");

            Assert.True(GalleryWindow.IsNavigationTextInput(searchBox));
            Assert.True(GalleryWindow.IsNavigationTextInput(new RichTextBox()));
            Assert.True(GalleryWindow.IsNavigationTextInput(new PasswordBox()));
            Assert.True(GalleryWindow.IsNavigationTextInput(new ComboBox { IsEditable = true }));
            Assert.False(GalleryWindow.IsNavigationTextInput(more));

            foreach (CaptureFolderScope location in new[] { CaptureFolderScope.All, CaptureFolderScope.ForFolder(review.Id) })
            {
                vm.NavigateTo(location);
                Guid? destination = vm.NewCaptureFolderId;
                Task<CaptureFolder> creation = window.Dispatcher.Invoke(() => vm.CreateFolderAsync("New folder", location));
                PumpLibraryUntil(window, () => creation.IsCompleted, language + ": folder created");
                CaptureFolder created = creation.GetAwaiter().GetResult();
                window.RevealCreatedFolder(created.Id);
                SettleGalleryLayout(window);
                Assert.Equal(location, vm.SelectedScope);
                Assert.Equal(destination, vm.NewCaptureFolderId);
                var tree = (TreeView)window.FindName("FolderTree");
                Assert.Equal(created.Id, Assert.IsType<GalleryFolderNode>(tree.SelectedItem).FolderId);
                Button card = Descendants(rows).OfType<Button>().Single(button =>
                    button.DataContext is GalleryFolderCardViewModel folder && folder.FolderId == created.Id);
                Assert.True(card.IsKeyboardFocused);
                AssertInsideClient(card, client, language);
                Assert.True(vm.FindFolderCard(created.Id)!.IsSelected);
                SaveLibraryUxEvidence(window, AppTheme.Glass,
                    $"after-create-{(location.IsAll ? "root" : "child")}-{language}.png");
                if (location.IsAll) continue;

                vm.NavigateTo(CaptureFolderScope.ForFolder(created.Id));
                SettleGalleryLayout(window);
                searchBox.Focus();
                searchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Back)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.Equal(created.Id, vm.SelectedScope.FolderId);
                rows.Focus();
                rows.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Back)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.Equal(review.Id, vm.SelectedScope.FolderId);
                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.XButton2)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Assert.Equal(created.Id, vm.SelectedScope.FolderId);
                var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent };
                window.RaiseEvent(rightClick);
                Assert.False(rightClick.Handled);
                Assert.Equal(created.Id, vm.SelectedScope.FolderId);
                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.XButton1)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Assert.Equal(review.Id, vm.SelectedScope.FolderId);
            }
        }
        finally
        {
            window.CloseForExit();
            ThemeService.Apply(AppTheme.Midnight);
        }
    }

    private static void VerifyVirtualizedFolderReveal(GalleryWindow window, CaptureQueue queue, GalleryViewModel vm)
    {
        for (int index = 0; index < 100; index++)
            queue.CreateFolderAsync($"Folder {index:000}").GetAwaiter().GetResult();
        CaptureFolder parent = queue.CreateFolderAsync("ZZZ parent").GetAwaiter().GetResult();
        for (int index = 0; index < 80; index++)
            queue.CreateFolderAsync($"Child {index:000}", parent.Id).GetAwaiter().GetResult();
        vm.ShowView(GalleryFilter.Images);
        vm.SearchQuery = "needle";
        var tree = (TreeView)window.FindName("FolderTree");
        SettleGalleryLayout(window);
        var scroll = Descendants(tree).OfType<ScrollViewer>().First();
        Assert.True(VirtualizingPanel.GetIsVirtualizing(tree));
        Assert.True(scroll.ScrollableHeight > 0);

        foreach (CaptureFolderScope creationScope in new[] { CaptureFolderScope.All, CaptureFolderScope.ForFolder(parent.Id) })
        {
            scroll.ScrollToTop();
            SettleGalleryLayout(window);
            Guid? destination = vm.NewCaptureFolderId;
            Task<CaptureFolder> creation = window.Dispatcher.Invoke(() => vm.CreateFolderAsync("ZZZ new folder", creationScope));
            PumpLibraryUntil(window, () => creation.IsCompleted, "virtualized folder created");
            CaptureFolder created = creation.GetAwaiter().GetResult();
            scroll.ScrollToTop();
            SettleGalleryLayout(window);
            GalleryFolderNode root = vm.FolderRoots.Single(node => node.FolderId == (created.ParentId ?? created.Id));
            Assert.Null(tree.ItemContainerGenerator.ContainerFromItem(root));
            Assert.DoesNotContain(vm.Rows, row => row is GalleryFolderRow);

            window.RevealCreatedFolder(created.Id);
            PumpLibraryUntil(window, () => Keyboard.FocusedElement is TreeViewItem item
                && item.DataContext is GalleryFolderNode node && node.FolderId == created.Id, "virtualized folder focused");
            SettleGalleryLayout(window);
            var selected = Assert.IsType<TreeViewItem>(Keyboard.FocusedElement);
            Assert.Equal(created.Id, Assert.IsType<GalleryFolderNode>(tree.SelectedItem).FolderId);
            Assert.True(selected.IsSelected);
            var header = Assert.IsAssignableFrom<FrameworkElement>(selected.Template.FindName("PART_Header", selected));
            var viewport = Descendants(scroll).OfType<ScrollContentPresenter>().First();
            Point top = header.TranslatePoint(new Point(), viewport);
            Assert.InRange(top.Y, -0.5, viewport.ActualHeight - header.ActualHeight + 0.5);
            Assert.True(vm.IsImagesView);
            Assert.Equal("needle", vm.SearchQuery);
            Assert.Equal(destination, vm.NewCaptureFolderId);
        }
    }

    /// <summary>Capture cards whose whole frame is inside the library viewport.</summary>
    private static int CardsFullyInView(ListBox rows, ScrollViewer scroll) =>
        Descendants(rows).OfType<ListBoxItem>().Where(item => item.DataContext is GalleryItemViewModel && item.IsVisible)
            .Count(item =>
            {
                Point topLeft = item.TranslatePoint(new Point(), scroll);
                return topLeft.X >= -0.5 && topLeft.Y >= -0.5 && topLeft.X + item.ActualWidth <= scroll.ViewportWidth + 0.5
                    && topLeft.Y + item.ActualHeight <= scroll.ViewportHeight + 0.5;
            });

    private static void SettleGalleryLayout(Window window)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void AssertInsideClient(FrameworkElement element, FrameworkElement client, string language)
    {
        Assert.True(element.IsVisible, $"{language}: {element.Name} must stay visible");
        Point topLeft = element.TranslatePoint(new Point(), client);
        Point bottomRight = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), client);
        Assert.InRange(topLeft.X, -0.5, client.ActualWidth + 0.5);
        Assert.InRange(topLeft.Y, -0.5, client.ActualHeight + 0.5);
        Assert.InRange(bottomRight.X, 0, client.ActualWidth + 0.5);
        Assert.InRange(bottomRight.Y, 0, client.ActualHeight + 0.5);
    }

    private static void AssertSelectionActionsReachable(GalleryWindow window, bool compareVisible)
    {
        Assert.True(((Button)window.FindName("ClearSelectionButton")).IsVisible);
        Assert.True(((Button)window.FindName("KeepSelectedButton")).IsVisible);
        Assert.Equal(compareVisible, ((MenuItem)window.FindName("CompareSelectedMenu")).IsEnabled);
        foreach (string menuName in new[] { "ArchiveOverflowMenu", "MoveOverflowMenu", "DeleteSelectedMenu" })
        {
            var menu = (MenuItem)window.FindName(menuName);
            Assert.Equal(Visibility.Visible, menu.Visibility);
            Assert.True(menu.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace((string)menu.Header));
        }
    }

    private static void AssertLeftToRight(FrameworkElement client, params FrameworkElement[] elements)
    {
        for (int index = 1; index < elements.Length; index++)
        {
            FrameworkElement left = elements[index - 1], right = elements[index];
            double leftEnd = left.TranslatePoint(new Point(left.ActualWidth, 0), client).X;
            Assert.True(leftEnd <= right.TranslatePoint(new Point(), client).X + 0.5,
                $"{left.Name} must end before {right.Name} starts");
        }
    }

    private static void AssertNotTrimmed(TextBlock text, string language)
    {
        var probe = new TextBlock
        {
            Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
            FontWeight = text.FontWeight, FontStyle = text.FontStyle, FontStretch = text.FontStretch,
            UseLayoutRounding = text.UseLayoutRounding,
        };
        // Window.Standard uses Display formatting, which measures narrower than the default.
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(text));
        VisualTreeHelper.SetRootDpi(probe, VisualTreeHelper.GetDpi(text));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(text.ActualWidth + 0.5 >= probe.DesiredSize.Width,
            $"{language}: '{text.Text}' needs {probe.DesiredSize.Width:0.#} DIP but has {text.ActualWidth:0.#} DIP");
    }

    private sealed class GalleryCompactOcr : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en", "ko"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Compact layout test must not run OCR.");
    }
}
