using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The library's column count follows the real content width from the window's minimum size
/// up: no card, and no command on a card, is ever cut off by the right edge of the library,
/// in the root, a date, an opened folder and a search. Set MYCAPTURE_LIBRARY_UX_EVIDENCE to
/// keep PNGs of the narrowest states.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryColumns_NoCardIsClippedFromTheMinimumWidthUp() =>
        RunIsolated(nameof(LibraryColumns_NoCardIsClippedFromTheMinimumWidthUp), () =>
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
                using var scope = UiText.UseLanguage("ko-KR");
                VerifyLibraryColumns();
            }
            finally
            {
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
        });

    private static void VerifyLibraryColumns()
    {
        ThemeService.Apply(AppTheme.Glass);
        LibraryUxFixture f = CreateLibraryUxFixture("mc-library-columns-");
        GalleryWindow window = f.Window;
        GalleryViewModel vm = f.ViewModel;
        // Enough cards that the first row of every view is full at the widest column count.
        DateTimeOffset today = new DateTimeOffset(DateTime.Today).AddHours(12);
        var filed = new List<Guid>();
        for (int index = 0; index < 22; index++)
        {
            CaptureRecord record = AddLibraryFolderCapture(f.Queue, today.AddMinutes(-300 - index * 3),
                $"Column sample {index + 1}", Color.FromRgb((byte)(40 + index * 9), 0x82, 0xF6));
            if (index < 11) filed.Add(record.Id);
        }
        f.Queue.MoveCapturesAsync(filed, f.Design.Id).GetAwaiter().GetResult();
        try
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.Show();
            var rowsList = (ListBox)window.FindName("RowsList");
            vm.Refresh();
            PumpLibraryUntil(window, () => FolderCardButtons(rowsList).Length == 2, "library columns: folder cards");
            ScrollViewer scroll = Descendants(rowsList).OfType<ScrollViewer>().First();

            // The minimum window with the details pane open fits one full card, not one and a half.
            Assert.Equal(1, vm.ColumnCount);
            AssertCardsInsideLibrary(rowsList, scroll, vm, "minimum root");
            SaveLibraryUxEvidence(window, AppTheme.Glass, "columns-minimum-root-glass-ko-KR.png");

            var views = new (string Name, Action Show)[]
            {
                ("root", () => vm.NavigateTo(CaptureFolderScope.All)),
                ("date", () => vm.NavigateTo(vm.FolderNodes[0].Scope)),
                ("folder", () => vm.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id))),
                ("search", () => { vm.NavigateTo(CaptureFolderScope.All); vm.SearchQuery = "sample"; }),
            };
            var client = (FrameworkElement)window.Content;
            var details = (FrameworkElement)window.FindName("GalleryDetailsPanel");
            var rail = (FrameworkElement)window.FindName("GalleryDetailsRail");
            var hide = (Button)window.FindName("HideDetailsButton");
            var showDetails = (Button)window.FindName("ShowDetailsButton");
            foreach (bool detailsOpen in new[] { true, false })
            {
                string pane = detailsOpen ? "details open" : "details hidden";
                if (!detailsOpen)
                {
                    // Hiding the pane from the keyboard leaves focus on the rail's button.
                    window.Width = window.MinWidth;
                    vm.NavigateTo(CaptureFolderScope.All);
                    // Keyboard focus needs the window to be foreground, which a test desktop may refuse.
                    window.Activate();
                    bool keyboard = hide.Focus() && hide.IsKeyboardFocused;
                    hide.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, hide));
                    SettleGalleryLayout(window);
                    Assert.False(window.IsDetailsPaneOpen);
                    Assert.False(details.IsVisible);
                    Assert.True(rail.IsVisible);
                    if (keyboard) Assert.True(showDetails.IsKeyboardFocused, "focus must move to the rail's show button");
                    AssertInsideClient(showDetails, client, pane);
                    Assert.Equal(UiText.Get("LibraryUx_DetailsShow"), AutomationProperties.GetName(showDetails));
                    // Two full cards fit the minimum window once the pane is hidden.
                    Assert.Equal(2, vm.ColumnCount);
                    AssertCardsInsideLibrary(rowsList, scroll, vm, $"minimum root, {pane}");
                    SaveLibraryUxEvidence(window, AppTheme.Glass, "columns-minimum-root-details-hidden-glass-ko-KR.png");
                }

                foreach ((string name, Action show) in views)
                {
                    window.Width = window.MinWidth;
                    show();
                    SettleGalleryLayout(window);
                    Assert.Equal(name == "folder", vm.IsCompactLayout);
                    AssertCardsInsideLibrary(rowsList, scroll, vm, $"{name} at the minimum width, {pane}");
                    if (detailsOpen && name is "folder" or "search")
                        SaveLibraryUxEvidence(window, AppTheme.Glass, $"columns-minimum-{name}-glass-ko-KR.png");
                    var columnsSeen = new HashSet<int>();
                    foreach (double width in LibrarySweepWidths(window, scroll, vm.IsCompactLayout))
                    {
                        window.Width = width;
                        SettleGalleryLayout(window);
                        AssertCardsInsideLibrary(rowsList, scroll, vm, $"{name} at {width:0.#} DIP, {pane}");
                        columnsSeen.Add(vm.ColumnCount);
                    }
                    // Every desktop this runs on is wide enough to cross the first column boundaries.
                    Assert.True(columnsSeen.Count >= 3, $"{name}, {pane}: only {string.Join(", ", columnsSeen)} columns were reached");
                    vm.SearchQuery = string.Empty;
                }
            }

            // The rail brings the pane back, with focus on its hide button, and the columns follow.
            window.Width = window.MinWidth;
            vm.NavigateTo(CaptureFolderScope.All);
            bool keyboardBack = showDetails.Focus() && showDetails.IsKeyboardFocused;
            showDetails.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, showDetails));
            SettleGalleryLayout(window);
            Assert.True(window.IsDetailsPaneOpen);
            Assert.True(details.IsVisible);
            Assert.False(rail.IsVisible);
            if (keyboardBack) Assert.True(hide.IsKeyboardFocused, "focus must move to the pane's hide button");
            // The pane is usable at once; its fade-in only softens the arrival and settles at full opacity.
            Assert.True(details.IsHitTestVisible && details.IsEnabled);
            PumpLibraryUntil(window, () => details.Opacity, opacity => opacity == 1.0, "details fade-in");
            Assert.Equal(1, vm.ColumnCount);
            AssertCardsInsideLibrary(rowsList, scroll, vm, "minimum root, details shown again");
        }
        finally
        {
            window.CloseForExit();
        }
    }

    /// <summary>
    /// The navigation pane's views are one radio group per window: choosing a view in one library
    /// window never unchecks the view of another, open or closed, and within a window the views
    /// still exclude each other.
    /// </summary>
    [Fact]
    public Task LibraryViews_AreGroupedPerWindow() =>
        RunIsolated(nameof(LibraryViews_AreGroupedPerWindow), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            using var scope = UiText.UseLanguage("ko-KR");
            ThemeService.Apply(AppTheme.Glass);
            LibraryUxFixture first = CreateLibraryUxFixture("mc-library-views-a-");
            LibraryUxFixture second = CreateLibraryUxFixture("mc-library-views-b-");
            LibraryUxFixture? third = null;
            try
            {
                first.Window.Show();
                second.Window.Show();
                SettleGalleryLayout(first.Window);
                SettleGalleryLayout(second.Window);
                RadioButton[] firstViews = LibraryViews(first.Window), secondViews = LibraryViews(second.Window);
                Assert.True(firstViews[0].IsChecked);
                Assert.True(secondViews[0].IsChecked);

                // Images in the second window: its own "all items" turns off, the first window keeps its view.
                Select(secondViews[1]);
                PumpLibraryUntil(second.Window, () => second.ViewModel.Filter == GalleryFilter.Images, "second window images view");
                Assert.Equal([false, true, false, false], secondViews.Select(view => view.IsChecked == true));
                Assert.Equal([true, false, false, false], firstViews.Select(view => view.IsChecked == true));
                Assert.Equal(GalleryFilter.All, first.ViewModel.Filter);

                // A closed library window's views do not reach the open one either.
                Select(firstViews[2]);
                PumpLibraryUntil(first.Window, () => first.ViewModel.Filter == GalleryFilter.Videos, "first window videos view");
                Assert.True(secondViews[1].IsChecked);
                first.Window.CloseForExit();
                Select(secondViews[3]);
                PumpLibraryUntil(second.Window, () => second.ViewModel.Filter == GalleryFilter.Pinned, "second window kept view");
                Assert.Equal([false, false, false, true], secondViews.Select(view => view.IsChecked == true));
                Select(secondViews[0]);
                PumpLibraryUntil(second.Window, () => second.ViewModel.Filter == GalleryFilter.All, "second window all items");
                Assert.Equal([true, false, false, false], secondViews.Select(view => view.IsChecked == true));

                // A library window built but not shown yet has no visual root, like a closed one. A
                // shared group name let the closed window's view change uncheck the new window's view.
                third = CreateLibraryUxFixture("mc-library-views-c-");
                var thirdAll = (RadioButton)third.Window.FindName("AllLibraryFilter");
                Drain(third.Window);
                Assert.True(thirdAll.IsChecked, "a new library window starts on all items");
                first.ViewModel.ShowView(GalleryFilter.Images);
                Drain(third.Window);
                Assert.True(thirdAll.IsChecked, "a closed library window unchecked a new window's view");
                third.Window.Show();
                SettleGalleryLayout(third.Window);
                Assert.Equal([true, false, false, false], LibraryViews(third.Window).Select(view => view.IsChecked == true));
            }
            finally
            {
                if (first.Window.IsLoaded) first.Window.CloseForExit();
                second.Window.CloseForExit();
                third?.Window.CloseForExit();
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }

            static RadioButton[] LibraryViews(Window window) =>
                Descendants((DependencyObject)window.FindName("GalleryFilterRail")).OfType<RadioButton>().ToArray();
            static void Select(RadioButton view) =>
                ((System.Windows.Automation.Provider.ISelectionItemProvider)new System.Windows.Automation.Peers.RadioButtonAutomationPeer(view)).Select();
        });

    /// <summary>
    /// Window widths from the minimum up: each column boundary, one DIP either side of it, and a
    /// coarse sweep in between. Widths the desktop cannot show are skipped.
    /// </summary>
    private static IEnumerable<double> LibrarySweepWidths(Window window, ScrollViewer scroll, bool compact)
    {
        double track = compact ? GalleryRowBuilder.CompactTileTrackWidth : GalleryRowBuilder.TileTrackWidth;
        int max = compact ? GalleryRowBuilder.CompactMaxColumns : GalleryRowBuilder.MaxColumns;
        // The library's scrolling area grows one DIP for every DIP the window grows.
        double chrome = window.ActualWidth - scroll.ActualWidth;
        double reserve = Math.Max(16, SystemParameters.VerticalScrollBarWidth);
        var widths = new SortedSet<double>();
        for (int columns = 1; columns <= max; columns++)
            foreach (double delta in new[] { -1d, 0, 1 })
                widths.Add(Math.Round(columns * track + reserve + chrome + delta));
        for (double width = window.MinWidth; width <= max * track + reserve + chrome + track; width += 37)
            widths.Add(width);
        foreach (double width in widths.Where(width => width >= window.MinWidth))
        {
            window.Width = width;
            window.UpdateLayout();
            if (Math.Abs(window.ActualWidth - width) > 0.5) yield break;
            yield return width;
        }
    }

    /// <summary>
    /// Every realised card, and every visible command on it, lies inside the library viewport,
    /// and the row holds as many cards as the width fits.
    /// </summary>
    private static void AssertCardsInsideLibrary(ListBox rowsList, ScrollViewer scroll, GalleryViewModel vm, string label)
    {
        FrameworkElement[] cards = Descendants(rowsList).OfType<FrameworkElement>()
            .Where(element => element.IsVisible && (element is ListBoxItem { DataContext: GalleryItemViewModel }
                || element is Button { DataContext: GalleryFolderCardViewModel }))
            .ToArray();
        Assert.NotEmpty(cards);
        foreach (FrameworkElement card in cards)
        {
            double left = card.TranslatePoint(new Point(), scroll).X;
            double right = card.TranslatePoint(new Point(card.ActualWidth, 0), scroll).X;
            Assert.True(left >= -0.5 && right <= scroll.ViewportWidth + 0.5,
                $"{label}: a card spans {left:0.#}..{right:0.#} in a {scroll.ViewportWidth:0.#}-DIP library ({vm.ColumnCount} columns)");
            foreach (Button command in Descendants(card).OfType<Button>().Where(button => button.IsVisible))
            {
                double commandRight = command.TranslatePoint(new Point(command.ActualWidth, 0), scroll).X;
                Assert.True(commandRight <= scroll.ViewportWidth + 0.5,
                    $"{label}: a card command ends at {commandRight:0.#} in a {scroll.ViewportWidth:0.#}-DIP library");
            }
        }

        double track = vm.IsCompactLayout ? GalleryRowBuilder.CompactTileTrackWidth : GalleryRowBuilder.TileTrackWidth;
        int max = vm.IsCompactLayout ? GalleryRowBuilder.CompactMaxColumns : GalleryRowBuilder.MaxColumns;
        Assert.True(vm.ColumnCount == max || (vm.ColumnCount + 1) * track > scroll.ViewportWidth - 2,
            $"{label}: {vm.ColumnCount} columns leave room for another card in {scroll.ViewportWidth:0.#} DIP");
        GalleryTileRow? fullest = vm.Rows.OfType<GalleryTileRow>().MaxBy(row => row.Tiles.Count);
        if (fullest is not null) Assert.True(fullest.Tiles.Count <= vm.ColumnCount, $"{label}: a row holds more cards than columns");
    }
}
