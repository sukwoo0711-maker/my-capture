using System.Diagnostics;
using System.IO;
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
/// directory (and MYCAPTURE_LIBRARY_OVERHAUL_PHASE to a name); without it the test does nothing. The same file runs
/// against the build before and after the change.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
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
                    // The first valid thumbnails: at least six realized cards with a decoded picture.
                    PumpLibraryUntil(window, () => Descendants(rows).OfType<GalleryThumbnailImage>().Count(image => image.Source is not null) >= 6, "thumbnails");
                    double firstThumbnailsMs = timer.Elapsed.TotalMilliseconds;
                    long openAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    ListBoxItem[] realized = Descendants(rows).OfType<ListBoxItem>().Where(item => item.DataContext is GalleryItemViewModel).ToArray();
                    // Objects the realized cards own: card menus (the container's and the More button's) and source badge menus.
                    var menus = new HashSet<ContextMenu>(ReferenceEqualityComparer.Instance);
                    foreach (ListBoxItem item in realized)
                    {
                        if (item.ContextMenu is { } own) menus.Add(own);
                        foreach (Button button in Descendants(item).OfType<Button>())
                            if (button.ContextMenu is { } buttonMenu) menus.Add(buttonMenu);
                    }
                    int toolTips = realized.Sum(item => Descendants(item).OfType<FrameworkElement>().Count(element => element.ToolTip is ToolTip));
                    int elementsPerCard = realized.Length == 0 ? 0 : Descendants(realized[0]).Count();

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
                    var reopenMs = new List<double>();
                    for (int index = 0; index < 5; index++)
                    {
                        var reopen = Stopwatch.StartNew();
                        window.Show();
                        SettleGalleryLayout(window);
                        reopenMs.Add(reopen.Elapsed.TotalMilliseconds);
                        PumpLibraryUntil(window, () => Descendants(rows).OfType<GalleryThumbnailImage>().Count(image => image.Source is not null) >= 6, "reopened thumbnails");
                        window.Hide();
                        SettleGalleryLayout(window);
                    }

                    report.AppendLine($"phase={phase}");
                    report.AppendLine($"realizedCards={realized.Length} distinctCardMenus={menus.Count} cardToolTips={toolTips} visualElementsInFirstCard={elementsPerCard}");
                    report.AppendLine($"showAndLayoutMs={shownMs:F1} firstSixThumbnailsMs={firstThumbnailsMs:F1} openAllocatedKB={openAllocated / 1024}");
                    report.AppendLine($"scrollStepMsMedian={stepMs[stepMs.Count / 2]:F1} scrollStepMsP95={stepMs[(int)Math.Ceiling(stepMs.Count * 0.95) - 1]:F1} scrollStepMsMax={stepMs[^1]:F1} scrollAllocatedKB={scrollAllocated / 1024}");
                    report.AppendLine($"managedVisibleKB={managedVisible / 1024} managedAfterHideKB={managedHidden / 1024} privateAfterHideKB={privateHidden / 1024}");
                    report.AppendLine($"reopenMs={string.Join(", ", reopenMs.Select(value => value.ToString("F1")))}");
                }
                finally
                {
                    window.CloseForExit();
                    application.Shutdown();
                }

                string directory = DiagnosticOutputPaths.Create(requested);
                File.AppendAllText(DiagnosticOutputPaths.Child(directory, $"library-perf-{phase}.txt"), report.ToString() + Environment.NewLine);
            });
}
