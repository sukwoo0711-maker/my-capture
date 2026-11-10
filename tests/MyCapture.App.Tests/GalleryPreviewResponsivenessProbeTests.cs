using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
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
/// before and after the background decode, so the same file measures both revisions. The library
/// sits in the primary monitor's work area, so the preview opens there wherever the pointer is.
/// </summary>
public sealed class GalleryPreviewResponsivenessProbeTests
{
    internal const string OutputVariable = "MYCAPTURE_PREVIEW_PERF";
    private const string LabelVariable = "MYCAPTURE_PREVIEW_PERF_LABEL";
    // Comma-separated WIDTHxHEIGHT; the isolated child has 150 s, so a run can measure one size.
    private const string SizesVariable = "MYCAPTURE_PREVIEW_PERF_SIZES";
    private const int Warmups = 3, Measured = 20, StallSamples = 5;
    // A sample whose preview did not open inside the primary work area is skipped; this many tries at most.
    private const int MaxSamples = Warmups + 2 * Measured;
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
            var rows = new List<string>
            {
                "label,width,height,file_bytes,sample,phase,handler_ms,open_ms,longest_stall_ms,preview_on_primary,cursor_x,cursor_y,foreign_send_ops",
            };
            var summary = new List<object>();
            Rect32 primaryWork = PrimaryWorkArea();
            (int X, int Y) cursorAtStart = Cursor();
            (int Width, int Height)[] sizes = Environment.GetEnvironmentVariable(SizesVariable) is { Length: > 0 } list
                ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseSize)]
                : Sizes;
            try
            {
                using var fixture = new ProbeFixture(sizes);
                GalleryWindow window = fixture.Window;
                window.Show();
                window.UpdateLayout();
                var previewButton = (Button)window.FindName("PreviewSelectedButton");
                var clock = Stopwatch.StartNew();
                using var foreign = new ForeignSendCounter(window.Dispatcher);
                foreach ((CaptureRecord record, long fileBytes) in fixture.Images)
                {
                    var handler = new List<double>();
                    var open = new List<double>();
                    var foreignSend = new List<double>();
                    var stall = new List<double>();
                    var stallOpen = new List<double>();
                    int skipped = 0;
                    // Opening times come from samples without the stall watch: its beat, posted about every
                    // millisecond, is a posted message, and WPF holds Input-priority work such as
                    // ContentRendered while one is queued.
                    for (int sample = 0; sample < MaxSamples && handler.Count < Measured; sample++)
                    {
                        (string phase, double handled, double opened, _, long others) =
                            Sample(window, fixture, previewButton, record, fileBytes, sample, watch: null, foreign, clock, primaryWork, label, rows);
                        if (phase == "skipped") skipped++;
                        if (phase != "measured") continue;
                        handler.Add(handled); open.Add(opened); foreignSend.Add(others);
                    }
                    using (var watch = new UiStallWatch(window.Dispatcher))
                    {
                        for (int sample = 0; sample < StallSamples; sample++)
                        {
                            (string phase, _, double opened, double longest, _) =
                                Sample(window, fixture, previewButton, record, fileBytes, sample, watch, foreign, clock, primaryWork, label, rows);
                            if (phase != "stall") continue;
                            stall.Add(longest); stallOpen.Add(opened);
                        }
                    }
                    summary.Add(new
                    {
                        width = record.Width, height = record.Height, file_bytes = fileBytes, n = handler.Count,
                        skipped_off_primary = skipped,
                        handler_ms = Stats(handler), open_ms = Stats(open), foreign_send_ops = Stats(foreignSend),
                        longest_stall_ms = Stats(stall), open_ms_with_stall_watch = Stats(stallOpen),
                    });
                }
                Settle(window);
            }
            finally { application.Shutdown(); }
            (int X, int Y) cursorAtEnd = Cursor();

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
                primary_work_area_px = new { left = primaryWork.Left, top = primaryWork.Top, right = primaryWork.Right, bottom = primaryWork.Bottom },
                cursor_px = new { start = new { x = cursorAtStart.X, y = cursorAtStart.Y }, end = new { x = cursorAtEnd.X, y = cursorAtEnd.Y } },
                sizes = summary,
                notes = new[]
                {
                    "Each sample clicks PreviewSelectedButton on the UI thread and waits in a dispatcher frame until the preview's ContentRendered (PracticeSignals.PictureOpened).",
                    "handler_ms and open_ms: the click handler's own time and the time to ContentRendered, from samples with no stall watch (phase measured).",
                    "longest_stall_ms: the longest wait of a Send-priority beat posted about every 1 ms from another thread, or the handler time if longer, from separate samples (phase stall). The beat is a posted message, which holds Input-priority work such as ContentRendered, so open_ms_with_stall_watch is not an opening time.",
                    "The library window sits inside the primary monitor's work area (SystemParameters.WorkArea), so the preview, centred on it, opens there whatever monitor the pointer is on. A sample whose preview window rectangle (GetWindowRect) is not inside the primary monitor's work area is recorded as skipped and left out. cursor_x/cursor_y: the pointer read with GetCursorPos; the probe never moves it.",
                    "foreign_send_ops: Send-priority operations other threads posted to the library's dispatcher between the click and ContentRendered (Dispatcher.Hooks.OperationPosted). A UI Automation client's calls arrive this way (ElementUtil.Invoke), and work at a lower priority waits behind them, so a sample with hundreds of them measured the client as well. The count includes the preview's own completion when it resumes from the pool at Send priority (one per opening), and in stall samples the stall watch's beats.",
                    "The PNG files are synthetic screenshot-like images; the OS file cache is warm after the first sample. n=20 measured per size and run; p50 and p90 interpolate linearly between ranks.",
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
        }, typeof(GalleryPreviewResponsivenessProbeTests));

    private static (int Width, int Height) ParseSize(string size)
    {
        string[] parts = size.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int width)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int height) && width > 0 && height > 0
            ? (width, height) : throw new FormatException($"{SizesVariable} takes WIDTHxHEIGHT values, not '{size}'.");
    }

    // One preview opening from a settled library; its row says how the sample counts.
    private static (string Phase, double Handled, double Opened, double Longest, long ForeignSend) Sample(GalleryWindow window,
        ProbeFixture fixture, Button button, CaptureRecord record, long fileBytes, int sample, UiStallWatch? watch,
        ForeignSendCounter foreign, Stopwatch clock, Rect32 primaryWork, string label, List<string> rows)
    {
        Settle(window);
        fixture.Model.Select(record.Id);
        Settle(window);
        (int cursorX, int cursorY) = Cursor();
        foreign.Reset();
        (double handled, double opened, double longest, bool onPrimary) = Open(window, button, record.Id, watch, clock, primaryWork);
        long others = foreign.Count;
        string phase = !onPrimary ? "skipped" : watch is not null ? "stall" : sample < Warmups ? "warmup" : "measured";
        rows.Add(string.Join(',', label, record.Width, record.Height, fileBytes, sample, phase,
            Ms(handled), Ms(opened), Ms(longest), onPrimary ? 1 : 0, cursorX, cursorY, others));
        return (phase, handled, opened, longest, others);
    }

    private static (double Handled, double Opened, double Longest, bool OnPrimary) Open(GalleryWindow window, Button button, Guid id,
        UiStallWatch? watch, Stopwatch clock, Rect32 primaryWork)
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
            watch?.Reset();
            double start = clock.Elapsed.TotalMilliseconds;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            double handled = clock.Elapsed.TotalMilliseconds;
            timeout.Start();
            if (double.IsNaN(openedAt)) Dispatcher.PushFrame(frame);
            if (double.IsNaN(openedAt)) throw new TimeoutException("The preview did not render.");
            bool onPrimary = window.OwnedWindows.Cast<Window>().SingleOrDefault() is { } preview
                && GetWindowRect(new WindowInteropHelper(preview).Handle, out Rect32 bounds)
                && bounds.Left >= primaryWork.Left && bounds.Top >= primaryWork.Top
                && bounds.Right <= primaryWork.Right && bounds.Bottom <= primaryWork.Bottom;
            return (handled - start, openedAt - start, Math.Max(watch?.MaxLatencyMs ?? 0, handled - start), onPrimary);
        }
        finally
        {
            timeout.Stop();
            PracticeSignals.PictureOpened -= Opened;
        }
    }

    private static Rect32 PrimaryWorkArea()
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(new Point32(0, 0), MonitorDefaultToPrimary), ref info))
            throw new InvalidOperationException("The primary monitor's work area could not be read.");
        return info.Work;
    }

    private static (int X, int Y) Cursor() => GetCursorPos(out Point32 point) ? (point.X, point.Y) : (int.MinValue, int.MinValue);

    private const uint MonitorDefaultToPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

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
        if (values.Count == 0) return new { n = 0 };
        double[] sorted = [.. values.Order()];
        return new
        {
            n = sorted.Length,
            p50 = Math.Round(Percentile(sorted, 0.5), 3), p90 = Math.Round(Percentile(sorted, 0.9), 3),
            min = Math.Round(sorted[0], 3), max = Math.Round(sorted[^1], 3),
            samples = values.Select(v => Math.Round(v, 3)).ToArray(),
        };
    }

    // Linear interpolation between the closest ranks (numpy's default, Excel's PERCENTILE.INC).
    private static double Percentile(double[] sorted, double fraction)
    {
        double position = fraction * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
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

    /// <summary>Counts the Send-priority operations other threads post to a dispatcher since the last reset.</summary>
    private sealed class ForeignSendCounter : IDisposable
    {
        private readonly Dispatcher _ui;
        private long _count;

        internal ForeignSendCounter(Dispatcher ui)
        {
            _ui = ui;
            _ui.Hooks.OperationPosted += OnPosted;
        }

        internal long Count => Interlocked.Read(ref _count);

        internal void Reset() => Interlocked.Exchange(ref _count, 0);

        // Raised on the thread that posted the operation.
        private void OnPosted(object? sender, DispatcherHookEventArgs e)
        {
            if (e.Operation.Priority == DispatcherPriority.Send && !_ui.CheckAccess()) Interlocked.Increment(ref _count);
        }

        public void Dispose() => _ui.Hooks.OperationPosted -= OnPosted;
    }

    private sealed class ProbeFixture : IDisposable
    {
        private readonly OcrResultPresenter _presenter;
        internal GalleryWindow Window { get; }
        internal GalleryViewModel Model { get; }
        internal List<(CaptureRecord Record, long FileBytes)> Images { get; } = [];

        internal ProbeFixture((int Width, int Height)[] sizes)
        {
            string root = OwnedTestDirectory.Create("preview-perf-");
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
            Rect work = SystemParameters.WorkArea;
            Window = new GalleryWindow(Model, controller,
                new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
                new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
                NullLoggerFactory.Instance, _presenter, () => settings.Ocr, indexing, new NoPrivacy(), NullLogger.Instance)
            {
                // Inside the primary monitor's work area, so the preview, centred on this window, opens on that
                // monitor whatever monitor the pointer is on.
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false,
                Left = work.Left + 24, Top = work.Top + 24,
                Width = Math.Min(1280, work.Width - 48), Height = Math.Min(860, work.Height - 48),
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
