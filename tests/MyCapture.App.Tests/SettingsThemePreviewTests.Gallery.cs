using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task GalleryCompiledFolderMenuLoadsAndKeepsReadableTree() =>
        RunIsolated(nameof(GalleryCompiledFolderMenuLoadsAndKeepsReadableTree), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            ThemeService.Apply(AppTheme.Midnight);
            string root = TestRecycleBin.CreateTempSubdirectory("mc-gallery-baml-").FullName;
            var paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var settings = new AppSettings();
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            queue.CreateFolderAsync("Review").GetAwaiter().GetResult();
            var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
            var ocr = new GalleryMarkupOcr();
            using var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => {}, NullLogger.Instance);
            var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
                NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
            var vm = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96);
            var window = new GalleryWindow(vm, controller,
                new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
                new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
                NullLoggerFactory.Instance, presenter, () => settings.Ocr, indexing,
                new GalleryMarkupPrivacy(), NullLogger.Instance)
            { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                // Load compiled BAML, including deferred menu event connections. Source-only
                // template parsing misses invalid IComponentConnector casts in setters.
                window.Show();
                window.UpdateLayout();
                var tree = Assert.IsType<TreeView>(window.FindName("FolderTree"));
                Assert.True(tree.ActualHeight >= 120);
                Assert.Same(window.FindResource("Text.Primary"), tree.Foreground);
                var menu = Assert.IsType<ContextMenu>(window.FindResource("Gallery.FolderContextMenu"));
                Assert.Equal(6, menu.Items.OfType<MenuItem>().Count());
                Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.NotNull(item.Header));
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });

    private sealed class GalleryMarkupOcr : IOcrService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<string> SupportedLanguages => [];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Markup smoke test must not run OCR.");
    }

    private sealed class GalleryMarkupPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Markup smoke test must not run OCR.");
    }
}
