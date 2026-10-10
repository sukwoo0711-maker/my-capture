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
            FluidMotion.SetWindowEntrance(window, false);
            window.Show();
            SettleGalleryLayout(window);
            var client = (FrameworkElement)window.Content;
            var banner = (FrameworkElement)window.FindName("OcrCoverageBanner");
            Assert.False(banner.IsVisible, language + ": the complete-coverage fixture must not show the OCR banner");

            // Nothing selected: capture, search, new folder, sort and more share one row and fit.
            var bar = (FrameworkElement)window.FindName("LibraryCommandBar");
            ComboBox sort = Descendants(window).OfType<ComboBox>()
                .Single(box => AutomationProperties.GetName(box) == UiText.Get("Folder410.Sort"));
            var searchBox = (TextBox)window.FindName("SearchBox");
            var searchField = (FrameworkElement)window.FindName("SearchField");
            TextBlock placeholder = Descendants(window).OfType<TextBlock>().Single(text => text.Text == vm.SearchPlaceholder);
            var capture = (Button)window.FindName("CaptureRegionButton");
            var record = (Button)window.FindName("RecordRegionButton");
            var newFolder = (Button)window.FindName("NewFolderButton");
            var more = (Button)window.FindName("MoreCommandsButton");
            foreach (FrameworkElement element in new FrameworkElement[] { capture, record, searchBox, placeholder, newFolder, sort, more })
                AssertInsideClient(element, client, language);
            AssertSameRow(bar, capture, record, searchField, newFolder, sort, more);
            AssertNotTrimmed(placeholder, language);
            foreach (TextBlock label in new[] { capture, record, newFolder }.SelectMany(button => Descendants(button).OfType<TextBlock>()))
                AssertNotTrimmed(label, language);
            // Tab order follows the visual order: capture, search, then the library commands.
            AssertLeftToRight(client, record, searchField, newFolder);
            AssertLeftToRight(client, newFolder, sort, more);
            double barHeight = bar.ActualHeight;
            var workspace = (FrameworkElement)window.FindName("GalleryWorkspace");
            double workspaceTop = workspace.TranslatePoint(new Point(), client).Y;

            // A single selected image: the selection's commands replace the library commands in place.
            vm.Select(vm.Groups.SelectMany(group => group.Items).First().Id);
            SettleGalleryLayout(window);
            var selection = (Panel)window.FindName("SelectionCommands");
            Button[] visibleActions = selection.Children.OfType<Button>().Where(button => button.IsVisible).ToArray();
            Assert.Equal(5, visibleActions.Length); // clear, ZIP, move, delete, reduce (GIF is video only)
            foreach (FrameworkElement element in visibleActions.Append<FrameworkElement>(placeholder))
                AssertInsideClient(element, client, language);
            AssertSameRow(bar, visibleActions.Cast<FrameworkElement>().Append(searchField)
                .Append((Button)window.FindName("CaptureRegionIconButton")).ToArray());
            AssertLeftToRight(client, (Button)window.FindName("RecordRegionIconButton"), searchField, visibleActions[0]);
            AssertNotTrimmed(placeholder, language);
            AssertNotTrimmed((TextBlock)window.FindName("SelectionCountLabel"), language);
            foreach (Button button in visibleActions.Skip(1))
                AssertNotTrimmed(Descendants(button).OfType<TextBlock>().Single(), language);
            Assert.Equal(barHeight, bar.ActualHeight, 1);
            Assert.Equal(workspaceTop, workspace.TranslatePoint(new Point(), client).Y, 1);

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

            // Inside a folder the location header joins the content column and a full card still fits.
            vm.ClearSelection();
            vm.NavigateTo(CaptureFolderScope.ForFolder(queue.Folders.Single().Id));
            SettleGalleryLayout(window);
            var check = (CheckBox)window.FindName("CurrentFolderCaptureCheck");
            AssertInsideClient(check, client, language);
            AssertNotTrimmed(Descendants(check).OfType<TextBlock>().Single(text => text.Text == UiText.Get("LibraryUx_SaveHere")), language);
            Assert.True(scroll.ViewportHeight >= GalleryCompactFullCardHeight,
                $"{language}: compact folder viewport {scroll.ViewportHeight:0.##} DIP must fit a full {GalleryCompactFullCardHeight}-DIP card");
            SaveLibraryUxEvidence(window, AppTheme.Glass, $"after-narrow-folder-glass-{language}.png");
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
