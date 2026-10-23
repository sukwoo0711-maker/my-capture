using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryPinShortcutTests
{
    [Fact]
    public Task PinAndKeepKeysTargetOneTileAndPreserveTextInput() =>
        SettingsThemePreviewTests.RunIsolated(nameof(PinAndKeepKeysTargetOneTileAndPreserveTextInput), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                using var fixture = new Fixture();
                GalleryWindow window = fixture.Window;
                window.Show();
                window.UpdateLayout();
                var floated = new List<BitmapSource>();
                var sources = new List<MyCapture.Core.Capture.CaptureProvenance>();
                int changes = 0;
                window.FloatRequested += (_, source) => { floated.Add(source.Image); sources.Add(source.Provenance); };
                window.CaptureChanged += (_, _) => changes++;

                // The focused image wins over another selected image, just like its menu.
                fixture.Model.Select(fixture.First.Id);
                fixture.Model.Select(fixture.Second.Id, control: true);
                Guid[] selection = fixture.Model.Selection.SelectedIds.ToArray();
                ListBoxItem imageTile = FindTile(window, fixture.Second.Id);
                window.Activate();
                Assert.True(imageTile.Focus());
                Assert.Same(imageTile, Keyboard.FocusedElement);
                Assert.True(PressTile(imageTile, Key.P).Handled);
                Assert.Equal(3, Assert.Single(floated).PixelWidth);
                Assert.Equal(fixture.Second.SourceDocumentPath, Assert.Single(sources).DocumentPath);
                Assert.False(fixture.First.IsPinned);
                Assert.False(fixture.Second.IsPinned);
                Assert.Equal(0, changes);
                Assert.Equal(selection, fixture.Model.Selection.SelectedIds);

                // Keep toggles on and off without opening another screen pin.
                Assert.True(PressTile(imageTile, Key.K).Handled);
                PumpUntil(window, () => changes == 1);
                Assert.True(fixture.Second.IsPinned);
                Assert.False(fixture.First.IsPinned);
                Assert.Single(floated);
                Assert.True(PressTile(FindTile(window, fixture.Second.Id), Key.K).Handled);
                PumpUntil(window, () => changes == 2);
                Assert.False(fixture.Second.IsPinned);
                Assert.Single(floated);

                Assert.False(PressTile(FindTile(window, fixture.Video.Id), Key.P).Handled);
                Assert.False(fixture.Video.IsPinned);
                Assert.Single(floated);
                Assert.Equal(2, changes);

                // Exercise the same handler with text input sources, including the actual
                // search and metadata fields. A selected image must not supply a fallback.
                fixture.Model.Select(fixture.Second.Id);
                var search = (TextBox)window.FindName("SearchBox");
                Assert.True(search.Focus());
                Assert.Same(search, Keyboard.FocusedElement);
                foreach (Key key in new[] { Key.P, Key.K })
                {
                    var args = KeyArgs(search, key);
                    search.RaiseEvent(args);
                    Assert.False(args.Handled);
                    // Even a tile event cannot run while a separate input owns focus.
                    Assert.False(PressTile(FindTile(window, fixture.Second.Id), key).Handled);
                }
                Assert.False(fixture.Second.IsPinned);
                Assert.Single(floated);
                Assert.Equal(2, changes);
                Assert.True(FindTile(window, fixture.Second.Id).Focus());
                var metadata = new GalleryOrganizationDialog(fixture.Second, (_, _) => Task.FromResult(true));
                try
                {
                    TextBox[] inputs = [(TextBox)window.FindName("SearchBox"), metadata.TitleInput,
                        metadata.TagsInput, new TextBox { Text = "Folder rename" }];
                    foreach (TextBox input in inputs)
                    {
                        foreach (Key key in new[] { Key.P, Key.K })
                        {
                            var args = KeyArgs(window, key);
                            args.Source = input;
                            InvokeTileKeyDown(window, args);
                            Assert.False(args.Handled);
                        }
                    }
                    Assert.False(fixture.Second.IsPinned);
                    Assert.Single(floated);
                    Assert.Equal(2, changes);
                }
                finally { metadata.Close(); }

                var menu = (ContextMenu)window.FindResource("Gallery.TileContextMenu");
                MenuItem pin = Assert.Single(menu.Items.OfType<MenuItem>(),
                    item => AutomationProperties.GetName(item) == UiText.Get("Gallery.Float"));
                MenuItem keep = Assert.Single(menu.Items.OfType<MenuItem>(),
                    item => AutomationProperties.GetName(item) == UiText.Get("LibraryUx_KeepToggleName"));
                Assert.Equal("P", pin.InputGestureText);
                Assert.Equal("K", keep.InputGestureText);

                // Menu and keyboard share rendering and target selection, even when
                // the menu's ambient DataContext refers to another image.
                pin.DataContext = fixture.Model.FindTile(fixture.First.Id);
                pin.CommandParameter = fixture.Model.FindTile(fixture.Second.Id);
                pin.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(2, floated.Count);
                Assert.All(floated, bitmap => Assert.Equal(3, bitmap.PixelWidth));
                Assert.All(sources, source => Assert.Equal(fixture.Second.SourceDocumentPath, source.DocumentPath));
                Assert.False(fixture.Second.IsPinned);
                Assert.Equal(2, changes);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                application.Shutdown();
            }
        }, typeof(GalleryPinShortcutTests));

    private static ListBoxItem FindTile(GalleryWindow window, Guid id) =>
        Assert.Single(VisualDescendants(window).OfType<ListBoxItem>(),
            item => item.DataContext is GalleryItemViewModel tile && tile.Id == id);

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static KeyEventArgs KeyArgs(Visual source, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source), Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };

    private static KeyEventArgs PressTile(ListBoxItem tile, Key key)
    {
        var args = KeyArgs(tile, key);
        tile.RaiseEvent(args);
        return args;
    }

    private static void InvokeTileKeyDown(GalleryWindow window, KeyEventArgs args) =>
        typeof(GalleryWindow).GetMethod("OnTileKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, args]);

    private static void PumpUntil(GalleryWindow window, Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
        Assert.True(completed(), "The library action did not finish.");
        window.UpdateLayout();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OcrResultPresenter _presenter;
        internal GalleryWindow Window { get; }
        internal GalleryViewModel Model { get; }
        internal CaptureRecord First { get; }
        internal CaptureRecord Second { get; }
        internal CaptureRecord Video { get; }

        internal Fixture()
        {
            string root = Path.Combine(Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")
                ?? Path.GetTempPath(), "pin-keys-" + Guid.NewGuid().ToString("N"));
            var paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var settings = new AppSettings();
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            First = Add(2);
            Second = Add(3);
            Video = Add(4, video: true);
            var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
            Model = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Rendered), 96);
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
            var ocr = new NoOcr();
            _presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
            var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
                NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
            Window = new GalleryWindow(Model, controller,
                new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
                new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
                NullLoggerFactory.Instance, _presenter, () => settings.Ocr, indexing, new NoPrivacy(), NullLogger.Instance)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            };

            CaptureRecord Add(int width, bool video = false)
            {
                var record = new CaptureRecord
                {
                    RelativeDirectory = Guid.NewGuid().ToString("N"), Width = width, Height = 2,
                    MediaKind = video ? CaptureMediaKind.Video : CaptureMediaKind.Image,
                    SourceDocumentPath = $@"C:\Specs\capture-{width}.xlsx", SourceAppName = "Excel",
                };
                queue.Add(record);
                Directory.CreateDirectory(queue.GetDirectory(record));
                if (!video)
                {
                    var bitmap = BitmapSource.Create(width, 2, 96, 96, PixelFormats.Bgra32, null,
                        new byte[width * 2 * 4], width * 4);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(queue.GetFilePath(record, CaptureFileNames.Rendered));
                    encoder.Save(stream);
                }
                return record;
            }
        }

        public void Dispose()
        {
            Window.CloseForExit();
            _presenter.Dispose();
        }
    }

    private sealed class NoOcr : IOcrService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<string> SupportedLanguages => [];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pin shortcuts must not run OCR.");
    }

    private sealed class NoPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pin shortcuts must not run OCR.");
    }
}
