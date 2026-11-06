using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
        RunLibraryColumnTest(nameof(LibraryColumns_NoCardIsClippedFromTheMinimumWidthUp), constrainWindow: false);

    [Fact]
    public Task LibraryColumns_WideLayoutIsCoveredWhenTheDesktopCapsTheWindow() =>
        RunLibraryColumnTest(nameof(LibraryColumns_WideLayoutIsCoveredWhenTheDesktopCapsTheWindow), constrainWindow: true);

    [Fact]
    public Task LibraryColumns_ChangingViewsAfterMixedWindowAndClientWidthsKeepsCardsInside() =>
        RunLibraryColumnTest(nameof(LibraryColumns_ChangingViewsAfterMixedWindowAndClientWidthsKeepsCardsInside), constrainWindow: false, maximumWidth: 1044);

    private Task RunLibraryColumnTest(string testName, bool constrainWindow, double? maximumWidth = null) =>
        RunIsolated(testName, () =>
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
                VerifyLibraryColumns(constrainWindow, maximumWidth);
            }
            finally
            {
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
        });

    private static void VerifyLibraryColumns(bool constrainWindow, double? maximumWidth)
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
        // The step a failure, or a timeout of the isolated process, is reported against.
        string stage = "minimum window";
        var elapsed = Stopwatch.StartNew();
        try
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            if (constrainWindow) window.MaxWidth = window.MinWidth;
            if (maximumWidth.HasValue) window.MaxWidth = maximumWidth.Value;
            window.Show();
            window.SetDetailsPaneOpen(true);
            var rowsList = (ListBox)window.FindName("RowsList");
            vm.Refresh();
            PumpLibraryUntil(window, () => FolderCardButtons(rowsList).Length == 2, "library columns: folder cards");
            ScrollViewer scroll = Descendants(rowsList).OfType<ScrollViewer>().First();
            RecordIsolatedProgress($"library columns shown; {LibraryLayoutState(window, vm)}");

            // The minimum window with the details pane open fits two full cards that share its width.
            stage = "minimum root";
            Assert.True(vm.ColumnCount == 2,
                $"Minimum root needs two columns; actual {vm.ColumnCount}, window {window.ActualWidth}, client {((FrameworkElement)window.Content).ActualWidth}, rows {rowsList.ActualWidth}, scroll {scroll.ActualWidth}, viewport {scroll.ViewportWidth}");
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
                    stage = "hiding details at the minimum width";
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
                    // Hiding details leaves enough space for a third card.
                    Assert.Equal(3, vm.ColumnCount);
                    AssertCardsInsideLibrary(rowsList, scroll, vm, $"minimum root, {pane}");
                    SaveLibraryUxEvidence(window, AppTheme.Glass, "columns-minimum-root-details-hidden-glass-ko-KR.png");
                }

                foreach ((string name, Action show) in views)
                {
                    stage = $"{name} at the minimum width, {pane}";
                    window.Width = window.MinWidth;
                    show();
                    SettleGalleryLayout(window);
                    Assert.Equal(name == "folder", vm.IsCompactLayout);
                    AssertCardsInsideLibrary(rowsList, scroll, vm, stage);
                    if (detailsOpen && name is "folder" or "search")
                        SaveLibraryUxEvidence(window, AppTheme.Glass, $"columns-minimum-{name}-glass-ko-KR.png");
                    var columnsSeen = new HashSet<int>();
                    double windowChrome = window.ActualWidth - client.ActualWidth;
                    double originalClientWidth = client.Width;
                    var sweep = Stopwatch.StartNew();
                    int windowWidths = 0, virtualWidths = 0;
                    // Where the sweep spends its time: changing the width, settling layout, checking the cards.
                    long resizing = 0, settling = 0, checking = 0, mark = 0;
                    long Lap()
                    {
                        long now = sweep.ElapsedMilliseconds, lap = now - mark;
                        mark = now;
                        return lap;
                    }
                    (long Milliseconds, double Width) slowest = (0, 0);
                    try
                    {
                        bool virtualLayout = false;
                        foreach (double width in LibrarySweepWidths(window, scroll, vm).ToArray())
                        {
                            long stepStarted = mark = sweep.ElapsedMilliseconds;
                            stage = $"{name} at {width:0.#} DIP, {pane}";
                            if (!virtualLayout)
                            {
                                window.Width = width;
                                resizing += Lap();
                                SettleGalleryLayout(window);
                                settling += Lap();
                                virtualLayout = Math.Abs(window.ActualWidth - width) > 0.5;
                            }
                            string layout = "window";
                            if (virtualLayout)
                            {
                                // Hosted desktops can cap the HWND before the next column boundary.
                                // Exercise the same loaded content, SizeChanged handler and row panel
                                // at the remaining logical widths without changing the desktop.
                                double expectedClientWidth = width - windowChrome;
                                client.Width = expectedClientWidth;
                                resizing += Lap();
                                SettleGalleryLayout(window);
                                settling += Lap();
                                Assert.InRange(client.ActualWidth, expectedClientWidth - 0.5, expectedClientWidth + 0.5);
                                layout = "virtual client";
                            }
                            stage = $"{name} at {width:0.#} DIP ({layout}), {pane}";
                            AssertCardsInsideLibrary(rowsList, scroll, vm, stage);
                            checking += Lap();
                            columnsSeen.Add(vm.ColumnCount);
                            if (virtualLayout) virtualWidths++; else windowWidths++;
                            long step = sweep.ElapsedMilliseconds - stepStarted;
                            if (step > slowest.Milliseconds) slowest = (step, width);
                        }
                    }
                    finally
                    {
                        client.Width = originalClientWidth;
                        SettleGalleryLayout(window);
                    }
                    // A timeout of the isolated process then names the last finished sweep and its cost.
                    RecordIsolatedProgress($"library columns {name}, {pane}: {windowWidths} window and {virtualWidths} virtual client widths "
                        + $"in {sweep.ElapsedMilliseconds} ms (resize {resizing}, settle {settling}, check {checking}), "
                        + $"slowest {slowest.Milliseconds} ms at {slowest.Width:0.#} DIP, columns {string.Join(", ", columnsSeen.Order())}");
                    // Keep all column-boundary coverage even on desktops narrower than the sweep.
                    stage = $"{name}, {pane}: column coverage";
                    Assert.True(columnsSeen.Count >= 3, $"{name}, {pane}: only {string.Join(", ", columnsSeen)} columns were reached");
                    vm.SearchQuery = string.Empty;
                }
            }

            // The rail brings the pane back, with focus on its hide button, and the columns follow.
            stage = "details shown again at the minimum width";
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
            Assert.Equal(2, vm.ColumnCount);
            AssertCardsInsideLibrary(rowsList, scroll, vm, "minimum root, details shown again");

            // The default height shows whole rows; render the widths the owner compares, with the
            // details pane shown and hidden. Only kept when MYCAPTURE_LIBRARY_UX_EVIDENCE is set.
            window.Height = 760;
            vm.Select(f.Records[0].Id);
            foreach (bool open in new[] { true, false })
            {
                window.SetDetailsPaneOpen(open);
                foreach (double width in new[] { window.MinWidth, 1200, 1600 })
                {
                    stage = $"evidence {width:0} DIP, details {(open ? "shown" : "hidden")}";
                    window.Width = width;
                    SettleGalleryLayout(window);
                    if (Math.Abs(window.ActualWidth - width) > 0.5) continue;
                    AssertCardsInsideLibrary(rowsList, scroll, vm, stage);
                    SaveLibraryUxEvidence(window, AppTheme.Glass, $"layout-{width:0}-{(open ? "details" : "no-details")}-glass-ko-KR.png");
                }
            }
            window.SetDetailsPaneOpen(true);
        }
        catch (Exception error) when (CaptureLibraryLayout(error, window, vm, out string layout))
        {
            throw LibraryStepFailure(stage, elapsed, layout, error);
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
    /// coarse sweep in between. The caller covers desktop-capped widths in the loaded content.
    /// </summary>
    private static IEnumerable<double> LibrarySweepWidths(Window window, ScrollViewer scroll, GalleryViewModel vm)
    {
        bool compact = vm.IsCompactLayout;
        // The layout's own minimum: the text size and a filed search result can raise the base track.
        double track = vm.MinimumTrackWidth;
        int max = compact ? GalleryRowBuilder.CompactMaxColumns : GalleryRowBuilder.MaxColumns;
        // The library's scrolling area grows one DIP for every DIP the window grows; the themed
        // vertical scrollbar track is always reserved when the column count is chosen.
        double chrome = window.ActualWidth - scroll.ActualWidth;
        double reserve = scroll.ActualWidth - LibraryUsableWidth(scroll);
        var widths = new SortedSet<double>();
        for (int columns = 1; columns <= max; columns++)
            foreach (double delta in new[] { -1d, 0, 1 })
                widths.Add(Math.Round(columns * track + reserve + chrome + delta));
        for (double width = window.MinWidth; width <= max * track + reserve + chrome + track; width += 37)
            widths.Add(width);
        foreach (double width in widths.Where(width => width >= window.MinWidth))
            yield return width;
    }

    private static double LibraryUsableWidth(ScrollViewer scroll)
    {
        // The default Windows scrollbar is 17 DIP; a supplied theme can set another width.
        // Measure the loaded template and reserve the same track when it is collapsed.
        var bar = Assert.IsAssignableFrom<ScrollBar>(scroll.Template.FindName("PART_VerticalScrollBar", scroll));
        double reserve = bar.ActualWidth > 0 ? bar.ActualWidth
            : !double.IsNaN(bar.Width) && bar.Width > 0 ? Math.Max(bar.Width, bar.MinWidth) : SystemParameters.VerticalScrollBarWidth;
        return scroll.ActualWidth - scroll.Padding.Left - scroll.Padding.Right - reserve;
    }

    /// <summary>
    /// Every realised card, and every visible command on it, lies inside the library viewport;
    /// cards are never narrower than the minimum track allows and keep their height for their
    /// width; and a full row reaches the right edge of the library, leaving no empty band beyond
    /// the card's own margin and layout rounding.
    /// </summary>
    private static void AssertCardsInsideLibrary(ListBox rowsList, ScrollViewer scroll, GalleryViewModel vm, string label)
    {
        bool compact = vm.IsCompactLayout;
        double track = vm.MinimumTrackWidth;
        int max = compact ? GalleryRowBuilder.CompactMaxColumns : GalleryRowBuilder.MaxColumns;
        (double baseWidth, double baseHeight, double slope) = compact ? (148d, 160d, 0.625) : (244d, 220d, 0.625);
        // The visible card: a capture card inside its selection outline, or a folder card button.
        FrameworkElement[] cards = Descendants(rowsList).OfType<FrameworkElement>()
            .Where(element => element.IsVisible && (element is Border { Name: "TileCard" or "CompactTileCard" }
                || element is Button { DataContext: GalleryFolderCardViewModel }))
            .ToArray();
        Assert.NotEmpty(cards);
        foreach (FrameworkElement card in cards)
        {
            double left = card.TranslatePoint(new Point(), scroll).X;
            double right = card.TranslatePoint(new Point(card.ActualWidth, 0), scroll).X;
            Assert.True(left >= -0.5 && right <= scroll.ViewportWidth + 0.5,
                $"{label}: a card spans {left:0.#}..{right:0.#} in a {scroll.ViewportWidth:0.#}-DIP library ({vm.ColumnCount} columns)");
            Assert.True(card.ActualWidth >= track - GalleryRowBuilder.CardInset - 1,
                $"{label}: a {card.ActualWidth:0.#}-DIP card is narrower than the {track - GalleryRowBuilder.CardInset}-DIP minimum");
            double expectedHeight = card is Button { DataContext: GalleryFolderCardViewModel }
                ? 40 : GalleryCardRowPanel.CardHeightFor(card.ActualWidth, baseWidth, baseHeight, slope);
            Assert.Equal(expectedHeight, card.ActualHeight, 1.5);
            foreach (Button command in Descendants(card).OfType<Button>().Where(button => button.IsVisible))
            {
                double commandRight = command.TranslatePoint(new Point(command.ActualWidth, 0), scroll).X;
                Assert.True(commandRight <= scroll.ViewportWidth + 0.5,
                    $"{label}: a card command ends at {commandRight:0.#} in a {scroll.ViewportWidth:0.#}-DIP library");
            }
        }

        // Rows of the full column count end one card margin (8 DIPs) short of the right edge.
        foreach (IGrouping<double, FrameworkElement> row in cards.GroupBy(card => Math.Round(card.TranslatePoint(new Point(), scroll).Y)))
        {
            double first = row.Min(card => card.TranslatePoint(new Point(), scroll).X);
            Assert.Equal(GalleryRowBuilder.CardInset / 2, first, 1);
            if (row.Count() < vm.ColumnCount) continue;
            double end = row.Max(card => card.TranslatePoint(new Point(card.ActualWidth, 0), scroll).X) + GalleryRowBuilder.CardInset / 2;
            Assert.True(Math.Abs(scroll.ViewportWidth - end) < 2,
                $"{label}: a full row of {row.Count()} cards ends at {end:0.#} in a {scroll.ViewportWidth:0.#}-DIP library");
        }

        // As many columns as minimum tracks fit beside the reserved scrollbar, and not one more.
        double usable = LibraryUsableWidth(scroll);
        Assert.True(vm.ColumnCount * track <= usable + 0.5, $"{label}: {vm.ColumnCount} columns do not fit {usable:0.#} DIP (scroll {scroll.ActualWidth}, viewport {scroll.ViewportWidth})");
        Assert.True(vm.ColumnCount == max || (vm.ColumnCount + 1) * track > usable - 0.5,
            $"{label}: {vm.ColumnCount} columns leave room for another card in {usable:0.#} DIP");
        GalleryTileRow? fullest = vm.Rows.OfType<GalleryTileRow>().MaxBy(row => row.Tiles.Count);
        if (fullest is not null) Assert.True(fullest.Tiles.Count <= vm.ColumnCount, $"{label}: a row holds more cards than columns");
    }

    /// <summary>A library assertion failure whose message already carries the layout facts.</summary>
    private sealed class LibraryLayoutFailure(string message, Exception? inner = null)
        : Xunit.Sdk.XunitException(message, inner);

    /// <summary>
    /// Exception filter for the library layout tests. It runs before any inner <c>finally</c>
    /// restores a width, so the layout it keeps is the one the assertion saw; the caller rethrows
    /// the failure with it. A failure that already carries the layout passes through unchanged.
    /// </summary>
    private static bool CaptureLibraryLayout(Exception error, GalleryWindow window, GalleryViewModel vm, out string layout)
    {
        layout = error is LibraryLayoutFailure ? string.Empty : LibraryLayoutState(window, vm);
        return error is not LibraryLayoutFailure;
    }

    /// <summary>
    /// A failed library step with the layout it left. xUnit prints an assertion without its inner
    /// exception, so the original failure, with its message and stack, is part of this message.
    /// </summary>
    private static LibraryLayoutFailure LibraryStepFailure(string stage, Stopwatch elapsed, string layout, Exception error) =>
        new($"{stage}, {elapsed.ElapsedMilliseconds} ms into the test. Layout at the failure: {layout}"
            + $"{Environment.NewLine}Original failure: {error}", error);

    /// <summary>
    /// Requires a resize to move the column count the expected way. On a failure the message keeps
    /// the layout as the assertion saw it, then lets the dispatcher settle layout a few more times and
    /// keeps that layout too, so a resize the dispatcher had not processed yet is told apart from a
    /// width that never allowed another count. The assertion fails in both cases.
    /// </summary>
    private static void AssertColumnCountMoved(GalleryWindow window, GalleryViewModel vm, int before, bool fewer,
        string stage, WindowTrackSizeScope? tracking = null)
    {
        int after = vm.ColumnCount;
        if (fewer ? after < before : after > before) return;
        string asserted = LibraryLayoutState(window, vm, before, tracking);
        var settling = Stopwatch.StartNew();
        int passes = 0;
        while (passes < 5 && vm.ColumnCount == after)
        {
            SettleGalleryLayout(window);
            passes++;
        }
        string later = vm.ColumnCount == after
            ? $"still {after} after {passes} more settle passes ({settling.ElapsedMilliseconds} ms)"
            : $"{vm.ColumnCount} after {passes} more settle passes ({settling.ElapsedMilliseconds} ms)";
        throw new LibraryLayoutFailure($"{stage}: the column count must {(fewer ? "fall below" : "rise above")} {before} "
            + $"but is {after}; {later}.{Environment.NewLine}As asserted: {asserted}{Environment.NewLine}"
            + $"After settling: {LibraryLayoutState(window, vm, before, tracking)}");
    }

    /// <summary>
    /// The facts a library resize failure needs, read without changing layout or focus: the desktop,
    /// work area and DPI the window was given; its requested, actual, minimum and maximum sizes; the
    /// library's scrolling width; the column count before and now against the count that width allows;
    /// whether layout, the dispatcher and the product's focus restoration had settled; and where focus is.
    /// </summary>
    private static string LibraryLayoutState(GalleryWindow window, GalleryViewModel vm, int? columnsBefore = null,
        WindowTrackSizeScope? tracking = null)
    {
        var facts = new List<string>();
        void Add(string name, Func<string> read)
        {
            // An unreadable fact is reported as such; it never replaces the failure it explains.
            try { facts.Add($"{name} {read()}"); }
            catch (Exception error) { facts.Add($"{name} <{error.GetType().Name}: {error.Message}>"); }
        }

        nint hwnd = new WindowInteropHelper(window).Handle;
        var rows = window.FindName("RowsList") as ListBox;
        ScrollViewer? scroll = rows is null ? null : Descendants(rows).OfType<ScrollViewer>().FirstOrDefault();
        Add("desktop", () => $"small {Environment.GetEnvironmentVariable(WindowTrackSizeScope.SmallDesktopEnvironmentVariable) ?? "unset"}, "
            + $"work area {LibraryRect(SystemParameters.WorkArea)}, virtual screen {SystemParameters.VirtualScreenWidth:0.#}x"
            + $"{SystemParameters.VirtualScreenHeight:0.#}, max track {SystemParameters.MaximumWindowTrackWidth:0.#}x"
            + $"{SystemParameters.MaximumWindowTrackHeight:0.#} DIP");
        Add("monitor", () => LibraryMonitor(hwnd));
        Add("dpi", () => $"{VisualTreeHelper.GetDpi(window).PixelsPerInchX:0.#} layout"
            + (hwnd == 0 ? ", no window handle" : $", {LibraryUser32.GetDpiForWindow(hwnd)} native, {LibraryDpiAwareness(hwnd)}"));
        Add("window", () => $"requested {window.Width:0.#}x{window.Height:0.#}, actual {window.ActualWidth:0.#}x{window.ActualHeight:0.#}, "
            + $"min {window.MinWidth:0.#}x{window.MinHeight:0.#} DIP, max width {LibraryLimit(window.MaxWidth)}, "
            + $"max height {LibraryLimit(window.MaxHeight)}, {window.WindowState}, "
            + (hwnd != 0 && LibraryUser32.GetWindowRect(hwnd, out LibraryNativeRect frame)
                ? $"native {frame.Right - frame.Left}x{frame.Bottom - frame.Top} px at {frame.Left},{frame.Top}"
                : "no native frame"));
        if (tracking is not null) Add("track size scope", () => $"{tracking.MessageCount} WM_GETMINMAXINFO");
        Add("client", () => window.Content is FrameworkElement client
            ? $"{client.ActualWidth:0.#} (width {client.Width:0.#})" : "none");
        Add("details", () => $"{(window.IsDetailsPaneOpen ? "open" : "hidden")} "
            + $"{((FrameworkElement)window.FindName("GalleryDetailsPanel")).ActualWidth:0.#}");
        Add("rows list", () => rows is null ? "none" : $"{rows.ActualWidth:0.#}");
        Add("scroll", () => scroll is null ? "none"
            : $"{scroll.ActualWidth:0.#}, viewport {scroll.ViewportWidth:0.#}x{scroll.ViewportHeight:0.#}, "
            + $"extent {scroll.ExtentWidth:0.#}x{scroll.ExtentHeight:0.#}, offset {scroll.VerticalOffset:0.#} of "
            + $"{scroll.ScrollableHeight:0.#}, bar {scroll.ComputedVerticalScrollBarVisibility}");
        Add("columns", () =>
        {
            string allowed = scroll is null ? "no scroll viewer" : LibraryAllowedColumns(scroll, vm);
            int widest = vm.Rows.OfType<GalleryTileRow>().Select(row => row.Tiles.Count).DefaultIfEmpty().Max();
            return (columnsBefore is { } before ? $"{before} before, " : string.Empty)
                + $"{vm.ColumnCount} now; {allowed}; widest row {widest}";
        });
        Add("layout", () => $"window {LibraryLayoutValidity(window)}, rows {(rows is null ? "-" : LibraryLayoutValidity(rows))}, "
            + $"scroll {(scroll is null ? "-" : LibraryLayoutValidity(scroll))}");
        Add("dispatcher", () => $"{(window.Dispatcher.CheckAccess() ? "owner thread" : "other thread")}, "
            + $"{(window.Dispatcher.HasShutdownStarted ? "shutting down" : "running")}, "
            + $"focus restore {(window.GridResizeFocusRestorePendingForTest ? "pending" : "idle")}");
        Add("focus", () => $"{LibraryFocusedElement(Keyboard.FocusedElement)}, window {(window.IsActive ? "active" : "inactive")}, "
            + $"foreground {(hwnd != 0 && LibraryUser32.GetForegroundWindow() == hwnd ? "this window" : "another window")}, "
            + $"grid {(rows?.IsKeyboardFocusWithin == true ? "has focus" : "has no focus")}");
        return string.Join("; ", facts);
    }

    /// <summary>The column count the test's own rule allows for the loaded width (see <see cref="AssertCardsInsideLibrary"/>).</summary>
    private static string LibraryAllowedColumns(ScrollViewer scroll, GalleryViewModel vm)
    {
        double usable = LibraryUsableWidth(scroll);
        int allowed = GalleryRowBuilder.ColumnCountForWidth(usable, vm.IsCompactLayout, vm.MinimumTrackWidth);
        return $"{usable:0.#} DIP usable allows {allowed} at a {vm.MinimumTrackWidth:0.#}-DIP "
            + $"{(vm.IsCompactLayout ? "compact" : "full")} track";
    }

    private static string LibraryLayoutValidity(UIElement element) =>
        element.IsMeasureValid && element.IsArrangeValid ? "valid"
            : $"pending (measure {(element.IsMeasureValid ? "valid" : "invalid")}, arrange {(element.IsArrangeValid ? "valid" : "invalid")})";

    private static string LibraryFocusedElement(IInputElement? element) => element switch
    {
        null => "focus on nothing",
        FrameworkElement framework => $"focus on {framework.GetType().Name}"
            + (string.IsNullOrEmpty(framework.Name) ? string.Empty : $" {framework.Name}")
            + (framework.DataContext is null ? string.Empty : $" ({framework.DataContext.GetType().Name})"),
        _ => $"focus on {element.GetType().Name}",
    };

    private static string LibraryMonitor(nint hwnd)
    {
        if (hwnd == 0) return "no window handle";
        var info = new LibraryMonitorInfo { Size = Marshal.SizeOf<LibraryMonitorInfo>() };
        nint monitor = LibraryUser32.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        return monitor != 0 && LibraryUser32.GetMonitorInfo(monitor, ref info)
            ? $"{LibraryRect(info.Monitor)}, work {LibraryRect(info.Work)} px{((info.Flags & 1) != 0 ? ", primary" : string.Empty)}"
            : "unavailable";
    }

    private static string LibraryDpiAwareness(nint hwnd) =>
        LibraryUser32.GetAwarenessFromDpiAwarenessContext(LibraryUser32.GetWindowDpiAwarenessContext(hwnd)) switch
        {
            0 => "DPI unaware",
            1 => "system DPI aware",
            2 => "per-monitor DPI aware",
            int other => $"DPI awareness {other}",
        };

    // The isolated child's output is read back through a console code page that loses the infinity sign.
    private static string LibraryLimit(double length) => double.IsPositiveInfinity(length) ? "none" : $"{length:0.#}";

    private static string LibraryRect(Rect rect) => $"{rect.Left:0.#},{rect.Top:0.#} {rect.Width:0.#}x{rect.Height:0.#}";

    private static string LibraryRect(LibraryNativeRect rect) =>
        $"{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";

    [StructLayout(LayoutKind.Sequential)]
    private struct LibraryNativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LibraryMonitorInfo
    {
        public int Size;
        public LibraryNativeRect Monitor, Work;
        public uint Flags;
    }

    /// <summary>
    /// The user32 calls the library layout facts read. Nested so that an import of the same name in
    /// another part of this partial class cannot collide with them.
    /// </summary>
    private static class LibraryUser32
    {
        [DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(nint monitor, ref LibraryMonitorInfo info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint hwnd, out LibraryNativeRect rect);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint hwnd);

        [DllImport("user32.dll")]
        internal static extern nint GetWindowDpiAwarenessContext(nint hwnd);

        [DllImport("user32.dll")]
        internal static extern int GetAwarenessFromDpiAwarenessContext(nint context);

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();
    }
}
