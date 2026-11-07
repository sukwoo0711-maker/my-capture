using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Layout checks from the second round of the library aesthetic review (2026-11-07): the card's hover,
/// selection and focus rings follow the card's corners; row 2's buttons sit 4 DIP further out than row 1's
/// text without narrowing the title; a selected folder row shows only its wash and pill; the tags section
/// says how tags appear while there are none; the empty-state cards keep their buttons as wide as their
/// text and centred, with 40 x 32 padding; a folder view does not repeat the folder as a heading; and the
/// sort box ends a choice it cannot show whole in an ellipsis instead of running under its arrow.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryAesthetic2_CardRingsFooterEdgesFolderRowAndSortEllipsis() =>
        RunLibraryAesthetic2Layout(nameof(LibraryAesthetic2_CardRingsFooterEdgesFolderRowAndSortEllipsis), VerifyLibraryAesthetic2Cards);

    [Fact]
    public Task LibraryAesthetic2_TagHintAndEmptyStateCards() =>
        RunLibraryAesthetic2Layout(nameof(LibraryAesthetic2_TagHintAndEmptyStateCards), VerifyLibraryAesthetic2EmptyStates);

    private static Task RunLibraryAesthetic2Layout(string name, Action<string, double> verify) =>
        RunIsolated(name, () =>
        {
            Application application = CreateLibraryApplication();
            bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            try
            {
                foreach (string language in new[] { "en-US", "ko-KR" })
                foreach (double scale in new[] { 1d, 1.5d })
                {
                    using var scope = UiText.UseLanguage(language);
                    RecordIsolatedProgress($"{language} {scale}");
                    verify($"{language} at {scale * 100:0} %", scale);
                }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
                application.Shutdown();
            }
        });

    private static void VerifyLibraryAesthetic2Cards(string label, double scale)
    {
        LibraryUxFixture f = CreateLibraryUxFixture("mc-aes2-cards-");
        f.Records[0].SourcePageUrl = "https://example.com/source";
        f.ViewModel.Refresh();
        GalleryWindow window = f.Window;
        try
        {
            window.Width = 1280;
            window.Height = 760;
            window.Show();
            ApplyAestheticDpi(window, scale);
            WaitForAestheticCards(window, 3);
            foreach (ListBoxItem card in window.RealizedCards()) AssertRingsFollowTheCard(card, label + ", full cards");
            AssertFooterButtonsSitOutside(window, label);

            // The All view keeps its day headings; a folder view does not repeat the folder it shows (checked last,
            // so the other parts are checked whatever this finds).
            Assert.Contains(f.ViewModel.Rows, row => row is GalleryHeaderRow);
            f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
            PumpLibraryUntil(window, () => f.ViewModel.IsCompactLayout && !f.ViewModel.IsEmpty, "folder");
            WaitForAestheticCards(window, 2);
            Assert.True(f.ViewModel.ShowLocationHeader, $"{label}: the folder view has no location header");
            string[] folderHeadings = f.ViewModel.Rows.OfType<GalleryHeaderRow>().Select(row => row.Heading).ToArray();
            foreach (ListBoxItem card in window.RealizedCards()) AssertRingsFollowTheCard(card, label + ", compact cards");
            TreeViewItem folder = PumpLibraryUntil(window,
                () => Descendants(window).OfType<TreeViewItem>().FirstOrDefault(item => item.IsSelected), item => item is not null, "selected folder");
            AssertSelectedFolderRowShowsWashAndPillOnly(folder, label);

            // English at 150 % text beside the details pane at the minimum width: the compact bar does not
            // fit, the sort box gives back width, and the choice it shows ends in an ellipsis inside the box.
            f.ViewModel.NavigateTo(CaptureFolderScope.All);
            WaitForAestheticCards(window, 1);
            window.Resources["FontSize.Body"] = 14d * 1.5;
            window.Resources["FontSize.Caption"] = 12d * 1.5;
            window.Width = 980;
            window.Height = 560;
            window.SetDetailsPaneOpen(true);
            f.ViewModel.Select(f.Records[0].Id);
            f.ViewModel.Select(f.Records[2].Id, control: true);
            SettleGalleryLayout(window);
            AssertSortChoicesEndInsideTheBox(window, label + ", 150 % text beside the details pane at 980 DIP",
                expectEllipsis: CultureIsEnglish());

            Assert.True(folderHeadings.Length == 0,
                $"{label}: the folder view repeats its location as a heading ({string.Join(", ", folderHeadings)})");
        }
        finally { window.CloseForExit(); }
    }

    private static bool CultureIsEnglish() => UiText.Get("Folder410.Newest") == "Newest first";

    private static void VerifyLibraryAesthetic2EmptyStates(string label, double scale)
    {
        LibraryUxFixture f = CreateLibraryUxFixture("mc-aes2-empty-");
        GalleryWindow window = f.Window;
        try
        {
            window.Width = 1280;
            window.Height = 760;
            window.Show();
            ApplyAestheticDpi(window, scale);
            WaitForAestheticCards(window, 3);

            // No capture has a tag: the section says how tags get there, in the saved searches' quiet line.
            var hint = (TextBlock)window.FindName("TagEmptyHint");
            var savedHint = (TextBlock)window.FindName("SavedSearchEmptyHint");
            Assert.True(hint.IsVisible, $"{label}: the tags section shows nothing without tags");
            // Korean wrapping text carries word joiners; the reader gets the copy without them.
            Assert.Equal(UiText.Get("Library.Tags.EmptyHint"), KoreanWordWrap.Strip(hint.Text));
            Assert.True(savedHint.IsVisible, $"{label}: the saved searches hint is hidden, so the two cannot be compared");
            Assert.Equal(savedHint.Margin, hint.Margin);
            Assert.Equal(savedHint.FontSize, hint.FontSize, 2);
            // Bigger caption text (the sort check's 150 % case) changes the two quiet lines alike.
            window.Resources["FontSize.Caption"] = 12d * 1.5;
            SettleGalleryLayout(window);
            Assert.Equal(savedHint.FontSize, hint.FontSize, 2);
            window.Resources.Remove("FontSize.Caption");
            SettleGalleryLayout(window);
            Assert.Equal(savedHint.TextWrapping, hint.TextWrapping);
            Assert.Equal(((SolidColorBrush)savedHint.Foreground).Color, ((SolidColorBrush)hint.Foreground).Color);
            f.Records[2].Tags = "review";
            f.ViewModel.Refresh();
            PumpLibraryUntil(window, () => f.ViewModel.VisibleTags.Count == 1, "a tag");
            SettleGalleryLayout(window);
            Assert.False(hint.IsVisible, $"{label}: the tags hint stays beside a tag");

            // A search with no result: the clear button is as wide as its text and centred.
            var search = (TextBox)window.FindName("SearchBox");
            search.Text = "zqxv";
            PumpLibraryUntil(window, () => f.ViewModel.HasSearchQuery && f.ViewModel.IsEmpty, "no result");
            AssertEmptyStateButton((Button)window.FindName("EmptyStateClearButton"), label + ", no result");
            search.Text = string.Empty;
            PumpLibraryUntil(window, () => !f.ViewModel.HasSearchQuery && !f.ViewModel.IsEmpty, "search cleared");

            f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Empty.Id));
            PumpLibraryUntil(window, () => f.ViewModel.IsEmpty, "empty folder");
            Assert.Equal(UiText.Get("Folder410.EmptyFolder"), f.ViewModel.EmptyStateTitle);
            AssertEmptyStateCardPadding(window, label + ", empty folder");
        }
        finally { window.CloseForExit(); }

        // An empty library: the capture button is as wide as its text and centred under the title.
        EvidenceLibrary empty = CreateEvidenceLibrary(0, "mc-aes2-none-");
        try
        {
            empty.Window.Show();
            ApplyAestheticDpi(empty.Window, scale);
            PumpLibraryUntil(empty.Window, () => empty.ViewModel.IsEmpty, "empty library");
            AssertEmptyStateButton((Button)empty.Window.FindName("EmptyStateCaptureButton"), label + ", empty library");
        }
        finally { empty.Window.CloseForExit(); }

        // The three empty titles end alike: none with a period.
        foreach (string key in new[] { "Folder410.EmptyFolder", "Library.Empty.NoCaptures", "Library.Empty.NoResults" })
            Assert.False(UiText.Get(key).TrimEnd().EndsWith('.'), $"{label}: {key} '{UiText.Get(key)}' ends with a period");
    }

    /// <summary>Each ring's radius is the card's plus the distance from the ring to the card, so the corners run parallel.</summary>
    private static void AssertRingsFollowTheCard(ListBoxItem item, string label)
    {
        Border tile = Descendants(item).OfType<Border>().Single(part => part.Name == "TileCard");
        double cardRadius = tile.CornerRadius.TopLeft;
        double cardLeft = tile.TranslatePoint(new Point(), item).X;
        foreach (string name in new[] { "Outline", "HoverLayer", "SelectedLayer", "FocusRing" })
        {
            var ring = (Border)item.Template.FindName(name, item);
            CornerRadius radius = ring.CornerRadius;
            Assert.True(radius.TopLeft == radius.TopRight && radius.TopLeft == radius.BottomRight && radius.TopLeft == radius.BottomLeft,
                $"{label}: the {name} corners differ ({radius})");
            double expected = cardRadius + cardLeft - ring.TranslatePoint(new Point(), item).X;
            Assert.True(Math.Abs(radius.TopLeft - expected) <= 0.5,
                $"{label}: the {name} radius is {radius.TopLeft:0.#} DIP; {expected:0.#} DIP would follow the card's {cardRadius:0.#} DIP corner");
        }
    }

    /// <summary>
    /// Row 2's source badge and commands sit 4 DIP outside row 1's margins, at their full size, and the
    /// commands' column stays as wide as the commands, so the title keeps its room.
    /// </summary>
    private static void AssertFooterButtonsSitOutside(GalleryWindow window, string label)
    {
        ListBoxItem item = window.RealizedCards().First(card =>
            Descendants(card).OfType<Button>().Any(button => button.Name == "SourceBadge" && button.IsVisible));
        T Part<T>(string name) where T : FrameworkElement => Descendants(item).OfType<T>().Single(part => part.Name == name);
        var information = Part<Grid>("TileInformation");
        Button badge = Part<Button>("SourceBadge");
        Button[] commands = [Part<Button>("PinButton"), Part<Button>("CopyButton"), Part<Button>("MoreButton")];
        TextBlock time = Part<TextBlock>("TileTime");
        Assert.All(commands, command => Assert.True(command.IsVisible, $"{label}: {command.Name} is hidden on an image card"));

        double badgeLeft = badge.TranslatePoint(new Point(), information).X;
        Assert.True(Math.Abs(badgeLeft + 4) <= 0.5, $"{label}: the source badge starts at {badgeLeft:0.##} DIP; row 1 starts at 0");
        double moreRight = commands[^1].TranslatePoint(new Point(commands[^1].ActualWidth, 0), information).X;
        Assert.True(Math.Abs(moreRight - information.ActualWidth - 4) <= 0.5,
            $"{label}: More ends at {moreRight:0.##} DIP; row 1 ends at {information.ActualWidth:0.##}");
        Assert.Equal(24, badge.ActualWidth, 1);
        Assert.All(commands, command => Assert.Equal(28, command.ActualWidth, 1));

        double column = information.ColumnDefinitions[2].ActualWidth;
        double needed = Math.Max(commands.Sum(command => command.ActualWidth), time.ActualWidth + time.Margin.Left + time.Margin.Right);
        Assert.True(Math.Abs(column - needed) <= 0.5,
            $"{label}: the commands' column is {column:0.##} DIP for {needed:0.##} DIP of commands, so the title's room changed");
    }

    /// <summary>A selected folder row: its wash and pill, the folder glyph, no check box and no outline.</summary>
    private static void AssertSelectedFolderRowShowsWashAndPillOnly(TreeViewItem row, string label)
    {
        var header = (FrameworkElement)row.Template.FindName("PART_Header", row);
        Assert.DoesNotContain(Descendants(header).OfType<CheckBox>(), box => box.IsVisible);
        FrameworkElement glyph = Descendants(header).OfType<FrameworkElement>().Single(part => part.Name == "FolderGlyph");
        Assert.True(glyph.IsVisible, $"{label}: the selected folder lost its glyph");
        var wash = (Border)row.Template.FindName("SelectedLayer", row);
        var pill = (FrameworkElement)row.Template.FindName("Indicator", row);
        Assert.True(wash.Opacity > 0.99, $"{label}: the selected folder's wash is at {wash.Opacity:0.##}");
        Assert.True(pill.Opacity > 0.99, $"{label}: the selected folder's pill is at {pill.Opacity:0.##}");
        bool outlined = wash.BorderThickness.Left > 0 && wash.BorderBrush is SolidColorBrush { Color.A: > 0 };
        Assert.False(outlined, $"{label}: the selected folder row still draws a {wash.BorderThickness.Left:0.#} DIP outline");
    }

    /// <summary>The choice the sort box shows never runs under its arrow; it ends in an ellipsis when it is cut.</summary>
    private static void AssertSortChoicesEndInsideTheBox(GalleryWindow window, string label, bool expectEllipsis)
    {
        var sort = (ComboBox)window.FindName("SortBox");
        var selection = (ContentPresenter)sort.Template.FindName("Selection", sort);
        int previous = sort.SelectedIndex;
        bool cut = false;
        try
        {
            for (int index = 0; index < sort.Items.Count; index++)
            {
                sort.SelectedIndex = index;
                SettleGalleryLayout(window);
                TextBlock text = Descendants(selection).OfType<TextBlock>().Single();
                double room = sort.ActualWidth - sort.Padding.Left - sort.Padding.Right;
                double full = FullTextWidth(text);
                SettleGalleryLayout(window);
                Assert.True(text.ActualWidth <= room + 0.5,
                    $"{label}: '{text.Text}' takes {text.ActualWidth:0.#} DIP in a {sort.ActualWidth:0.#} DIP box with {room:0.#} DIP for text");
                if (full > room + 0.5)
                {
                    cut = true;
                    Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                }
            }
        }
        finally
        {
            sort.SelectedIndex = previous;
            SettleGalleryLayout(window);
        }
        if (expectEllipsis) Assert.True(cut, $"{label}: no choice was cut, so the ellipsis was not exercised");
    }

    /// <summary>
    /// The width a text block asks for with no limit, measured on the block itself (its font and DPI). Its parent is
    /// measured again too, so the block gets its real limit back on the next layout pass; callers settle the layout.
    /// </summary>
    private static double FullTextWidth(TextBlock text)
    {
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = text.DesiredSize.Width;
        text.InvalidateMeasure();
        (VisualTreeHelper.GetParent(text) as UIElement)?.InvalidateMeasure();
        return width;
    }

    private static void AssertEmptyStateButton(Button button, string label)
    {
        Assert.True(button.IsVisible, $"{label}: the empty state has no {button.Name}");
        var stack = (FrameworkElement)VisualTreeHelper.GetParent(button);
        double natural = button.DesiredSize.Width - button.Margin.Left - button.Margin.Right;
        Assert.True(Math.Abs(button.ActualWidth - natural) <= 0.5,
            $"{label}: {button.Name} is {button.ActualWidth:0.#} DIP wide for {natural:0.#} DIP of text and padding");
        double middle = button.TranslatePoint(new Point(button.ActualWidth / 2, 0), stack).X;
        Assert.True(Math.Abs(middle - stack.ActualWidth / 2) <= 0.5,
            $"{label}: {button.Name} is centred at {middle:0.#} DIP in a {stack.ActualWidth:0.#} DIP column");
        AssertEmptyStateCardPadding((FrameworkElement)VisualTreeHelper.GetParent(stack), label);
    }

    private static void AssertEmptyStateCardPadding(Window window, string label)
    {
        var title = (FrameworkElement)window.FindName("EmptyStateLabel");
        AssertEmptyStateCardPadding((FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(title)), label);
    }

    private static void AssertEmptyStateCardPadding(FrameworkElement card, string label) =>
        Assert.True(card is Border { Padding: { Left: 40, Top: 32, Right: 40, Bottom: 32 } },
            $"{label}: the empty-state card's padding is {(card as Border)?.Padding}");
}
