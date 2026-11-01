using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Media;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The key windows in the content states they really show (empty, in progress, failed, long content),
/// beside the windows as they open, laid out offscreen in English and Korean at their default and minimum
/// sizes. Text cut by its box or a clipping ancestor (CLIPPED), controls that reach past the window or are cut
/// by what holds them (OOB) and Korean left in the English UI (HANGUL) fail the sweep; on the new states the
/// Korean pass also keeps the word-wrap checks of <see cref="KoreanWordWrapSweepTests"/>. States are reached
/// only through the windows' own internal APIs; a state a window cannot show offscreen is left out where it
/// is listed below.
/// </summary>
/// <remarks>
/// Left out: the library's timed notices (ShowNotice shows them only while the window is visible), a file
/// name on the quick access card (the card shows none) and a recording control error state of its own (the
/// bar reports errors through its status line, which the watermark failure here fills).
/// </remarks>
public sealed class ContentStateSweepTests
{
    /// <summary>How far ink or a control may reach past what holds it before it counts as cut, in DIPs.</summary>
    private const double Tolerance = 1;

    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-30T23:00:00Z", CultureInfo.InvariantCulture);

    /// <summary>English text in a capture and its Korean translation: the translation model works from English to Korean.</summary>
    private static readonly (string Source, string Text, string[] Missing)[] Translations =
    [
        ("Save the capture to the library.", "캡처를 라이브러리에 저장합니다.", []),
        ("Pinned captures stay above other windows until you close them, so you can compare a design with the running app.",
            "고정한 캡처는 닫을 때까지 다른 창 위에 남아 있으므로 실행 중인 앱과 디자인을 비교할 수 있습니다.", []),
        ("Press F3 in MyCapture to pin what you see.", "보이는 것을 고정하려면 키를 누릅니다.", ["F3", "MyCapture"]),
    ];

    [Fact]
    public Task ContentStates_InBothLanguages_AreNotCutOutOfReachOrUntranslated() =>
        SettingsThemePreviewTests.RunIsolated(nameof(ContentStates_InBothLanguages_AreNotCutOutOfReachOrUntranslated),
            () => Run(evidence: false), typeof(ContentStateSweepTests));

    /// <summary>
    /// Opt-in evidence: MYCAPTURE_CONTENT_STATE_EVIDENCE=1 renders every layout into
    /// artifacts/content-states/en-US and /ko-KR, with findings.json beside them. The variable only turns the
    /// output on; the directories are fixed.
    /// </summary>
    [Fact]
    public Task RenderContentStateEvidence()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_CONTENT_STATE_EVIDENCE") != "1") return Task.CompletedTask;
        return SettingsThemePreviewTests.RunIsolated(nameof(RenderContentStateEvidence),
            () => Run(evidence: true), typeof(ContentStateSweepTests));
    }

    private static void Run(bool evidence)
    {
        string root = Path.Combine(OffscreenSurfaceSweep.RepositoryRoot(), "artifacts", "content-states");
        Application application = OffscreenSurfaceSweep.CreateApplication();
        ThemeService.Apply(AppTheme.Midnight);
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        CultureInfo oldCulture = CultureInfo.CurrentCulture;
        var findings = new List<Finding>();
        try
        {
            VerifyChecks(application);
            // English first: a string a window keeps in a static field then holds English, which the
            // Korean pass does not look for, never Korean that the English pass would report.
            foreach (Pass pass in new[] { Pass.English, Pass.Korean })
                findings.AddRange(RunPass(application, pass, root,
                    evidence ? DiagnosticOutputPaths.Create(Path.Combine(root, pass.Tag)) : null));
            if (!evidence) Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
            application.Shutdown();
        }
    }

    private static List<Finding> RunPass(Application application, Pass pass, string root, string? output)
    {
        using var language = UiText.UseLanguage(pass.Tag);
        UiText.Configure(pass.Tag);
        // Dates and numbers read as they do for a person whose regional format matches the UI language.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(pass.Tag);
        // A storage setting left at its default shows its folder inside the person's Pictures or application
        // data folder, whose path can hold Korean (a localized OneDrive folder or the account name). Only the
        // paths are read; test sources never construct the default AppPaths (OcrTestIsolationTests).
        string[] profileFolders = [.. new[] { Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.ApplicationData }
            .Select(folder => Environment.GetFolderPath(folder)).Where(path => path.Length > 0)];
        string[] fixtureText = [.. pass.Text.All, .. pass.Long.All, .. Translations.SelectMany(item => new[] { item.Source, item.Text }),
            .. profileFolders];
        fixtureText = [.. fixtureText.Select(OneLine).OrderByDescending(item => item.Length)];
        var findings = new List<Finding>();
        bool newStates = false;
        var sweep = new OffscreenSurfaceSweep(application, layout =>
        {
            Inspect(layout, pass.Tag, text => KoreanOnlyFrom(fixtureText, text), findings);
            // The default surfaces already go through these checks in KoreanWordWrapSweepTests.
            if (!newStates || pass.Tag != "ko-KR") return;
            var wrap = new List<string>();
            OffscreenSurfaceSweep.CheckKoreanWrap(layout, wrap, IsFixture);
            string prefix = $"{layout.Name}/{layout.Mode}; ";
            foreach (string line in wrap)
            {
                int space = line.IndexOf(' ');
                string detail = line[(space + 1)..];
                findings.Add(new Finding(line[..space], pass.Tag, layout.Name, layout.Mode,
                    detail.StartsWith(prefix, StringComparison.Ordinal) ? detail[prefix.Length..] : detail));
            }
        })
        { Output = output, ConsolePrefix = "CONTENT STATE", LanguageName = pass.Name, StopsAtMaximumSize = true, SizesAsLoaded = true };
        var watch = Stopwatch.StartNew();
        // All persistence services point at this fixture root; no real application profile is read.
        using var fixtures = new OffscreenSurfaceSweep.Fixtures(root, Path.Combine(root, "fixtures", pass.Tag), pass.Text);
        sweep.DefaultSurfaces(fixtures, verify: true);
        int defaults = sweep.Layouts;
        newStates = true;
        // The states finish asynchronous work as a shown window would: continuations return to the dispatcher.
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            Library(sweep, pass, root);
            QuickAccess(sweep, fixtures.Sample);
            Pins(sweep, fixtures, pass.Long);
            OcrResults(sweep, fixtures, pass);
            TranslationPanel(sweep);
            Export(sweep, fixtures, pass.Long, gif: false);
            Export(sweep, fixtures, pass.Long, gif: true);
            LegacyImport(sweep, fixtures, pass);
            Dialogs(sweep, fixtures, pass.Long);
            RecordingControls(sweep);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Console.WriteLine(FormattableString.Invariant(
            $"CONTENT STATE SWEEP {pass.Tag}: {defaults} default and {sweep.Layouts - defaults} state layouts in {watch.Elapsed.TotalSeconds:0.0} s; {findings.Count} findings."));
        foreach (Finding finding in findings) Console.WriteLine(finding);
        if (output is not null)
            File.WriteAllText(DiagnosticOutputPaths.Child(output, "findings.json"), JsonSerializer.Serialize(new
            {
                language = pass.Tag, layouts = sweep.Layouts, stateLayouts = sweep.Layouts - defaults,
                findings = findings.Select(finding => new { finding = finding.ToString(), png = finding.Png }),
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return findings;

        // Text whose Korean all comes from what the fixtures typed or captured, or that shows only part of it.
        bool IsFixture(string text)
        {
            string core = OneLine(text).TrimEnd('…').TrimEnd();
            return core.Length > 0 && fixtureText.Any(item => item.Contains(core, StringComparison.Ordinal))
                || KoreanOnlyFrom(fixtureText, text);
        }
    }

    /// <summary>
    /// Whether all Korean in <paramref name="text"/> comes from whole fixture strings, longest first. A Korean
    /// UI word can also appear inside what a person typed, so part of a fixture does not excuse it.
    /// </summary>
    private static bool KoreanOnlyFrom(IReadOnlyList<string> fixtureText, string text)
    {
        string rest = OneLine(text);
        foreach (string item in fixtureText) rest = rest.Replace(item, " ", StringComparison.Ordinal);
        return !rest.Any(OffscreenSurfaceSweep.IsHangul);
    }

    /// <summary>
    /// A library with a long titled capture, also in its details pane, a capture in a long named folder, a search
    /// without results, and a result with Undo beside an update that is available, downloading and failed. It
    /// has a fixture root of its own: creating a folder writes the library index, and the default surfaces'
    /// library must stay empty. A later run finds that folder and reuses it; the captures are added after the
    /// write, so they stay in memory and nothing accumulates.
    /// </summary>
    private static void Library(OffscreenSurfaceSweep sweep, Pass pass, string root)
    {
        LongContent text = pass.Long;
        using var library = new OffscreenSurfaceSweep.Fixtures(root, Path.Combine(root, "fixtures", pass.Tag + "-library"), pass.Text);
        CaptureQueue queue = library.Queue;
        queue.Load();
        CaptureFolder folder = queue.Folders.FirstOrDefault(item => item.Name == text.Folder)
            ?? Finish(queue.CreateFolderAsync(text.Folder, createdAt: Created));
        var titled = new CaptureRecord
        {
            CreatedAt = Created, UpdatedAt = Created, Width = 1280, Height = 720, Title = text.Title, TotalBytes = 245_760,
            SourceAppName = "Microsoft Word", SourceWindowTitle = text.WindowTitle,
            SourceDocumentPath = Path.Combine(library.Paths.DataRoot, text.Folder, text.Document),
        };
        queue.Add(titled);
        queue.Add(new CaptureRecord
        {
            CreatedAt = Created.AddMinutes(-5), UpdatedAt = Created.AddMinutes(-5), Width = 1920, Height = 1080,
            Title = pass.Text.Title, TotalBytes = 512_000, FolderId = folder.Id,
        });
        (GalleryWindow window, GalleryViewModel model) = library.Gallery();
        try
        {
            FrameworkElement content = OffscreenSurfaceSweep.Detach(window);
            (Size size, Size minimum) = sweep.WindowSizes(window, content);
            model.Select(titled.Id);
            Settle();
            sweep.Surface("gallery-long-title", content, size, minimum);
            // The pane the details rail opens: the title again and where the capture came from.
            window.SetDetailsPaneOpen(true);
            Settle();
            sweep.Surface("gallery-details-long-title", content, size, minimum);
            window.SetDetailsPaneOpen(false);
            model.SelectScope(CaptureFolderScope.ForFolder(folder.Id));
            Settle();
            sweep.Surface("gallery-long-folder", content, size, minimum);
            model.SelectScope(CaptureFolderScope.All);
            model.SearchQuery = text.SearchMiss;
            Settle();
            Assert.True(model.IsEmpty);
            sweep.Surface("gallery-no-results", content, size, minimum);
            model.SearchQuery = string.Empty;
            window.ShowEngagementStatusForTest(UiText.Get("Library.Undo.Changed"), UiText.Get("LibraryUndo_Action"), () => Task.CompletedTask);
            var update = new LibraryEngagementNotice(window);
            update.ShowUpdateAvailable("4.12.1");
            Settle();
            sweep.Surface("gallery-status", content, size, minimum);
            // The update while it downloads and after it failed, with the status App.Updates passes.
            update.ShowUpdateAvailable("4.12.1", busy: true, SettingsWindow.StoreUpdateStatusText(StoreUpdatePhase.Downloading));
            Settle();
            sweep.Surface("gallery-update-downloading", content, size, minimum);
            update.ShowUpdateAvailable("4.12.1", status: SettingsWindow.StoreUpdateStatusText(StoreUpdatePhase.Failed), failed: true);
            Settle();
            sweep.Surface("gallery-update-failed", content, size, minimum);
        }
        finally
        {
            OffscreenSurfaceSweep.Close(window);
        }
    }

    private static void QuickAccess(OffscreenSurfaceSweep sweep, BitmapSource sample)
    {
        QuickAccessWindow copying = QuickAccessWindow.ForPendingCapture(sample, copiedToClipboard: null);
        States(sweep, copying, [],
            ("quick-access-copying", () => { }),
            ("quick-access-copy-failure", () => copying.ReportInitialCopy(false)));
        var saving = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var card = new QuickAccessWindow(sample, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("fixture.png")) { IsKept = true };
        Task save = Task.CompletedTask;
        States(sweep, card, [],
            ("quick-access-saving", () => save = card.RunAsync("Save", () => saving.Task, "QuickAccess.Saving", "QuickAccess.Saved")),
            ("quick-access-save-failure", () =>
            {
                saving.SetResult(false);
                Finish(save);
            }));
    }

    private static void Pins(OffscreenSurfaceSweep sweep, OffscreenSurfaceSweep.Fixtures fixtures, LongContent text)
    {
        PinWindow failed = fixtures.CodePin();
        failed.ReportSaveResult(new PinSaveResult(PinSaveStatus.Failed, ErrorMessage: "Fixture save failure."));
        sweep.Window("pin-save-failure", failed);
        PinWindow saved = fixtures.CodePin();
        saved.ReportSaveResult(new PinSaveResult(PinSaveStatus.Saved,
            Path.Combine(fixtures.Paths.QuickSaveRoot, Path.ChangeExtension(text.FileName, ".png"))));
        sweep.Window("pin-saved-long-name", saved);
        PinWindow original = fixtures.CodePin();
        original.ReportOriginalTextCopyResult(false);
        sweep.Window("pin-original-copy-failure", original);
    }

    private static void OcrResults(OffscreenSurfaceSweep sweep, OffscreenSurfaceSweep.Fixtures fixtures, Pass pass)
    {
        var window = new OcrResultWindow { SuppressPresentation = true };
        var settings = new OcrSettings();
        window.ConfigureAccuracy(new OcrAccuracyOptions(() => settings, () => { }, new FixtureAccuracyModel()),
            _ => Task.CompletedTask, () => { });
        string context = UiText.Format("Text_16DBAB47AC29", "16:01");
        BitmapSource sample = fixtures.Sample;
        States(sweep, window, [],
            ("ocr-result-busy", () => window.ShowBusy(context)),
            ("ocr-result-no-text", () => window.ShowResult(MyCapture.Ocr.OcrResult.NoText(pass.Tag, TimeSpan.FromMilliseconds(420)), context, sample)),
            ("ocr-result-failure", () => window.ShowResult(MyCapture.Ocr.OcrResult.Failed(UiText.Get("Text_F09A6C5A6A10")), context, sample)),
            ("ocr-result-long-text", () =>
            {
                window.ShowResult(MyCapture.Ocr.OcrResult.Success(pass.Long.OcrText, pass.Tag, [], TimeSpan.FromMilliseconds(860))
                    .WithQuality(OcrQuality.Fast), context, sample);
                // The window offers the accurate model once it has shown a fast result.
                window.CompleteAccuracyPresentation();
                Finish(window.AccuracyModelCheckTask);
            }),
            ("ocr-result-model-downloading", () =>
            {
                window.BeginQualityOperation(downloading: true);
                window.ReportQualityProgress(0.42);
            }),
            ("ocr-result-model-failure", () => window.ShowAccuracyFailure(UiText.Get("Translate_DownloadFailed"))));
    }

    private static void TranslationPanel(OffscreenSurfaceSweep sweep)
    {
        var panel = new OcrTranslationPanel();
        TranslatedBlock[] results = [.. Translations.Select((item, index) => new TranslatedBlock(
            new TranslationBlock(index + 1, item.Source, new OcrRect(16, 16 + index * 40, 520, 32)),
            new TranslatedText(item.Source, item.Text, item.Missing, [])))];
        (string Name, Action Enter)[] states =
        [
            ("ocr-translation-downloading", () => panel.ShowDownloading(134_000_000, 320_000_000)),
            ("ocr-translation-download-failed", () => panel.ShowDownloadFailed(
                OcrResultWindow.DownloadFailureMessage(new HttpRequestException("Fixture network failure."), 320_000_000))),
            ("ocr-translation-translating", () => panel.ShowTranslating(3, 8)),
            ("ocr-translation-failed", () => panel.ShowFailed(UiText.Get("Translate_LoadFailed"))),
            ("ocr-translation-nothing", panel.ShowNothingToTranslate),
            ("ocr-translation-results", () => panel.ShowResults(results)),
        ];
        foreach ((string name, Action enter) in states)
        {
            enter();
            sweep.Surface(name, panel, new Size(480, 320), new Size(360, 320));
        }
    }

    /// <summary>
    /// An export held in progress, then the failure a person meets first: saving over the recording itself.
    /// The export waits for the preview reader to let go of the recording, so holding that reader keeps it
    /// running without an encoder; cancelling releases it before the failure.
    /// </summary>
    private static void Export(OffscreenSurfaceSweep sweep, OffscreenSurfaceSweep.Fixtures fixtures, LongContent text, bool gif)
    {
        string name = gif ? "gif-export" : "video-export";
        var dialog = new VideoExportDialog(fixtures.Recording, fixtures.Document, fixtures.Logs, gif);
        var reader = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampler = new TaskCompletionSource<GifPreviewSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.SourcePreviewRendererForTest = _ => reader.Task;
        dialog.SampleRendererForTest = (_, _) => sampler.Task;
        dialog.DestinationChooserForTest = () => Path.Combine(fixtures.Paths.QuickSaveRoot,
            Path.ChangeExtension(text.FileName, gif ? ".gif" : ".export.mp4"));
        Task export = Task.CompletedTask;
        var states = new List<(string Name, Action Enter)>
        {
            (name + "-in-progress", () =>
            {
                if (gif) dialog.StartSample();
                else dialog.RequestSourcePreview();
                export = dialog.ExportAsync();
                Assert.False(export.IsCompleted);
                ShowExportProgress(dialog, 120, 360);
            }),
            (name + "-failure", () =>
            {
                dialog.CancelExport();
                reader.TrySetCanceled();
                sampler.TrySetCanceled();
                Finish(export);
                dialog.DestinationChooserForTest = () => fixtures.Recording.OutputPath;
                Finish(dialog.ExportAsync());
            }),
        };
        if (gif)
            states.Add((name + "-sample-failure", () =>
            {
                dialog.SampleRendererForTest = (_, _) => Task.FromException<GifPreviewSample>(new IOException("0xC00D36C4"));
                dialog.StartSample();
                Finish(dialog.SampleCompletion);
            }));
        States(sweep, dialog, [], [.. states]);
    }

    /// <summary>
    /// Puts a running export at <paramref name="completed"/> of <paramref name="total"/> frames, as its encoder's
    /// progress reports would, with a fixed elapsed time so the remaining time reads the same on every run.
    /// </summary>
    private static void ShowExportProgress(VideoExportDialog dialog, int completed, int total)
    {
        var progress = new VideoFrameRenderProgress(completed, total, completed * 1000d / 30);
        typeof(VideoExportDialog).GetMethod("ShowProgress", OffscreenSurfaceSweep.Members)!.Invoke(dialog,
            [progress, Stopwatch.StartNew(), OffscreenSurfaceSweep.Field<CancellationTokenSource>(dialog, "_cancellation")]);
        OffscreenSurfaceSweep.Field<MediaExportProgressRow>(dialog, "_progressRow").Report(progress, TimeSpan.FromSeconds(4));
    }

    private static void LegacyImport(OffscreenSurfaceSweep sweep, OffscreenSurfaceSweep.Fixtures fixtures, Pass pass)
    {
        var copying = new TaskCompletionSource<LegacyMigrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<LegacyMigrationResult> result = copying.Task;
        var operations = new LegacyImportOperations(new OffscreenSurfaceSweep.FixtureLegacyService(), fixtures.Paths,
            _ => Task.FromResult(true), () => { },
            (_, progress, _) =>
            {
                progress.Report(new LegacyMigrationProgress(LegacyMigrationStage.Copying, 5, 12, 5_242_880, 12_345_678));
                return result;
            },
            () => { }, () => Task.CompletedTask);
        var window = new LegacyDataImportWindow(operations, Path.Combine(fixtures.Evidence, pass.Text.LegacySource));
        Finish(window.ScanAsync());
        Task import = Task.CompletedTask;
        States(sweep, window, [OffscreenSurfaceSweep.Field<TextBlock>(window, "_source")],
            ("legacy-import-in-progress", () =>
            {
                import = window.ImportAsync();
                Settle();
                Assert.False(import.IsCompleted);
            }),
            ("legacy-import-failure", () =>
            {
                copying.SetResult(new LegacyMigrationResult(LegacyMigrationStatus.Failed, ErrorCode: "IoFailure"));
                Finish(import);
            }),
            ("legacy-import-no-space", () =>
            {
                result = Task.FromResult(new LegacyMigrationResult(LegacyMigrationStatus.Failed, ErrorCode: "InsufficientSpace",
                    RequiredBytes: 12_345_678, AvailableBytes: 2_097_152));
                Finish(window.ImportAsync());
            }));
    }

    /// <summary>
    /// The dialogs failures open, built as the app builds them and with their Details open: an unexpected
    /// error and a failed start, a draft that would not open (the longest title the app gives a dialog), and a
    /// pin save on a full disk and a denied settings export, which put three commands on one row.
    /// </summary>
    private static void Dialogs(OffscreenSurfaceSweep sweep, OffscreenSurfaceSweep.Fixtures fixtures, LongContent text)
    {
        // The folder the failures name exists, so the dialogs offer to open it as they would for a person.
        fixtures.Paths.EnsureCreated();
        string data = fixtures.Paths.DataRoot;
        Exception inUse = Thrown(new IOException($"The process cannot access the file '{Path.Combine(data, text.FileName)}' "
            + "because it is being used by another process.", unchecked((int)0x80070020)));
        sweep.Window("dialog-unhandled-details", Expanded(ThemedDialog.CreateUnhandled(inUse)));
        sweep.Window("dialog-startup-details", Expanded(ThemedDialog.CreateStartupFailure(inUse)));
        string draft = Path.Combine(data, "drafts", "11111111-1111-1111-1111-111111111111", "document.json");
        sweep.Window("dialog-draft-open-failure", Expanded(ThemedDialog.CreateError(UiText.Get("EditRecovery_OpenFailed"),
            Thrown(new FileNotFoundException($"Could not find file '{draft}'.", draft)))));
        // What PinManager shows when a pin cannot be saved: the cause, another location and the folder.
        string pin = Path.Combine(data, Path.ChangeExtension(text.FileName, ".png"));
        Exception full = Thrown(new IOException($"There is not enough space on the disk. : '{pin}'", unchecked((int)0x80070070)));
        string folder = ThemedDialog.SaveLocationFor(full, pin) ?? throw new DirectoryNotFoundException(data);
        sweep.Window("dialog-pin-save-failure", Expanded(new ThemedDialog(ThemedDialog.SaveFailureMessage(full, pin),
            UiText.Get("Text_A97A3B71F038"), MessageBoxButton.OK, MessageBoxResult.None, full.Message, () => { },
            UiText.Get("Dialog.ChooseLocation"), commands: [ThemedDialog.OpenSaveLocationCommand(folder)])));
        // Settings passes no location: the dialog finds the folder in the error.
        Exception denied = Thrown(new UnauthorizedAccessException($"Access to the path '{Path.Combine(data, text.Folder + ".json")}' is denied."));
        sweep.Window("dialog-settings-export-failure", Expanded(ThemedDialog.CreateError(UiText.Get("Settings_Export"), denied,
            () => { }, UiText.Get("Dialog.ChooseLocation"))));
        var rename = GalleryFolderEditDialog.ForName(text.Folder, false, _ => throw new ArgumentException("Fixture validation failure."));
        rename.SaveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        sweep.Window("folder-rename-long-name-invalid", rename);
        sweep.Window("folder-delete-long-name", new GalleryFolderDeletionDialog(text.Folder, new CaptureFolderDeletionPreview(12, 11, 340, 27)));
        string media = Path.Combine(fixtures.Paths.CapturesRoot, "2026", "10", text.FileName);
        sweep.Window("media-details-long-name", new MediaDetailsWindow([new MediaDetails(
            new MediaDetailsSource(media, 1920, 1080, DurationMs: 83_000), media, 48_123_456,
            new DateTime(2026, 10, 30, 16, 1, 0), new DateTime(2026, 10, 31, 9, 30, 0), 1920, 1080, "MP4", false)]));
        var player = new GalleryVideoPlayerWindow(NullLogger.Instance);
        TextBlock title = OffscreenSurfaceSweep.Field<TextBlock>(player, "_title");
        title.Text = text.FileName;
        sweep.Window("gallery-video-player-long-title", player, title);
    }

    private static void RecordingControls(OffscreenSurfaceSweep sweep)
    {
        var controls = new RecordingControlWindow(new RectD(100, 100, 800, 450), new RecordingSettings(),
            () => throw new InvalidOperationException("Fixtures never create a recorder."),
            () => throw new InvalidOperationException("Fixtures never create a recording file."), NullLogger.Instance)
        {
            LoadWatermarkImage = () => throw new FileFormatException("The fixture watermark image is not a PNG."),
        };
        States(sweep, controls, [],
            ("recording-control", () => { }),
            ("recording-control-watermark-error", () => Assert.True(controls.ApplyWatermarkSettings(
                new RecordingWatermarkSettings { Enabled = true, Source = RecordingWatermarkSource.Image }))),
            ("recording-control-recording", () => controls.PreviewRecordingForTest(paused: false, TimeSpan.FromSeconds(83))),
            ("recording-control-saved", () => controls.PreviewSavedForTest(TimeSpan.FromSeconds(83))));
    }

    /// <summary>
    /// Lays one window out in each state in turn, at the size the window takes in that state, then closes it.
    /// </summary>
    /// <param name="content">Blocks that show user content.</param>
    private static void States(OffscreenSurfaceSweep sweep, Window window, TextBlock[] content, params (string Name, Action Enter)[] states)
    {
        try
        {
            FrameworkElement root = OffscreenSurfaceSweep.Detach(window);
            foreach ((string name, Action enter) in states)
            {
                enter();
                Settle();
                (Size size, Size minimum) = sweep.WindowSizes(window, root);
                sweep.Surface(name, root, size, minimum, content);
            }
        }
        finally
        {
            OffscreenSurfaceSweep.Close(window);
        }
    }

    /// <summary>
    /// The checks of both languages, CLIPPED and OOB, and in English HANGUL. Hidden and fully transparent
    /// elements are not on screen and are skipped.
    /// </summary>
    private static void Inspect(OffscreenSurfaceSweep.Layout layout, string language, Func<string, bool> isFixture, List<Finding> findings)
    {
        Border surface = layout.Surface;
        foreach (DependencyObject node in OffscreenSurfaceSweep.Descendants(surface))
        {
            if (node is not FrameworkElement element || element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0
                || !IsShown(element, surface)) continue;
            if (element is TextBlock block)
            {
                string text = KoreanWordWrap.Strip(KoreanWordWrapProbe.TextOf(block));
                if (string.IsNullOrWhiteSpace(text)) continue;
                // An ellipsis is the block saying that it shortened its text on purpose.
                if (block.TextTrimming == TextTrimming.None && Cut(block, Ink(block), surface) is { } cut)
                    Add("CLIPPED", block, text, "cut " + cut);
                if (language == "en-US" && text.Any(OffscreenSurfaceSweep.IsHangul) && !layout.Excluded.Contains(block) && !isFixture(text))
                    Add("HANGUL", block, text);
            }
            else if (element is TextBox box && language == "en-US" && box.Text.Any(OffscreenSurfaceSweep.IsHangul) && !isFixture(box.Text))
            {
                Add("HANGUL", box, box.Text);
            }
            // A control's own parts (a combo box's toggle, a slider's thumb buttons) go with the control.
            if (element is ButtonBase or TextBox or ComboBox or Slider
                && element.TemplatedParent is not (ButtonBase or TextBox or ComboBox or Slider)
                && Cut(element, new Rect(element.RenderSize), surface) is { } hidden)
                Add("OOB", element, ControlText(element), "cut " + hidden);
        }

        void Add(string kind, FrameworkElement element, string text, string? detail = null) =>
            findings.Add(new Finding(kind, language, layout.Name, layout.Mode,
                $"element={OffscreenSurfaceSweep.ElementName(element)}; text={Shorten(OneLine(text))}" + (detail is null ? "" : "; " + detail)));
    }

    /// <summary>
    /// What part of <paramref name="bounds"/>, in <paramref name="element"/>'s coordinates, the element, its
    /// ancestors and finally the surface do not let through, as "right 12 by Rail, bottom 3 by its slot in Row"; null
    /// when nothing is cut by more than <see cref="Tolerance"/>, or when an empty clip hides the element on
    /// purpose. Each clip is a visual's layout clip (an arrange slot smaller than the element, MaxWidth/MaxHeight,
    /// ClipToBounds) combined with its Clip, and each side names the visual that cuts it most. Along an axis
    /// that a viewport in between can scroll, what lies past the viewport is reachable: only what the
    /// viewport's own holders cut along that axis still counts.
    /// </summary>
    private static string? Cut(Visual element, Rect bounds, Border surface)
    {
        if (bounds.IsEmpty) return null;
        var whole = new Rect(surface.RenderSize);
        Rect area = element.TransformToAncestor(surface).TransformBounds(bounds);
        var sides = new (double Amount, Visual? By)[4];
        for (DependencyObject? node = element; node is Visual visual; node = VisualTreeHelper.GetParent(node))
        {
            bool atSurface = ReferenceEquals(visual, surface);
            Rect? limit = VisualTreeHelper.GetClip(visual)?.Bounds;
            if (limit is { IsEmpty: false } local && !atSurface) limit = visual.TransformToAncestor(surface).TransformBounds(local);
            if (atSurface) limit = Rect.Intersect(limit ?? whole, whole);
            if (limit is { } box)
            {
                if (box.IsEmpty || box.Width <= 0 || box.Height <= 0) return null;
                ScrollViewer? viewer = (visual as ScrollContentPresenter)?.TemplatedParent as ScrollViewer;
                if (viewer?.ScrollableWidth > 0)
                {
                    double from = Math.Clamp(area.Left, box.Left, box.Right), to = Math.Clamp(area.Right, box.Left, box.Right);
                    area = new Rect(from, area.Top, to - from, area.Height);
                }
                else
                {
                    Widen(0, box.Left - area.Left, visual);
                    Widen(2, area.Right - box.Right, visual);
                }
                if (viewer?.ScrollableHeight > 0)
                {
                    double from = Math.Clamp(area.Top, box.Top, box.Bottom), to = Math.Clamp(area.Bottom, box.Top, box.Bottom);
                    area = new Rect(area.Left, from, area.Width, to - from);
                }
                else
                {
                    Widen(1, box.Top - area.Top, visual);
                    Widen(3, area.Bottom - box.Bottom, visual);
                }
            }
            if (atSurface) break;
        }

        string[] names = ["left", "top", "right", "bottom"];
        string[] cut = [.. sides.Select((side, index) => (side.Amount, side.By, Name: names[index]))
            .Where(side => side.Amount > Tolerance)
            .Select(side => FormattableString.Invariant($"{side.Name} {side.Amount:0.#} by {Holder(side.By!)}"))];
        return cut.Length == 0 ? null : string.Join(", ", cut);

        void Widen(int side, double amount, Visual by)
        {
            if (amount > sides[side].Amount) sides[side] = (amount, by);
        }

        string Holder(Visual by) => ReferenceEquals(by, surface) ? "the surface"
            : ReferenceEquals(by, element) ? "its slot in " + Name(VisualTreeHelper.GetParent(by)) : Name(by);

        // A template part goes by its control, as in "Selection of SortBox".
        static string Name(DependencyObject? visual) => visual switch
        {
            FrameworkElement { TemplatedParent: FrameworkElement owner } part =>
                OffscreenSurfaceSweep.ElementName(part) + " of " + OffscreenSurfaceSweep.ElementName(owner),
            FrameworkElement named => OffscreenSurfaceSweep.ElementName(named),
            _ => visual?.GetType().Name ?? "nothing",
        };
    }

    /// <summary>
    /// The glyphs a text block drew in its last arrange, in its own coordinates. Its background is not ink, and
    /// the box it was given does not matter: only where the letters landed.
    /// </summary>
    private static Rect Ink(TextBlock block)
    {
        return Glyphs(VisualTreeHelper.GetDrawing(block));

        static Rect Glyphs(Drawing? drawing)
        {
            switch (drawing)
            {
                case GlyphRunDrawing glyphs:
                    return glyphs.Bounds;
                case DrawingGroup group:
                    Rect union = Rect.Empty;
                    foreach (Drawing child in group.Children) union.Union(Glyphs(child));
                    // A group clips its content in its own space, before its transform places it.
                    if (!union.IsEmpty && group.ClipGeometry is { } clip) union.Intersect(clip.Bounds);
                    if (!union.IsEmpty && group.Transform is { } transform) union = transform.TransformBounds(union);
                    return union;
                default:
                    return Rect.Empty;
            }
        }
    }

    private static bool IsShown(DependencyObject node, Border surface)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible } or UIElement { Opacity: < 0.01 }) return false;
            if (ReferenceEquals(current, surface)) break;
        }
        return true;
    }

    /// <summary>What a person reads on a control, or its accessible name when it shows no text of its own.</summary>
    private static string ControlText(FrameworkElement control)
    {
        string text = control switch
        {
            TextBox box => box.Text,
            ComboBox combo => combo.Text,
            ContentControl { Content: string content } => content,
            _ => string.Empty,
        };
        return text.Length > 0 ? text : AutomationProperties.GetName(control);
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..119] + "…";

    /// <summary>
    /// Known cases for the checks, laid out the same way as the sweep: a change that blinds a check, or makes
    /// it report fitted text, fails here instead of passing the sweep.
    /// </summary>
    private static void VerifyChecks(Application application)
    {
        string user = OffscreenSurfaceSweep.FixtureText.Korean.Title;
        (string Name, Func<FrameworkElement> Create, string[] Expected)[] cases =
        [
            ("text that fits", () => Line("Fits"), []),
            ("text past a clipping ancestor", () => new Border
            {
                Width = 40, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Child = Line("Wider than forty"),
            }, ["CLIPPED"]),
            ("wrapped text taller than its fixed height", () => new TextBlock
            {
                Text = "Wrapped text needs more lines than its height", TextWrapping = TextWrapping.Wrap, Width = 80, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            }, ["CLIPPED"]),
            ("a column narrower than its text", () =>
            {
                var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(60) }, new ColumnDefinition() } };
                grid.Children.Add(Line("Wider than its column"));
                return grid;
            }, ["CLIPPED"]),
            ("text past the surface", () => At(Line("Past the edge"), 170), ["CLIPPED"]),
            ("text past a scrollable viewport", () => Scrolling(Enumerable.Range(1, 6).Select(index => (UIElement)Line("Line " + index))), []),
            ("an ellipsis", () => new TextBlock
            {
                Text = "Shortened with an ellipsis", Width = 40, TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            }, []),
            ("a wide box with short text", () => new Border
            {
                Width = 100, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = "Short", Width = 300 },
            }, []),
            ("a combo box narrower than its choice", () => new ComboBox
            {
                Width = 60, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Items = { "Wider than the box" }, SelectedIndex = 0,
            }, ["CLIPPED"]),
            ("a field past the surface", () => At(new TextBox { Width = 100 }, 150), ["OOB"]),
            ("a button wider than its slot", () => new Border
            {
                Width = 48, Padding = new Thickness(7), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Child = new Button { Width = 40, Height = 24 },
            }, ["OOB"]),
            ("a field in a scrollable viewport", () => Scrolling(Enumerable.Range(1, 4)
                .Select(_ => (UIElement)new TextBox { Margin = new Thickness(0, 0, 0, 4) })), []),
            ("Korean UI text", () => Line("한국어 문구"), ["HANGUL"]),
            ("Korean user content", () => Line(user), []),
            ("a Korean UI word that user content also contains", () => Line("캡처"), ["HANGUL"]),
        ];
        var size = new Size(200, 60);
        foreach ((string name, Func<FrameworkElement> create, string[] expected) in cases)
        {
            var surface = new Border { Child = create(), Background = (Brush)application.FindResource("Surface.Base") };
            TextElement.SetFontFamily(surface, ThemeFonts.Ui);
            VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
            for (int pass = 0; pass < 2; pass++)
            {
                surface.Measure(size);
                surface.Arrange(new Rect(size));
                surface.UpdateLayout();
            }
            var findings = new List<Finding>();
            Inspect(new OffscreenSurfaceSweep.Layout("check", name, size, surface, new HashSet<TextBlock>()), "en-US",
                text => KoreanOnlyFrom([user], text), findings);
            string[] kinds = [.. findings.Select(finding => finding.Kind).Distinct().Order()];
            Assert.True(kinds.SequenceEqual(expected.Order()),
                $"Check self-test '{name}' expected [{string.Join(", ", expected)}] but found:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
            surface.Child = null;
        }
        Console.WriteLine($"CONTENT STATE CHECKS: {cases.Length} known cases passed.");

        static TextBlock Line(string text) => new()
        {
            Text = text, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };

        static Canvas At(UIElement child, double left)
        {
            Canvas.SetLeft(child, left);
            return new Canvas { Children = { child } };
        }

        static ScrollViewer Scrolling(IEnumerable<UIElement> children)
        {
            var panel = new StackPanel();
            foreach (UIElement child in children) panel.Children.Add(child);
            return new ScrollViewer { Height = 40, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }
    }

    /// <summary>A dialog with its Details section open, as after the person opens it.</summary>
    private static ThemedDialog Expanded(ThemedDialog dialog)
    {
        foreach (Expander details in OffscreenSurfaceSweep.LogicalDescendants(dialog).OfType<Expander>()) details.IsExpanded = true;
        return dialog;
    }

    /// <summary>The exception as it reaches a dialog, with the stack trace its details show.</summary>
    private static Exception Thrown(Exception error)
    {
        try { throw error; }
        catch (Exception caught) { return caught; }
    }

    /// <summary>Runs what the dispatcher has queued, as a shown window would before its next frame.</summary>
    private static void Settle() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);

    private static T Finish<T>(Task<T> task)
    {
        Finish((Task)task);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Runs the dispatcher until <paramref name="task"/> ends, as the shown window would.</summary>
    private static void Finish(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
        Assert.True(task.IsCompleted, "A fixture operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    /// <summary>One language's run: the fixture text of the default surfaces and the long content of the states.</summary>
    private sealed record Pass(string Tag, string Name, OffscreenSurfaceSweep.FixtureText Text, LongContent Long)
    {
        internal static Pass English { get; } = new("en-US", "English", OffscreenSurfaceSweep.FixtureText.English, LongContent.English);
        internal static Pass Korean { get; } = new("ko-KR", "Korean", OffscreenSurfaceSweep.FixtureText.Korean, LongContent.Korean);
    }

    /// <summary>Long content a person typed, named or captured, in the language of the run.</summary>
    private sealed record LongContent(string Title, string Folder, string FileName, string WindowTitle, string Document,
        string OcrText, string SearchMiss)
    {
        internal static LongContent Korean { get; } = new(
            "2026년 4분기 디자인 검토 회의에서 정리한 결정 사항과 후속 작업, 그리고 다음 출시 전에 다시 확인할 화면 캡처",
            "2026년 4분기 디자인 검토 회의 자료와 출시 전에 다시 확인할 화면 캡처 모음",
            "2026년 4분기 디자인 검토 회의 화면 녹화 최종본 - 출시 전 확인용.mp4",
            "디자인 검토 회의록 - 2026년 4분기 - 공유 문서 편집기",
            "2026년 4분기 디자인 검토 회의록 최종본.docx",
            "프로젝트 일정 공유\n"
            + "1. 디자인 검토는 11월 첫째 주 화요일 오전 10시에 3층 회의실에서 진행합니다.\n"
            + "2. 출시 후보 빌드는 검토가 끝난 다음 날 오후까지 사내 배포 채널에 올립니다.\n"
            + "3. 접근성 점검 결과와 남은 결함 목록은 공유 폴더의 최신 문서를 기준으로 정리합니다.\n"
            + "담당: 김민지, 이서준 · 연락처: design-review@example.com",
            "찾을 수 없는 검색어");

        internal static LongContent English { get; } = new(
            "Decisions and follow-up tasks from the Q4 2026 design review meeting, plus screens to check again before the next release",
            "Q4 2026 design review material and screens to check again before release",
            "Q4 2026 design review screen recording final cut - for the release check.mp4",
            "Design review minutes - Q4 2026 - Shared document editor",
            "Q4 2026 design review minutes final.docx",
            "Project schedule\n"
            + "1. The design review takes place on the first Tuesday of November at 10 a.m. in the third-floor meeting room.\n"
            + "2. The release candidate build goes to the internal distribution channel by the afternoon after the review.\n"
            + "3. Accessibility results and the remaining defect list follow the latest document in the shared folder.\n"
            + "Owners: Minji Kim, Seojun Lee · Contact: design-review@example.com",
            "no-matching-search-term");

        internal IReadOnlyList<string> All => [Title, Folder, FileName, WindowTitle, Document, OcrText, SearchMiss];
    }

    private sealed record Finding(string Kind, string Language, string Surface, string Mode, string Detail)
    {
        /// <summary>The evidence image of the layout, under artifacts/content-states.</summary>
        internal string Png => $"artifacts/content-states/{Language}/{Surface}-{Mode}.png";

        public override string ToString() => $"{Kind} {Language} {Surface}/{Mode}; {Detail}";
    }

    /// <summary>An accurate model that is supported but not yet downloaded; fixtures never download it.</summary>
    private sealed class FixtureAccuracyModel : IOcrAccuracyModel
    {
        public bool IsSupported => true;
        public bool IsReady => false;
        public bool IsMetered => false;
        public long DownloadBytes => OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate);
        public Task<bool> DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fixtures never download models.");
    }
}
