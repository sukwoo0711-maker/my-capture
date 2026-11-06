using System.ComponentModel;
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
/// 124 captures, restore-menu open and settings open. Set <c>MYCAPTURE_LIBSET_EVIDENCE</c> to a
/// directory and <c>MYCAPTURE_LIBSET_PHASE</c> to a label; without the directory nothing runs
/// except the delete budget, which runs in every test run. Everything lives in temporary roots
/// with a fake trash: the real Recycle Bin is never used.
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

    /// <summary>
    /// The 2026-11-06 report in every test run: 124 of 1,000 captures deleted from the library
    /// window and brought back with Undo, through a fake Shell that takes time per operation and per
    /// item (<see cref="LibraryDeleteShell"/>), with the deletion journal under its 200 entries and
    /// with a delete that takes it over them. <see cref="MeasureDeleteUndoAndMenu"/> holds the budget.
    /// With <c>MYCAPTURE_LIBSET_EVIDENCE</c> set, the times are added to its timings file.
    /// The child used 94 to 101 s of the default 150 s on four CPUs, most of it building the library
    /// of 1,000, so it counts as hung only after 300 s; the budgets above are unchanged.
    /// </summary>
    [Fact]
    public Task LibraryDelete_124Of1000ThroughASlowShell_StaysWithinItsBudget() =>
        RunIsolated(nameof(LibraryDelete_124Of1000ThroughASlowShell_StaysWithinItsBudget), hungAfter: TimeSpan.FromSeconds(300), verify: () =>
        {
            Application application = CreateLibraryApplication();
            var timings = new List<string>();
            try
            {
                using var language = UiText.UseLanguage("ko-KR");
                MeasureDeleteUndoAndMenu(timings);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LibrarySettingsEvidenceVariable)))
                    File.AppendAllLines(DiagnosticOutputPaths.Child(LibrarySettingsDirectory, $"{LibrarySettingsPhase}-timings.txt"), timings);
                application.Shutdown();
            }
        });

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
        RecoverableLibraryService Recovery, AppPaths Paths, IReadOnlyList<CaptureRecord> Records, EvidenceTrash Trash);

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
        // Anchor at 23:00 yesterday, not at the current time: the library groups cards by day, and
        // samples taken from "now" split across midnight for a while after it (the CI runner clock
        // is UTC), which moved the cards the delete-gap test follows into different day groups.
        DateTimeOffset now = new(DateTime.Today.AddHours(-1));
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
        var trash = new EvidenceTrash(Path.Combine(root, "fake-trash"));
        var recovery = new RecoverableLibraryService(queue, paths, trash, NullLogger.Instance)
        {
            // Retired journals, which the library sends to the Recycle Bin as one folder, go to the
            // fake trash too; the library then finds nothing left to move.
            BeforeRetiredFolderRecycleForTest = trash.TakeRetiredJournals,
        };
        // As in the app, a delete of several captures is prepared as one batch.
        queue.PrepareRemoval = recovery.PrepareRemoval;
        queue.PrepareRemovals = recovery.PrepareRemovals;
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
        return new EvidenceLibrary(window, vm, queue, recovery, paths, records, trash);
    }

    /// <summary>
    /// The time a fake Shell takes: the start-up of one operation and the work per item, to move to
    /// the Recycle Bin and to restore from it. With <see cref="Batches"/> a whole selection is one
    /// operation, as <see cref="WindowsRecycleBin"/> does it; without, each directory is an operation
    /// of its own, as the library used the Recycle Bin before 2026-11-06.
    /// </summary>
    private sealed record EvidenceShell(TimeSpan RecycleStart, TimeSpan RecycleItem, TimeSpan RestoreStart,
        TimeSpan RestoreItem, bool Batches);

    /// <summary>One fake Shell operation: each item is done at its share of the operation's time.</summary>
    private sealed class EvidenceShellOperation(string kind, TimeSpan start, TimeSpan perItem)
    {
        public string Kind { get; } = kind;
        public long Started { get; } = Stopwatch.GetTimestamp();
        private int _items;

        public void Item()
        {
            // Due times count from the operation's start, so sleep overshoot does not add up.
            TimeSpan due = start + perItem * ++_items;
            while (Stopwatch.GetElapsedTime(Started) < due) Thread.Sleep(1);
        }
    }

    /// <summary>A fake Shell operation that ran: what it did, and its start and end timestamps.</summary>
    private sealed record EvidenceShellRun(string Kind, long Started, long Ended);

    /// <summary>The fake Shell operations of one command by kind, in milliseconds from the command.</summary>
    private static string ShellSummary(IReadOnlyList<EvidenceShellRun> runs, long command)
    {
        if (runs.Count == 0) return "no Shell operation";
        return string.Join(", ", runs.GroupBy(run => run.Kind).Select(group => FormattableString.Invariant(
            $"{group.Key} x{group.Count()} {Stopwatch.GetElapsedTime(command, group.Min(run => run.Started)).TotalMilliseconds:0}-{Stopwatch.GetElapsedTime(command, group.Max(run => run.Ended)).TotalMilliseconds:0} ms")));
    }

    /// <summary>
    /// Moves directories into an app-owned temporary folder; never the Windows Recycle Bin. A test
    /// can hold a move part-way, as a slow Recycle Bin would, to see the window while it runs, and
    /// can give every operation the time of a real Shell (<see cref="Shell"/>).
    /// </summary>
    private sealed class EvidenceTrash(string root) : IRecoverableTrash
    {
        private readonly WindowsRecycleBin _identity = new();
        private readonly Dictionary<string, RecycledPathReceipt> _receipts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ManualResetEventSlim _released = new(initialState: true);
        private int _holdAfter = -1, _moves, _retiredJournals;
        private volatile bool _holding;

        /// <summary>The move after the next <paramref name="moved"/> directories waits for <see cref="ReleaseHold"/>.</summary>
        public void HoldAfter(int moved)
        {
            _released.Reset();
            Volatile.Write(ref _moves, 0);
            Volatile.Write(ref _holdAfter, moved);
        }

        public bool IsHolding => _holding;
        public void ReleaseHold() => _released.Set();

        /// <summary>The time of each Shell operation; null moves at once.</summary>
        public EvidenceShell? Shell { get; set; }

        /// <summary>Folders of retired journals the library sent to the Recycle Bin and this trash took.</summary>
        public int RetiredJournalFolders => Volatile.Read(ref _retiredJournals);

        private readonly List<EvidenceShellRun> _runs = [];

        /// <summary>The <see cref="Shell"/> operations that ran since the last call.</summary>
        public EvidenceShellRun[] TakeShellRuns()
        {
            lock (_runs)
            {
                EvidenceShellRun[] runs = [.. _runs];
                _runs.Clear();
                return runs;
            }
        }

        private void Ran(EvidenceShellOperation operation)
        {
            lock (_runs) _runs.Add(new EvidenceShellRun(operation.Kind, operation.Started, Stopwatch.GetTimestamp()));
        }

        public string GetIdentity(string path) => _identity.GetIdentity(path);

        public RecycledPathReceipt RecycleDirectory(string path)
        {
            if (Shell is not { } shell) return Move(path);
            var operation = new EvidenceShellOperation("move", shell.RecycleStart, shell.RecycleItem);
            try
            {
                operation.Item();
                return Move(path);
            }
            finally { Ran(operation); }
        }

        public IReadOnlyList<RecycleOutcome> RecycleDirectories(IReadOnlyList<string> paths, Action<int>? progress = null)
        {
            EvidenceShellOperation? batch = Shell is { Batches: true } shell
                ? new EvidenceShellOperation("move", shell.RecycleStart, shell.RecycleItem) : null;
            var outcomes = new RecycleOutcome[paths.Count];
            for (int index = 0; index < paths.Count; index++)
            {
                try
                {
                    batch?.Item();
                    outcomes[index] = new RecycleOutcome(paths[index], batch is null ? RecycleDirectory(paths[index]) : Move(paths[index]), null);
                }
                catch (Exception ex) { outcomes[index] = new RecycleOutcome(paths[index], null, ex); }
                progress?.Invoke(index + 1);
            }
            if (batch is not null) Ran(batch);
            return outcomes;
        }

        private RecycledPathReceipt Move(string path)
        {
            if (Interlocked.Increment(ref _moves) == Volatile.Read(ref _holdAfter) + 1)
            {
                _holding = true;
                _released.Wait(TimeSpan.FromSeconds(30));
                _holding = false;
            }
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
            if (Shell is not { } shell)
            {
                MoveBack(receipt);
                return;
            }
            var operation = new EvidenceShellOperation("restore", shell.RestoreStart, shell.RestoreItem);
            try
            {
                operation.Item();
                MoveBack(receipt);
            }
            finally { Ran(operation); }
        }

        public IReadOnlyList<Exception?> RestoreDirectories(IReadOnlyList<RecycledPathReceipt> receipts)
        {
            EvidenceShellOperation? batch = Shell is { Batches: true } shell
                ? new EvidenceShellOperation("restore", shell.RestoreStart, shell.RestoreItem) : null;
            var failures = new Exception?[receipts.Count];
            for (int index = 0; index < receipts.Count; index++)
            {
                try
                {
                    if (batch is null) RestoreDirectory(receipts[index]);
                    else { batch.Item(); MoveBack(receipts[index]); }
                }
                catch (Exception ex) { failures[index] = ex; }
            }
            if (batch is not null) Ran(batch);
            return failures;
        }

        private static void MoveBack(RecycledPathReceipt receipt)
        {
            if (Directory.Exists(receipt.OriginalPath)) throw new IOException("restore collision");
            Directory.Move(receipt.RecycledPath, receipt.OriginalPath);
        }

        /// <summary>
        /// Takes a folder of retired journals just before the library moves it to the Recycle Bin, in
        /// the time of one Shell operation: the folder moves here, and the library finds nothing left.
        /// </summary>
        public void TakeRetiredJournals(string folder)
        {
            EvidenceShellOperation? operation = Shell is { } shell
                ? new EvidenceShellOperation("retire", shell.RecycleStart, shell.RecycleItem) : null;
            try
            {
                operation?.Item();
                string journals = Directory.CreateDirectory(Path.Combine(root, "retired-journals")).FullName;
                Directory.Move(folder, Path.Combine(journals, Path.GetFileName(folder)));
                Interlocked.Increment(ref _retiredJournals);
            }
            finally
            {
                if (operation is not null) Ran(operation);
            }
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

    private static readonly (string Page, SettingsSection Section)[] SettingsEvidencePages =
        [("editing", SettingsSection.Editing), ("storage", SettingsSection.Storage), ("about", SettingsSection.About)];

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
                    foreach ((string page, SettingsSection section) in SettingsEvidencePages)
                    {
                        if (!packaged && page != "about") continue;
                        Assert.True(window.NavigateToSection(section));
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

    /// <summary>The selection of the 2026-11-06 report.</summary>
    private const int LibraryDeleteCount = 124;

    /// <summary>
    /// The fake Shell of the delete budget. Its operations keep the ratio of start-up to per-item time
    /// measured with the real Recycle Bin and 124 directories on the developer PC (one operation per
    /// directory: 42.6 s to move and 396 s to restore; one operation for all: 14.9 s and 8.1 s), at
    /// half that time to move and a twentieth to restore: moving 124 directories takes 7.4 s in one
    /// operation and 21.2 s one at a time, restoring them 0.4 s and 19.8 s.
    /// <c>MYCAPTURE_LIBSET_SHELL=one-at-a-time</c> runs the same budget with every directory as its own
    /// operation, the way the library used the Recycle Bin before 2026-11-06: it fails.
    /// </summary>
    private static EvidenceShell LibraryDeleteShell => new(
        RecycleStart: TimeSpan.FromMilliseconds(112), RecycleItem: TimeSpan.FromMilliseconds(59),
        RestoreStart: TimeSpan.FromMilliseconds(158), RestoreItem: TimeSpan.FromMilliseconds(2),
        Batches: Environment.GetEnvironmentVariable("MYCAPTURE_LIBSET_SHELL") != "one-at-a-time");

    /// <summary>
    /// The deleted cards leave the view while their files are still moving, before the 7.4 s the fake
    /// Shell takes to move them; the window waited for the move before 2026-11-06. They took up to
    /// 1.9 s on the developer PC while other test runs were going (2026-11-06); the rest is room for a
    /// slower hosted runner.
    /// </summary>
    private static readonly TimeSpan LibraryDeleteCardsBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The library's own part of a delete of 124 captures (journals, index, view), on top of one
    /// Shell operation for the whole selection. It took up to 2.4 s on the developer PC while other
    /// test runs were going; the rest is room for a slower hosted runner. One operation per capture
    /// adds 13.8 s to the fake move, past it on any PC: the fake Shell waits, it does not compute.
    /// </summary>
    private static readonly TimeSpan LibraryDeleteOwnWork = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The library's own part of an Undo of 124 captures (the records back in the index and the view,
    /// their journals marked and retired), on top of one Recycle Bin restore. It took up to 4.6 s on
    /// the developer PC while other test runs were going; the rest is room for a slower hosted runner.
    /// One restore per capture adds 19.4 s, past it on any PC.
    /// </summary>
    private static readonly TimeSpan LibraryUndoOwnWork = TimeSpan.FromSeconds(15);

    /// <summary>
    /// One delete from the library window and its Undo, in milliseconds from each command, with the
    /// fake Shell operations each of them ran.
    /// </summary>
    private sealed record LibraryDeleteRun(string Label, int Count, double CardsGoneMs, double ResultMs, double MenuMs,
        double UndoAfterResultMs, double UndoMs, string MoveShell, string RestoreShell);

    /// <summary>
    /// Deletes 124 of 1,000 captures from the library window through <see cref="LibraryDeleteShell"/>
    /// and undoes it, twice: with the deletion journal under its 200 entries, and after an earlier
    /// delete of 100, so that the timed one takes the journal over 200 and retires the oldest entries
    /// as it runs. A small delete and Undo first load the code both runs use, as the earlier commands
    /// of a session have. Each run must stay within its budget: the cards leave within
    /// <see cref="LibraryDeleteCardsBudget"/>, the status line's text gives the result of the delete
    /// within one Shell operation for the whole selection plus <see cref="LibraryDeleteOwnWork"/>, and
    /// the result of the Undo within one restore plus <see cref="LibraryUndoOwnWork"/>. These are the
    /// times the text changes; the resident probe times the frame that shows it. The restore menu is
    /// timed with the selection deleted.
    /// </summary>
    private static void MeasureDeleteUndoAndMenu(List<string> timings)
    {
        EvidenceShell shell = LibraryDeleteShell;
        double moveBudget = (shell.RecycleStart + shell.RecycleItem * LibraryDeleteCount + LibraryDeleteOwnWork).TotalMilliseconds;
        double undoBudget = (shell.RestoreStart + shell.RestoreItem * LibraryDeleteCount + LibraryUndoOwnWork).TotalMilliseconds;
        string mode = shell.Batches ? "one Shell operation" : "one Shell operation per capture";
        void Judge(LibraryDeleteRun run)
        {
            string name = $"delete {run.Count} ({run.Label}, {mode})";
            var over = new List<string>();
            timings.Add($"{name}: cards gone {Ms(run.CardsGoneMs)} ms (budget {Ms(LibraryDeleteCardsBudget.TotalMilliseconds)}), "
                + $"result {Ms(run.ResultMs)} ms (budget {Ms(moveBudget)}), undo {Ms(run.UndoMs)} ms (budget {Ms(undoBudget)}), "
                + $"restore menu {Ms(run.MenuMs)} ms, undo started {Ms(run.UndoAfterResultMs)} ms after the result; "
                + $"Shell: delete {run.MoveShell}; undo {run.RestoreShell}");
            if (run.CardsGoneMs > LibraryDeleteCardsBudget.TotalMilliseconds) over.Add($"{name}: the deleted cards stayed {Ms(run.CardsGoneMs)} ms");
            if (run.ResultMs > moveBudget) over.Add($"{name}: the delete took {Ms(run.ResultMs)} ms");
            if (run.UndoMs > undoBudget) over.Add($"{name}: the undo took {Ms(run.UndoMs)} ms");
            // The first run over its budget ends the test: with one Shell operation per capture, the next
            // run would not end within the isolated test's time.
            Assert.True(over.Count == 0, "Over the delete budget:" + Environment.NewLine
                + string.Join(Environment.NewLine, over.Concat(timings)));
        }

        EvidenceLibrary library = CreateEvidenceLibrary(1_000, "mc-libset-delete-");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        try
        {
            // The cards leave with their fade, as they do for most users.
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            library.Window.ConfirmDelete = (_, _) => true;
            library.Window.Show();
            library.Window.ShowGallery();
            SettleLibrary(library.Window);
            library.Trash.Shell = shell;
            MeasureLibraryDelete(library, library.Records.Take(4), "warm-up");
            Judge(MeasureLibraryDelete(library, library.Records.Take(LibraryDeleteCount), "journal under 200"));
            Assert.Equal(1_000, library.Queue.Count);

            // The earlier delete moves at once; the timed one takes the journal to 224 entries.
            library.Trash.Shell = null;
            DeleteForTiming(library, library.Records.Take(100).Select(record => record.Id).ToArray());
            library.Trash.Shell = shell;
            int retired = library.Trash.RetiredJournalFolders;
            Judge(MeasureLibraryDelete(library, library.Records.Skip(100).Take(LibraryDeleteCount), "journal over 200",
                afterDelete: () =>
                {
                    Assert.Equal(RecoverableLibraryService.MaxJournalEntries, library.Recovery.RecentDeleted().Count);
                    Assert.True(library.Trash.RetiredJournalFolders > retired, "The delete must retire the oldest journals.");
                }));
            Assert.Equal(900, library.Queue.Count);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            library.Window.CloseForExit();
        }
    }

    /// <summary>
    /// Deletes <paramref name="captures"/> (on screen) from the library window, waits for the result on
    /// the status line and its Undo, times the restore menu, then undoes the delete. The times are
    /// taken when the window changes: its status line, and the layout pass after which no deleted card
    /// is left. A step that fails says every status line of the run, with its time.
    /// </summary>
    private static LibraryDeleteRun MeasureLibraryDelete(EvidenceLibrary library, IEnumerable<CaptureRecord> captures,
        string label, Action? afterDelete = null)
    {
        GalleryWindow window = library.Window;
        Guid[] ids = captures.Select(record => record.Id).ToArray();
        var deleted = new HashSet<Guid>(ids);
        var status = (TextBlock)window.FindName("LibraryActionStatus");
        string Status() => KoreanWordWrap.Strip(status.Text);
        bool DeletedShown() => window.RealizedCards()
            .Any(card => card.DataContext is GalleryItemViewModel item && deleted.Contains(item.Id));
        PumpLibraryUntil(window, DeletedShown, label + ": the selection on screen");
        string moved = UiText.Format("LibraryUndo_Moved", ids.Length);
        string restored = UiText.Format("LibraryUndo_Restored", ids.Length);
        Assert.NotEqual(moved, Status());

        var lines = new List<string>();
        long started = 0, resultShown = 0;
        bool undoing = false;
        double cardsGone = -1, result = -1, menu = -1, undoAfterResult = -1, undone = -1;
        string moveShell = "-", restoreShell = "-";
        string? resultAction = null;
        void OnStatus(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double at = Stopwatch.GetElapsedTime(started, now).TotalMilliseconds;
            string text = Status();
            lines.Add(FormattableString.Invariant($"{(undoing ? "undo" : "delete")} +{at:0} ms \"{text}\""));
            if (!undoing && result < 0 && text == moved)
            {
                (result, resultShown) = (at, now);
                // The window shows the line's action in the same call, after its text; the line's timer,
                // which takes the action away, runs seconds later, however late the test looks.
                window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => resultAction = window.StatusActionLabelForTest));
            }
            if (undoing && undone < 0 && text == restored) undone = at;
        }
        void OnLayout(object? sender, EventArgs e)
        {
            // Gone means the rest of the library shows in their place, not an empty view.
            if (!undoing && cardsGone < 0 && !DeletedShown() && window.RealizedCards().Any())
                cardsGone = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        string RunSoFar() => FormattableString.Invariant(
            $"{label}: cards gone {cardsGone:0} ms, result {result:0} ms, restore menu {menu:0} ms, ")
            + FormattableString.Invariant($"undo started {undoAfterResult:0} ms after the result, undo {undone:0} ms; ")
            + $"Shell: delete {moveShell}; undo {restoreShell}" + Environment.NewLine + string.Join(Environment.NewLine, lines);

        DependencyPropertyDescriptor statusText = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        statusText.AddValueChanged(status, OnStatus);
        window.LayoutUpdated += OnLayout;
        try
        {
            library.Trash.TakeShellRuns();
            started = Stopwatch.GetTimestamp();
            Task delete = StartUiTask(window, () => window.DeleteCapturesAsync(ids));
            // The limits only end a hung run: one directory per Shell operation still finishes in them.
            Assert.True(PumpLibraryFor(window, () => delete.IsCompleted, TimeSpan.FromSeconds(60)), "The delete did not finish. " + RunSoFar());
            Assert.True(delete.IsCompletedSuccessfully, delete.Exception?.ToString());
            moveShell = ShellSummary(library.Trash.TakeShellRuns(), started);
            PumpLibraryFor(window, () => cardsGone >= 0, TimeSpan.FromSeconds(5));
            Assert.True(result >= 0, $"The delete did not end with \"{moved}\". " + RunSoFar());
            Assert.True(cardsGone >= 0, "The deleted cards stayed on screen. " + RunSoFar());
            PumpLibraryFor(window, () => resultAction is not null, TimeSpan.FromSeconds(5));
            Assert.True(resultAction == UiText.Get("LibraryUndo_Action"), $"The result offers no Undo (\"{resultAction}\"). " + RunSoFar());
            afterDelete?.Invoke();

            long menuStarted = Stopwatch.GetTimestamp();
            OpenRestoreMenu(window);
            menu = Stopwatch.GetElapsedTime(menuStarted).TotalMilliseconds;
            CloseRestoreMenu(window);

            // The command behind the result's Undo and Ctrl+Z; the button adds only its display time.
            undoing = true;
            started = Stopwatch.GetTimestamp();
            undoAfterResult = Stopwatch.GetElapsedTime(resultShown, started).TotalMilliseconds;
            Task undo = StartUiTask(window, window.UndoLibraryAsync);
            Assert.True(PumpLibraryFor(window, () => undo.IsCompleted, TimeSpan.FromSeconds(90)), "The undo did not finish. " + RunSoFar());
            Assert.True(undo.IsCompletedSuccessfully, undo.Exception?.ToString());
            restoreShell = ShellSummary(library.Trash.TakeShellRuns(), started);
            Assert.True(undone >= 0, $"The undo did not end with \"{restored}\". " + RunSoFar());
        }
        finally
        {
            window.LayoutUpdated -= OnLayout;
            statusText.RemoveValueChanged(status, OnStatus);
        }
        return new LibraryDeleteRun(label, ids.Length, cardsGone, result, menu, undoAfterResult, undone, moveShell, restoreShell);
    }

    /// <summary>Pumps the library window until <paramref name="done"/>; false when <paramref name="limit"/> passed first.</summary>
    private static bool PumpLibraryFor(Window window, Func<bool> done, TimeSpan limit)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            SettleGalleryLayout(window);
            if (done()) return true;
            if (Stopwatch.GetElapsedTime(started) > limit) return false;
            Thread.Sleep(5);
        }
    }

    /// <summary>The selection delete through the window's own path, without its confirmation dialog.</summary>
    private static void DeleteForTiming(EvidenceLibrary library, Guid[] ids)
    {
        library.Window.ConfirmDelete = (_, _) => true;
        PumpUiTask(library.Window, async () => { await library.Window.DeleteCapturesAsync(ids); return true; });
    }

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
