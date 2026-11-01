using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryFolder_TodayShowsFolderCardsAndTheOpenFolderReceivesCaptures() =>
        RunIsolated(nameof(LibraryFolder_TodayShowsFolderCardsAndTheOpenFolderReceivesCaptures), () =>
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
                         { ("ko-KR", AppTheme.Glass), ("ko-KR", AppTheme.HighContrast), ("en-US", AppTheme.Glass) })
                {
                    using var scope = UiText.UseLanguage(language);
                    VerifyLibraryFolderCards(language, theme);
                }
            }
            finally
            {
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
        });

    private static void VerifyLibraryFolderCards(string language, AppTheme theme)
    {
        string label = $"{language}/{theme}";
        ThemeService.Apply(theme);
        string root = TestRecycleBin.CreateTempSubdirectory("mc-library-folder-").FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        // A fixed noon keeps every fixture item in "today" whenever the test runs.
        DateTimeOffset today = new DateTimeOffset(DateTime.Today).AddHours(12);
        CaptureFolder empty = queue.CreateFolderAsync("제품리뷰", createdAt: today.AddHours(-3)).GetAwaiter().GetResult();
        CaptureFolder design = queue.CreateFolderAsync("디자인 시안", createdAt: today.AddHours(-4)).GetAwaiter().GetResult();
        Color[] colors = [Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0xF5, 0x9E, 0x0B),
            Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x8B, 0x5C, 0xF6)];
        var records = new List<CaptureRecord>();
        for (int index = 0; index < colors.Length; index++)
        {
            CaptureRecord record = AddLibraryFolderCapture(queue, today.AddMinutes(-index * 40 - (index >= 3 ? 60 * 24 : 0)),
                $"Library sample {index + 1}", colors[index]);
            records.Add(record);
        }
        queue.MoveCapturesAsync([records[3].Id, records[4].Id], design.Id).GetAwaiter().GetResult();

        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var ocr = new GalleryCompactOcr();
        using var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
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
        // Render the settled window, not a frame of its fade-in.
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            var rowsList = (ListBox)window.FindName("RowsList");
            Button[] cards = PumpLibraryUntil(window, () => FolderCardButtons(rowsList).Length == 2, label + ": folder strips");

            // (1) Today: heading, then the folder cards, then today's captures.
            Assert.Equal(GalleryDateGrouping.Resolve(today, today).Heading, Assert.IsType<GalleryHeaderRow>(vm.Rows[0]).Heading);
            Assert.IsType<GalleryFolderRow>(vm.Rows[1]);
            Button heading = Descendants(rowsList).OfType<Button>().First(button => button.DataContext is GalleryHeaderRow);
            ListBoxItem firstTile = Descendants(rowsList).OfType<ListBoxItem>()
                .First(item => item.DataContext is GalleryItemViewModel tile && tile.Id == records[0].Id);
            double headingBottom = heading.TranslatePoint(new Point(0, heading.ActualHeight), rowsList).Y;
            double tileTop = firstTile.TranslatePoint(new Point(), rowsList).Y;
            Button emptyCard = cards.Single(card => ((GalleryFolderCardViewModel)card.DataContext).FolderId == empty.Id);
            Button designCard = cards.Single(card => ((GalleryFolderCardViewModel)card.DataContext).FolderId == design.Id);
            foreach (Button card in cards)
            {
                Point top = card.TranslatePoint(new Point(), rowsList);
                Assert.True(top.Y >= headingBottom, $"{label}: a folder card must sit below the day heading");
                Assert.True(top.Y + card.ActualHeight <= tileTop, $"{label}: a folder card must sit above the first capture");
                Assert.True(top.X + card.ActualWidth <= rowsList.ActualWidth, $"{label}: a folder card must stay inside the library");
                // A card with a preview collapses its "빈 폴더" label; every shown label must fit.
                foreach (TextBlock text in Descendants(card).OfType<TextBlock>().Where(text => text.IsVisible))
                    AssertNotTrimmed(text, label);
            }
            // Cards share the tile column grid, so the first card and the first capture align.
            Assert.Equal(firstTile.TranslatePoint(new Point(), rowsList).X + 8,
                cards.Min(card => card.TranslatePoint(new Point(), rowsList).X), 1);
            Assert.Equal(language == "ko-KR" ? "폴더 제품리뷰, 캡처 0개" : "Folder 제품리뷰, Captures: 0",
                AutomationProperties.GetName(emptyCard));
            Assert.Equal(language == "ko-KR" ? "폴더 디자인 시안, 캡처 2개" : "Folder 디자인 시안, Captures: 2",
                AutomationProperties.GetName(designCard));
            Assert.Empty(Descendants(designCard).OfType<Image>());
            Assert.Equal(40, designCard.ActualHeight, 1);
            SaveLibraryFolderEvidence(window, $"library-folder-cards-{theme}-{language}.png");

            // (2) Opening a card shows the folder and makes it the new capture destination.
            emptyCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, emptyCard));
            var check = (CheckBox)window.FindName("CurrentFolderCaptureCheck");
            var destination = (TextBlock)window.FindName("NewCaptureDestinationLabel");
            PumpLibraryUntil(window, () => vm.SelectedScope == CaptureFolderScope.ForFolder(empty.Id)
                && check.IsChecked == true, label + ": opened folder");
            Assert.Equal(empty.Id, queue.BrowsingCaptureFolderId);
            Assert.True(check.IsEnabled);
            // The save-here chip beside the folder name replaces the separate check box and
            // destination line; the status line names a destination only when it is not this folder.
            Assert.Equal(language == "ko-KR" ? "새 캡처 저장 위치: 제품리뷰" : "New capture destination: 제품리뷰",
                vm.NewCaptureDestinationText);
            Assert.Equal(UiText.Format("LibraryUx_SaveHereOn", "제품리뷰"), check.ToolTip);
            Assert.False(destination.IsVisible);
            TextBlock checkLabel = Descendants(check).OfType<TextBlock>().Single(text => text.Text == UiText.Get("LibraryUx_SaveHere"));
            AssertNotTrimmed(checkLabel, label);
            Assert.True(vm.IsEmpty);
            Assert.Equal(UiText.Get("Folder410.EmptyFolder"), vm.EmptyStateTitle);
            Assert.Equal(UiText.Get("LibraryFolder_EmptyFolderSavesHere"), KoreanWordWrap.Strip(((TextBlock)window.FindName("EmptyStateLabel")).Text));
            Assert.Contains(FlattenFolderNodes(vm.FolderNodes), node => node.FolderId == empty.Id && node.IsSelected);
            SaveLibraryFolderEvidence(window, $"library-folder-destination-{theme}-{language}.png");

            // A capture keeps the library visible and still belongs to the open folder.
            Button captureButton = Descendants(window).OfType<Button>()
                .Single(button => button.IsVisible && AutomationProperties.GetName(button) == UiText.Get("Text_2C20ADDA2018"));
            int captureRequests = 0;
            window.CaptureRegionRequested += () =>
            {
                Assert.True(window.IsVisible);
                captureRequests++;
            };
            captureButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, captureButton));
            Assert.Equal(1, captureRequests);
            Assert.Null(captureButton.ContextMenu);
            var options = (Button)window.FindName("CaptureOptionsButton");
            options.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, options));
            ContextMenu menu = Assert.IsType<ContextMenu>(options.ContextMenu);
            Assert.True(menu.IsOpen);
            Assert.Equal(1, captureRequests);
            menu.IsOpen = false;
            Assert.True(window.IsVisible);
            Assert.Equal(empty.Id, queue.BrowsingCaptureFolderId);
            Assert.Equal(empty.Id, AddLibraryFolderCapture(queue, today, "Inside folder", colors[0]).FolderId);

            // Closing the library ends the folder session and starts the next visit at All.
            window.Show();
            PumpLibraryUntil(window, () => window.IsVisible, label + ": shown again");
            Assert.Equal(empty.Id, queue.BrowsingCaptureFolderId);
            window.Close();
            PumpLibraryUntil(window, () => !window.IsVisible, label + ": closed to the tray");
            Assert.True(vm.SelectedScope.IsAll);
            Assert.Null(queue.BrowsingCaptureFolderId);
            Assert.Null(AddLibraryFolderCapture(queue, today, "After close", colors[1]).FolderId);
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static Button[] FolderCardButtons(DependencyObject root) =>
        Descendants(root).OfType<Button>().Where(button => button.DataContext is GalleryFolderCardViewModel).ToArray();

    private static IEnumerable<GalleryFolderNode> FlattenFolderNodes(IEnumerable<GalleryFolderNode> nodes) =>
        nodes.SelectMany(node => new[] { node }.Concat(FlattenFolderNodes(node.Children)));

    private static T PumpLibraryUntil<T>(Window window, Func<T?> probe, Func<T?, bool> done, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            SettleGalleryLayout(window);
            T? value = probe();
            if (done(value)) return value!;
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what + " did not settle.");
            Thread.Sleep(15);
        }
    }

    private static Button[] PumpLibraryUntil(Window window, Func<bool> done, string what) =>
        PumpLibraryUntil(window, () => FolderCardButtons(window), _ => done(), what);

    private static CaptureRecord AddLibraryFolderCapture(CaptureQueue queue, DateTimeOffset created, string title, Color color)
    {
        const int width = 160, height = 100;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // A light document band over a solid colour keeps each preview distinct.
                bool band = y is > 18 and < 30 || (y is > 40 and < 48 && x < width * 2 / 3);
                int offset = (y * width + x) * 4;
                pixels[offset] = band ? (byte)0xF5 : color.B;
                pixels[offset + 1] = band ? (byte)0xF5 : color.G;
                pixels[offset + 2] = band ? (byte)0xF5 : color.R;
                pixels[offset + 3] = 0xFF;
            }
        }
        BitmapSource sample = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        byte[] png = ImageCodec.EncodePng(sample);
        var record = new CaptureRecord
        {
            Width = width, Height = height, Title = title, CreatedAt = created, UpdatedAt = created,
            OcrText = "Library folder fixture", OcrContentRevision = 0,
        };
        queue.Add(record);
        string directory = queue.GetDirectory(record);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Thumbnail), png);
        File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Rendered), png);
        File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), png);
        return record;
    }

    private static void SaveLibraryFolderEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_FOLDER_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Drain(window);
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            // Glass themes leave the window transparent for Mica; give the evidence an opaque base.
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }
        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
