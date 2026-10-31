using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in measurement (MYCAPTURE_WRAP_LAYOUT_COST = report file): the layout cost of keeping
/// Korean words whole in the settings window, the library and the shortcut help (the window with
/// the most joined blocks of the three: 20, against 6 and 5). Each sample builds the real
/// window in Korean, shows it offscreen and times the first layout, then times a full
/// re-measure of every text block. Word keeping is switched off and on for the same build;
/// runs alternate so warm-up and caches affect both alike. Run it alone in its own process.
/// </summary>
public sealed class KoreanWrapLayoutCostTests
{
    private const int Samples = 9;

    [Fact]
    public void MeasureSettingsAndLibraryLayoutWithAndWithoutWordKeeping()
    {
        string? report = Environment.GetEnvironmentVariable("MYCAPTURE_WRAP_LAYOUT_COST");
        if (string.IsNullOrWhiteSpace(report)) return;
        StaTestHost.Run(() =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            ThemeService.Apply(AppTheme.Daylight);
            bool? previous = KoreanWordWrap.EnabledOverrideForTest;
            var results = new Dictionary<string, List<(double First, double Relayout, int Joined)>>();
            try
            {
                foreach (string surface in new[] { "settings", "library", "shortcuts" })
                {
                    // One warm-up per mode, then alternating samples.
                    foreach (bool keep in new[] { false, true }) Sample(surface, keep);
                    for (int index = 0; index < Samples; index++)
                    {
                        foreach (bool keep in index % 2 == 0 ? new[] { false, true } : new[] { true, false })
                        {
                            string label = $"{surface} {(keep ? "keep-words" : "plain")}";
                            if (!results.TryGetValue(label, out var list)) results[label] = list = [];
                            list.Add(Sample(surface, keep));
                        }
                    }
                }
            }
            finally { KoreanWordWrap.EnabledOverrideForTest = previous; }

            var text = new StringBuilder($"Korean word keeping layout cost; median of {Samples} samples (ms)\n");
            foreach ((string label, var list) in results)
            {
                text.AppendLine($"{label}: first layout {Median(list.Select(sample => sample.First)):0.00}"
                    + $" (all {string.Join(", ", list.Select(sample => sample.First.ToString("0.0")))});"
                    + $" relayout {Median(list.Select(sample => sample.Relayout)):0.00}"
                    + $" (all {string.Join(", ", list.Select(sample => sample.Relayout.ToString("0.0")))});"
                    + $" text blocks with joiners {list[0].Joined}");
            }
            File.WriteAllText(report, text.ToString());
        });
    }

    private static (double First, double Relayout, int Joined) Sample(string surface, bool keep)
    {
        KoreanWordWrap.EnabledOverrideForTest = keep;
        // Earlier samples' windows must not be collected inside this one's timing.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var clock = Stopwatch.StartNew();
        Window window = surface switch
        {
            "settings" => CreateSettings(),
            "shortcuts" => new ShortcutHelpWindow(),
            _ => CreateLibrary(),
        };
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000; window.Width = 940; window.Height = 700;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            window.UpdateLayout();
            double first = clock.Elapsed.TotalMilliseconds;
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            TextBlock[] blocks = Descendants(window).OfType<TextBlock>().ToArray();
            int joined = blocks.Count(block => block.Text.Contains(KoreanWordWrap.WordJoiner));
            clock.Restart();
            foreach (TextBlock block in blocks) block.InvalidateMeasure();
            window.UpdateLayout();
            double relayout = clock.Elapsed.TotalMilliseconds;
            return (first, relayout, joined);
        }
        finally
        {
            if (window is GalleryWindow gallery) gallery.CloseForExit();
            else window.Close();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }
    }

    private static Window CreateSettings()
    {
        var settings = new AppSettings();
        return new SettingsWindow(() => settings, _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance);
    }

    private static Window CreateLibrary()
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-wrap-cost-").FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var ocr = new NoOcr();
        var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
            NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
        var vm = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96);
        return new GalleryWindow(vm, controller, new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
            new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
            NullLoggerFactory.Instance, presenter, () => settings.Ocr, indexing, new NoPrivacy(), NullLogger.Instance);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class NoPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(System.Windows.Media.Imaging.BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The layout measurement never runs OCR.");
    }

    private sealed class NoOcr : IOcrService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<string> SupportedLanguages => [];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The layout measurement never runs OCR.");
    }
}
