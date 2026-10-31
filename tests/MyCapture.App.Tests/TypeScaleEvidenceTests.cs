using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
#if DEBUG
using MyCapture.App.Preview;
#endif
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Sources;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
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

/// <summary>Opt-in, deterministic Korean typography evidence; no window is shown or native input used.</summary>
public sealed class TypeScaleEvidenceTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public Task RenderChangedSurfaces()
    {
        string? phase = Environment.GetEnvironmentVariable("MYCAPTURE_TYPE_SCALE_EVIDENCE");
        if (phase is not ("before" or "after")) return Task.CompletedTask;
        return SettingsThemePreviewTests.RunIsolated(nameof(RenderChangedSurfaces), () => RenderAll(phase), typeof(TypeScaleEvidenceTests));
    }

#if DEBUG
    [Fact]
    public Task RenderDebugPinPreview()
    {
        string? phase = Environment.GetEnvironmentVariable("MYCAPTURE_TYPE_SCALE_EVIDENCE");
        if (phase is not ("before" or "after")) return Task.CompletedTask;
        return SettingsThemePreviewTests.RunIsolated(nameof(RenderDebugPinPreview), () =>
        {
            string output = DiagnosticOutputPaths.Create(Path.Combine(RepositoryRoot(), "artifacts", "type-scale", phase));
            using var language = UiText.UseLanguage("ko-KR");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            ThemeService.Apply(AppTheme.Midnight);
            try
            {
                using var input = new PreviewPinInput(new MemoryRegistrar(), () => throw new InvalidOperationException("No clipboard access."));
                input.RegisterShortcut();
                RenderWindow(output, "pin-preview.png", new PinPreviewWindow(input), 470, 380);
            }
            finally { app.Shutdown(); }
        }, typeof(TypeScaleEvidenceTests));
    }
#endif

    private static void RenderAll(string phase)
    {
        string repository = RepositoryRoot();
        // The environment chooses one of two fixed directories, never an arbitrary output path.
        string output = DiagnosticOutputPaths.Create(Path.Combine(repository, "artifacts", "type-scale", phase));
        using var language = UiText.UseLanguage("ko-KR");
        UiText.Configure("ko-KR");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        ThemeService.Apply(AppTheme.Midnight);
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            RenderWelcome(output);
            RenderVideo(output);
            RenderRecording(output);
            var support = new SupportWindow(new NoSupportActions(), _ => new SupportDiagnosticSnapshot(
                "4.12.0", "evidence", false, "10.0.26100", 1, "ko-KR", "Midnight"));
            support.MessageBox.Text = "도움이 필요한 내용을 입력합니다.\n글자 크기와 줄바꿈을 확인합니다.";
            RenderWindow(output, "support.png", support, 680, 680);
            var ocr = new OcrResultWindow();
            RenderWindow(output, "ocr-result.png", ocr, 1000, 700);
            var translation = new OcrTranslationPanel();
            translation.ShowModelNeeded(320_000_000);
            Save(output, "ocr-translation.png", translation, 480, 300);
            RenderWindow(output, "settings-guide.png", new SettingsGuideWindow(SettingsGuideCatalog.Get("G03"), reducedMotion: () => true), 1040, 650);
            var countdown = new CountdownWindow(3);
            countdown.PoseAt(3, TimeSpan.FromMilliseconds(500));
            RenderWindow(output, "countdown.png", countdown, 260, 240);
            var palette = CaptureGuidePalette.Resolve();
            var guide = new CaptureStartGuideCard();
            guide.Configure(detectWindows: true);
            guide.ApplyPalette(palette);
            Save(output, "capture-start-guide.png", guide, 430, 220);
            var keys = new CaptureKeyPanel(ThemeFonts.Ui, (FontFamily)app.FindResource("Font.Mono"));
            keys.Configure(new CaptureKeyPanelContext(true, true, true, true, true, true, true, false, true, new Hotkey(HotkeyModifiers.None, 0x72)));
            keys.ApplyPalette(palette);
            Save(output, "capture-key-panel.png", keys, 430, 420);
            var source = SourceChip.CreateButton(CaptureProvenance.Create(documentPath: @"C:\Evidence\사양서.xlsx", appName: "Excel"), actions: NoDesktop());
            Save(output, "source-chip.png", source, 330, 70);
            var heading = (TextBlock)typeof(ThemedShellPresenter).GetMethod("Heading", Members)!.Invoke(null, ["캡처가 저장되었습니다"])!;
            Save(output, "themed-shell-heading.png", heading, 360, 70);
            using (var timeline = new TwoLineTimeline())
            {
                timeline.Initialize(12000, 30);
                timeline.SetIn(2000); timeline.SetOut(10000); timeline.SetDetailVisible(true);
                timeline.SetCuts(VideoCutTimeline.Create(2000, 10000, [new VideoTimeRange(5000, 6500)], [3500]));
                timeline.SetPlayhead(8000);
                timeline.SetSnapGuide(new VideoSnapCandidate(8000, VideoSnapKind.Playhead));
                Save(output, "two-line-timeline.png", timeline, 920, 240, timeline.FlushRenderForTest);
            }
            var layers = new VideoLayerTimeline();
            layers.Initialize(12000);
            layers.SetLayers([new TimedTextOverlay { Text = "편집할 텍스트", StartMs = 1000, EndMs = 8000 }], []);
            Save(output, "video-layer-timeline.png", layers, 920, 110);
            RenderGalleryEditor(output);
            var text = new TimedTextOverlayDialog(12000, 2000, new TimedTextOverlay { Text = "중요한 순간을 설명하는 자막입니다.", StartMs = 2000, EndMs = 8000 });
            RenderWindow(output, "timed-text-dialog.png", text, 520, 500);
            RenderLibrarySwitch(output, repository);
            RenderWindow(output, "gallery-organization.png", new GalleryOrganizationDialog(new CaptureRecord { Title = "화면 검토", Tags = "업무, 참고" }, (_, _) => Task.FromResult(true)), 500, 500);
            RenderCaptureOverlay(output);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            app.Shutdown();
        }
    }

    private static void RenderWelcome(string output)
    {
        var monitor = new MonitorInfo("evidence", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1040), 96, true);
        PracticeStep?[] steps = [null, .. PracticeSession.Steps.Select(step => (PracticeStep?)step), PracticeStep.Results];
        foreach (PracticeStep? step in steps)
        {
            var window = OnboardingGuideTests.Create(new OnboardingGuideTests.FakeClipboard(), new WelcomeOptions
            { ReducedMotion = true, TextScale = 1, MonitorProvider = () => monitor, ShowCoreShortcutsConsent = step is null });
            if (step is { } lesson) PracticeJourneyDriver.AdvanceTo(window.Session, lesson);
            RenderWindow(output, $"welcome-{step?.ToString().ToLowerInvariant() ?? "consent"}.png", window, 640, 720);
        }
        var illustrationHost = OnboardingGuideTests.Create(new OnboardingGuideTests.FakeClipboard(), new WelcomeOptions
        { ReducedMotion = true, TextScale = 1, MonitorProvider = () => monitor });
        try
        {
            var tray = (FrameworkElement)typeof(WelcomeWindow).GetMethod("MakeTrayDiagram", Members)!.Invoke(illustrationHost, null)!;
            Save(output, "welcome-taskbar.png", tray, 584, 270);
        }
        finally { illustrationHost.Close(); }
    }

    private static void RenderVideo(string output)
    {
        var result = new RecordingResult(DiagnosticOutputPaths.Child(output, "not-opened.mp4"), 12000, 30, 360, 640, 360);
        var window = new VideoEditorWindow(result, AppPaths.CreateForRoot(output), NullLoggerFactory.Instance);
        FrameworkElement root = Detach(window);
        try
        {
            Save(output, "video-editor-loading.png", root, 1100, 780);
            Field<Border>(window, "_loadingOverlay").Visibility = Visibility.Collapsed;
            Field<TextBlock>(window, "_statusLabel").Text = "녹화 영상 편집";
            Field<TextBlock>(window, "_keptLengthLabel").Text = "남길 길이 00:08.000";
            Field<TextBlock>(window, "_removedLengthLabel").Text = "제외할 길이 00:04.000";
            Field<TextBlock>(window, "_trimPreviewBadgeText").Text = "시작 00:02.000";
            Field<Border>(window, "_trimPreviewBadge").Visibility = Visibility.Visible;
            Field<TwoLineTimeline>(window, "_timeline").Initialize(12000, 30);
            window.SetDetailsExpandedForTest(true);
            Save(output, "video-editor-details.png", root, 1100, 780, Field<TwoLineTimeline>(window, "_timeline").FlushRenderForTest);
        }
        finally { window.Close(); }
    }

    private static void RenderRecording(string output)
    {
        var window = new RecordingControlWindow(new RectD(0, 0, 640, 360), new RecordingSettings(),
            () => throw new InvalidOperationException("Evidence never records."),
            () => throw new InvalidOperationException("Evidence never creates a recording."), NullLogger.Instance);
        window.ReadPauseHotkey = () => Hotkey.None;
        window.PreviewRecordingForTest(false, TimeSpan.FromSeconds(83));
        FrameworkElement strip = Field<FrameworkElement>(window, "_controlStrip");
        ((Panel)VisualTreeHelper.GetParent(strip)).Children.Remove(strip);
        try { Save(output, "recording-control.png", strip, 640, 80); }
        finally { window.Close(); }
    }

    private static void RenderGalleryEditor(string output)
    {
        byte[] pixels = Enumerable.Repeat((byte)190, 640 * 360 * 4).ToArray();
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
        bitmap.Freeze();
        var record = new CaptureRecord { Width = 640, Height = 360, CreatedAt = DateTimeOffset.Parse("2026-10-31T00:00:00Z"), IsPinned = true };
        var region = new RectD(0, 0, 640, 360);
        var context = new GalleryReeditContext(record, new FrozenFrame(bitmap, region, null, 0), region, bitmap,
            AnnotationDocument.CreateFor(640, 360), new Dictionary<string, BitmapSource>());
        var monitor = new MonitorInfo("evidence", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1040), 96, true);
        var window = new GalleryEditorWindow(context, record: record, retentionHours: () => 168, pointerMonitor: monitor);
        var root = (DockPanel)window.Content;
        Border banner = (Border)root.Children[0];
        root.Children.Remove(banner);
        Save(output, "gallery-editor-banner.png", banner, 640, 70);
        var controller = Field<AnnotationEditorController>(window.Editor, "_controller");
        typeof(AnnotationEditorControl).GetMethod("SelectTool", Members)!.Invoke(window.Editor, [EditorTool.StepNumber]);
        Assert.True(controller.TrySetNextStepNumber(999));
        typeof(AnnotationEditorControl).GetMethod("UpdateStepPreview", Members)!.Invoke(window.Editor, null);
        Border preview = Field<Border>(window.Editor, "_stepPreview");
        ((Panel)VisualTreeHelper.GetParent(preview)).Children.Remove(preview);
        Save(output, "annotation-step-preview.png", preview, 100, 80);
        window.Close();
    }

    private static void RenderLibrarySwitch(string output, string repository)
    {
        XDocument xaml = XDocument.Load(Path.Combine(repository, "src", "MyCapture.App", "Gallery", "GalleryWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement source = xaml.Descendants(presentation + "Style").Single(element => (string?)element.Attribute(x + "Key") == "LibraryUx.SaveHereSwitch");
        XElement edge = xaml.Descendants(presentation + "Style").Single(element => (string?)element.Attribute(x + "Key") == "LibraryUx.RestEdge");
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "themes", "clr-namespace:MyCapture.App.Themes;assembly=MyCapture"), new XElement(edge), new XElement(source));
        var resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        resources.MergedDictionaries.Add(new GalleryWorkspaceResources());
        var check = new CheckBox { Content = "새 캡처를 이 폴더에 저장", IsChecked = true, Style = (Style)resources["LibraryUx.SaveHereSwitch"] };
        check.Resources = resources;
        Save(output, "gallery-save-here-switch.png", check, 360, 70);
    }

    private static void RenderCaptureOverlay(string output)
    {
        byte[] pixels = new byte[1000 * 700 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        { pixels[index] = 100; pixels[index + 1] = 80; pixels[index + 2] = 60; pixels[index + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(1000, 700, 96, 96, PixelFormats.Bgra32, null, pixels, 4000);
        bitmap.Freeze();
        var view = new CaptureOverlayView(new FrozenFrame(bitmap, new RectD(0, 0, 1000, 700), null, 0));
        view.ReadCursor = () => new PointD(620, 390);
        view.SimulatedPointerInside = true;
        typeof(CaptureOverlayView).GetField("_pointerInitialized", Members)!.SetValue(view, true);
        typeof(CaptureOverlayView).GetField("_cursorPixel", Members)!.SetValue(view, new PointD(620, 390));
        typeof(CaptureOverlayView).GetField("_dragAnchor", Members)!.SetValue(view, new PointD(260, 170));
        typeof(CaptureOverlayView).GetField("_selection", Members)!.SetValue(view, new RectD(260, 170, 360, 220));
        FieldInfo interaction = typeof(CaptureOverlayView).GetField("_interaction", Members)!;
        interaction.SetValue(view, Enum.Parse(interaction.FieldType, "Create"));
        try { Save(output, "capture-overlay.png", view, 1000, 700); }
        finally { view.ReleaseResources(); }
    }

    private static FrameworkElement Detach(Window window)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        TextElement.SetFontFamily(content, window.FontFamily);
        TextElement.SetForeground(content, window.Foreground);
        TextElement.SetFontSize(content, window.FontSize);
        return content;
    }

    private static void RenderWindow(string output, string name, Window window, double width, double height)
    {
        try { Save(output, name, Detach(window), width, height); }
        finally { if (window is OcrResultWindow ocr) ocr.CloseForExit(); else window.Close(); }
    }

    private static void Save(string output, string name, FrameworkElement element, double width, double height, Action? beforeRender = null)
    {
        var surface = new Border { Child = element, Background = (Brush)Application.Current.FindResource("Surface.Base") };
        TextElement.SetFontFamily(surface, ThemeFonts.Ui);
        TextElement.SetForeground(surface, (Brush)Application.Current.FindResource("Text.Primary"));
        ThemeFonts.ApplyText(surface);
        VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        beforeRender?.Invoke();
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(DiagnosticOutputPaths.Child(output, name))) encoder.Save(stream);
        surface.Child = null;
        Console.WriteLine($"TYPE SCALE {name}: {width} x {height}, 96 DPI");
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Members)!.GetValue(owner)!;

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static CaptureSourceActions NoDesktop() => new(_ => throw new InvalidOperationException("No external action."),
        _ => throw new InvalidOperationException("No external action."), _ => throw new InvalidOperationException("No external action."),
        _ => throw new InvalidOperationException("No external action."), @"C:\Windows");

    private sealed class NoSupportActions : ISupportActions
    {
        public void CopyText(string text) => throw new InvalidOperationException("No clipboard access.");
        public bool TryOpenUri(string uri) => throw new InvalidOperationException("No external action.");
    }

    private sealed class MemoryRegistrar : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        { errorCode = 0; errorMessage = string.Empty; return true; }
        public void Unregister(int id) { }
    }
}
