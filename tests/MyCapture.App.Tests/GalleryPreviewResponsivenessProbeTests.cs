using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
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
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// #221 opt-in probe. Opens the library preview of large PNG captures through the real toolbar
/// button and records, per sample, the click handler's own time, the longest UI-thread stall until
/// the preview's first render, and the time to that render. It uses only members that exist both
/// before and after the background decode, so the same file measures both revisions.
/// </summary>
public sealed class GalleryPreviewResponsivenessProbeTests
{
    internal const string OutputVariable = "MYCAPTURE_PREVIEW_PERF";
    private const string LabelVariable = "MYCAPTURE_PREVIEW_PERF_LABEL";
    private const int Warmups = 3, Measured = 10;
    private static readonly (int Width, int Height)[] Sizes = [(1920, 1080), (3840, 2160), (1920, 12000)];

    [GalleryPreviewPerfFact]
    public Task MeasurePreviewOpen() =>
        SettingsThemePreviewTests.RunIsolated(nameof(MeasurePreviewOpen), () =>
        {
            string output = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(OutputVariable)!);
            string label = Environment.GetEnvironmentVariable(LabelVariable) is { Length: > 0 } value ? value : "run";
            using var language = UiText.UseLanguage("ko-KR");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            var rows = new List<string> { "label,width,height,file_bytes,sample,phase,handler_ms,open_ms,longest_stall_ms" };
            var summary = new List<object>();
            try
            {
                using var fixture = new ProbeFixture(Sizes);
                GalleryWindow window = fixture.Window;
                window.Show();
                window.UpdateLayout();
                var previewButton = (Button)window.FindName("PreviewSelectedButton");
                var clock = Stopwatch.StartNew();
                using var watch = new UiStallWatch(window.Dispatcher);
                foreach ((CaptureRecord record, long fileBytes) in fixture.Images)
                {
                    var handler = new List<double>();
                    var open = new List<double>();
                    var stall = new List<double>();
                    for (int sample = 0; sample < Warmups + Measured; sample++)
                    {
                        Settle(window);
                        fixture.Model.Select(record.Id);
                        Settle(window);
                        (double handled, double opened, double longest) = Open(window, previewButton, record.Id, watch, clock);
                        bool measured = sample >= Warmups;
                        rows.Add(string.Join(',', label, record.Width, record.Height, fileBytes, sample,
                            measured ? "measured" : "warmup", Ms(handled), Ms(opened), Ms(longest)));
                        if (!measured) continue;
                        handler.Add(handled); open.Add(opened); stall.Add(longest);
                    }
                    summary.Add(new
                    {
                        width = record.Width, height = record.Height, file_bytes = fileBytes, n = Measured,
                        handler_ms = Stats(handler), open_ms = Stats(open), longest_stall_ms = Stats(stall),
                    });
                }
                Settle(window);
            }
            finally { application.Shutdown(); }

            string assembly = typeof(GalleryWindow).Assembly.Location;
            File.WriteAllLines(DiagnosticOutputPaths.Child(output, $"preview-{label}-samples.csv"), rows, Encoding.UTF8);
            File.WriteAllText(DiagnosticOutputPaths.Child(output, $"preview-{label}-summary.json"), JsonSerializer.Serialize(new
            {
                label,
                app_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
                processors = Environment.ProcessorCount,
                os = Environment.OSVersion.VersionString,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                warmups = Warmups,
                sizes = summary,
                notes = new[]
                {
                    "Each sample clicks PreviewSelectedButton on the UI thread and waits in a dispatcher frame until the preview's ContentRendered (PracticeSignals.PictureOpened).",
                    "handler_ms: the click handler's own time. longest_stall_ms: the longest wait of a Send-priority beat posted about every 1 ms from another thread, or the handler time if longer.",
                    "The PNG files are synthetic screenshot-like images; the OS file cache is warm after the first sample. n=10 per size: report p50 and max, not p95.",
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
        }, typeof(GalleryPreviewResponsivenessProbeTests));

    private static (double Handled, double Opened, double Longest) Open(GalleryWindow window, Button button, Guid id,
        UiStallWatch watch, Stopwatch clock)
    {
        var frame = new DispatcherFrame();
        double openedAt = double.NaN;
        void Opened(Guid recordId)
        {
            if (recordId != id || !double.IsNaN(openedAt)) return;
            openedAt = clock.Elapsed.TotalMilliseconds;
            frame.Continue = false;
        }
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Send,
            (_, _) => frame.Continue = false, window.Dispatcher);
        PracticeSignals.PictureOpened += Opened;
        try
        {
            watch.Reset();
            double start = clock.Elapsed.TotalMilliseconds;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            double handled = clock.Elapsed.TotalMilliseconds;
            timeout.Start();
            if (double.IsNaN(openedAt)) Dispatcher.PushFrame(frame);
            if (double.IsNaN(openedAt)) throw new TimeoutException("The preview did not render.");
            return (handled - start, openedAt - start, Math.Max(watch.MaxLatencyMs, handled - start));
        }
        finally
        {
            timeout.Stop();
            PracticeSignals.PictureOpened -= Opened;
        }
    }

    private static void Settle(GalleryWindow window)
    {
        foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
        for (int pass = 0; pass < 3; pass++)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static string Ms(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private static object Stats(List<double> values)
    {
        double[] sorted = [.. values.Order()];
        double p50 = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return new { p50 = Math.Round(p50, 3), min = Math.Round(sorted[0], 3), max = Math.Round(sorted[^1], 3), samples = values.Select(v => Math.Round(v, 3)).ToArray() };
    }

    /// <summary>Posts a Send-priority beat about every millisecond and keeps the longest wait since the last reset.</summary>
    private sealed class UiStallWatch : IDisposable
    {
        private readonly Dispatcher _ui;
        private readonly Thread _thread;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly AutoResetEvent _ran = new(false);
        private volatile bool _stop;
        private long _epoch;
        private long _maxTicks;

        internal UiStallWatch(Dispatcher ui)
        {
            _ui = ui;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Preview probe stall watch", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        internal double MaxLatencyMs => Interlocked.Read(ref _maxTicks) * 1000.0 / Stopwatch.Frequency;

        internal void Reset()
        {
            Interlocked.Increment(ref _epoch);
            Interlocked.Exchange(ref _maxTicks, 0);
        }

        private void Loop()
        {
            using var delay = new PreciseDelay();
            while (!_stop)
            {
                long epoch = Interlocked.Read(ref _epoch);
                long posted = _clock.ElapsedTicks;
                _ui.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    long waited = _clock.ElapsedTicks - posted;
                    if (Interlocked.Read(ref _epoch) == epoch)
                    {
                        long current = Interlocked.Read(ref _maxTicks);
                        while (waited > current)
                        {
                            long seen = Interlocked.CompareExchange(ref _maxTicks, waited, current);
                            if (seen == current) break;
                            current = seen;
                        }
                    }
                    _ran.Set();
                }));
                while (!_stop && !_ran.WaitOne(100)) { }
                delay.Wait(1);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ProbeFixture : IDisposable
    {
        private readonly OcrResultPresenter _presenter;
        internal GalleryWindow Window { get; }
        internal GalleryViewModel Model { get; }
        internal List<(CaptureRecord Record, long FileBytes)> Images { get; } = [];

        internal ProbeFixture((int Width, int Height)[] sizes)
        {
            string root = Path.Combine(Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")
                ?? Path.GetTempPath(), "preview-perf-" + Guid.NewGuid().ToString("N"));
            var paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var settings = new AppSettings();
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            foreach ((int width, int height) in sizes)
            {
                var record = new CaptureRecord { RelativeDirectory = Guid.NewGuid().ToString("N"), Width = width, Height = height };
                queue.Add(record);
                Directory.CreateDirectory(queue.GetDirectory(record));
                string file = queue.GetFilePath(record, CaptureFileNames.Rendered);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                    Screenshot(width, height), width * 4)));
                using (FileStream stream = File.Create(file)) encoder.Save(stream);
                Images.Add((record, new FileInfo(file).Length));
            }
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
        }

        // Light rows with dark glyph-like runs, so the PNG compresses like a text screenshot.
        private static byte[] Screenshot(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            var random = new Random(221);
            for (int y = 0; y < height; y++)
            {
                int band = y / 24;
                bool text = y % 24 is >= 6 and < 18;
                byte shade = (byte)(236 + band % 3 * 6);
                for (int x = 0; x < width; x++)
                {
                    byte value = text && (x / 9 + band) % 7 != 0 && random.Next(4) == 0 ? (byte)40 : shade;
                    int offset = (y * width + x) * 4;
                    pixels[offset] = value;
                    pixels[offset + 1] = value;
                    pixels[offset + 2] = (byte)Math.Min(255, value + 8);
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
            throw new InvalidOperationException("The preview probe must not run OCR.");
    }

    private sealed class NoPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The preview probe must not run privacy detection.");
    }
}

internal sealed class GalleryPreviewPerfFactAttribute : FactAttribute
{
    public GalleryPreviewPerfFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GalleryPreviewResponsivenessProbeTests.OutputVariable)))
            Skip = "Opt-in preview probe: set MYCAPTURE_PREVIEW_PERF to an output directory.";
    }
}
