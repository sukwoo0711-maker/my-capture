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
using MyCapture.Core.Capture;
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
    /// <summary>
    /// The real library window at its minimum size shows each kind of capture source inside the
    /// details panel without horizontal overflow. With MYCAPTURE_LIBRARY_UX_EVIDENCE set it also
    /// keeps a PNG of each state.
    /// </summary>
    [Fact]
    public Task GallerySourceDetails_MinimumWindowKeepsEverySourceKindInsideTheDetailsPanel() =>
        RunIsolated(nameof(GallerySourceDetails_MinimumWindowKeepsEverySourceKindInsideTheDetailsPanel), () =>
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
                foreach (string language in new[] { "ko-KR", "en-US" })
                {
                    using var scope = UiText.UseLanguage(language);
                    VerifySourceDetails(language);
                }
            }
            finally { application.Shutdown(); }
        });

    private static void VerifySourceDetails(string language)
    {
        ThemeService.Apply(AppTheme.Glass);
        string root = TestRecycleBin.CreateTempSubdirectory("mc-gallery-source-").FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        BitmapSource sample = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 3 * 4], 4 * 4);
        byte[] png = ImageCodec.EncodePng(sample);
        DateTimeOffset now = DateTimeOffset.Now;
        var sources = new (string Kind, string Title, string Url, CaptureSourceDetails Source)[]
        {
            ("page", "Pull request #313 · 캡처 출처 메타데이터 — Microsoft Edge",
                "https://github.com/example/my-capture/pull/313?diff=split#discussion_r42",
                new("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")),
            ("folder", "2026 하반기 프로젝트 자료", "",
                new("Windows 탐색기", @"C:\Windows\explorer.exe", @"C:\Users\test\Documents\계약 검토\2026 하반기 프로젝트 자료")),
            ("app", "회의록.txt - 메모장", "", new("메모장", @"C:\Windows\System32\notepad.exe")),
        };
        var ids = new Dictionary<string, Guid>();
        for (int index = 0; index < sources.Length; index++)
        {
            var record = new CaptureRecord
            {
                Width = sample.PixelWidth, Height = sample.PixelHeight, CreatedAt = now.AddMinutes(-index),
                SourceWindowTitle = sources[index].Title, SourcePageUrl = sources[index].Url,
                OcrText = "Source fixture", OcrContentRevision = 0,
            };
            record.SetSourceDetails(sources[index].Source);
            queue.Add(record);
            string directory = queue.GetDirectory(record);
            Directory.CreateDirectory(directory);
            foreach (string file in new[] { CaptureFileNames.Thumbnail, CaptureFileNames.Rendered, CaptureFileNames.Original })
                File.WriteAllBytes(Path.Combine(directory, file), png);
            ids[sources[index].Kind] = record.Id;
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
            window.SetDetailsPaneOpen(true);
            SettleGalleryLayout(window);
            var details = (Border)window.FindName("GalleryDetailsPanel");
            var metadata = (ScrollViewer)window.FindName("GalleryDetailsScroll");
            Assert.Equal(new Thickness(16), details.Padding);
            foreach ((string kind, _, _, _) in sources)
            {
                vm.Select(ids[kind]);
                SettleGalleryLayout(window);
                // Edit, pin and copy sit above the information and are never scrolled away; the
                // information area is the panel's only scrolling surface and holds no text box.
                metadata.ScrollToBottom();
                SettleGalleryLayout(window);
                Assert.Same(metadata, Assert.Single(Descendants(details).OfType<ScrollViewer>()));
                Assert.Empty(Descendants(details).OfType<TextBox>());
                double metadataTop = metadata.TranslatePoint(new Point(), details).Y;
                Button[] main = Descendants(details).OfType<Button>()
                    .Where(button => button.Name is "DetailsEditButton" or "DetailsFloatButton" or "DetailsCopyButton").ToArray();
                Assert.Equal(3, main.Length);
                foreach (Button button in main)
                {
                    Assert.True(button.IsVisible, $"{language}/{kind}: {button.Name} is hidden");
                    Rect bounds = button.TransformToAncestor(details).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Top >= 0 && bounds.Bottom <= metadataTop && bounds.Left >= 0 && bounds.Right <= details.ActualWidth,
                        $"{language}/{kind}: {button.Name} at {bounds} is not in the fixed area above {metadataTop:0.#}");
                    Assert.False(IsDescendantOf(button, metadata), $"{language}/{kind}: {button.Name} scrolls with the information");
                }
                metadata.ScrollToTop();
                SettleGalleryLayout(window);
                FrameworkElement section = Descendants(details).OfType<FrameworkElement>()
                    .Single(element => AutomationProperties.GetAutomationId(element) == "LibrarySourceSection");
                Assert.True(IsDescendantOf(section, metadata), $"{language}/{kind}: the source must scroll with the information");
                Assert.True(section.IsVisible, $"{language}/{kind}: the source section must be shown");
                FrameworkElement[] visible = Descendants(section).OfType<FrameworkElement>()
                    .Where(element => element.IsVisible && element is TextBlock or Button).ToArray();
                Assert.Contains(visible, element => element is TextBlock text && text.Text == sources.Single(item => item.Kind == kind).Source.AppName);
                Assert.Equal(kind == "app" ? 0 : 2, visible.OfType<Button>().Count());
                foreach (FrameworkElement element in visible.Where(element => element is TextBlock or Button))
                {
                    Point left = element.TranslatePoint(new Point(), details);
                    Point right = element.TranslatePoint(new Point(element.ActualWidth, 0), details);
                    Assert.True(left.X >= -0.5 && right.X <= details.ActualWidth + 0.5,
                        $"{language}/{kind}: {element.GetType().Name} spans {left.X:0.#}..{right.X:0.#} in a {details.ActualWidth:0.#}-DIP panel");
                }
                // At the minimum height the section sits below the fold; the panel scrolls to it.
                FrameworkElement last = visible.Last();
                last.BringIntoView();
                SettleGalleryLayout(window);
                Point bottom = last.TranslatePoint(new Point(0, last.ActualHeight), details);
                Assert.True(bottom.Y <= details.ActualHeight + 0.5, $"{language}/{kind}: the panel could not scroll to the source actions");
                SaveLibraryUxEvidence(window, AppTheme.Glass, $"source-details-{kind}-glass-{language}.png");
            }
        }
        finally
        {
            window.CloseForExit();
            ThemeService.Apply(AppTheme.Midnight);
        }
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
