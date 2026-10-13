using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
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
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in evidence for the library and settings review: PNGs of the empty and filled library, the
/// restore menu and the About, Storage and Editing settings pages (Korean and English, normal and
/// minimum size), and the timings of library open (1,000 and 10,000 captures), delete and undo of
/// 100 captures, restore-menu open and settings open. Set <c>MYCAPTURE_LIBSET_EVIDENCE</c> to a
/// directory and <c>MYCAPTURE_LIBSET_PHASE</c> to a label; without the directory nothing runs.
/// Everything lives in temporary roots with a fake trash: the real Recycle Bin is never used.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    private const string LibrarySettingsEvidenceVariable = "MYCAPTURE_LIBSET_EVIDENCE";

    [Fact]
    public Task LibrarySettings_EvidenceImagesAndTimings()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LibrarySettingsEvidenceVariable))) return Task.CompletedTask;
        return RunIsolated(nameof(LibrarySettings_EvidenceImagesAndTimings), VerifyLibrarySettingsEvidence);
    }

    [Fact]
    public Task LibrarySettings_LargeLibraryTimings()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LibrarySettingsEvidenceVariable))
            || Environment.GetEnvironmentVariable("MYCAPTURE_LIBSET_ONLY") == "settings") return Task.CompletedTask;
        return RunIsolated(nameof(LibrarySettings_LargeLibraryTimings), VerifyLargeLibraryTimings);
    }

    private static string LibrarySettingsPhase =>
        Environment.GetEnvironmentVariable("MYCAPTURE_LIBSET_PHASE") is { Length: > 0 } phase ? phase : "after";

    private static string LibrarySettingsDirectory =>
        DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(LibrarySettingsEvidenceVariable)!);

    private static void VerifyLibrarySettingsEvidence()
    {
        Application application = CreateLibraryApplication();
        var timings = new List<string>();
        try
        {
            // MYCAPTURE_LIBSET_TIMINGS_ONLY=1 skips the images for repeated timing runs.
            bool images = Environment.GetEnvironmentVariable("MYCAPTURE_LIBSET_TIMINGS_ONLY") != "1";
            foreach (string language in images ? new[] { "ko-KR", "en-US" } : [])
            {
                using var scope = UiText.UseLanguage(language);
                RenderLibraryEvidence(language);
                RenderSettingsEvidence(language);
            }

            using (UiText.UseLanguage("ko-KR"))
            {
                // MYCAPTURE_LIBSET_ONLY=settings limits a timing run to the settings window in a fresh
                // process: closed library windows of the same process slow later window construction.
                if (Environment.GetEnvironmentVariable("MYCAPTURE_LIBSET_ONLY") != "settings")
                {
                    MeasureLibraryOpen(1_000, timings);
                    MeasureDeleteUndoAndMenu(timings);
                }
                MeasureSettingsOpen(timings);
            }
        }
        finally
        {
            File.AppendAllLines(DiagnosticOutputPaths.Child(LibrarySettingsDirectory, $"{LibrarySettingsPhase}-timings.txt"), timings);
            application.Shutdown();
        }
    }

    private static void VerifyLargeLibraryTimings()
    {
        Application application = CreateLibraryApplication();
        var timings = new List<string>();
        try
        {
            using var scope = UiText.UseLanguage("ko-KR");
            MeasureLibraryOpen(10_000, timings);
        }
        finally
        {
            File.AppendAllLines(DiagnosticOutputPaths.Child(LibrarySettingsDirectory, $"{LibrarySettingsPhase}-timings.txt"), timings);
            application.Shutdown();
        }
    }

    private static Application CreateLibraryApplication()
    {
        Application application = CreateThemedApplication();
        application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
        application.Resources["PinLabel"] = new PinLabelConverter();
        application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
        ThemeService.Apply(AppTheme.Glass);
        return application;
    }

    // ---- Library fixture -------------------------------------------------------------

    private sealed record EvidenceLibrary(GalleryWindow Window, GalleryViewModel ViewModel, CaptureQueue Queue,
        RecoverableLibraryService Recovery, AppPaths Paths, IReadOnlyList<CaptureRecord> Records);

    private static byte[] EvidenceThumbnailPng(Color color)
    {
        const int width = 160, height = 100;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool band = y is > 18 and < 30 || (y is > 40 and < 48 && x < width * 2 / 3);
                int offset = (y * width + x) * 4;
                pixels[offset] = band ? (byte)0xF5 : color.B;
                pixels[offset + 1] = band ? (byte)0xF5 : color.G;
                pixels[offset + 2] = band ? (byte)0xF5 : color.R;
                pixels[offset + 3] = 0xFF;
            }
        }
        return ImageCodec.EncodePng(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4));
    }

    private static EvidenceLibrary CreateEvidenceLibrary(int count, string prefix)
    {
        string root = TestRecycleBin.CreateTempSubdirectory(prefix).FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        settings.Queue.MaxItems = Math.Max(settings.Queue.MaxItems, count + 10);
        settings.Queue.MaxBytes = long.MaxValue / 4;
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        Color[] colors = [Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0xF5, 0x9E, 0x0B),
            Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x06, 0xB6, 0xD4)];
        byte[][] pngs = colors.Select(EvidenceThumbnailPng).ToArray();
        DateTimeOffset now = DateTimeOffset.Now;
        var records = new List<CaptureRecord>(count);
        using (queue.SuspendEviction())
        {
            for (int index = 0; index < count; index++)
            {
                DateTimeOffset created = now.AddMinutes(-index * 17);
                var record = new CaptureRecord
                {
                    Width = 160, Height = 100, Title = index % 3 == 0 ? $"Review sample {index + 1}" : string.Empty,
                    CreatedAt = created, UpdatedAt = created, TotalBytes = 4096, OcrText = "Evidence fixture", OcrContentRevision = 0,
                };
                queue.Add(record);
                string directory = queue.GetDirectory(record);
                Directory.CreateDirectory(directory);
                byte[] png = pngs[index % pngs.Length];
                File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Thumbnail), png);
                File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), png);
                records.Add(record);
            }
        }
        queue.Save();
        var recovery = new RecoverableLibraryService(queue, paths, new EvidenceTrash(Path.Combine(root, "fake-trash")), NullLogger.Instance);
        queue.PrepareRemoval = recovery.PrepareRemoval;
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance) { Recovery = recovery };
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var ocr = new GalleryCompactOcr();
        var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
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
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Width = 1280, Height = 760,
        };
        window.ReadHotkeys = () => settings.Hotkeys;
        window.Closed += (_, _) => presenter.Dispose();
        FluidMotion.SetWindowEntrance(window, false);
        return new EvidenceLibrary(window, vm, queue, recovery, paths, records);
    }

    /// <summary>Moves directories into an app-owned temporary folder; never the Windows Recycle Bin.</summary>
    private sealed class EvidenceTrash(string root) : IRecoverableTrash
    {
        private readonly WindowsRecycleBin _identity = new();
        private readonly Dictionary<string, RecycledPathReceipt> _receipts = new(StringComparer.OrdinalIgnoreCase);
        public string GetIdentity(string path) => _identity.GetIdentity(path);
        public RecycledPathReceipt RecycleDirectory(string path)
        {
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, Guid.NewGuid().ToString("N"));
            string identity = GetIdentity(path);
            Directory.Move(path, target);
            var receipt = new RecycledPathReceipt(path, target, identity, "evidence-shell-id", true);
            lock (_receipts) _receipts[path] = receipt;
            return receipt;
        }
        public void RestoreDirectory(RecycledPathReceipt receipt)
        {
            if (Directory.Exists(receipt.OriginalPath)) throw new IOException("restore collision");
            Directory.Move(receipt.RecycledPath, receipt.OriginalPath);
        }
        public RecycledPathReceipt? FindRecycledDirectory(string originalPath, string identity)
        {
            lock (_receipts)
                return _receipts.TryGetValue(originalPath, out RecycledPathReceipt? receipt) && receipt.Identity == identity
                    && Directory.Exists(receipt.RecycledPath) ? receipt : null;
        }
    }

    // ---- Images ----------------------------------------------------------------------

    private static readonly (string Name, double Width, double Height)[] LibraryEvidenceSizes =
        [("normal", 1280, 760), ("compact", 980, 560)];

    private static void RenderLibraryEvidence(string language)
    {
        foreach (int count in new[] { 0, 12 })
        {
            EvidenceLibrary library = CreateEvidenceLibrary(count, "mc-libset-evidence-");
            try
            {
                library.Window.Show();
                library.Window.ShowGallery();
                foreach ((string size, double width, double height) in LibraryEvidenceSizes)
                {
                    library.Window.Width = width;
                    library.Window.Height = height;
                    SettleLibrary(library.Window);
                    SaveLibraryEvidencePng(library.Window, $"library-{(count == 0 ? "empty" : "normal")}-{language}-{size}.png");
                }

                if (count > 0)
                {
                    // Three deletions of different ages fill the restore menu.
                    foreach (int take in new[] { 1, 3, 2 })
                    {
                        Guid[] ids = library.Queue.Records.Take(take).Select(record => record.Id).ToArray();
                        PumpUiTask(library.Window, () => library.Queue.RemoveManyAsync(ids));
                    }
                    library.Window.RefreshFromQueue();
                    library.Window.Width = 1280;
                    library.Window.Height = 760;
                    SettleLibrary(library.Window);
                    OpenRestoreMenu(library.Window);
                    SaveRestoreMenuPng(library.Window, $"restore-menu-{language}.png");
                    CloseRestoreMenu(library.Window);

                    // The status line after a delete (Undo), with a selection (More stays reachable).
                    library.Window.ConfirmDelete = (_, _) => true;
                    Guid[] more = library.Queue.Records.Take(2).Select(record => record.Id).ToArray();
                    PumpUiTask(library.Window, async () => { await library.Window.DeleteCapturesAsync(more); return true; });
                    library.ViewModel.Select(library.Queue.Records[0].Id);
                    SettleLibrary(library.Window);
                    SaveLibraryEvidencePng(library.Window, $"library-deleted-undo-{language}.png");
                }
            }
            finally
            {
                library.Window.CloseForExit();
            }
        }
    }

    private static void SettleLibrary(Window window)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void OpenRestoreMenu(GalleryWindow window)
    {
        var more = (Button)window.FindName("MoreCommandsButton");
        ContextMenu menu = more.ContextMenu!;
        menu.PlacementTarget = more;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        MethodInfo populate = typeof(GalleryWindow).GetMethod("PopulateDeletedCapturesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        PumpUiTask(window, async () => { await (Task)populate.Invoke(window, null)!; return true; });
        var restore = (MenuItem)window.FindName("RestoreDeletedMenu");
        restore.IsSubmenuOpen = true;
        SettleLibrary(window);
    }

    private static void CloseRestoreMenu(GalleryWindow window)
    {
        var restore = (MenuItem)window.FindName("RestoreDeletedMenu");
        restore.IsSubmenuOpen = false;
        ((Button)window.FindName("MoreCommandsButton")).ContextMenu!.IsOpen = false;
        SettleLibrary(window);
    }

    private static void SaveLibraryEvidencePng(Window window, string name) =>
        SavePng(RenderAtDpi(window, 96), name);

    private static void SaveRestoreMenuPng(GalleryWindow window, string name)
    {
        var restore = (MenuItem)window.FindName("RestoreDeletedMenu");
        ContextMenu menu = ((Button)window.FindName("MoreCommandsButton")).ContextMenu!;
        var submenu = restore.Template.FindName("PART_Popup", restore) as Popup;
        var parts = new List<FrameworkElement> { menu };
        if (submenu?.Child is FrameworkElement child) parts.Add(child);
        foreach (FrameworkElement part in parts) part.UpdateLayout();
        double width = parts.Sum(part => part.ActualWidth) + 8 * parts.Count;
        double height = parts.Max(part => part.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, width, height));
            double x = 0;
            foreach (FrameworkElement part in parts)
            {
                context.DrawRectangle(new VisualBrush(part) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(x, 0, part.ActualWidth, part.ActualHeight));
                x += part.ActualWidth + 8;
            }
        }
        bitmap.Render(visual);
        SavePng(bitmap, name);
    }

    private static void SavePng(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(LibrarySettingsDirectory, $"{LibrarySettingsPhase}-{name}"));
        encoder.Save(stream);
    }

    private static readonly (string Page, int Index)[] SettingsEvidencePages = [("editing", 3), ("storage", 5), ("about", 6)];

    private static void RenderSettingsEvidence(string language)
    {
        // The Store build (update check, rating, earlier-version import, uninstall note) and a
        // development or portable copy, which points to the Store instead.
        foreach (bool packaged in new[] { true, false })
        {
            MyCapture.Core.Platform.AppPackage.IsPackagedOverride = packaged;
            string build = packaged ? "store" : "unpackaged";
            SettingsWindow window = CreateSettingsUxWindow(new AppSettings { General = { Theme = AppThemeNames.Glass } }).Window;
            window.LegacyImportPackaged = packaged;
            try
            {
                window.Show();
                var tabs = (TabControl)window.FindName("CategoryTabs");
                foreach ((string size, double width, double height) in SettingsDensitySizes)
                {
                    window.Width = width;
                    window.Height = height;
                    foreach ((string page, int index) in SettingsEvidencePages)
                    {
                        if (!packaged && page != "about") continue;
                        tabs.SelectedIndex = index;
                        Drain(window);
                        var scroll = Descendants(window).OfType<ScrollViewer>()
                            .FirstOrDefault(viewer => viewer.IsVisible && viewer.TemplatedParent is null && viewer.Content is StackPanel);
                        scroll?.ScrollToTop();
                        SavePng(RenderAtDpi(window, 96), $"settings-{page}-{build}-{language}-{size}.png");
                        if (scroll is not null && scroll.ScrollableHeight > 1)
                        {
                            scroll.ScrollToBottom();
                            SavePng(RenderAtDpi(window, 96), $"settings-{page}-{build}-{language}-{size}-bottom.png");
                            scroll.ScrollToTop();
                        }
                    }
                }
            }
            finally
            {
                window.CloseForExit();
                MyCapture.Core.Platform.AppPackage.IsPackagedOverride = null;
            }
        }
    }

    // ---- Timings ---------------------------------------------------------------------

    private static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static void Record(List<string> timings, string name, IReadOnlyList<double> samples) =>
        timings.Add($"{name}: median {Ms(Median(samples))} ms (n={samples.Count}; {string.Join(", ", samples.Select(Ms))})");

    private static void MeasureLibraryOpen(int count, List<string> timings)
    {
        EvidenceLibrary library = CreateEvidenceLibrary(count, "mc-libset-perf-");
        try
        {
            var watch = Stopwatch.StartNew();
            library.Window.Show();
            library.Window.ShowGallery();
            SettleLibrary(library.Window);
            timings.Add($"library open {count} cold: {Ms(watch.Elapsed.TotalMilliseconds)} ms");
            var warm = new List<double>();
            for (int run = 0; run < 7; run++)
            {
                library.Window.Hide();
                SettleLibrary(library.Window);
                watch.Restart();
                library.Window.ShowGallery();
                SettleLibrary(library.Window);
                warm.Add(watch.Elapsed.TotalMilliseconds);
            }
            Record(timings, $"library open {count} warm", warm);
        }
        finally
        {
            library.Window.CloseForExit();
        }
    }

    private static void MeasureDeleteUndoAndMenu(List<string> timings)
    {
        var deletes = new List<double>();
        var undos = new List<double>();
        var menus = new List<double>();
        EvidenceLibrary library = CreateEvidenceLibrary(1_000, "mc-libset-delete-");
        try
        {
            library.Window.Show();
            library.Window.ShowGallery();
            SettleLibrary(library.Window);
            var watch = new Stopwatch();
            for (int run = 0; run < 5; run++)
            {
                Guid[] ids = library.Queue.Records.Skip(run * 7).Take(100).Select(record => record.Id).ToArray();
                watch.Restart();
                DeleteForTiming(library, ids);
                SettleLibrary(library.Window);
                deletes.Add(watch.Elapsed.TotalMilliseconds);

                watch.Restart();
                OpenRestoreMenu(library.Window);
                menus.Add(watch.Elapsed.TotalMilliseconds);
                CloseRestoreMenu(library.Window);

                watch.Restart();
                UndoForTiming(library);
                SettleLibrary(library.Window);
                undos.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Equal(1_000, library.Queue.Count);
            }
        }
        finally
        {
            library.Window.CloseForExit();
        }
        Record(timings, "delete 100 of 1000", deletes);
        Record(timings, "undo 100 of 1000", undos);
        Record(timings, "restore menu open (100 deleted)", menus);
    }

    /// <summary>
    /// The selection delete without its confirmation dialog. The "before" build removed through the
    /// queue directly (its delete path ended in a modal result); this build runs the window's own path.
    /// </summary>
    private static void DeleteForTiming(EvidenceLibrary library, Guid[] ids)
    {
        library.Window.ConfirmDelete = (_, _) => true;
        PumpUiTask(library.Window, async () => { await library.Window.DeleteCapturesAsync(ids); return true; });
    }

    /// <summary>
    /// Undo of the whole batch. The "before" build had no undo: restoring meant one restore-menu
    /// click per capture, timed then as 100 sequential single restores.
    /// </summary>
    private static void UndoForTiming(EvidenceLibrary library) =>
        PumpUiTask(library.Window, async () => { await library.Window.InvokeStatusActionForTest(); return true; });

    private static void MeasureSettingsOpen(List<string> timings)
    {
        var cold = new List<double>();
        var warm = new List<double>();
        var construct = new List<double>();
        // MYCAPTURE_LIBSET_SAMPLES raises the settings sample count for a quieter median.
        int samples = int.TryParse(Environment.GetEnvironmentVariable("MYCAPTURE_LIBSET_SAMPLES"), out int requested) && requested > 5
            ? requested : 5;
        for (int run = 0; run < samples; run++)
        {
            var watch = Stopwatch.StartNew();
            SettingsWindow window = CreateSettingsUxWindow(new AppSettings { General = { Theme = AppThemeNames.Glass } }).Window;
            construct.Add(watch.Elapsed.TotalMilliseconds);
            window.ShowSettings();
            Drain(window);
            cold.Add(watch.Elapsed.TotalMilliseconds);
            try
            {
                window.Hide();
                Drain(window);
                watch.Restart();
                window.ShowSettings();
                Drain(window);
                warm.Add(watch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                window.CloseForExit();
            }
        }
        Record(timings, "settings open cold", cold);
        Record(timings, "settings open cold (construction part)", construct);
        Record(timings, "settings reopen", warm);
    }
}
