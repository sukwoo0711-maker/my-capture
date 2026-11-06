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
/// Layout checks from the 2026-11-06 library aesthetic review: a card's preview keeps the card's rounded
/// top corners, typed search text sits where its placeholder sat, the status line keeps its height when
/// Undo or another of its buttons shows, the sort box shows each choice whole at the normal and the
/// minimum width, and a selected folder, date or view row keeps its pill inside its outline.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryAesthetic_CardCornersSearchTextStatusLineSortChoicesAndRowPills() =>
        RunIsolated(nameof(LibraryAesthetic_CardCornersSearchTextStatusLineSortChoicesAndRowPills), () =>
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
                    VerifyLibraryAestheticLayout($"{language} at {scale * 100:0} %", scale);
                }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
                application.Shutdown();
            }
        });

    private static void VerifyLibraryAestheticLayout(string label, double scale)
    {
        LibraryUxFixture f = CreateLibraryUxFixture("mc-aes-layout-");
        GalleryWindow window = f.Window;
        try
        {
            window.Width = 1280;
            window.Height = 760;
            window.Show();
            ApplyAestheticDpi(window, scale);
            WaitForAestheticCards(window, 3);
            AssertPreviewKeepsCardCorners(window, label + ", full cards");

            // The placeholder is measured while it shows; the typed text replaces it in the same box.
            var search = (TextBox)window.FindName("SearchBox");
            var placeholder = (TextBlock)window.FindName("SearchPlaceholderText");
            double placeholderMiddle = placeholder.TranslatePoint(new Point(0, placeholder.ActualHeight / 2), search).Y;
            search.Text = "capture";
            PumpLibraryUntil(window, () => f.ViewModel.HasSearchQuery, "search");
            Rect first = search.GetRectFromCharacterIndex(0);
            double typedMiddle = first.Top + first.Height / 2;
            Assert.True(Math.Abs(typedMiddle - placeholderMiddle) <= 0.75,
                $"{label}: typed search text is centred at {typedMiddle:0.##} DIP, its placeholder at {placeholderMiddle:0.##} DIP");
            search.Text = string.Empty;
            PumpLibraryUntil(window, () => !f.ViewModel.HasSearchQuery, "search cleared");

            // A result's Undo, the archive's Cancel and the activity button fit the status line's height, so
            // the list above and the status text do not move when one of them comes or goes.
            var bar = (FrameworkElement)window.FindName("LibraryStatusBar");
            var summary = (FrameworkElement)window.FindName("SummaryLabel");
            double barHeight = bar.ActualHeight;
            double summaryTop = summary.TranslatePoint(new Point(), window).Y;
            void AssertStatusLineStill(string shown)
            {
                SettleGalleryLayout(window);
                Assert.True(Math.Abs(bar.ActualHeight - barHeight) < 0.5,
                    $"{label}: the status line is {bar.ActualHeight:0.#} DIP tall with {shown}, {barHeight:0.#} DIP without");
                double top = summary.TranslatePoint(new Point(), window).Y;
                Assert.True(Math.Abs(top - summaryTop) < 0.5, $"{label}: the summary moved from {summaryTop:0.##} to {top:0.##} DIP with {shown}");
            }
            window.ShowEngagementStatusForTest("8", UiText.Get("LibraryUndo_Action"), () => Task.CompletedTask);
            Assert.Equal(UiText.Get("LibraryUndo_Action"), window.StatusActionLabelForTest);
            AssertStatusLineStill("Undo");
            window.ExpireActionStatus();
            foreach (string name in new[] { "ArchiveCancelButton", "ActivityButton" })
            {
                var button = (Button)window.FindName(name);
                object content = button.Content;
                button.Content ??= name;
                button.Visibility = Visibility.Visible;
                AssertStatusLineStill(name);
                button.Visibility = Visibility.Collapsed;
                button.Content = content;
            }
            SettleGalleryLayout(window);

            foreach ((double width, double height) in new[] { (1280d, 760d), (980d, 560d) })
            {
                window.Width = width;
                window.Height = height;
                WaitForAestheticCards(window, 1);
                AssertSortChoicesFit(window, $"{label}, {width:0} DIP window");
                // With a selection the bar shows its commands; the box still fits and More stays inside.
                f.ViewModel.Select(f.Records[0].Id);
                SettleGalleryLayout(window);
                AssertSortChoicesFit(window, $"{label}, {width:0} DIP window with a selection");
                f.ViewModel.ClearSelection();
                SettleGalleryLayout(window);
            }

            window.Width = 1280;
            window.Height = 760;
            f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
            PumpLibraryUntil(window, () => f.ViewModel.IsCompactLayout && !f.ViewModel.IsEmpty, "folder");
            WaitForAestheticCards(window, 2);
            AssertPreviewKeepsCardCorners(window, label + ", compact cards");
            TreeViewItem folder = PumpLibraryUntil(window,
                () => Descendants(window).OfType<TreeViewItem>().FirstOrDefault(item => item.IsSelected), item => item is not null, "selected folder");
            AssertPillInsideOutline(folder, label + ", selected folder row");

            ((Expander)window.FindName("DateSection")).IsExpanded = true;
            f.ViewModel.NavigateTo(f.ViewModel.FolderNodes[0].Scope);
            var dates = (ListBox)window.FindName("DateList");
            ListBoxItem date = PumpLibraryUntil(window,
                () => Descendants(dates).OfType<ListBoxItem>().FirstOrDefault(item => item.IsSelected), item => item is not null, "selected date");
            AssertPillInsideOutline(date, label + ", selected date row");

            // "All" has its own style based on LibraryUx.NavItem (it is also a drop target), with the same template.
            f.ViewModel.NavigateTo(CaptureFolderScope.All);
            var all = (RadioButton)window.FindName("AllLibraryFilter");
            PumpLibraryUntil(window, () => all.IsChecked == true, "checked view");
            AssertPillInsideOutline(all, label + ", checked view row");
        }
        finally { window.CloseForExit(); }
    }

    /// <summary>The square corners of the preview are cut to the card's inner radius; the rest shows.</summary>
    private static void AssertPreviewKeepsCardCorners(GalleryWindow window, string label)
    {
        foreach (ListBoxItem card in window.RealizedCards())
        {
            Border tile = Descendants(card).OfType<Border>().Single(part => part.Name == "TileCard");
            Border preview = Descendants(card).OfType<Border>().Single(part => part.Name == "TilePreview");
            double radius = tile.CornerRadius.TopLeft - tile.BorderThickness.Top;
            double width = preview.ActualWidth, height = preview.ActualHeight;
            Geometry? clip = preview.Clip;
            Assert.True(clip is not null, $"{label}: a {width:0}x{height:0} preview drew over the card's rounded top corners");
            Assert.Equal(width, clip!.Bounds.Width, 1);
            Assert.True(clip.Bounds.Height >= height, $"{label}: the clip is {clip.Bounds.Height:0.#} DIP tall for a {height:0.#} DIP preview");
            Assert.False(clip.FillContains(new Point(0.5, 0.5)), $"{label}: the top left corner of the preview still shows");
            Assert.False(clip.FillContains(new Point(width - 0.5, 0.5)), $"{label}: the top right corner of the preview still shows");
            double arc = radius - radius / Math.Sqrt(2);
            foreach (Point inside in new[] { new Point(arc + 0.75, arc + 0.75), new Point(width - arc - 0.75, arc + 0.75),
                         new Point(width / 2, 0.5), new Point(0.5, height - 0.5), new Point(width - 0.5, height - 0.5) })
                Assert.True(clip.FillContains(inside), $"{label}: the preview is cut at {inside} inside the card's {radius:0.#} DIP corner");
        }
    }

    /// <summary>The sort box shows each choice whole: its text fits between the box's padding and arrow.</summary>
    private static void AssertSortChoicesFit(GalleryWindow window, string label)
    {
        var sort = (ComboBox)window.FindName("SortBox");
        var selection = (ContentPresenter)sort.Template.FindName("Selection", sort);
        int previous = sort.SelectedIndex;
        try
        {
            for (int index = 0; index < sort.Items.Count; index++)
            {
                sort.SelectedIndex = index;
                SettleGalleryLayout(window);
                TextBlock text = Descendants(selection).OfType<TextBlock>().Single();
                double room = sort.ActualWidth - sort.Padding.Left - sort.Padding.Right;
                Assert.True(text.ActualWidth <= room + 0.5,
                    $"{label}: the sort choice '{text.Text}' is {text.ActualWidth:0.#} DIP wide in a {sort.ActualWidth:0.#} DIP box with {room:0.#} DIP for text");
            }
        }
        finally
        {
            sort.SelectedIndex = previous;
            SettleGalleryLayout(window);
        }
        var more = (FrameworkElement)window.FindName("MoreCommandsButton");
        var bar = (FrameworkElement)window.FindName("LibraryContentCommandBar");
        Point right = more.TranslatePoint(new Point(more.ActualWidth, 0), bar);
        Assert.True(right.X <= bar.ActualWidth + 0.5, $"{label}: More ends at {right.X:0.#} DIP in a {bar.ActualWidth:0.#} DIP bar");
    }

    /// <summary>A selected row's pill starts inside its outline instead of on it.</summary>
    private static void AssertPillInsideOutline(Control row, string label)
    {
        var outline = (Border)row.Template.FindName("SelectedLayer", row);
        var pill = (FrameworkElement)row.Template.FindName("Indicator", row);
        double left = pill.TranslatePoint(new Point(), outline).X;
        double edge = outline.BorderThickness.Left;
        Assert.True(left >= edge - 0.01,
            $"{label}: the {pill.ActualWidth:0.#} DIP pill starts {left:0.##} DIP into a {edge:0.##} DIP outline");
    }
}
