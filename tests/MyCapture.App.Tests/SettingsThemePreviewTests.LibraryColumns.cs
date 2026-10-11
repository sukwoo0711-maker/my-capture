using System.Windows;
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
            foreach ((string name, Action show) in views)
            {
                window.Width = window.MinWidth;
                show();
                SettleGalleryLayout(window);
                Assert.Equal(name == "folder", vm.IsCompactLayout);
                AssertCardsInsideLibrary(rowsList, scroll, vm, $"{name} at the minimum width");
                if (name is "folder" or "search")
                    SaveLibraryUxEvidence(window, AppTheme.Glass, $"columns-minimum-{name}-glass-ko-KR.png");
                var columnsSeen = new HashSet<int>();
                foreach (double width in LibrarySweepWidths(window, scroll, vm.IsCompactLayout))
                {
                    window.Width = width;
                    SettleGalleryLayout(window);
                    AssertCardsInsideLibrary(rowsList, scroll, vm, $"{name} at {width:0.#} DIP");
                    columnsSeen.Add(vm.ColumnCount);
                }
                // Every desktop this runs on is wide enough to cross the first column boundaries.
                Assert.True(columnsSeen.Count >= 3, $"{name}: only {string.Join(", ", columnsSeen)} columns were reached");
                vm.SearchQuery = string.Empty;
            }
        }
        finally
        {
            window.CloseForExit();
        }
    }

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
