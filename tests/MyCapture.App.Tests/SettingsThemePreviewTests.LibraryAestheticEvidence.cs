using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in evidence for the 2026-11-06 library aesthetic review. It saves the library window in the Glass
/// and Glass light themes, in Korean and English, at 100, 125 and 150 % (rest, typed search, a search with
/// no result, an empty folder, a folder, the open sort list, the minimum and a wide window), and the delete
/// of 8 of 12 captures while the trash holds the move and after it. Element bounds go to geometry.tsv.
/// Set MYCAPTURE_LIBRARY_AES_EVIDENCE to a directory and MYCAPTURE_LIBRARY_AES_PHASE to "before" or
/// "after"; without the directory nothing runs. Deletes use the fake trash of the library settings
/// evidence, so the Windows Recycle Bin is never used.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    private const string LibraryAestheticVariable = "MYCAPTURE_LIBRARY_AES_EVIDENCE";

    [Fact]
    public Task LibraryAesthetic_WindowGlassKorean100() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassKorean100), AppTheme.Glass, "ko-KR", 1d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassKorean125() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassKorean125), AppTheme.Glass, "ko-KR", 1.25d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassKorean150() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassKorean150), AppTheme.Glass, "ko-KR", 1.5d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassEnglish100() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassEnglish100), AppTheme.Glass, "en-US", 1d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassEnglish125() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassEnglish125), AppTheme.Glass, "en-US", 1.25d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassEnglish150() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassEnglish150), AppTheme.Glass, "en-US", 1.5d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassLightKorean100() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassLightKorean100), AppTheme.GlassLight, "ko-KR", 1d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassLightKorean125() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassLightKorean125), AppTheme.GlassLight, "ko-KR", 1.25d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassLightKorean150() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassLightKorean150), AppTheme.GlassLight, "ko-KR", 1.5d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassLightEnglish100() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassLightEnglish100), AppTheme.GlassLight, "en-US", 1d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassLightEnglish125() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassLightEnglish125), AppTheme.GlassLight, "en-US", 1.25d);

    [Fact]
    public Task LibraryAesthetic_WindowGlassLightEnglish150() => RunLibraryAestheticWindow(nameof(LibraryAesthetic_WindowGlassLightEnglish150), AppTheme.GlassLight, "en-US", 1.5d);

    // One scale a test: the isolated child has 150 s, and a loaded PC took about 75 s for each scale's eight states.
    private static Task RunLibraryAestheticWindow(string name, AppTheme theme, string language, double scale) =>
        RunLibraryAesthetic(name, () => RenderLibraryAestheticWindow(theme, language, scale));

    [Fact]
    public Task LibraryAesthetic_DeleteGlassKorean() =>
        RunLibraryAesthetic(nameof(LibraryAesthetic_DeleteGlassKorean), () => RenderLibraryAestheticDelete(AppTheme.Glass, "ko-KR"));

    [Fact]
    public Task LibraryAesthetic_DeleteGlassEnglish() =>
        RunLibraryAesthetic(nameof(LibraryAesthetic_DeleteGlassEnglish), () => RenderLibraryAestheticDelete(AppTheme.Glass, "en-US"));

    [Fact]
    public Task LibraryAesthetic_DeleteGlassLightKorean() =>
        RunLibraryAesthetic(nameof(LibraryAesthetic_DeleteGlassLightKorean), () => RenderLibraryAestheticDelete(AppTheme.GlassLight, "ko-KR"));

    [Fact]
    public Task LibraryAesthetic_DeleteGlassLightEnglish() =>
        RunLibraryAesthetic(nameof(LibraryAesthetic_DeleteGlassLightEnglish), () => RenderLibraryAestheticDelete(AppTheme.GlassLight, "en-US"));

    private static Task RunLibraryAesthetic(string name, Action render) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LibraryAestheticVariable))
            ? Task.CompletedTask
            : RunIsolated(name, () =>
            {
                Application application = CreateLibraryApplication();
                bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
                FluidMotion.AnimationsEnabledOverrideForTest = false;
                try { render(); }
                finally
                {
                    FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
                    ThemeService.Apply(AppTheme.Glass);
                    application.Shutdown();
                }
            });

    private static string LibraryAestheticDirectory(string area)
    {
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_AES_PHASE") is { Length: > 0 } value ? value : "after";
        return DiagnosticOutputPaths.Create(Path.Combine(Environment.GetEnvironmentVariable(LibraryAestheticVariable)!, phase, area));
    }

    private static string AestheticStem(AppTheme theme, string language, double scale) =>
        $"{language[..2]}-{AppThemeNames.ToSetting(theme).ToLowerInvariant()}-{(int)Math.Round(scale * 100)}";

    private static void RenderLibraryAestheticWindow(AppTheme theme, string language, double scale)
    {
        string directory = LibraryAestheticDirectory("window");
        using var scope = UiText.UseLanguage(language);
        ThemeService.Apply(theme);
        LibraryUxFixture f = CreateLibraryUxFixture("mc-aes-library-");
        f.Records[0].Title = "Capture review - toolbar and keyboard navigation";
        f.ViewModel.Refresh();
        GalleryWindow window = f.Window;
        string stem = AestheticStem(theme, language, scale);
        RecordIsolatedProgress(stem);
        try
        {
            window.Width = 1280;
            window.Height = 760;
            window.Show();
            ApplyAestheticDpi(window, scale);
            WaitForAestheticCards(window, 3);
            SaveAestheticState(window, directory, stem, "rest");

            var search = (TextBox)window.FindName("SearchBox");
            search.Text = "capture";
            PumpLibraryUntil(window, () => f.ViewModel.HasSearchQuery, "search");
            SaveAestheticState(window, directory, stem, "search-typed");
            search.Text = "zqxv";
            PumpLibraryUntil(window, () => f.ViewModel.HasSearchQuery && f.ViewModel.IsEmpty, "no result");
            SaveAestheticState(window, directory, stem, "search-empty");
            search.Text = string.Empty;
            PumpLibraryUntil(window, () => !f.ViewModel.HasSearchQuery && !f.ViewModel.IsEmpty, "search cleared");

            f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Empty.Id));
            PumpLibraryUntil(window, () => f.ViewModel.IsEmpty, "empty folder");
            SaveAestheticState(window, directory, stem, "folder-empty");
            f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
            PumpLibraryUntil(window, () => f.ViewModel.IsCompactLayout && !f.ViewModel.IsEmpty, "folder");
            WaitForAestheticCards(window, 2);
            SaveAestheticState(window, directory, stem, "folder-design");
            f.ViewModel.NavigateTo(CaptureFolderScope.All);
            WaitForAestheticCards(window, 3);

            var sort = (ComboBox)window.FindName("SortBox");
            sort.IsDropDownOpen = true;
            SettleGalleryLayout(window);
            SaveAestheticSortList(window, sort, directory, stem);
            sort.IsDropDownOpen = false;
            SettleGalleryLayout(window);

            if (scale != 1.25d)
            {
                foreach ((string size, double width, double height) in new[] { ("min", 980d, 560d), ("wide", 1920d, 1080d) })
                {
                    window.Width = width;
                    window.Height = height;
                    WaitForAestheticCards(window, 3);
                    SaveAestheticState(window, directory, stem, size);
                }
            }
        }
        finally { window.CloseForExit(); }
    }

    private static void RenderLibraryAestheticDelete(AppTheme theme, string language)
    {
        string directory = LibraryAestheticDirectory("delete");
        using var scope = UiText.UseLanguage(language);
        foreach (double scale in new[] { 1d, 1.5d })
        {
            ThemeService.Apply(theme);
            string stem = AestheticStem(theme, language, scale);
            RecordIsolatedProgress(stem + " delete");
            EvidenceLibrary library = CreateEvidenceLibrary(12, "mc-aes-delete-");
            GalleryWindow window = library.Window;
            try
            {
                window.Show();
                ApplyAestheticDpi(window, scale);
                WaitForAestheticCards(window, 4);
                Guid[] deleted = library.Records.Take(8).Select(record => record.Id).ToArray();
                library.ViewModel.Select(deleted[0]);
                foreach (Guid id in deleted.Skip(1)) library.ViewModel.Select(id, control: true);
                SaveAestheticState(window, directory, stem, "1-selected");

                var status = (TextBlock)window.FindName("LibraryActionStatus");
                string counting = KoreanWordWrap.Strip(UiText.Format("LibrarySelection_MovingProgress", 2, 8));
                window.ConfirmDelete = (_, _) => true;
                window.DeleteProgressDelay = TimeSpan.Zero;
                library.Trash.HoldAfter(2);
                Task delete = StartUiTask(window, () => window.DeleteCapturesAsync(deleted));
                PumpLibraryUntil(window, () => library.Trash.IsHolding && KoreanWordWrap.Strip(status.Text) == counting, "counting");
                PumpLibraryUntil(window, () => window.RealizedCards().Count() == 4, "cards left");
                SaveAestheticState(window, directory, stem, "2-progress");

                library.Trash.ReleaseHold();
                PumpLibraryUntil(window, () => delete.IsCompleted, "delete");
                PumpLibraryUntil(window, () => !GalleryDeleteMotion.IsMoving(status)
                    && !window.RealizedCards().Any(GalleryDeleteMotion.IsMoving), "settled");
                SaveAestheticState(window, directory, stem, "3-moved");
            }
            finally
            {
                library.Trash.ReleaseHold();
                window.CloseForExit();
            }

            EvidenceLibrary empty = CreateEvidenceLibrary(0, "mc-aes-empty-");
            try
            {
                empty.Window.Show();
                ApplyAestheticDpi(empty.Window, scale);
                PumpLibraryUntil(empty.Window, () => empty.ViewModel.IsEmpty, "empty library");
                SaveAestheticState(empty.Window, directory, stem, "4-empty-library");
            }
            finally { empty.Window.CloseForExit(); }
        }
    }

    private static void ApplyAestheticDpi(Window window, double scale)
    {
        VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
        foreach (UIElement element in Descendants(window).Prepend(window).OfType<UIElement>())
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
        }
        SettleGalleryLayout(window);
    }

    private static void WaitForAestheticCards(GalleryWindow window, int count) =>
        PumpLibraryUntil(window, () => window.RealizedCards().Count() >= count
            && window.RealizedCards().All(card => Descendants(card).OfType<GalleryThumbnailImage>().All(image => image.Source is not null)),
            "cards and thumbnails");

    private static void SaveAestheticState(Window window, string directory, string stem, string state)
    {
        SaveOverhaulEvidence(window, directory, $"{stem}-{state}.png");
        RecordAestheticGeometry(window, directory, stem, state);
    }

    /// <summary>The bounds, in DIP from the window's top left, of the parts the review measures.</summary>
    private static void RecordAestheticGeometry(Window window, string directory, string stem, string state)
    {
        var lines = new List<string>();
        void Add(string label, FrameworkElement? element)
        {
            if (element is null || !element.IsVisible) return;
            Point origin = element.TranslatePoint(new Point(), window);
            lines.Add(string.Join('\t', stem, state, label, origin.X.ToString("0.##"), origin.Y.ToString("0.##"),
                element.ActualWidth.ToString("0.##"), element.ActualHeight.ToString("0.##")));
        }
        foreach (string name in new[] { "SearchField", "SearchBox", "SearchPlaceholderText", "SortBox", "LibraryStatusBar",
                     "LibraryActionStatus", "SummaryLabel", "RowsList", "FolderTree", "DateList" })
            Add(name, window.FindName(name) as FrameworkElement);
        if (window.FindName("SearchBox") is TextBox search && search.Template.FindName("PART_ContentHost", search) is FrameworkElement host)
            Add("SearchBox.PART_ContentHost", host);
        if (window.FindName("SortBox") is ComboBox sort)
        {
            foreach (FrameworkElement part in Descendants(sort).OfType<ContentPresenter>().Take(1))
                Add("SortBox.ContentPresenter", part);
        }
        ListBoxItem? card = (window as GalleryWindow)?.RealizedCards().FirstOrDefault();
        if (card is not null)
        {
            Add("Card[0]", card);
            foreach (string name in new[] { "TileCard", "TilePreview", "TileInformation", "TileTitle", "TileTime", "SourceBadge" })
                Add("Card[0]." + name, Descendants(card).OfType<FrameworkElement>().FirstOrDefault(part => part.Name == name));
        }
        foreach (TreeViewItem item in Descendants(window).OfType<TreeViewItem>().Where(item => item.IsSelected))
        {
            Add("SelectedTreeItem", item);
            foreach (string name in new[] { "SelectedLayer", "Indicator" })
                Add("SelectedTreeItem." + name, item.Template.FindName(name, item) as FrameworkElement);
        }
        File.AppendAllLines(DiagnosticOutputPaths.Child(directory, "geometry.tsv"), lines);
    }

    private static void SaveAestheticSortList(Window window, ComboBox sort, string directory, string stem)
    {
        var popup = (Popup)sort.Template.FindName("PART_Popup", sort);
        if (popup.Child is not FrameworkElement list) return;
        list.UpdateLayout();
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        double width = Math.Max(sort.ActualWidth, list.ActualWidth) + 16;
        double height = sort.ActualHeight + list.ActualHeight + 16;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(sort) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(8, 8, sort.ActualWidth, sort.ActualHeight));
            context.DrawRectangle(new VisualBrush(list) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(8, 8 + sort.ActualHeight, list.ActualWidth, list.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, $"{stem}-sort-open.png")))
            encoder.Save(stream);
        double widest = Descendants(list).OfType<ComboBoxItem>().Select(item => item.DesiredSize.Width).DefaultIfEmpty().Max();
        File.AppendAllLines(DiagnosticOutputPaths.Child(directory, "geometry.tsv"),
            [string.Join('\t', stem, "sort-open", "SortBox.Width/WidestItem", sort.ActualWidth.ToString("0.##"), widest.ToString("0.##"), "", "")]);
    }
}
