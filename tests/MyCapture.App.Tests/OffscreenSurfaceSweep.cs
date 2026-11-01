using System.IO;
using System.Reflection;
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
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Real window content laid out offscreen at its default and minimum sizes without showing a window.
/// The sweeps share the windows, their fixtures and the layout passes; each sweep inspects every
/// layout with its own checks. Content exclusions live in the fixtures instead of adding test-only
/// flags to product controls.
/// </summary>
internal sealed class OffscreenSurfaceSweep(Application application, Action<OffscreenSurfaceSweep.Layout> inspect)
{
    internal const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>One surface laid out and settled at one size, before it is rendered.</summary>
    /// <param name="Excluded">Blocks the caller named as user content.</param>
    internal sealed record Layout(string Name, string Mode, Size Size, Border Surface, IReadOnlySet<TextBlock> Excluded);

    /// <summary>What a person typed or named in the fixtures, in the language of the run.</summary>
    internal sealed record FixtureText(string LanguageTag, string Title, string Tags, string Subtitle, string Folder,
        string RecordingFile, string LegacySource)
    {
        internal static FixtureText Korean { get; } =
            new("ko-KR", "사용자 캡처 제목", "업무, 참고", "사용자가 입력한 자막", "사용자 폴더", "사용자 녹화 파일.mp4", "기존 자료");

        internal static FixtureText English { get; } =
            new("en-US", "User capture title", "work, reference", "Subtitle the user typed", "User folder", "User recording.mp4", "Existing data");

        internal IReadOnlyList<string> All => [Title, Tags, Subtitle, Folder, RecordingFile, LegacySource];
    }

    /// <summary>When set, every layout is also saved there as name-mode.png.</summary>
    internal string? Output { get; init; }

    /// <summary>Starts each layout's console line, as in "KOREAN WRAP gallery/default: …".</summary>
    internal string ConsolePrefix { get; init; } = "SURFACE";

    /// <summary>The language each layout's console line names.</summary>
    internal string LanguageName { get; init; } = "Korean";

    /// <summary>
    /// Whether a window stops growing at its MaxWidth and MaxHeight, as a shown window does. The Korean
    /// sweep keeps laying content out at its full height.
    /// </summary>
    internal bool StopsAtMaximumSize { get; init; }

    /// <summary>
    /// Whether a window also gets the sizes it sets when it loads, which an offscreen layout never does:
    /// the welcome window fits its main button to its longest label then. The Korean sweep keeps the
    /// size the window is built with.
    /// </summary>
    internal bool SizesAsLoaded { get; init; }

    /// <summary>Layouts so far: each surface twice, at its default and its minimum size.</summary>
    internal int Layouts { get; private set; }

    /// <summary>The application resources every surface resolves: the theme dictionaries and shared converters.</summary>
    internal static Application CreateApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
        application.Resources["PinLabel"] = new PinLabelConverter();
        application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
        return application;
    }

    /// <summary>
    /// The services and media the surfaces are built from. All persistence services point at the
    /// fixture root; no real application profile is read.
    /// </summary>
    internal sealed class Fixtures : IDisposable
    {
        internal Fixtures(string evidence, string root, FixtureText text)
        {
            Evidence = evidence;
            Text = text;
            Paths = AppPaths.CreateForRoot(root);
            Queue = new CaptureQueue(Paths, Settings.Queue, NullLogger<CaptureQueue>.Instance);
            Presenter = new OcrResultPresenter(Ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
            Controller = new GalleryController(Queue, NullLogger<GalleryController>.Instance);
            var persistence = new CapturePersistenceService(Queue, Paths, () => Settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            Commit = new CaptureCommitService(persistence, () => Settings, () => Paths,
                NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
            VideoLibrary = new VideoLibraryService(Queue, Paths, NullLogger<VideoLibraryService>.Instance);
            Sample = (BitmapSource)typeof(UxReviewSelfTest).GetMethod("CreateFixtureBitmap", Members)!.Invoke(null, null)!;
            Region = new RectD(0, 0, Sample.PixelWidth, Sample.PixelHeight);
            Recording = new RecordingResult(Path.Combine(Paths.DataRoot, "not-opened.mp4"), 12000, 30, 360, 640, 360);
            Document = VideoEditDocument.CreateFor(640, 360, 12000);
            Document.TextOverlays.Add(new TimedTextOverlay { Text = text.Subtitle, StartMs = 300, EndMs = 2200 });
        }

        /// <summary>The evidence directory; the fixture root and the legacy source folder sit in it.</summary>
        internal string Evidence { get; }
        internal FixtureText Text { get; }
        internal AppPaths Paths { get; }
        internal ILoggerFactory Logs { get; } = NullLoggerFactory.Instance;
        internal AppSettings Settings { get; } = new();
        internal CaptureQueue Queue { get; }
        internal UxReviewSelfTest.FixtureOcr Ocr { get; } = new();
        internal OcrResultPresenter Presenter { get; }
        internal GalleryController Controller { get; }
        internal CaptureCommitService Commit { get; }
        internal VideoLibraryService VideoLibrary { get; }
        internal BitmapSource Sample { get; }
        internal RectD Region { get; }
        internal RecordingResult Recording { get; }
        internal VideoEditDocument Document { get; }

        /// <summary>The library over the fixture queue, with nothing on disk behind its thumbnails.</summary>
        internal (GalleryWindow Window, GalleryViewModel ViewModel) Gallery()
        {
            var viewModel = new GalleryViewModel(Controller, _ => Path.Combine(Paths.DataRoot, "not-opened.png"), 320,
                clock: () => DateTimeOffset.Parse("2026-10-31T00:00:00Z"));
            var indexing = new OcrIndexingService(Controller, Ocr, Queue.GetDirectory, () => Settings.Ocr,
                NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
            var gallery = new GalleryWindow(viewModel, Controller,
                new GalleryReeditLoader(Queue, NullLogger<GalleryReeditLoader>.Instance), Commit, Queue,
                VideoLibrary, Paths, Logs, Presenter, () => Settings.Ocr, indexing,
                new UxReviewSelfTest.FixturePrivacy(), NullLogger.Instance);
            return (gallery, viewModel);
        }

        /// <summary>A pin of a short code snippet, as copied from an editor.</summary>
        internal PinWindow CodePin()
        {
            const string source = "public void Capture()\r\n{\r\n    Save();\r\n}";
            BitmapSource code = ClipboardCodeRenderer.TryRender(source,
                "<pre style=\"color:#d4d4d4;background-color:#1e1e1e\">public void Capture() { Save(); }</pre>")
                ?? ClipboardTextRenderer.Render(source, false);
            return new PinWindow(PinContent.FromText(code, source, false),
                new PinViewState(code.PixelWidth, code.PixelHeight, 1, 1, 0.1), 40, 40, () => Settings.Pin);
        }

        public void Dispose() => Presenter.Dispose();
    }

    /// <summary>
    /// The windows of the Korean word-wrap sweep, each in the content it opens with, as
    /// <c>name</c> layouts. <paramref name="verify"/> also asserts the hooks the surfaces rely on.
    /// </summary>
    internal void DefaultSurfaces(Fixtures fixtures, bool verify)
    {
        FixtureText text = fixtures.Text;
        AppSettings settings = fixtures.Settings;
        AppPaths paths = fixtures.Paths;
        BitmapSource sample = fixtures.Sample;
        Window("gallery", fixtures.Gallery().Window);
        Window("organization", new GalleryOrganizationDialog(new CaptureRecord { Title = text.Title, Tags = text.Tags },
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
        Window("annotation", new AnnotationEditorWindow(new FrozenFrame(sample, fixtures.Region, null, 0), fixtures.Region, sample,
            initialDocument: AnnotationDocument.CreateFor(sample.PixelWidth, sample.PixelHeight)));
        Window("image-export", new ImageReductionExportDialog(sample, Path.Combine(paths.QuickSaveRoot, "review.png")));
        Window("video", new VideoEditorWindow(fixtures.Recording, paths, fixtures.Logs, fixtures.Document));
        Window("pin-code", fixtures.CodePin());
        PinWindow feedbackPin = fixtures.CodePin();
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
            "4.12.0", "fixture", false, "10.0.26100", 1, text.LanguageTag, "Midnight")));
        Window("ocr-result", new OcrResultWindow());
        var translation = new OcrTranslationPanel();
        translation.ShowModelNeeded(320_000_000);
        Surface("ocr-translation", translation, new Size(480, 320), new Size(360, 320));
        Window("settings-guide", new SettingsGuideWindow(SettingsGuideCatalog.Get("G03"), reducedMotion: () => true));
        Window("timed-text", new TimedTextOverlayDialog(12000, 2000,
            new TimedTextOverlay { Text = text.Subtitle, StartMs = 2000, EndMs = 8000 }));
        Window("video-export", new VideoExportDialog(fixtures.Recording, fixtures.Document, fixtures.Logs, gif: false));
        Window("gif-export", new VideoExportDialog(fixtures.Recording, fixtures.Document, fixtures.Logs, gif: true));
        Window("feature-tutorial", new FeatureTutorialWindow(new HotkeySettings(), mediaFactory: () => new OnboardingGuideTests.FakeMedia()));
        using (var tutorial = new WelcomeTutorialPreview(new WelcomeOptions
            { ReducedMotion = true, MediaFactory = () => new OnboardingGuideTests.FakeMedia() }) { AssetDirectory = paths.DataRoot })
        {
            tutorial.SelectFeature("text-copy");
            Surface("tutorial-fallback", tutorial, new Size(600, 350), new Size(360, 350));
        }
        var operations = new LegacyImportOperations(new FixtureLegacyService(), paths, _ => Task.FromResult(true), () => { },
            (_, _, _) => Task.FromResult(new LegacyMigrationResult(LegacyMigrationStatus.Failed, ErrorCode: "IoFailure")),
            () => { }, () => Task.CompletedTask);
        var legacy = new LegacyDataImportWindow(operations, Path.Combine(fixtures.Evidence, text.LegacySource));
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
        var folder = GalleryFolderEditDialog.ForName(text.Folder, true, _ => throw new ArgumentException("Fixture validation failure."));
        folder.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Window("folder-edit", folder);
        Window("folder-delete", new GalleryFolderDeletionDialog(text.Folder, new CaptureFolderDeletionPreview(2, 1, 5, 3)));
        var tags = new GalleryBatchTagsDialog([new CaptureRecord { Tags = text.Tags }], _ => throw new InvalidOperationException("No persistence."));
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
        Field<TextBlock>(player, "_title").Text = text.RecordingFile;
        Window("gallery-video-player", player, Field<TextBlock>(player, "_title"));
        var rating = new EngagementNotice();
        rating.ShowInvitation();
        Surface("engagement-rating", rating, new Size(400, 200), new Size(360, 200));
        var update = new EngagementNotice();
        update.ShowUpdateAvailable("4.12.1");
        Surface("engagement-update", update, new Size(400, 200), new Size(360, 200));
    }

    /// <summary>Lays a window's content out as <paramref name="name"/>, then closes the window.</summary>
    /// <param name="content">Blocks that show user content.</param>
    internal void Window(string name, Window window, params TextBlock[] content)
    {
        try
        {
            FrameworkElement root = Detach(window);
            if (SizesAsLoaded && window is WelcomeWindow welcome)
                // The window's Loaded handler, without the parts that bring it forward and start its timer.
                foreach (string step in new[] { "MeasurePrimarySlot", "ApplyPrimarySlot" })
                    (typeof(WelcomeWindow).GetMethod(step, Members) ?? throw new MissingMethodException(nameof(WelcomeWindow), step))
                        .Invoke(welcome, null);
            (Size size, Size minimum) = WindowSizes(window, root);
            Surface(name, root, size, minimum, content);
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// Takes the content out of a window that is never shown, with what the window lent it: its data,
    /// language, flow direction, resources and text properties.
    /// </summary>
    internal static FrameworkElement Detach(Window window)
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
        return root;
    }

    /// <summary>The window's size for the content it holds now, and the smallest size it allows.</summary>
    internal (Size Size, Size Minimum) WindowSizes(Window window, FrameworkElement root)
    {
        root.Measure(new Size(double.IsFinite(window.Width) ? window.Width : 1000, double.PositiveInfinity));
        var size = new Size(double.IsFinite(window.Width) ? window.Width : Math.Max(root.DesiredSize.Width, 1),
            double.IsFinite(window.Height) ? window.Height : Math.Max(root.DesiredSize.Height, 1));
        if (StopsAtMaximumSize) size = new Size(Math.Min(size.Width, window.MaxWidth), Math.Min(size.Height, window.MaxHeight));
        var minimum = new Size(window.MinWidth > 0 ? window.MinWidth : size.Width,
            window.MinHeight > 0 ? window.MinHeight : size.Height);
        // A non-resizable SizeToContent dialog has no separate smaller supported size.
        if (window.ResizeMode == ResizeMode.NoResize) minimum = size;
        return (size, minimum);
    }

    /// <summary>Closes a window the way the app does on exit; reusable windows otherwise only hide.</summary>
    internal static void Close(Window window)
    {
        if (window is GalleryWindow library) library.CloseForExit();
        else if (window is SettingsWindow preferences) preferences.CloseForExit();
        else if (window is OcrResultWindow ocr) ocr.CloseForExit();
        else window.Close();
    }

    /// <summary>Lays <paramref name="root"/> out at both sizes on the theme's base surface, at 96 DPI.</summary>
    /// <param name="content">Blocks that show user content.</param>
    internal void Surface(string name, FrameworkElement root, Size normal, Size minimum, params TextBlock[] content)
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
            inspect(new Layout(name, mode, size, surface, excluded));
            if (Output is not null)
            {
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width)),
                    Math.Max(1, (int)Math.Ceiling(size.Height)), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using FileStream stream = File.Create(DiagnosticOutputPaths.Child(Output, $"{name}-{mode}.png"));
                encoder.Save(stream);
            }
            Layouts++;
            Console.WriteLine($"{ConsolePrefix} {name}/{mode}: {size.Width:0.##} x {size.Height:0.##}, {LanguageName}, Midnight, 96 DPI");
        }
        surface.Child = null;
    }

    /// <summary>
    /// The Korean word-wrap checks: a wrapping block of Korean UI copy keeps its words whole (UNJOINED)
    /// and never leaves one syllable alone on its last line (ORPHAN). Returns the blocks it checked.
    /// </summary>
    /// <param name="isUserContent">Further user content the caller knows by its text.</param>
    internal static int CheckKoreanWrap(Layout layout, List<string> failures, Func<string, bool>? isUserContent = null)
    {
        int wrappingCopy = 0;
        foreach (TextBlock block in Descendants(layout.Surface).OfType<TextBlock>())
        {
            if (block.TextWrapping == TextWrapping.NoWrap || block.ActualWidth <= 0 || block.ActualHeight <= 0 || IsHidden(block)) continue;
            string display = KoreanWordWrapProbe.TextOf(block);
            string plain = KoreanWordWrap.Strip(display);
            if (!plain.Any(IsHangul)) continue;
            // Library/OCR item rows and caller-named user paths stay on WPF's fast content path.
            if (layout.Excluded.Contains(block) || IsLibraryItem(block) || isUserContent?.Invoke(plain) == true) continue;
            wrappingCopy++;
            string label = $"{layout.Name}/{layout.Mode}; element={ElementName(block)}; text={plain}";
            if (display != KoreanWordWrap.KeepWords(plain)) failures.Add("UNJOINED " + label);
            if (KoreanWordWrapProbe.Inspect(block) is { } orphan)
                failures.Add($"ORPHAN {label}; lines={orphan.LineCount}; last={orphan.LastLine}");
        }
        return wrappingCopy;
    }

    internal static bool IsHangul(char value) => value is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    internal static bool IsHidden(DependencyObject node)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return true;
        return false;
    }

    internal static bool IsLibraryItem(DependencyObject node)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: GalleryItemViewModel }) return true;
        return false;
    }

    internal static string ElementName(FrameworkElement element)
    {
        if (element.Name.Length > 0) return element.Name;
        string name = AutomationProperties.GetName(element);
        return name.Length > 0 ? name : element.GetType().Name;
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(node, index))) yield return child;
    }

    internal static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject node)
    {
        yield return node;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject element)
                foreach (DependencyObject descendant in LogicalDescendants(element)) yield return descendant;
    }

    internal static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Members)!.GetValue(owner)!;

    internal static string RepositoryRoot()
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

    internal sealed class FixtureLegacyService : ILegacyLibraryMigrationService
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
