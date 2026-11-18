using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
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
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>
    /// Set MYCAPTURE_NATIVE_FEEL_EVIDENCE to a NEW absolute output directory on an unlocked desktop.
    /// Run only this test. PNGs come from the composited screen, at its current native scale.
    /// Run once at 100% and once at 150%, using MYCAPTURE_NATIVE_FEEL_SCALE=100 or 150
    /// to enforce that prerequisite. The tool never changes Windows display settings or resizes pixels.
    /// No application startup, user profile, native input, clipboard, recording or network is used.
    /// Synthetic data and noncapturable requests are identified in manifest.json. Fixture files are retained.
    /// </summary>
    [NativeFeelEvidenceFact]
    public Task NativeFeel_CaptureDesktopEvidence() => RunIsolated(nameof(NativeFeel_CaptureDesktopEvidence),
        CaptureNativeFeel, hungAfter: TimeSpan.FromMinutes(10));

    private static void CaptureNativeFeel()
    {
        // This check precedes Application, profile, theme and window creation in the child, too.
        string requested = Environment.GetEnvironmentVariable(NativeFeelEvidenceFactAttribute.OutputVariable)
            ?? throw new InvalidOperationException("The desktop evidence output was not requested.");
        if (!Path.IsPathFullyQualified(requested)) throw new IOException("Choose a new absolute output directory.");
        double? requestedScale = NativeFeelCoverage.RequestedScale(Environment.GetEnvironmentVariable(NativeFeelCoverage.ScaleVariable));
        string output = TestDiagnosticArtifacts.NewDirectory(requested);
        string profile = OwnedTestDirectory.Create("mc-native-feel-");
        var application = CreateLibraryApplication();
        var frames = new List<NativeFeelFrame>();
        var cases = new List<NativeFeelCase>();
        string? failure = null;
        try
        {
            using var language = UiText.UseLanguage("ko-KR");
            UiText.Configure("ko");
            AnnotationEditorPreferences.Read = () => new AnnotationDefaults();
            AnnotationEditorPreferences.Write = _ => { };
            AnnotationEditorPreferences.StampFolder = () => profile;
            ShortcutHelpWindow.ReadHotkeys = () => NewInstallDefaults.Create().Hotkeys;
            // Preserve the host's motion preference. Report it; never force an animation into existence.
            foreach (var theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
            {
                ThemeService.Apply(theme);
                string name = theme == AppTheme.Glass ? "dark" : "light";
                using var desktop = new NativeFeelDesktopCapture(output, name, requestedScale);
                try
                {
                    var context = new NativeFeelSceneContext(desktop, profile, name);
                    foreach (NativeFeelSceneGroup group in NativeFeelCoverage.Groups)
                    {
                        int first = desktop.Cases.Count;
                        group.Capture(context);
                        Assert.Equal(group.Scenes.Select(scene => scene.Id).Order(),
                            desktop.Cases.Skip(first).Select(item => item.Id).Order());
                    }
                    Assert.Equal(NativeFeelCoverage.Requested.Order(), desktop.Cases.Select(item => item.Id).Order());
                }
                finally { frames.AddRange(desktop.Frames); cases.AddRange(desktop.Cases); }
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            try
            {
                // The manifest survives a failed desktop prerequisite, so a partial run cannot look complete.
                foreach (string theme in new[] { "dark", "light" })
                foreach (string id in NativeFeelCoverage.Requested)
                    if (!cases.Any(item => item.Theme == theme && item.Id == id))
                        cases.Add(new(id, NativeFeelCoverage.SurfaceOf(id), id, theme, null, "not-run",
                            failure is null ? "The case did not finish." : "The run stopped; see the manifest failure."));
                TestDiagnosticArtifacts.WriteJson(output, "manifest.json", new
                {
                    schema = 2, capture = "GetDC(desktop) + BitBlt(SRCCOPY|CAPTUREBLT); DwmFlush; no cursor",
                    nativeScale = true, language = "ko-KR", themes = new[] { "Glass", "GlassLight" },
                    requestedScale, foundationMatrix = NativeFeelCoverage.FoundationMatrix(cases),
                    sceneGroups = NativeFeelCoverage.Groups.Select(group => new { group.Name, group.Order, group.Baseline, group.Scenes }),
                    requested = NativeFeelCoverage.Requested, cases, frames, failure,
                    sequences = NativeFeelDesktopCapture.SequenceSummaries(frames).ToArray(),
                    motionEnabled = FluidMotion.AnimationsEnabled,
                    frameTargetMs = 16, sequenceDurationMs = 500,
                    frameTimes = "BeginMs/EndMs are measured relative to the trigger, not nominal frame numbers. A negative sample is the baseline.",
                    fixtureProfile = profile, fixtureRetention = "Retained; no cleanup or real profile is used.",
                });
                if (frames.Count > 0) NativeFeelDesktopCapture.ContactSheets(output, frames);
            }
            finally
            {
                try
                {
                    foreach (Window window in application.Windows.Cast<Window>().ToArray())
                    {
                        if (window is GalleryWindow gallery) gallery.CloseForExit();
                        else if (window is SettingsWindow settings) settings.CloseForExit();
                        else if (window is OcrResultWindow ocr) ocr.CloseForExit();
                        else if (window is QuickAccessWindow card) card.CloseNow();
                        else window.Close();
                    }
                }
                finally { application.Shutdown(); }
            }
        }
    }

    [NativeFeelSceneGroup(10, baseline: true)]
    [NativeFeelScene("core-shortcut-notice", "welcome-window")]
    [NativeFeelScene("practice-first", "welcome-window")]
    [NativeFeelScene("practice-middle", "welcome-window")]
    private static void NativeFeelWelcome(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        foreach ((string id, bool notice, PracticeStep? step) in new[]
        {
            ("core-shortcut-notice", true, (PracticeStep?)null),
            ("practice-first", false, (PracticeStep?)null),
            ("practice-middle", false, (PracticeStep?)PracticeStep.B0),
        })
        {
            var window = OnboardingGuideTests.Create(new(), new WelcomeOptions
            { CoreShortcutsNotice = notice, ReducedMotion = !FluidMotion.AnimationsEnabled, MonitorProvider = () => d.Monitor });
            try
            {
                if (step is { } target) PracticeJourneyDriver.AdvanceTo(window.Session, target);
                d.Place(window);
                window.Show();
                d.Still(id, "first-minute", window, "Real WelcomeWindow; synthetic practice session and fake clipboard/media.");
            }
            finally { window.Close(); }
        }
    }

    [NativeFeelSceneGroup(20, baseline: true)]
    [NativeFeelScene("main-window-opening", "settings-window")]
    [NativeFeelScene("main-window-closing", "settings-window")]
    [NativeFeelScene("settings-category-switch", "settings-window")]
    [NativeFeelScene("settings-general", "settings-window")]
    [NativeFeelScene("settings-densest", "settings-window")]
    [NativeFeelScene("settings-search-results", "settings-window")]
    [NativeFeelScene("update-card", "settings-window")]
    private static void NativeFeelSettings(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        string profile = context.Profile;
        AppSettings saved = NewInstallDefaults.Create();
        saved.General.Theme = AppThemeNames.ToSetting(ThemeService.Current);
        var window = new SettingsWindow(() => saved,
            _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance,
            defaultCapturesRoot: () => profile);
        try
        {
            d.Place(window, 940, 700);
            var tabs = (TabControl)window.FindName("CategoryTabs");
            TabItem[] items = tabs.Items.OfType<TabItem>().ToArray();
            int general = Array.FindIndex(items, item => item.Name == "GeneralTab");
            Assert.InRange(general, 0, items.Length - 1);
            tabs.SelectedIndex = general;
            // SourceInitialized obtains the real frame while the HWND is still hidden.
            _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            RectD area = d.Bounds(window);
            d.Sequence("main-window-opening", "settings-motion", area, window.Show,
                "SettingsWindow.Show and its callbacks after the baseline; the constructor ran before this sequence.");
            d.Still("settings-general", "settings", window, $"{items[general].Name}: {items[general].Header}.");
            int densest = 0, maximum = -1;
            var counts = new List<int>();
            var measurements = new List<string>();
            // The densest page is the one with most visible text/input/action elements in its content tree.
            for (int i = 0; i < items.Length; i++)
            {
                tabs.SelectedIndex = i;
                NativeFeelDesktopCapture.Pump(30);
                // TabItem's visual tree holds its header; the selected content is hosted separately.
                DependencyObject content = Assert.IsAssignableFrom<DependencyObject>(items[i].Content);
                int count = Descendants(content).OfType<FrameworkElement>().Count(element =>
                    element.IsVisible && element is TextBlock or TextBox or Button or CheckBox or ComboBox or Slider);
                Assert.True(count > 0, $"The content tree for {items[i].Name} has no visible controls.");
                counts.Add(count);
                measurements.Add($"{items[i].Name} ({items[i].Header})={count}");
                if (count > maximum) { maximum = count; densest = i; }
            }
            Assert.True(counts.Distinct().Count() > 1, "Category density measurements must distinguish the actual page content.");
            string densityNote = $"Densest by visible content element count: {items[densest].Name} ({items[densest].Header})={maximum}. "
                + string.Join("; ", measurements);
            int source = densest == general ? Enumerable.Range(0, items.Length).First(index => index != densest) : general;
            tabs.SelectedIndex = source;
            NativeFeelDesktopCapture.Pump(250);
            Assert.NotEqual(densest, tabs.SelectedIndex);
            d.Sequence("settings-category-switch", "settings-motion", d.Bounds(window),
                () => tabs.SelectedIndex = densest,
                $"Category transition: {items[source].Name} ({items[source].Header}) to {items[densest].Name} ({items[densest].Header}). " + densityNote);
            d.Still("settings-densest", "settings", window, densityNote);
            var search = (TextBox)tabs.Template.FindName("SettingsSearchBox", tabs);
            search.Text = "캡처";
            NativeFeelDesktopCapture.Pump(250);
            Assert.NotNull(window.FirstSearchMatch);
            d.Still("settings-search-results", "settings", window, "Search query: 캡처 (capture).");
            search.Clear();
            var notice = new EngagementNotice();
            EngagementNoticeHost.Attach(window, notice, 76);
            notice.ShowUpdateAvailable("5.4.0");
            d.Still("update-card", "settings", window, "Existing EngagementNotice with a synthetic available version; no update service.");
            d.Sequence("main-window-closing", "settings-motion", d.Bounds(window), window.CloseForExit,
                "SettingsWindow.CloseForExit; captures the current close path, including an instant close if that is the product behavior.");
        }
        finally { if (window.IsLoaded) window.CloseForExit(); }
    }

    private sealed record NativeFeelLibraryFixture(GalleryWindow Window, GalleryViewModel ViewModel,
        CaptureQueue Queue, IReadOnlyList<CaptureRecord> Records);

    private static NativeFeelLibraryFixture NativeFeelNewLibrary(string parent, string name, int count)
    {
        string root = TestDiagnosticArtifacts.NewDirectory(Path.Combine(parent, name), parent);
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var records = new List<CaptureRecord>();
        for (int i = 0; i < count; i++)
        {
            var record = new CaptureRecord { Width = 160, Height = 100, Title = $"검토 이미지 {i + 1} / Review {i + 1}",
                CreatedAt = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero).AddMinutes(-i), TotalBytes = 4096 };
            queue.Add(record);
            string directory = queue.GetDirectory(record);
            Directory.CreateDirectory(directory);
            byte[] png = EvidenceThumbnailPng(Color.FromRgb((byte)(80 + i * 13 % 150), (byte)(80 + i * 29 % 150), (byte)(80 + i * 7 % 150)));
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Thumbnail), png);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), png);
            records.Add(record);
        }
        queue.Save();
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var ocr = new GalleryCompactOcr();
        var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
            NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
        var vm = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96);
        var window = new GalleryWindow(vm, controller, new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance),
            commit, queue, new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
            NullLoggerFactory.Instance, presenter, () => settings.Ocr, indexing, new GalleryMarkupPrivacy(), NullLogger.Instance)
        { ReadHotkeys = () => settings.Hotkeys };
        window.Closed += (_, _) => presenter.Dispose();
        return new(window, vm, queue, records);
    }

    [NativeFeelSceneGroup(30, baseline: true)]
    [NativeFeelScene("library-grid", "library-window")]
    [NativeFeelScene("library-selected-details", "library-window")]
    [NativeFeelScene("library-empty", "library-window")]
    [NativeFeelScene("library-tile-menu", "library-window")]
    [NativeFeelScene("library-tile-selection", "library-window")]
    [NativeFeelScene("library-wheel-three-notches", "library-window")]
    private static void NativeFeelLibrary(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        string profile = context.Profile, theme = context.Theme;
        NativeFeelLibraryFixture fixture = NativeFeelNewLibrary(profile, "library-" + theme, 30);
        GalleryWindow window = fixture.Window;
        try
        {
            d.Place(window, 1180, 720);
            window.Show();
            PumpLibraryUntil(window, () => window.RealizedCards().Count() >= 3, "native desktop library cards");
            d.Still("library-grid", "library", window);
            d.Sequence("library-tile-selection", "library-motion", d.Bounds(window),
                () => fixture.ViewModel.Select(fixture.Records[0].Id), "Real GalleryViewModel selection; no pointer synthesis.");
            window.SetDetailsPaneOpen(true);
            NativeFeelDesktopCapture.Pump(250);
            Assert.True(((FrameworkElement)window.FindName("GalleryDetailsPanel")).IsVisible);
            d.Still("library-selected-details", "library", window);
            var card = window.RealizedCards().First();
            var menu = window.TileMenu;
            window.PrepareTileMenu(menu, (GalleryItemViewModel)card.DataContext);
            menu.PlacementTarget = card;
            menu.Placement = PlacementMode.Bottom;
            try
            {
                menu.IsOpen = true;
                // Include the real Popup HWND and its shadow in the same desktop crop.
                d.AreaStill("library-tile-menu", "library", d.Monitor.WorkArea, "Product TileMenu prepared for a synthetic tile; placement is anchored to that tile.");
            }
            finally { menu.IsOpen = false; }
            var scroll = Descendants((DependencyObject)window.FindName("RowsList")).OfType<ScrollViewer>().First();
            double before = scroll.VerticalOffset;
            d.Sequence("library-wheel-three-notches", "library-motion", d.Bounds(window), () =>
            {
                for (int i = 0; i < 3; i++) scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = UIElement.MouseWheelEvent, Source = scroll });
            }, "Three routed -120 wheel events to the actual ScrollViewer; OS cursor is untouched.");
            Assert.True(scroll.VerticalOffset > before, "The actual library wheel route did not scroll.");
        }
        finally { window.CloseForExit(); }
        NativeFeelLibraryFixture empty = NativeFeelNewLibrary(profile, "empty-" + theme, 0);
        try { d.Place(empty.Window, 1180, 720); empty.Window.Show(); d.Still("library-empty", "library", empty.Window); }
        finally { empty.Window.CloseForExit(); }
    }

    [NativeFeelSceneGroup(40, baseline: true)]
    [NativeFeelScene("result-card-fresh", "result-card")]
    [NativeFeelScene("result-card-message", "result-card")]
    [NativeFeelScene("long-running-state", "result-card")]
    [NativeFeelScene("card-appearing", "result-card")]
    [NativeFeelScene("card-leaving", "result-card")]
    private static void NativeFeelCards(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        using var keyboard = new ReceiptKeyboardRouter(_ => new NativeFeelHotkeyRegistrar());
        var save = new TaskCompletionSource<bool>();
        var window = new QuickAccessWindow(FeedbackSample(), () => Task.FromResult(true), () => { },
            () => Task.CompletedTask, () => save.Task, () => { }, () => Task.FromResult("synthetic.png"),
            excludeFromCapture: false, targetMonitor: d.Monitor, keyboardRouter: keyboard);
        const string lifetimeNote = "The existing private lifetime timer is paused in Loaded before its first tick to prevent native cursor polling. "
            + "IsKept remains false; countdown and automatic lifetime expiry are not validated. ";
        var lifetimeTimer = Assert.IsType<DispatcherTimer>(typeof(QuickAccessWindow)
            .GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        // Product Loaded runs first, then this handler stops its 100 ms timer without changing any visual state.
        window.Loaded += (_, _) => { lifetimeTimer.Stop(); Assert.False(lifetimeTimer.IsEnabled); };
        try
        {
            d.Place(window);
            _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            d.Sequence("card-appearing", "card-motion", d.Monitor.WorkArea, window.Show,
                lifetimeNote + "Existing excludeFromCapture:false seam; fake actions and registrar; the card's normal appearance.");
            Assert.False(lifetimeTimer.IsEnabled);
            Assert.False(window.IsKept);
            d.Still("result-card-fresh", "card", window, lifetimeNote);
            window.ShortcutActions["Save"]();
            window.ElapseProgressDelayForTest();
            Assert.True(window.StatusForTest.Progress);
            d.Still("long-running-state", "card", window, lifetimeNote + "A held synthetic Save task, through the actual delayed progress path.");
            save.SetResult(true);
            NativeFeelDesktopCapture.Pump(100);
            Assert.Equal(FeedbackKind.Confirmation, window.StatusForTest.Kind);
            d.Still("result-card-message", "card", window, lifetimeNote + "Synthetic Save task completed; actual confirmation state.");
            d.Sequence("card-leaving", "card-motion", d.Bounds(window), window.RequestClose,
                lifetimeNote + "Actual card RequestClose exit.");
        }
        finally { window.CloseNow(); save.TrySetResult(false); }
    }

    [NativeFeelSceneGroup(50, baseline: true)]
    [NativeFeelScene("pin-rest", "image-pin")]
    [NativeFeelScene("pin-hover-bar", "image-pin")]
    [NativeFeelScene("pin-menu", "image-pin")]
    [NativeFeelScene("pin-landing", "image-pin")]
    [NativeFeelScene("bottom-message-appearing", "image-pin")]
    [NativeFeelScene("bottom-message-leaving", "image-pin")]
    [NativeFeelScene("text-pin", "text-pin")]
    private static void NativeFeelPins(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        BitmapSource image = FeedbackSample();
        var window = new PinWindow(PinContent.FromImage(image), new PinViewState(image.PixelWidth, image.PixelHeight, 1, 1, .1),
            0, 0, () => new PinSettings(), new SyntheticPinDragInput());
        Func<Window, bool> hoverExclusion = PinHoverBar.ApplyCaptureExclusion;
        Func<Window, bool> landingExclusion = PinLandingDecoration.ApplyCaptureExclusion;
        // Existing injection seams permit the test-owned auxiliary HWNDs to be photographed.
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        PinLandingDecoration.ApplyCaptureExclusion = _ => true;
        try
        {
            d.Place(window);
            window.ReadWorkArea = _ => d.Monitor.WorkArea;
            _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            d.Sequence("pin-landing", "pin-motion", d.Bounds(window, 64), () => { window.Show(); window.StartLandingEffect(); },
                "Real StartLandingEffect and current motion/remote support; fake drag input. Existing PinLandingDecoration.ApplyCaptureExclusion seam returns true without setting display affinity.");
            d.Still("pin-rest", "pin", window);
            window.ShowHoverBar();
            d.AreaStill("pin-hover-bar", "pin", d.Monitor.WorkArea,
                "Existing ShowHoverBar entry point. Existing PinHoverBar.ApplyCaptureExclusion seam returns true without setting display affinity; OS pointer untouched.");
            window.HideHoverBar();
            var menu = window.ContextMenu!;
            menu.PlacementTarget = window;
            menu.Placement = PlacementMode.Center;
            try { menu.IsOpen = true; d.AreaStill("pin-menu", "pin", d.Monitor.WorkArea, "Real pin ContextMenu; no menu command is invoked."); }
            finally { menu.IsOpen = false; }
            d.Sequence("bottom-message-appearing", "pin-motion", d.Bounds(window), () => window.ReportCopyResult(false),
                "Real ReportCopyResult with a synthetic failure; no clipboard operation.");
            d.Sequence("bottom-message-leaving", "pin-motion", d.Bounds(window), () =>
                Assert.True((bool)typeof(PinWindow).GetMethod("DismissFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!),
                "The product dismiss callback; no keyboard or pointer input.");
        }
        finally
        {
            window.Close();
            PinHoverBar.ApplyCaptureExclusion = hoverExclusion;
            PinLandingDecoration.ApplyCaptureExclusion = landingExclusion;
        }
        // The code renderer needs clipboard HTML and returns null for prose; a text pin of prose
        // is drawn by the plain text renderer, as PinManager does for clipboard text.
        BitmapSource textImage = ClipboardTextRenderer.Render("검토 메모 / Review notes\n캡처 결과를 확인합니다. / Check the capture.", false);
        var text = new PinWindow(PinContent.FromText(textImage, "검토 메모 / Review notes", false),
            new PinViewState(textImage.PixelWidth, textImage.PixelHeight, 1, 1, .1), 0, 0,
            () => new PinSettings(), new SyntheticPinDragInput());
        try { d.Place(text); text.Show(); d.Still("text-pin", "pin", text, "Synthetic text; no clipboard read."); }
        finally { text.Close(); }
    }

    [NativeFeelSceneGroup(60, baseline: true)]
    [NativeFeelScene("editor-empty-selection", "image-editor")]
    [NativeFeelScene("editor-property-bar", "image-editor")]
    private static void NativeFeelEditor(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        BitmapSource image = FeedbackSample();
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        var editor = new AnnotationEditorWindow(new FrozenFrame(image, region, d.Monitor, 0), region, image, pointerMonitor: d.Monitor);
        try
        {
            editor.ConfirmClose = () => EditorCloseChoice.DontSave;
            d.Place(editor, 980, 620);
            editor.Show();
            editor.Editor.ShortcutActions["Select"]();
            d.Still("editor-empty-selection", "editor", editor);
            editor.Editor.ShortcutActions["Rectangle"]();
            d.Still("editor-property-bar", "editor", editor, "Real Rectangle tool selected through its shortcut action.");
        }
        finally { editor.Editor.CancelPendingMosaics(closing: true); editor.Close(); }
    }

    [NativeFeelSceneGroup(70, baseline: true)]
    [NativeFeelScene("ocr-result", "ocr-result")]
    [NativeFeelScene("shortcut-help", "shortcut-help")]
    [NativeFeelScene("confirmation-dialog", "confirmation-dialog")]
    [NativeFeelScene("error-message", "error-message")]
    private static void NativeFeelOcrAndDialogs(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        var ocr = new OcrResultWindow { SuppressPresentation = true };
        try
        {
            (BitmapSource image, MyCapture.Ocr.OcrResult result) = TranslationFixture.RegisterTableWithImage();
            ocr.ShowResult(result, "검토 이미지 / Review image", image);
            d.Place(ocr, 1040, 720); ocr.Show();
            d.Still("ocr-result", "dialogs", ocr, "Synthetic OCR result; no OCR engine, translation or network call.");
        }
        finally { ocr.CloseForExit(); }
        var help = new ShortcutHelpWindow();
        try { d.Place(help); help.Show(); d.Still("shortcut-help", "dialogs", help); }
        finally { help.Close(); }
        foreach ((string id, string text, string title, MessageBoxButton buttons) in new[]
        {
            ("confirmation-dialog", "변경 사항을 저장하시겠습니까? / Save changes?", "저장 확인 / Confirm save", MessageBoxButton.YesNoCancel),
            ("error-message", "파일을 저장하지 못했습니다. / The file could not be saved.", "저장 오류 / Save error", MessageBoxButton.OK),
        })
        {
            var dialog = new ThemedDialog(text, title, buttons, MessageBoxResult.Cancel);
            try { d.Place(dialog); dialog.Show(); d.Still(id, "dialogs", dialog, "Real ThemedDialog with synthetic bilingual content."); }
            finally { dialog.Close(); }
        }
    }

    [NativeFeelSceneGroup(80, baseline: true)]
    [NativeFeelScene("tray-menu", "tray-menu")]
    [NativeFeelScene("menu-opening", "tray-menu")]
    private static void NativeFeelTray(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        using var presenter = new ThemedShellPresenter(() => false);
        Action noOp = () => { };
        var commands = new TrayMenuCommands(noOp, noOp, noOp, noOp, noOp, noOp, noOp, noOp,
            noOp, noOp, noOp, noOp, noOp, PinClipboard: noOp, HideOrShowAllPins: noOp,
            ClearClickThrough: noOp, ContactSupport: noOp, InstallUpdate: noOp, FirstUseGuide: noOp);
        var anchor = new Window { Width = 1, Height = 1, WindowStyle = WindowStyle.None, Background = Brushes.Gray, ShowInTaskbar = false };
        try
        {
            d.Place(anchor); anchor.Show();
            ContextMenu menu = presenter.CreateMenu(TrayMenuLayout.Build(commands, NewInstallDefaults.Create().Hotkeys, new TrayMenuState()));
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Center;
            try
            {
                // Full work area keeps menu shadows and a height-limited menu visible at any native scale.
                d.Sequence("menu-opening", "menu-motion", d.Monitor.WorkArea, () => menu.IsOpen = true,
                    "The actual themed tray ContextMenu, with inert commands and anchored placement; no tray click.");
                d.AreaStill("tray-menu", "menu", d.Monitor.WorkArea, "Real ContextMenu HWND and shadow over the backdrop.");
            }
            finally { menu.IsOpen = false; }
        }
        finally { anchor.Close(); }
    }

    [NativeFeelSceneGroup(90, baseline: true)]
    [NativeFeelScene("video-export-dialog", "video-export-dialog")]
    private static void NativeFeelExport(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        string profile = context.Profile;
        string source = TestDiagnosticArtifacts.Confined(Path.Combine(profile, "synthetic-not-encoded.mp4"));
        var recording = new RecordingResult(source, 6000, 30, 180, 1280, 720);
        var dialog = new VideoExportDialog(recording, VideoEditDocument.CreateFor(1280, 720, 6000), NullLoggerFactory.Instance, gif: false)
        { MonitorForTest = () => d.Monitor, SourcePreviewRendererForTest = _ => Task.FromResult(FeedbackSample()) };
        try { d.Place(dialog); dialog.Show(); d.Still("video-export-dialog", "video", dialog, "Real export dialog with synthetic preview callback; no recording or export is started."); }
        finally { dialog.Close(); NativeFeelDesktopCapture.Pump(100); }
    }

    [NativeFeelSceneGroup(0, baseline: true)]
    [NativeFeelScene("selection-mode-bar", "selection-screen")]
    [NativeFeelScene("selection-f1-panel", "selection-screen")]
    [NativeFeelScene("result-card-hovered", "result-card")]
    [NativeFeelScene("video-editor", "video-editor")]
    [NativeFeelScene("recording-ready", "recording-control")]
    [NativeFeelScene("recording-recording", "recording-control")]
    [NativeFeelScene("primary-button-hover", "primary-button")]
    [NativeFeelScene("primary-button-press", "primary-button")]
    [NativeFeelScene("secondary-button-hover", "secondary-button")]
    [NativeFeelScene("secondary-button-press", "secondary-button")]
    [NativeFeelScene("library-tile-hover", "library-window")]
    private static void NativeFeelOmissions(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture d = context.Desktop;
        d.Omit("selection-mode-bar", "NativeCaptureOverlay.Prepare accepts an exclusion callback, but NativeSelectorChrome.Bind unconditionally excludes every chrome HWND (NativeSelectorChrome.cs:202). The offscreen NativeSelectorVisualEvidenceTests do not solve real desktop capture; no chrome opt-out exists.");
        d.Omit("selection-f1-panel", "NativeSelectorChrome.Guides.cs:52 unconditionally excludes the F1 panel HWND. Synthetic cursor/frame hooks exist, but there is no existing capture-exclusion hook for this real panel.");
        d.Omit("result-card-hovered", "WPF IsMouseOver requires the real pointer. No existing scoped pointer-state hook; moving the OS cursor is prohibited.");
        d.Omit("video-editor", "Portable before/after omission: baseline bd1f8563 VideoEditorWindow reads MonitorEnumerator.GetFromCursor in its constructor and has no injectable monitor. Newer versions have a pointerMonitor seam, but this shared tool must also run safely on that baseline. A later phase can register a separate scene for versions with isolated placement.");
        d.Omit("recording-ready", "RecordingControlWindow unconditionally applies display affinity excluding itself from desktop capture; no existing opt-out.");
        d.Omit("recording-recording", "The recording preview hook exists, but its HWND remains capture-excluded. Capture exclusion was not disabled.");
        foreach (string id in new[] { "primary-button-hover", "primary-button-press", "secondary-button-hover", "secondary-button-press", "library-tile-hover" })
            d.Omit(id, "No existing scoped WPF pointer-state hook. A raised MouseEnter/MouseDown event would not prove IsMouseOver/IsPressed; OS input is prohibited.");
    }

    private sealed class NativeFeelHotkeyRegistrar : MyCapture.Platform.Shell.IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        { errorCode = 0; errorMessage = string.Empty; return true; }
        public void Unregister(int id) { }
    }
}

internal sealed class NativeFeelEvidenceFactAttribute : FactAttribute
{
    internal const string OutputVariable = "MYCAPTURE_NATIVE_FEEL_EVIDENCE";
    public NativeFeelEvidenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OutputVariable)))
            Skip = "Set MYCAPTURE_NATIVE_FEEL_EVIDENCE to a new absolute output directory on an unlocked desktop.";
    }
}

