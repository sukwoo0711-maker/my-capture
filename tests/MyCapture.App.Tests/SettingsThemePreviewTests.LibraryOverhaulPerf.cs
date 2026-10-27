using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Measures the library with many mixed items (images, videos, sources): objects per realized card, the first valid
/// thumbnail, scrolling, memory after hiding and repeated reopening. Set MYCAPTURE_LIBRARY_OVERHAUL_PERF to an output
/// directory (and MYCAPTURE_LIBRARY_OVERHAUL_PHASE to a name). This is an opt-in measurement: without that variable
/// the test does nothing, so a normal test-suite pass provides no performance result. The same file runs against
/// the build before and after the change. Times cover dispatcher/layout work in an offscreen window, not rendered
/// frame rate; allocation counts cover the UI thread only, excluding background thumbnail decoding.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    [Trait("Category", "OptInPerformance")]
    public Task LibraryOverhaulPerf_MeasuresOpenScrollHideAndReopenWithManyMixedItems() =>
        Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_OVERHAUL_PERF") is not { Length: > 0 } requested
            ? Task.CompletedTask
            : RunIsolated(nameof(LibraryOverhaulPerf_MeasuresOpenScrollHideAndReopenWithManyMixedItems), () =>
            {
                string phase = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_OVERHAUL_PHASE") ?? "after";
                Application application = CreateLibraryMotionApplication();
                using var scope = UiText.UseLanguage("ko-KR");
                EvidenceLibrary library = CreateEvidenceLibrary(600, "mc-overhaul-perf-");
                for (int index = 0; index < library.Records.Count; index++)
                {
                    CaptureRecord record = library.Records[index];
                    if (index % 5 == 0) { record.MediaKind = CaptureMediaKind.Video; record.DurationMs = 12_000 + index; }
                    if (index % 3 == 0) record.SourcePageUrl = $"https://example.com/page/{index}";
                    if (index % 7 == 0) { record.SourceAppName = "Excel"; record.SourceDocumentPath = $@"C:\Specs\capture-{index}.xlsx"; }
                    if (index % 4 == 0) record.HasAnnotations = true;
                }
                library.ViewModel.Refresh();
                GalleryWindow window = library.Window;
                var report = new StringBuilder();
                try
                {
                    var rows = (ListBox)window.FindName("RowsList");
                    Process process = Process.GetCurrentProcess();
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    var timer = Stopwatch.StartNew();
                    window.Show();
                    SettleGalleryLayout(window);
                    double shownMs = timer.Elapsed.TotalMilliseconds;
                    PumpLibraryUntil(window, () => Descendants(rows).OfType<GalleryThumbnailImage>().Any(image => image.Source is not null), "first thumbnail");
                    double firstThumbnailMs = timer.Elapsed.TotalMilliseconds;
                    // Also wait for six decoded pictures before measuring the realized cards.
                    PumpLibraryUntil(window, () => Descendants(rows).OfType<GalleryThumbnailImage>().Count(image => image.Source is not null) >= 6, "thumbnails");
                    double firstThumbnailsMs = timer.Elapsed.TotalMilliseconds;
                    long openAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    // Only numbers leave the helper; no realized card/menu is rooted across scroll or collection.
                    LibraryCardObjectCounts objects = CountLibraryCardObjects(rows);

                    // Scrolling: 30 steps of 260 DIPs through the library, each laid out.
                    var scroll = Descendants(rows).OfType<ScrollViewer>().First();
                    var stepMs = new List<double>();
                    long scrollBefore = GC.GetAllocatedBytesForCurrentThread();
                    for (int step = 1; step <= 30; step++)
                    {
                        var stepTimer = Stopwatch.StartNew();
                        scroll.ScrollToVerticalOffset(step * 260);
                        SettleGalleryLayout(window);
                        stepMs.Add(stepTimer.Elapsed.TotalMilliseconds);
                    }
                    long scrollAllocated = GC.GetAllocatedBytesForCurrentThread() - scrollBefore;
                    stepMs.Sort();
                    scroll.ScrollToVerticalOffset(0);
                    SettleGalleryLayout(window);

                    // Hide, then memory after collecting; and five reopenings in the same process.
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long managedVisible = GC.GetTotalMemory(true);
                    window.Hide();
                    SettleGalleryLayout(window);
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long managedHidden = GC.GetTotalMemory(true);
                    process.Refresh();
                    long privateHidden = process.PrivateMemorySize64;
                    var reopenLayoutMs = new List<double>();
                    var reopenFirstThumbnailMs = new List<double>();
                    for (int index = 0; index < 5; index++)
                    {
                        var reopen = Stopwatch.StartNew();
                        window.Show();
                        SettleGalleryLayout(window);
                        reopenLayoutMs.Add(reopen.Elapsed.TotalMilliseconds);
                        PumpLibraryUntil(window, () => Descendants(rows).OfType<GalleryThumbnailImage>().Any(image => image.Source is not null), "first reopened thumbnail");
                        reopenFirstThumbnailMs.Add(reopen.Elapsed.TotalMilliseconds);
                        PumpLibraryUntil(window, () => Descendants(rows).OfType<GalleryThumbnailImage>().Count(image => image.Source is not null) >= 6, "reopened thumbnails");
                        window.Hide();
                        SettleGalleryLayout(window);
                    }

                    report.AppendLine($"phase={phase}");
                    report.AppendLine("measurement=opt-in offscreen-dispatcher-and-layout allocations=UI-thread-only");
                    report.AppendLine($"realizedCards={objects.Cards} distinctCardMenus={objects.Menus} cardToolTips={objects.ToolTips} visualElementsInFirstCard={objects.FirstCardElements}");
                    report.AppendLine($"showAndLayoutMs={shownMs:F1} firstThumbnailMs={firstThumbnailMs:F1} firstSixThumbnailsMs={firstThumbnailsMs:F1} openUiThreadAllocatedKB={openAllocated / 1024}");
                    report.AppendLine($"scrollStepMsMedian={stepMs[stepMs.Count / 2]:F1} scrollStepMsP95={stepMs[(int)Math.Ceiling(stepMs.Count * 0.95) - 1]:F1} scrollStepMsMax={stepMs[^1]:F1} scrollUiThreadAllocatedKB={scrollAllocated / 1024}");
                    report.AppendLine($"managedVisibleKB={managedVisible / 1024} managedAfterHideKB={managedHidden / 1024} privateAfterHideKB={privateHidden / 1024}");
                    report.AppendLine($"reopenLayoutMs={string.Join(", ", reopenLayoutMs.Select(value => value.ToString("F1")))}");
                    report.AppendLine($"reopenFirstThumbnailMs={string.Join(", ", reopenFirstThumbnailMs.Select(value => value.ToString("F1")))}");
                }
                finally
                {
                    window.CloseForExit();
                    application.Shutdown();
                }

                string directory = DiagnosticOutputPaths.Create(requested);
                File.AppendAllText(DiagnosticOutputPaths.Child(directory, $"library-perf-{phase}.txt"), report.ToString() + Environment.NewLine);
            });

    private readonly record struct LibraryCardObjectCounts(int Cards, int Menus, int ToolTips, int FirstCardElements);

    // Keep these temporary strong references in a separate frame even in Release/JIT-optimized runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LibraryCardObjectCounts CountLibraryCardObjects(ListBox rows)
    {
        ListBoxItem[] realized = Descendants(rows).OfType<ListBoxItem>()
            .Where(item => item.DataContext is GalleryItemViewModel).ToArray();
        var menus = new HashSet<ContextMenu>(ReferenceEqualityComparer.Instance);
        foreach (ListBoxItem item in realized)
        {
            if (item.ContextMenu is { } own) menus.Add(own);
            foreach (Button button in Descendants(item).OfType<Button>())
                if (button.ContextMenu is { } buttonMenu) menus.Add(buttonMenu);
        }
        int toolTips = realized.Sum(item => Descendants(item).OfType<FrameworkElement>().Count(element => element.ToolTip is ToolTip));
        int elements = realized.Length == 0 ? 0 : Descendants(realized[0]).Count();
        return new LibraryCardObjectCounts(realized.Length, menus.Count, toolTips, elements);
    }
}
