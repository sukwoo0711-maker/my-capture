using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// #221: the library preview decodes its PNG off the UI thread. These tests hold what that move
/// needs: the file's pixels and size, no late image after the selection, the request, the capture
/// or the window changed, one full-size decode at a time, and no image or file kept afterwards.
/// </summary>
public sealed class GalleryPreviewLoadTests
{
    [Fact]
    public Task PreviewDecodesOffTheUiThread_AndOpensOnlyTheLatestCurrentImage() =>
        SettingsThemePreviewTests.RunIsolated(nameof(PreviewDecodesOffTheUiThread_AndOpensOnlyTheLatestCurrentImage), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                using var fixture = new Fixture();
                GalleryWindow window = fixture.Window;
                window.Show();
                window.UpdateLayout();
                var decoder = new GatedDecoder(window.DecodePreviewForTest);
                window.DecodePreviewForTest = decoder.Decode;
                var previewButton = (Button)window.FindName("PreviewSelectedButton");
                int uiThread = Environment.CurrentManagedThreadId;

                // The click returns while the pool decodes, and the preview shows the file's pixels.
                Request(window, fixture.Model, previewButton, fixture.Wide);
                PumpUntil(window, () => decoder.Started.Count == 1, "first decode");
                Pump(window);
                Assert.Empty(window.OwnedWindows);
                Assert.True(window.IsLoadingPreviewForTest);
                decoder.Release();
                PumpUntil(window, () => window.OwnedWindows.Count == 1 && !window.IsLoadingPreviewForTest, "first preview");
                Assert.NotEqual(uiThread, decoder.Finished[0].Thread);
                AssertShowsFile(window, fixture, fixture.Wide);
                // The image is loaded into memory: the file is not held open while the preview shows it.
                using (new FileStream(fixture.RenderedPath(fixture.Wide), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                ClosePreview(window);
                Assert.True(Collected(decoder.Finished[0].Image), "A closed preview kept its image.");

                // A selection change while the image decodes drops it.
                Request(window, fixture.Model, previewButton, fixture.Tall);
                PumpUntil(window, () => decoder.Started.Count == 2, "second decode");
                fixture.Model.Select(fixture.Small.Id);
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "dropped preview");
                Pump(window);
                Assert.Empty(window.OwnedWindows);
                Assert.True(Collected(decoder.Finished[1].Image), "A dropped preview image stayed referenced.");

                // Newer requests replace the waiting one, so only one full-size decode runs at a time and a
                // replaced request is never decoded; the image of the superseded request is dropped.
                Request(window, fixture.Model, previewButton, fixture.Wide);
                PumpUntil(window, () => decoder.Started.Count == 3, "third decode");
                Request(window, fixture.Model, previewButton, fixture.Tall);
                Request(window, fixture.Model, previewButton, fixture.Small);
                Pump(window);
                Assert.Equal(3, decoder.Started.Count);
                decoder.Release();
                PumpUntil(window, () => decoder.Started.Count == 4, "latest decode");
                Assert.Equal(fixture.RenderedPath(fixture.Small), decoder.Started[3]);
                Assert.Empty(window.OwnedWindows);
                decoder.Release();
                PumpUntil(window, () => window.OwnedWindows.Count == 1 && !window.IsLoadingPreviewForTest, "latest preview");
                AssertShowsFile(window, fixture, fixture.Small);
                Assert.True(Collected(decoder.Finished[2].Image), "A superseded preview image stayed referenced.");
                ClosePreview(window);

                // A capture whose content changed, and a hidden library, drop the late image too.
                Request(window, fixture.Model, previewButton, fixture.Wide);
                PumpUntil(window, () => decoder.Started.Count == 5, "changed capture decode");
                fixture.Wide.ContentRevision++;
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "changed capture");
                Pump(window);
                Assert.Empty(window.OwnedWindows);

                Request(window, fixture.Model, previewButton, fixture.Tall);
                PumpUntil(window, () => decoder.Started.Count == 6, "hidden library decode");
                window.Hide();
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "hidden library");
                Pump(window);
                Assert.Empty(Application.Current.Windows.OfType<GalleryImagePreviewWindow>());
                window.Show();
                window.UpdateLayout();

                // A capture whose files cannot be read reports it, as before, and opens nothing.
                Request(window, fixture.Model, previewButton, fixture.Missing);
                PumpUntil(window, () => decoder.Started.Count == 7, "missing decode");
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "missing preview");
                Pump(window);
                Assert.Empty(window.OwnedWindows);
                Assert.Null(decoder.Finished[6].Image.Target);
                Assert.Equal(KoreanWordWrap.Strip(UiText.Get("Gallery.FloatFailed")), window.ActionStatusText);

                Assert.Equal(1, decoder.MaxRunning);
                Assert.Equal(7, decoder.Finished.Count);
            }
            finally { application.Shutdown(); }
        }, typeof(GalleryPreviewLoadTests));

    private static void Request(GalleryWindow window, GalleryViewModel model, Button previewButton, CaptureRecord record)
    {
        model.Select(record.Id);
        var timer = Stopwatch.StartNew();
        previewButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        // The click only queues the request; a gated decode would otherwise hang it here.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"The preview click took {timer.ElapsedMilliseconds} ms.");
        Assert.True(window.IsLoadingPreviewForTest);
    }

    // Separate frames so no local of the test body keeps a shown image alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertShowsFile(GalleryWindow window, Fixture fixture, CaptureRecord record)
    {
        Window preview = Assert.Single(window.OwnedWindows.Cast<Window>());
        Assert.IsType<GalleryImagePreviewWindow>(preview);
        var shown = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<Image>(
            Assert.Single(Assert.IsType<Canvas>(preview.Content).Children.Cast<UIElement>())).Source);
        Assert.True(shown.IsFrozen);
        Assert.Equal(record.Width, shown.PixelWidth);
        Assert.Equal(record.Height, shown.PixelHeight);
        var pixels = new byte[record.Width * record.Height * 4];
        new FormatConvertedBitmap(shown, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, record.Width * 4, 0);
        Assert.Equal(Fixture.Pattern(record.Width, record.Height), pixels);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClosePreview(GalleryWindow window)
    {
        Assert.Single(window.OwnedWindows.Cast<Window>()).Close();
        Pump(window);
        Assert.Empty(window.OwnedWindows);
    }

    private static bool Collected(WeakReference reference)
    {
        for (int attempt = 0; attempt < 5 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return !reference.IsAlive;
    }

    private static void Pump(Window window)
    {
        for (int pass = 0; pass < 5; pass++)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
    }

    private static void PumpUntil(Window window, Func<bool> done, string what)
    {
        var timeout = Stopwatch.StartNew();
        while (!done())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(what + " did not happen.");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
    }

    /// <summary>Wraps the window's own decoder; every decode waits until the test releases it.</summary>
    private sealed class GatedDecoder(Func<string, string, BitmapSource?> decode)
    {
        private readonly SemaphoreSlim _gate = new(0);
        private readonly object _sync = new();
        private readonly List<string> _started = [];
        private readonly List<(int Thread, WeakReference Image)> _finished = [];
        private int _running;
        private int _maxRunning;

        internal IReadOnlyList<string> Started { get { lock (_sync) return [.. _started]; } }
        internal IReadOnlyList<(int Thread, WeakReference Image)> Finished { get { lock (_sync) return [.. _finished]; } }
        internal int MaxRunning => Volatile.Read(ref _maxRunning);

        internal void Release() => _gate.Release();

        internal BitmapSource? Decode(string renderedPath, string originalPath)
        {
            int running = Interlocked.Increment(ref _running);
            lock (_sync)
            {
                _started.Add(renderedPath);
                _maxRunning = Math.Max(_maxRunning, running);
            }
            try
            {
                if (!_gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test never released the decode.");
                BitmapSource? image = decode(renderedPath, originalPath);
                lock (_sync) _finished.Add((Environment.CurrentManagedThreadId, new WeakReference(image)));
                return image;
            }
            finally { Interlocked.Decrement(ref _running); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OcrResultPresenter _presenter;
        private readonly CaptureQueue _queue;
        internal GalleryWindow Window { get; }
        internal GalleryViewModel Model { get; }
        internal CaptureRecord Wide { get; }
        internal CaptureRecord Tall { get; }
        internal CaptureRecord Small { get; }
        internal CaptureRecord Missing { get; }

        internal Fixture()
        {
            string root = Path.Combine(Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")
                ?? Path.GetTempPath(), "preview-load-" + Guid.NewGuid().ToString("N"));
            var paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var settings = new AppSettings();
            _queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            Wide = Add(300, 200);
            Tall = Add(90, 240);
            Small = Add(64, 48);
            Missing = Add(40, 30, writeFile: false);
            var controller = new GalleryController(_queue, NullLogger<GalleryController>.Instance);
            Model = new GalleryViewModel(controller, record => _queue.GetFilePath(record, CaptureFileNames.Rendered), 96);
            var persistence = new CapturePersistenceService(_queue, paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
            var ocr = new NoOcr();
            _presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
            var indexing = new OcrIndexingService(controller, ocr, _queue.GetDirectory, () => settings.Ocr,
                NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
            Window = new GalleryWindow(Model, controller,
                new GalleryReeditLoader(_queue, NullLogger<GalleryReeditLoader>.Instance), commit, _queue,
                new VideoLibraryService(_queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
                NullLoggerFactory.Instance, _presenter, () => settings.Ocr, indexing, new NoPrivacy(), NullLogger.Instance)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            };

            CaptureRecord Add(int width, int height, bool writeFile = true)
            {
                var record = new CaptureRecord
                {
                    RelativeDirectory = Guid.NewGuid().ToString("N"), Width = width, Height = height,
                };
                _queue.Add(record);
                Directory.CreateDirectory(_queue.GetDirectory(record));
                if (writeFile)
                {
                    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                        Pattern(width, height), width * 4);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(_queue.GetFilePath(record, CaptureFileNames.Rendered));
                    encoder.Save(stream);
                }
                return record;
            }
        }

        internal string RenderedPath(CaptureRecord record) => _queue.GetFilePath(record, CaptureFileNames.Rendered);

        internal static byte[] Pattern(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 4;
                    pixels[offset] = (byte)x;
                    pixels[offset + 1] = (byte)y;
                    pixels[offset + 2] = (byte)(x * 7 + y * 3);
                    pixels[offset + 3] = 255;
                }
            }
            return pixels;
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
            throw new InvalidOperationException("The preview must not run OCR.");
    }

    private sealed class NoPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The preview must not run privacy detection.");
    }
}
