using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Media;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
#if DEBUG
using MyCapture.App.Preview;
#endif
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Korean UI copy in real window content, laid out offscreen without showing a window.
/// Content exclusions live in this fixture instead of adding test-only flags to product controls.
/// </summary>
public sealed class KoreanWordWrapSweepTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public Task KoreanWindows_DefaultAndMinimumSizes_HaveNoOrphansOrUnjoinedCopy() =>
        SettingsThemePreviewTests.RunIsolated(nameof(KoreanWindows_DefaultAndMinimumSizes_HaveNoOrphansOrUnjoinedCopy),
            () => Run(null, verify: true), typeof(KoreanWordWrapSweepTests));

    /// <summary>
    /// Opt-in evidence: MYCAPTURE_KOREAN_WRAP_EVIDENCE=before|after|after-consolas renders every
    /// surface into artifacts/korean-wrap/before, /after or /after-consolas (the switch hands on a
    /// literal, so the variable chooses between fixed directories and never supplies a path).
    /// "after-consolas" draws the monospace text in Consolas: the "before" set came from a run in
    /// which Cascadia Mono was not available, so key caps and time readouts are wider in "after".
    /// </summary>
    [Fact]
    public Task RenderBeforeAndAfterEvidence()
    {
        string? phase = Environment.GetEnvironmentVariable("MYCAPTURE_KOREAN_WRAP_EVIDENCE") switch
        {
            "before" => "before",
            "after" => "after",
            "after-consolas" => "after-consolas",
            _ => null,
        };
        if (phase is null) return Task.CompletedTask;
        return SettingsThemePreviewTests.RunIsolated(nameof(RenderBeforeAndAfterEvidence),
            () => Run(phase, verify: false), typeof(KoreanWordWrapSweepTests));
    }

    private static void Run(string? phase, bool verify)
    {
        string evidence = Path.Combine(RepositoryRoot(), "artifacts", "korean-wrap");
        string? output = phase is null ? null : DiagnosticOutputPaths.Create(Path.Combine(evidence, phase));
        // All persistence services point at this fixture root; no real application profile is read.
        AppPaths paths = AppPaths.CreateForRoot(Path.Combine(evidence, "fixtures"));
        using var language = UiText.UseLanguage("ko-KR");
        UiText.Configure("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
        application.Resources["PinLabel"] = new PinLabelConverter();
        application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
        if (phase == "after-consolas") application.Resources["Font.Mono"] = new FontFamily("Consolas, Malgun Gothic, monospace");
        ThemeService.Apply(AppTheme.Midnight);
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var failures = new List<string>();
        int surfaces = 0, wrappingCopy = 0;
        using ILoggerFactory logs = NullLoggerFactory.Instance;
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var ocrService = new UxReviewSelfTest.FixtureOcr();
        using var presenter = new OcrResultPresenter(ocrService, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var videoLibrary = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        BitmapSource sample = (BitmapSource)typeof(UxReviewSelfTest).GetMethod("CreateFixtureBitmap", Members)!.Invoke(null, null)!;
        var region = new RectD(0, 0, sample.PixelWidth, sample.PixelHeight);
        var recording = new RecordingResult(Path.Combine(paths.DataRoot, "not-opened.mp4"), 12000, 30, 360, 640, 360);
        var document = VideoEditDocument.CreateFor(640, 360, 12000);
        document.TextOverlays.Add(new TimedTextOverlay { Text = "사용자가 입력한 자막", StartMs = 300, EndMs = 2200 });

        try
        {
            var viewModel = new GalleryViewModel(controller, _ => Path.Combine(paths.DataRoot, "not-opened.png"), 320,
                clock: () => DateTimeOffset.Parse("2026-10-31T00:00:00Z"));
            var indexing = new OcrIndexingService(controller, ocrService, queue.GetDirectory, () => settings.Ocr,
                NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
            var gallery = new GalleryWindow(viewModel, controller,
                new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
                videoLibrary, paths, logs, presenter, () => settings.Ocr, indexing,
                new UxReviewSelfTest.FixturePrivacy(), NullLogger.Instance);
            Window("gallery", gallery);
            Window("organization", new GalleryOrganizationDialog(new CaptureRecord { Title = "사용자 캡처 제목", Tags = "업무, 참고" },
                (_, _) => Task.FromResult(true)));
            Window("settings", new SettingsWindow(() => settings,
                _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance, settingsStore: null));
            var hotkeys = new SettingsWindow(() => settings,
                _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance, settingsStore: null);
            hotkeys.CategoryTabs.SelectedItem = hotkeys.CategoryTabs.Items.OfType<TabItem>()
                .Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabShortcuts")));
            Window("hotkeys", hotkeys);
            // Every category with its folded groups open: only the selected category is built into
            // the visual tree, so the two surfaces above leave most of the window unread.
            foreach (SettingsSection section in Enum.GetValues<SettingsSection>())
            {
                var category = new SettingsWindow(() => settings,
                    _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance, settingsStore: null);
                Assert.True(category.NavigateToSection(section));
                foreach (Expander group in LogicalDescendants(category).OfType<Expander>()) group.IsExpanded = true;
                Window("settings-" + section.ToString().ToLowerInvariant() + "-expanded", category);
            }
            Window("quick-access", new QuickAccessWindow(sample, () => Task.FromResult(true), () => { },
                () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("fixture.png"),
                firstCaptureTip: UiText.Get("Onboard_QuickCardTip")) { IsKept = true });
            var failedCard = QuickAccessWindow.ForPendingCapture(sample, false);
            failedCard.ShowCaptureFailure(UiText.Get("Dialog.Storage"), UiText.Get("Dialog.AppFailure"));
            Window("quick-access-failure", failedCard);
            Window("error", new ThemedDialog(UiText.Get("Dialog.Storage"), UiText.Get("Dialog.AppFailure"),
                MessageBoxButton.OK, MessageBoxResult.OK, "Synthetic diagnostic details — 0x80070070"));
            Window("shortcuts", new ShortcutHelpWindow());
            var monitor = new MonitorInfo("fixture", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1040), 96, true);
            PracticeStep?[] steps = [null, .. PracticeSession.Steps.Select(item => (PracticeStep?)item), PracticeStep.Results];
            foreach (PracticeStep? step in steps)
            {
                var welcome = OnboardingGuideTests.Create(new OnboardingGuideTests.FakeClipboard(), new WelcomeOptions
                { ReducedMotion = true, TextScale = 1, MonitorProvider = () => monitor, ShowCoreShortcutsConsent = step is null });
                if (step is { } lesson) PracticeJourneyDriver.AdvanceTo(welcome.Session, lesson);
                Window("onboarding-" + (step?.ToString().ToLowerInvariant() ?? "consent"), welcome);
            }
            Window("annotation", new AnnotationEditorWindow(new FrozenFrame(sample, region, null, 0), region, sample,
                initialDocument: AnnotationDocument.CreateFor(sample.PixelWidth, sample.PixelHeight)));
            Window("image-export", new ImageReductionExportDialog(sample, Path.Combine(paths.QuickSaveRoot, "review.png")));
            Window("video", new VideoEditorWindow(recording, paths, logs, document));
            const string source = "public void Capture()\r\n{\r\n    Save();\r\n}";
            BitmapSource code = ClipboardCodeRenderer.TryRender(source,
                "<pre style=\"color:#d4d4d4;background-color:#1e1e1e\">public void Capture() { Save(); }</pre>")
                ?? ClipboardTextRenderer.Render(source, false);
            Window("pin-code", new PinWindow(PinContent.FromText(code, source, false),
                new PinViewState(code.PixelWidth, code.PixelHeight, 1, 1, 0.1), 40, 40, () => settings.Pin));
            var feedbackPin = new PinWindow(PinContent.FromText(code, source, false),
                new PinViewState(code.PixelWidth, code.PixelHeight, 1, 1, 0.1), 40, 40, () => settings.Pin);
            feedbackPin.ReportCopyResult(false);
            Window("pin-feedback", feedbackPin);
            var hint = new PinClickThroughHint(null!);
            MethodInfo? setHintText = typeof(PinClickThroughHint).GetMethod("SetText", Members);
            if (verify) Assert.NotNull(setHintText);
            // The received baseline has only the handle's direct Text.Text assignment. The new
            // pure setter is the actual handle path, without TryReveal's forbidden native window.
            if (setHintText is null) hint.Text.Text = UiText.Get("Pin.ClickThroughIntro");
            else setHintText.Invoke(hint, [UiText.Get("Pin.ClickThroughIntro")]);
            Window("pin-click-through-hint", hint);
#if DEBUG
            using (var input = new PreviewPinInput(new MemoryRegistrar(), () => throw new InvalidOperationException("No clipboard access.")))
                Window("pin-preview", new PinPreviewWindow(input));
#endif
            Window("support", new SupportWindow(new NoSupportActions(), _ => new SupportDiagnosticSnapshot(
                "4.12.0", "fixture", false, "10.0.26100", 1, "ko-KR", "Midnight")));
            Window("ocr-result", new OcrResultWindow());
            var translation = new OcrTranslationPanel();
            translation.ShowModelNeeded(320_000_000);
            Surface("ocr-translation", translation, new Size(480, 320), new Size(360, 320));
            Window("settings-guide", new SettingsGuideWindow(SettingsGuideCatalog.Get("G03"), reducedMotion: () => true));
            Window("timed-text", new TimedTextOverlayDialog(12000, 2000,
                new TimedTextOverlay { Text = "사용자가 입력한 자막", StartMs = 2000, EndMs = 8000 }));
            Window("video-export", new VideoExportDialog(recording, document, logs, gif: false));
            Window("gif-export", new VideoExportDialog(recording, document, logs, gif: true));
            Window("feature-tutorial", new FeatureTutorialWindow(new HotkeySettings(), mediaFactory: () => new OnboardingGuideTests.FakeMedia()));
            using (var tutorial = new WelcomeTutorialPreview(new WelcomeOptions
                { ReducedMotion = true, MediaFactory = () => new OnboardingGuideTests.FakeMedia() }) { AssetDirectory = paths.DataRoot })
            {
                tutorial.SelectFeature("text-copy");
                Surface("tutorial-fallback", tutorial, new Size(600, 350), new Size(360, 350));
            }
            var service = new FixtureLegacyService();
            var operations = new LegacyImportOperations(service, paths, _ => Task.FromResult(true), () => { },
                (_, _, _) => Task.FromResult(new LegacyMigrationResult(LegacyMigrationStatus.Failed, ErrorCode: "IoFailure")),
                () => { }, () => Task.CompletedTask);
            var legacy = new LegacyDataImportWindow(operations, Path.Combine(evidence, "기존 자료"));
            legacy.ScanAsync().GetAwaiter().GetResult();
            Window("legacy-import", legacy, Field<TextBlock>(legacy, "_source"));
            var recovery = new LegacyMigrationRecoveryWindow(Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.DecisionRequired, "IoFailure")),
                () => Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.DecisionRequired, "IoFailure")),
                () => Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.Clean)));
            recovery.RetryAsync().GetAwaiter().GetResult();
            Window("legacy-recovery", recovery);
            Window("unapplied-changes", new UnappliedChangesDialog());
            Window("media-details", new MediaDetailsWindow([new MediaDetails(
                new MediaDetailsSource("fixture.png", 640, 360), "fixture.png", null, null, null, 640, 360, "PNG", true)]));
            Window("contact-sheet", new GalleryContactSheetDialog([], 3, () => true));
            Window("draft-recovery", new DraftRecoveryDialog(
                [new DraftRecoveryItem(Guid.Parse("11111111-1111-1111-1111-111111111111"), false,
                    DateTimeOffset.Parse("2026-10-31T00:00:00Z"), 640, 360, sample)], (_, _) => Task.FromResult(true)));
            var folder = GalleryFolderEditDialog.ForName("사용자 폴더", true, _ => throw new ArgumentException("Fixture validation failure."));
            folder.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Window("folder-edit", folder);
            Window("folder-delete", new GalleryFolderDeletionDialog("사용자 폴더", new CaptureFolderDeletionPreview(2, 1, 5, 3)));
            var tags = new GalleryBatchTagsDialog([new CaptureRecord { Tags = "업무, 참고" }], _ => throw new InvalidOperationException("No persistence."));
            tags.NewTagInput.Text = string.Empty;
            Assert.False(tags.AddNewTag());
            Window("batch-tags", tags);
            var watermark = new RecordingWatermarkSettingsDialog(new RecordingWatermarkSettings(), null);
            Field<TextBox>(watermark, "_width").Text = "invalid";
            var watermarkPanel = (Panel)((ScrollViewer)watermark.Content).Content;
            var watermarkActions = (Panel)watermarkPanel.Children[^1];
            watermarkActions.Children.OfType<Button>().Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Window("recording-watermark", watermark);
            var player = new GalleryVideoPlayerWindow(NullLogger.Instance);
            Field<TextBlock>(player, "_title").Text = "사용자 녹화 파일.mp4";
            Window("gallery-video-player", player, Field<TextBlock>(player, "_title"));
            var rating = new EngagementNotice();
            rating.ShowInvitation();
            Surface("engagement-rating", rating, new Size(400, 200), new Size(360, 200));
            var update = new EngagementNotice();
            update.ShowUpdateAvailable("4.12.1");
            Surface("engagement-update", update, new Size(400, 200), new Size(360, 200));
            Console.WriteLine($"KOREAN WRAP SWEEP: {surfaces} layouts; {wrappingCopy} wrapping Korean UI blocks; {failures.Count} findings.");
            foreach (string failure in failures) Console.WriteLine(failure);
            if (output is not null)
                File.WriteAllText(DiagnosticOutputPaths.Child(output, "findings.json"), JsonSerializer.Serialize(new
                { phase, surfaces, wrappingCopy, findings = failures }, new JsonSerializerOptions { WriteIndented = true }));
            if (verify) Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
            application.Shutdown();
        }

        void Window(string name, Window window, params TextBlock[] content)
        {
            try
            {
                var root = (FrameworkElement)window.Content;
                root.DataContext = window.DataContext;
                root.Language = window.Language;
                root.FlowDirection = window.FlowDirection;
                window.Content = null;
                root.Resources.MergedDictionaries.Add(window.Resources);
                TextElement.SetFontFamily(root, window.FontFamily);
                TextElement.SetForeground(root, window.Foreground);
                TextElement.SetFontSize(root, window.FontSize);
                root.Measure(new Size(double.IsFinite(window.Width) ? window.Width : 1000, double.PositiveInfinity));
                var size = new Size(double.IsFinite(window.Width) ? window.Width : Math.Max(root.DesiredSize.Width, 1),
                    double.IsFinite(window.Height) ? window.Height : Math.Max(root.DesiredSize.Height, 1));
                var minimum = new Size(window.MinWidth > 0 ? window.MinWidth : size.Width,
                    window.MinHeight > 0 ? window.MinHeight : size.Height);
                // A non-resizable SizeToContent dialog has no separate smaller supported size.
                if (window.ResizeMode == ResizeMode.NoResize) minimum = size;
                Surface(name, root, size, minimum, content);
            }
            finally
            {
                if (window is GalleryWindow library) library.CloseForExit();
                else if (window is SettingsWindow preferences) preferences.CloseForExit();
                else if (window is OcrResultWindow ocr) ocr.CloseForExit();
                else window.Close();
            }
        }

        void Surface(string name, FrameworkElement root, Size normal, Size minimum, params TextBlock[] content)
        {
            var excluded = content.ToHashSet();
            var surface = new Border { Child = root, Background = (Brush)application.FindResource("Surface.Base") };
            TextElement.SetFontFamily(surface, ThemeFonts.Ui);
            TextElement.SetForeground(surface, (Brush)application.FindResource("Text.Primary"));
            ThemeFonts.ApplyText(surface);
            VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
            foreach ((string mode, Size size) in new[] { ("default", normal), ("minimum", minimum) })
            {
                // Initializing text pointers before layout gives the probe stable complex-text geometry.
                foreach (TextBlock block in Descendants(surface).OfType<TextBlock>()) _ = block.ContentStart;
                surface.Measure(size);
                surface.Arrange(new Rect(size));
                surface.UpdateLayout();
                // Templates create additional text blocks during the first measure. Initialize
                // their text containers too, then settle layout before reading character rectangles.
                foreach (TextBlock block in Descendants(surface).OfType<TextBlock>()) _ = block.ContentStart;
                surface.Measure(size);
                surface.Arrange(new Rect(size));
                surface.UpdateLayout();
                Assert.Equal(1, VisualTreeHelper.GetDpi(surface).DpiScaleX);
                foreach (TextBlock block in Descendants(surface).OfType<TextBlock>())
                {
                    if (block.TextWrapping == TextWrapping.NoWrap || block.ActualWidth <= 0 || block.ActualHeight <= 0 || IsHidden(block)) continue;
                    string display = KoreanWordWrapProbe.TextOf(block);
                    string plain = KoreanWordWrap.Strip(display);
                    if (!plain.Any(IsHangul)) continue;
                    // Library/OCR item rows and caller-named user paths stay on WPF's fast content path.
                    if (excluded.Contains(block) || IsLibraryItem(block)) continue;
                    wrappingCopy++;
                    string label = $"{name}/{mode}; element={ElementName(block)}; text={plain}";
                    if (display != KoreanWordWrap.KeepWords(plain)) failures.Add("UNJOINED " + label);
                    if (KoreanWordWrapProbe.Inspect(block) is { } orphan)
                        failures.Add($"ORPHAN {label}; lines={orphan.LineCount}; last={orphan.LastLine}");
                }
                if (output is not null)
                {
                    var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width)),
                        Math.Max(1, (int)Math.Ceiling(size.Height)), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using FileStream stream = File.Create(DiagnosticOutputPaths.Child(output, $"{name}-{mode}.png"));
                    encoder.Save(stream);
                }
                surfaces++;
                Console.WriteLine($"KOREAN WRAP {name}/{mode}: {size.Width:0.##} x {size.Height:0.##}, Korean, Midnight, 96 DPI");
            }
            surface.Child = null;
        }
    }

    private static bool IsHangul(char value) => value is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    private static bool IsHidden(DependencyObject node)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return true;
        return false;
    }

    private static bool IsLibraryItem(DependencyObject node)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: GalleryItemViewModel }) return true;
        return false;
    }

    private static string ElementName(TextBlock block)
    {
        if (block.Name.Length > 0) return block.Name;
        string name = AutomationProperties.GetName(block);
        return name.Length > 0 ? name : block.GetType().Name;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(node, index))) yield return child;
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject node)
    {
        yield return node;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject element)
                foreach (DependencyObject descendant in LogicalDescendants(element)) yield return descendant;
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Members)!.GetValue(owner)!;

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class NoSupportActions : ISupportActions
    {
        public void CopyText(string text) => throw new InvalidOperationException("Fixture never accesses the clipboard.");
        public bool TryOpenUri(string uri) => throw new InvalidOperationException("Fixture never opens an external URI.");
    }

#if DEBUG
    private sealed class MemoryRegistrar : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        { errorCode = 0; errorMessage = string.Empty; return true; }
        public void Unregister(int id) { }
    }
#endif

    private sealed class FixtureLegacyService : ILegacyLibraryMigrationService
    {
        public Task<LegacyMigrationScan> ScanAsync(string sourceDataRoot, AppPaths targetPaths, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LegacyMigrationScan(LegacyMigrationScanStatus.Ready, 12, 3, 2, 12345678, true, UnusableCount: 1, SettingsUnreadable: true));
        public Task<LegacyMigrationResult> ImportAsync(string sourceDataRoot, AppPaths targetPaths, CaptureQueue quiescedTargetQueue,
            AppSettings currentSettings, IProgress<LegacyMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fixture never imports files.");
        public Task<LegacyRecoveryResult> RecoverPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.DecisionRequired, "IoFailure"));
        public Task<LegacyRecoveryResult> AbandonPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fixture never abandons imported files.");
    }
}
