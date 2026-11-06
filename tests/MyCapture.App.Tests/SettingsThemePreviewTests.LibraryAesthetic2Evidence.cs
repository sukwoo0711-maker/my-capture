using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in evidence for the second round of the 2026-11-06 library aesthetic review (decided 2026-11-07). It saves
/// the library window in Glass (Korean and English at 100 and 150 %) and Glass light (both at 100 %). The cards
/// group shows a card at rest, hovered, selected and focused beside the tags section with no tag, then the sort box
/// at 150 % text beside the details pane at the minimum width. The places group shows a folder holding two
/// subfolders and two captures, an empty folder, a search with no result and an empty library. The parts the round
/// changed go to round2.tsv. It runs with the first round's variables (MYCAPTURE_LIBRARY_AES_EVIDENCE and
/// MYCAPTURE_LIBRARY_AES_PHASE) and finds the parts by name only, so it also builds and runs against the product
/// before the round.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryAesthetic2_CardsGlassKorean100() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_CardsGlassKorean100), AppTheme.Glass, "ko-KR", 1d, cards: true);

    [Fact]
    public Task LibraryAesthetic2_CardsGlassKorean150() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_CardsGlassKorean150), AppTheme.Glass, "ko-KR", 1.5d, cards: true);

    [Fact]
    public Task LibraryAesthetic2_CardsGlassEnglish100() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_CardsGlassEnglish100), AppTheme.Glass, "en-US", 1d, cards: true);

    [Fact]
    public Task LibraryAesthetic2_CardsGlassEnglish150() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_CardsGlassEnglish150), AppTheme.Glass, "en-US", 1.5d, cards: true);

    [Fact]
    public Task LibraryAesthetic2_CardsGlassLightKorean100() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_CardsGlassLightKorean100), AppTheme.GlassLight, "ko-KR", 1d, cards: true);

    [Fact]
    public Task LibraryAesthetic2_CardsGlassLightEnglish100() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_CardsGlassLightEnglish100), AppTheme.GlassLight, "en-US", 1d, cards: true);

    [Fact]
    public Task LibraryAesthetic2_PlacesGlassKorean100() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_PlacesGlassKorean100), AppTheme.Glass, "ko-KR", 1d, cards: false);

    [Fact]
    public Task LibraryAesthetic2_PlacesGlassKorean150() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_PlacesGlassKorean150), AppTheme.Glass, "ko-KR", 1.5d, cards: false);

    [Fact]
    public Task LibraryAesthetic2_PlacesGlassEnglish100() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_PlacesGlassEnglish100), AppTheme.Glass, "en-US", 1d, cards: false);

    [Fact]
    public Task LibraryAesthetic2_PlacesGlassEnglish150() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_PlacesGlassEnglish150), AppTheme.Glass, "en-US", 1.5d, cards: false);

    [Fact]
    public Task LibraryAesthetic2_PlacesGlassLightKorean100() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_PlacesGlassLightKorean100), AppTheme.GlassLight, "ko-KR", 1d, cards: false);

    [Fact]
    public Task LibraryAesthetic2_PlacesGlassLightEnglish100() => RunLibraryAesthetic2(nameof(LibraryAesthetic2_PlacesGlassLightEnglish100), AppTheme.GlassLight, "en-US", 1d, cards: false);

    private static Task RunLibraryAesthetic2(string name, AppTheme theme, string language, double scale, bool cards) =>
        RunLibraryAesthetic(name, () =>
        {
            using var scope = UiText.UseLanguage(language);
            ThemeService.Apply(theme);
            string stem = AestheticStem(theme, language, scale);
            RecordIsolatedProgress(stem + (cards ? " cards" : " places"));
            if (cards) RenderLibraryAesthetic2Cards(stem, scale);
            else RenderLibraryAesthetic2Places(stem, scale);
        });

    private static void RenderLibraryAesthetic2Cards(string stem, double scale)
    {
        string directory = LibraryAestheticDirectory("cards");
        LibraryUxFixture f = CreateLibraryUxFixture("mc-aes2-cards-");
        f.Records[0].Title = "Capture review - toolbar and keyboard navigation";
        f.Records[0].SourcePageUrl = "https://example.com/review";
        f.ViewModel.Refresh();
        GalleryWindow window = f.Window;
        try
        {
            window.Width = 1280;
            window.Height = 760;
            window.Show();
            ApplyAestheticDpi(window, scale);
            WaitForAestheticCards(window, 3);
            ListBoxItem Card() => PumpLibraryUntil(window,
                () => window.RealizedCards().FirstOrDefault(item => item.DataContext is GalleryItemViewModel tile && tile.Id == f.Records[0].Id),
                item => item is not null, "card");

            SaveAesthetic2State(window, f.ViewModel, directory, stem, "cards-rest");
            SetLibraryTileState440(Card(), "IsMouseOver", true);
            SettleGalleryLayout(window);
            SaveAesthetic2State(window, f.ViewModel, directory, stem, "cards-hover");
            SetLibraryTileState440(Card(), "IsMouseOver", false);
            f.ViewModel.Select(f.Records[0].Id);
            SettleGalleryLayout(window);
            SaveAesthetic2State(window, f.ViewModel, directory, stem, "cards-selected");
            f.ViewModel.ClearSelection();
            ListBoxItem focused = Card();
            SetLibraryTileState440(focused, "IsKeyboardFocusWithin", true);
            ForceKeyboardInput(focused);
            SettleGalleryLayout(window);
            SaveAesthetic2State(window, f.ViewModel, directory, stem, "cards-focus");
            SetLibraryTileState440(focused, "IsKeyboardFocusWithin", false);
            SettleGalleryLayout(window);

            // English at 150 % text beside the details pane at the minimum width: the compact bar does not fit,
            // and the sort box gives back width (GalleryWindow.FitContentToolbar).
            window.Resources["FontSize.Body"] = 14d * 1.5;
            window.Resources["FontSize.Caption"] = 12d * 1.5;
            window.Width = 980;
            window.Height = 560;
            window.SetDetailsPaneOpen(true);
            f.ViewModel.Select(f.Records[0].Id);
            f.ViewModel.Select(f.Records[2].Id, control: true);
            WaitForAestheticCards(window, 1);
            SaveAesthetic2State(window, f.ViewModel, directory, stem, "sort-text150-min");
        }
        finally { window.CloseForExit(); }
    }

    private static void RenderLibraryAesthetic2Places(string stem, double scale)
    {
        string directory = LibraryAestheticDirectory("places");
        LibraryUxFixture f = CreateLibraryUxFixture("mc-aes2-places-");
        DateTimeOffset created = f.Design.CreatedAt;
        CaptureFolder reference = f.Queue.CreateFolderAsync("참고 자료", f.Design.Id, createdAt: created.AddMinutes(10)).GetAwaiter().GetResult();
        f.Queue.CreateFolderAsync("최종본", f.Design.Id, createdAt: created.AddMinutes(20)).GetAwaiter().GetResult();
        f.Queue.MoveCapturesAsync([f.Records[3].Id], reference.Id).GetAwaiter().GetResult();
        f.ViewModel.Refresh();
        GalleryWindow window = f.Window;
        try
        {
            window.Width = 1280;
            window.Height = 760;
            window.Show();
            ApplyAestheticDpi(window, scale);
            WaitForAestheticCards(window, 2);

            // A folder holding two subfolders and two captures of its own: the location header names it above
            // the folder cards and the captures.
            f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
            PumpLibraryUntil(window, () => f.ViewModel.IsCompactLayout && !f.ViewModel.IsEmpty, "folder");
            WaitForAestheticCards(window, 2);
            SaveAesthetic2State(window, f.ViewModel, directory, stem, "folder-mixed");

            f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Empty.Id));
            PumpLibraryUntil(window, () => f.ViewModel.IsEmpty, "empty folder");
            SaveAesthetic2State(window, f.ViewModel, directory, stem, "folder-empty");

            f.ViewModel.NavigateTo(CaptureFolderScope.All);
            WaitForAestheticCards(window, 2);
            var search = (TextBox)window.FindName("SearchBox");
            search.Text = "zqxv";
            PumpLibraryUntil(window, () => f.ViewModel.HasSearchQuery && f.ViewModel.IsEmpty, "no result");
            SaveAesthetic2State(window, f.ViewModel, directory, stem, "search-empty");
            search.Text = string.Empty;
            PumpLibraryUntil(window, () => !f.ViewModel.HasSearchQuery, "search cleared");
        }
        finally { window.CloseForExit(); }

        EvidenceLibrary empty = CreateEvidenceLibrary(0, "mc-aes2-none-");
        try
        {
            empty.Window.Show();
            ApplyAestheticDpi(empty.Window, scale);
            PumpLibraryUntil(empty.Window, () => empty.ViewModel.IsEmpty, "empty library");
            SaveAesthetic2State(empty.Window, empty.ViewModel, directory, stem, "empty-library");
        }
        finally { empty.Window.CloseForExit(); }
    }

    private static void SaveAesthetic2State(GalleryWindow window, GalleryViewModel viewModel, string directory, string stem, string state)
    {
        SaveOverhaulEvidence(window, directory, $"{stem}-{state}.png");
        RecordAesthetic2Geometry(window, viewModel, directory, stem, state);
    }

    /// <summary>
    /// The values the round changed, one part a line: stem, state, part, then key=value pairs. Bounds are in DIP
    /// from the window's top left (x,y,width,height); "left" and "right" are a footer part's edges from the card
    /// information grid's own left and right edges.
    /// </summary>
    private static void RecordAesthetic2Geometry(GalleryWindow window, GalleryViewModel viewModel, string directory, string stem, string state)
    {
        var lines = new List<string>();
        void Add(string part, params (string Key, object? Value)[] values) =>
            lines.Add(string.Join('\t', new[] { stem, state, part }.Concat(values.Select(pair => pair.Key + "=" + Aesthetic2Value(pair.Value)))));
        Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), window), element.RenderSize);

        Add("Rows", ("headers", viewModel.Rows.OfType<GalleryHeaderRow>().Count()),
            ("headings", string.Join(" | ", viewModel.Rows.OfType<GalleryHeaderRow>().Select(row => row.Heading))),
            ("first", viewModel.Rows.FirstOrDefault()?.GetType().Name), ("location", viewModel.ShowLocationHeader));
        foreach (string name in new[] { "LibraryItemsSurface", "LocationHeader", "RowsList", "SortBox" })
        {
            if (window.FindName(name) is FrameworkElement { IsVisible: true } element) Add(name + ".Bounds", ("bounds", Bounds(element)));
        }

        ListBoxItem? card = window.RealizedCards().FirstOrDefault(item =>
                Descendants(item).OfType<Button>().Any(button => button.Name == "SourceBadge" && button.IsVisible))
            ?? window.RealizedCards().FirstOrDefault();
        if (card is not null)
        {
            FrameworkElement? Part(string name) => Descendants(card).OfType<FrameworkElement>().FirstOrDefault(part => part.Name == name);
            Add("Card", ("bounds", Bounds(card)));
            if (Part("TileCard") is Border tile) Add("Card.TileCard", ("bounds", Bounds(tile)), ("radius", tile.CornerRadius.TopLeft));
            foreach (string name in new[] { "Outline", "HoverLayer", "SelectedLayer", "FocusRing" })
            {
                if (card.Template.FindName(name, card) is Border ring)
                    Add("Card." + name, ("bounds", Bounds(ring)), ("radius", ring.CornerRadius.TopLeft),
                        ("thickness", ring.BorderThickness.Left), ("opacity", ring.Opacity));
            }
            if (Part("TileInformation") is Grid information)
            {
                Add("Card.TileInformation", ("bounds", Bounds(information)),
                    ("columns", string.Join(",", information.ColumnDefinitions.Select(column => Aesthetic2Value(column.ActualWidth)))),
                    ("rows", string.Join(",", information.RowDefinitions.Select(row => Aesthetic2Value(row.ActualHeight)))));
                foreach (string name in new[] { "TileTitle", "TileTime", "SourceBadge", "TileMetadata", "PinButton", "CopyButton", "MoreButton" })
                {
                    if (Part(name) is not { IsVisible: true } element) continue;
                    double left = element.TranslatePoint(new Point(), information).X;
                    Add("Card." + name, ("bounds", Bounds(element)), ("left", left),
                        ("right", left + element.ActualWidth - information.ActualWidth));
                }
            }
        }

        foreach (string name in new[] { "TagEmptyHint", "SavedSearchEmptyHint" })
        {
            if (window.FindName(name) is TextBlock hint)
                Add(name, ("visible", hint.IsVisible), ("bounds", hint.IsVisible ? Bounds(hint) : null), ("size", hint.FontSize),
                    ("margin", hint.Margin), ("width", hint.ActualWidth),
                    // The one-line width the navigation pane's check compares with the width the hint gets. The
                    // hint's desired size carries its margins; the check measures the text alone.
                    ("natural", hint.IsVisible ? FullTextWidth(hint) - hint.Margin.Left - hint.Margin.Right : (object?)null));
        }
        if (window.FindName("TagSection") is Expander tags) Add("TagSection", ("bounds", Bounds(tags)));

        if (window.FindName("EmptyStateLabel") is FrameworkElement { IsVisible: true } title
            && VisualTreeHelper.GetParent(title) is StackPanel stack && VisualTreeHelper.GetParent(stack) is Border emptyCard)
        {
            Add("EmptyState.Card", ("bounds", Bounds(emptyCard)), ("padding", emptyCard.Padding));
            Add("EmptyState.Title", ("bounds", Bounds(title)));
            int index = 0;
            foreach (Button button in stack.Children.OfType<Button>().Where(button => button.IsVisible))
                Add($"EmptyState.Button[{index++}]", ("bounds", Bounds(button)), ("text", button.Content),
                    ("centreOffset", button.TranslatePoint(new Point(button.ActualWidth / 2, 0), stack).X - stack.ActualWidth / 2));
        }

        if (window.FindName("SortBox") is ComboBox sort && sort.Template.FindName("Selection", sort) is ContentPresenter selection
            && Descendants(selection).OfType<TextBlock>().FirstOrDefault() is { } choice)
        {
            double room = sort.ActualWidth - sort.Padding.Left - sort.Padding.Right;
            double actual = choice.ActualWidth;
            choice.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double full = choice.DesiredSize.Width;
            choice.InvalidateMeasure();
            selection.InvalidateMeasure();
            SettleGalleryLayout(window);
            Add("SortBox", ("bounds", Bounds(sort)), ("room", room), ("text", choice.Text), ("actual", actual), ("full", full),
                ("trimming", choice.TextTrimming));
        }

        foreach (TreeViewItem item in Descendants(window).OfType<TreeViewItem>().Where(item => item.IsSelected))
        {
            var header = item.Template.FindName("PART_Header", item) as FrameworkElement;
            Add("SelectedTreeItem", ("bounds", Bounds(item)),
                ("checkBoxes", header is null ? null : Descendants(header).OfType<CheckBox>().Count(box => box.IsVisible)),
                ("glyph", header is null ? null : Descendants(header).OfType<FrameworkElement>().Any(part => part.Name == "FolderGlyph" && part.IsVisible)));
            if (item.Template.FindName("SelectedLayer", item) is Border wash)
                Add("SelectedTreeItem.SelectedLayer", ("bounds", Bounds(wash)), ("thickness", wash.BorderThickness.Left),
                    ("brush", wash.BorderBrush is SolidColorBrush brush ? brush.Color : null), ("opacity", wash.Opacity));
            if (item.Template.FindName("Indicator", item) is FrameworkElement pill)
                Add("SelectedTreeItem.Indicator", ("bounds", Bounds(pill)), ("opacity", pill.Opacity));
        }
        File.AppendAllLines(DiagnosticOutputPaths.Child(directory, "round2.tsv"), lines);
    }

    private static string Aesthetic2Value(object? value) => value switch
    {
        null => string.Empty,
        double number => number.ToString("0.##", CultureInfo.InvariantCulture),
        Rect rect => string.Join(",", new[] { rect.X, rect.Y, rect.Width, rect.Height }.Select(part => Aesthetic2Value(part))),
        Thickness thickness => string.Join(",", new[] { thickness.Left, thickness.Top, thickness.Right, thickness.Bottom }.Select(part => Aesthetic2Value(part))),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
