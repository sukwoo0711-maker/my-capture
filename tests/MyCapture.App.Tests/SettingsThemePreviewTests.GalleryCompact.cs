using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
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

    private static void VerifyCompactGallery(string language)
    {
        ThemeService.Apply(AppTheme.Midnight);
        string root = TestRecycleBin.CreateTempSubdirectory("mc-gallery-compact-").FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        queue.CreateFolderAsync("Review").GetAwaiter().GetResult();
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
            // A single selected image shows every selection action except GIF export.
            vm.Select(vm.Groups.SelectMany(group => group.Items).First().Id);
            window.Show();
            SettleGalleryLayout(window);
            var client = (FrameworkElement)window.Content;
            var banner = (FrameworkElement)window.FindName("OcrCoverageBanner");
            Assert.False(banner.IsVisible, language + ": the complete-coverage fixture must not show the OCR banner");

            var check = (CheckBox)window.FindName("CurrentFolderCaptureCheck");
            ComboBox sort = Descendants(window).OfType<ComboBox>()
                .Single(box => AutomationProperties.GetName(box) == UiText.Get("Folder410.Sort"));
            var destination = (TextBlock)window.FindName("NewCaptureDestinationLabel");
            var searchBox = (TextBox)window.FindName("SearchBox");
            TextBlock placeholder = Descendants(window).OfType<TextBlock>().Single(text => text.Text == vm.SearchPlaceholder);
            foreach (FrameworkElement element in new FrameworkElement[] { check, sort, destination, searchBox, placeholder })
                AssertInsideClient(element, client, language);
            Assert.Equal(vm.NewCaptureDestinationText, destination.Text);
            AssertNotTrimmed(destination, language);
            AssertNotTrimmed(placeholder, language);
            TextBlock checkLabel = Descendants(check).OfType<TextBlock>().Single(text => text.Text == UiText.Get("Folder410.CurrentFolderOption"));
            AssertNotTrimmed(checkLabel, language);
            // Tab order follows the visual order of the search row.
            Assert.True(searchBox.TranslatePoint(new Point(), client).X < check.TranslatePoint(new Point(), client).X);
            Assert.True(check.TranslatePoint(new Point(), client).X < sort.TranslatePoint(new Point(), client).X);

            WrapPanel actions = Descendants(window).OfType<WrapPanel>().Single(panel => panel.Children.Contains((UIElement)window.FindName("ArchiveExportButton")));
            Button[] visibleActions = actions.Children.OfType<Button>().Where(button => button.IsVisible).ToArray();
            Assert.Equal(4, visibleActions.Length);
            double firstLine = visibleActions[0].TranslatePoint(new Point(), actions).Y;
            Assert.All(visibleActions, button => Assert.Equal(firstLine, button.TranslatePoint(new Point(), actions).Y, 1));
            Button last = visibleActions[^1];
            Assert.True(last.TranslatePoint(new Point(last.ActualWidth, 0), client).X <= destination.TranslatePoint(new Point(), client).X,
                language + ": selection actions must not run under the destination text");

            ListBox rows = (ListBox)window.FindName("RowsList");
            ScrollViewer scroll = Descendants(rows).OfType<ScrollViewer>().First();
            foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            {
                ThemeService.Apply(theme);
                SettleGalleryLayout(window);
                Assert.True(scroll.ViewportHeight >= GalleryCompactFullCardHeight,
                    $"{language}/{theme}: compact library viewport {scroll.ViewportHeight:0.##} DIP must fit a full {GalleryCompactFullCardHeight}-DIP card");
            }
        }
        finally
        {
            window.CloseForExit();
            ThemeService.Apply(AppTheme.Midnight);
        }
    }

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
