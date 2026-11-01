using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Opt-in asset production from real controls and synthetic documents. No desktop/clipboard/profile access.</summary>
public sealed class SettingsGuideAssetTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static bool Korean => UiText.Culture.TwoLetterISOLanguageName == "ko";
    private static string L(string ko, string en) => Korean ? ko : en;
    private static string Root => Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_GUIDES_SOURCE")!;

    [Fact]
    public Task GenerateProductAssets() => SettingsThemePreviewTests.RunIsolated(nameof(GenerateProductAssets), () =>
    {
        if (string.IsNullOrWhiteSpace(Root)) return;
        Assert.True(Path.IsPathFullyQualified(Root));
        string output = Path.Combine(Root, "src", "MyCapture.App", "Assets", "SettingsGuides");
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = Resources() };
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            foreach (string language in new[] { "ko", "en" })
            {
                using var culture = UiText.UseLanguage(language);
                foreach (var guide in SettingsGuideCatalog.All)
                foreach (var comparison in guide.Comparisons)
                {
                    string? selectedGuide = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_GUIDES_ONLY");
                    if (!string.IsNullOrWhiteSpace(selectedGuide) && guide.Id != selectedGuide) continue;
                    // G09 is produced by the separate real-engine test, never from a synthetic OCR result.
                    if (guide.Id == "G09") continue;
                    Write(Render(comparison.Id, false), Path.Combine(output, comparison.AssetFile(language, "before")));
                    Write(Render(comparison.Id, true), Path.Combine(output, comparison.AssetFile(language, "after")));
                    for (int frame = 0; frame < comparison.MotionFrames; frame++)
                        Write(Render(comparison.Id, true, frame), Path.Combine(output, comparison.AssetFile(language, $"motion{frame}")));
                }
            }
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; application.Shutdown(); }
    }, typeof(SettingsGuideAssetTests));

    private static BitmapSource Render(string id, bool after, int frame = -1) => id switch
    {
        "G01.card" => after ? QuickCard() : Sample(),
        "G01.editor" => after ? Editor() : Sample(),
        "G01.clipboard" => after ? Diagram(L("캡처 → 클립보드 → 붙여넣기", "Capture → Clipboard → Paste"), Sample()) : Sample(),
        "G02.magnifier" or "G02.grid" or "G02.windows" or "G03.keys" or "G04.size" => Selector(id, after, frame),
        "G05.highlight" => CursorScene(true, after, frame == 1),
        "G05.hidden" => CursorScene(!after, true, false),
        "G06.method" => RecordingMethod(after),
        "G07.mosaic" or "G07.highlighter" or "G07.blur" => Effect(id, after),
        "G08.restart" => Diagram(after ? L("앱 종료 → 다시 실행 → 열린 핀 복원", "Exit → Relaunch → Restore open pins") : L("앱 종료 → 다시 실행", "Exit → Relaunch"), after ? Pin() : null),
        "G08.reopen" => Diagram(after ? L("핀 닫기 → 닫은 핀 다시 열기", "Close pin → Reopen closed pin") : L("열린 핀 → 닫기", "Open pin → Close"), after ? Pin() : null),
        "G08.none" => Diagram(after ? L("핀 닫기 → 복원 기록 없음", "Close pin → No restore history") : L("핀 닫기 → 최근 10개 기록", "Close pin → Keep latest 10"), after ? null : Pin()),
        "G10.alt" or "G10.always" => ShortcutLabels(id, after),
        "G11.time" => Timeline(after ? L("7일 경과 → 보관 기간 도달", "7 days later → Retention period reached") : L("시간 경과 → 기간 제한 없음", "Time passes → No time limit"), after ? ["1", "2", "3", "4", "5", "6", "7"] : ["1", "7", "30", "90"], after),
        "G11.items" => Timeline(after ? L("10개 + 새 캡처 → 오래된 항목 정리 대상", "10 items + new capture → Oldest eligible item") : L("항목 수 9개 / 한도 10개", "9 items / limit 10"), after ? ["1", "…", "9", "10", "+11"] : ["1", "…", "9"], after),
        "G11.bytes" => Timeline(after ? L("0.8 + 0.4 GB → 한도 1 GB 초과", "0.8 + 0.4 GB → Above 1 GB limit") : L("보관 용량 0.8 GB / 한도 1 GB", "0.8 GB stored / limit 1 GB"), after ? ["0.4 GB", "0.4 GB", "+0.4 GB"] : ["0.4 GB", "0.4 GB"], after),
        "G12.folder" => Diagram(after ? L("새 캡처 → 라이브러리 / 업무", "New capture → Library / Work") : L("새 캡처 → 라이브러리 / 기본 폴더", "New capture → Library / Default folder"), Sample()),
        "G12.storage" => Diagram(after ? L("다시 실행 → 새 캡처 → 지정 보관 폴더", "Relaunch → New capture → Custom storage") : L("새 캡처 → 기본 보관 폴더", "New capture → Default storage"), Sample()),
        "G12.export" => Diagram(after ? L("빠른 저장 → 그림 / 예시 / capture.png", "Quick save → Pictures / Example / capture.png") : L("캡처 → 라이브러리 / 업무", "Capture → Library / Work"), Sample()),
        "G13.title" => AnnotationFlattener.Flatten(Sample(), AnnotationDocument.CreateFor(720, 400), new AnnotationRenderer(new AnnotationImageStore()), after ? L("업무 메모", "Work notes") : null),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    internal static ResourceDictionary Resources()
    {
        var resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Daylight, resources, systemHighContrast: false);
        return resources;
    }

    internal static BitmapSource Sample()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 720, 400));
            Text(dc, L("업무 메모", "Work notes"), 32, 38, 30);
            Text(dc, L("검토 일정과 준비 사항", "Review schedule and preparation"), 32, 110, 23);
            Text(dc, L("10:30  자료 확인", "10:30  Review materials"), 32, 170, 25);
            Text(dc, L("14:00  결과 공유", "14:00  Share results"), 32, 228, 25);
            dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(32, 300, 190, 38));
            dc.DrawRectangle(Brushes.Goldenrod, null, new Rect(242, 300, 150, 38));
        }
        return Bitmap(visual, 720, 400);
    }

    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush? brush = null) =>
        dc.DrawText(new FormattedText(text, UiText.Culture, FlowDirection.LeftToRight,
            new Typeface("Malgun Gothic"), size, brush ?? Brushes.Black, 1), new Point(x, y));

    private static BitmapSource QuickCard()
    {
        var window = new QuickAccessWindow(Sample(), () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult(string.Empty), excludeFromCapture: false);
        var content = (FrameworkElement)window.Content; window.Content = null;
        try { return Snapshot(content, 430, 340); }
        finally { window.Close(); }
    }

    private static BitmapSource Editor(bool? alt = null)
    {
        BitmapSource sample = Sample(); var region = new RectD(0, 0, 720, 400);
        var editor = new AnnotationEditorControl(new FrozenFrame(sample, region, null, 0), region, sample, null, null);
        var host = Host(editor, 1000, 640);
        try
        {
            host.Show();
            if (alt.HasValue) ShortcutPresentation.SetAltRevealed(host, alt.Value);
            ShortcutPresentation.Refresh(); Drain(host);
            BitmapSource full = Snapshot(editor, 1000, 640);
            return alt.HasValue ? new CroppedBitmap(full, new Int32Rect(660, 0, 340, 62)) : full;
        }
        finally { host.Close(); }
    }

    private static BitmapSource Selector(string id, bool after, int frame)
    {
        bool fixedSize = id == "G04.size";
        int width = fixedSize ? 1600 : 960, height = fixedSize ? 900 : 620;
        var desktop = Diagram(L("업무 메모", "Work notes"), Sample(), width, height);
        var bounds = new RectD(0, 0, width, height);
        var monitor = new MonitorInfo("Guide", bounds, bounds, 96, true);
        // Show the magnifier over the document's blue edge so the enlarged pixels are visible.
        var point = id == "G02.magnifier" ? new PointD(150, 464) : new PointD(width * .6, height * .42);
        var view = new CaptureOverlayView(new FrozenFrame(desktop, bounds, monitor, 0),
            showMagnifier: id == "G02.magnifier" && after, showSelectionGrid: id == "G02.grid" && after)
        {
            ReadCursor = () => point, PlaceCursor = _ => false, ReadMonitors = () => [monitor],
            SimulatedPointerInside = true, DetectWindows = id == "G02.windows" && after,
            WindowCandidates = [new WindowCandidate(new IntPtr(1), new RectD(100, 80, width - 200, height - 160), L("업무 메모", "Work notes"))],
            ShowModeBar = !fixedSize, RecordingSelection = fixedSize, ShowKeyPanel = id == "G03.keys" && after,
        };
        var host = Host(view, width, height);
        try
        {
            host.Show(); view.InitializePointer(); view.NotifyFirstFramePresented();
            if (fixedSize)
            {
                if (after)
                {
                    Assert.True(view.PlaceFixedSize(new FixedSelectionSize(1280, 720)));
                    var source = PresentationSource.FromVisual(view)!;
                    int moves = frame switch { 0 => 0, 1 => 35, _ => 80 };
                    for (int i = 0; i < moves; i++)
                        view.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent }, ModifierKeys.None);
                }
                else
                {
                    point = new PointD(260, 180); view.SimulatePointerDown();
                    point = new PointD(920, 600); view.SimulatePointerMove();
                }
            }
            view.FlushFeedbackForTest(); Drain(host);
            return Snapshot(view, width, height);
        }
        finally { view.ReleaseResources(); host.Close(); }
    }

    private static BitmapSource CursorScene(bool include, bool highlight, bool pressed)
    {
        BitmapSource sample = Sample();
        byte[] pixels = new byte[720 * 400 * 4]; sample.CopyPixels(pixels, 720 * 4, 0);
        if (include && highlight) RegionFrameGrabber.PaintRing(pixels, 720 * 4, 720, 400, 360, 190, pressed);
        var baseImage = BitmapSource.Create(720, 400, 96, 96, PixelFormats.Pbgra32, null, pixels, 720 * 4);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(baseImage, new Rect(0, 0, 720, 400));
            if (include)
            {
                dc.PushTransform(new TranslateTransform(360, 190));
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.4), Geometry.Parse("M0,0 L0,21 L5,16 L10,25 L14,23 L9,14 L17,14 Z"));
                dc.Pop();
            }
        }
        return Bitmap(visual, 720, 400);
    }

    private static BitmapSource RecordingMethod(bool compatibility)
    {
        // Parse the actual product selector, preserving its real option text and order.
        var xml = XDocument.Load(Path.Combine(Root, "src", "MyCapture.App", "Settings", "SettingsWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var node = new XElement(xml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "RecordingCaptureModeSelector"));
        node.Add(new XAttribute(XNamespace.Xmlns + "loc", "clr-namespace:MyCapture.App.Localization;assembly=MyCapture"));
        node.Add(new XAttribute(XNamespace.Xmlns + "x", x));
        var selector = (ComboBox)XamlReader.Parse(node.ToString());
        selector.SelectedValue = compatibility ? "gdi" : "auto";
        selector.FontSize = 18; selector.MinHeight = 40; selector.MinWidth = 320;
        var stack = new StackPanel { Margin = new Thickness(20, 30, 20, 20) };
        stack.Children.Add(new TextBlock { Text = UiText.Get("RecordingBackend_Label"), FontSize = 18, Margin = new Thickness(0, 0, 0, 14) });
        stack.Children.Add(selector);
        stack.Children.Add(new TextBlock { Text = compatibility ? L("창이 제대로 녹화되지 않을 때 → 호환", "Window not recorded correctly → Compatibility") : L("녹화 방식 → 자동", "Recording method → Automatic"), FontSize = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 26, 0, 0) });
        return Snapshot(new Border { Background = Brushes.White, Child = stack }, 440, 280);
    }

    private static BitmapSource Effect(string id, bool after)
    {
        BitmapSource sample = Sample();
        var doc = AnnotationDocument.CreateFor(720, 400);
        var store = new AnnotationImageStore();
        var rect = new RectD(25, 160, 620, 56);
        if (id == "G07.blur") doc.Add(new BlurAnnotation { Rect = rect, Radius = after ? 18 : 4 });
        else if (id == "G07.highlighter")
            doc.Add(new PenAnnotation { Points = [new PointD(30, 192), new PointD(630, 192)], IsHighlighter = true,
                StrokeThickness = 34, Stroke = new ColorRgba(after ? (byte)179 : (byte)51, 255, 220, 0) });
        else
        {
            var compute = typeof(AnnotationEditorControl).GetMethod("BuildMosaicBitmap", Members)!;
            var mosaic = (BitmapSource)compute.Invoke(null, [new AnnotationEditorControl.MosaicInput(sample, rect, after ? 20 : 4), CancellationToken.None])!;
            string asset = store.RegisterInMemory(mosaic).AssetFileName;
            doc.Add(new ImageAnnotation { AssetFileName = asset, SourceWidth = mosaic.PixelWidth, SourceHeight = mosaic.PixelHeight, Rect = rect });
        }
        return AnnotationFlattener.Flatten(sample, doc, new AnnotationRenderer(store));
    }

    private static BitmapSource Pin()
    {
        var pin = new PinWindow(PinContent.FromImage(Sample()), new PinViewState(720, 400, 1, 1, .1), -10000, -10000) { ShowActivated = false };
        var content = (FrameworkElement)pin.Content; pin.Content = null;
        try { return Snapshot(content, 720, 400); }
        finally { pin.Close(); }
    }

    private static BitmapSource ShortcutLabels(string id, bool after)
    {
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        ShortcutPresentation.ReadEnabled = () => id == "G10.always" && after;
        try
        {
            return Diagram(L("편집기 명령 · Ctrl+C 복사 후 닫기", "Editor commands · Ctrl+C copies and closes"), Editor(id == "G10.alt" && after));
        }
        finally { ShortcutPresentation.ReadEnabled = previous; ShortcutPresentation.Refresh(); }
    }

    private static BitmapSource Timeline(string title, string[] ticks, bool limit)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 720, 400));
            Text(dc, title, 25, 38, 21);
            dc.DrawLine(new Pen(Brushes.Gray, 2), new Point(45, 210), new Point(670, 210));
            for (int i = 0; i < ticks.Length; i++)
            {
                double at = 55 + i * 590.0 / Math.Max(1, ticks.Length - 1);
                dc.DrawEllipse(limit && i == 0 ? Brushes.DarkOrange : Brushes.SteelBlue, null, new Point(at, 210), 9, 9);
                Text(dc, ticks[i], at - 18, 240, 18);
            }
            Text(dc, L("보관 예외 유지", "Retention exceptions remain"), 25, 332, 17, Brushes.DimGray);
        }
        return Bitmap(visual, 720, 400);
    }

    private static BitmapSource Diagram(string title, BitmapSource? image, int width = 720, int height = 400)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, height));
            Text(dc, title, 24, 24, width > 720 ? 26 : 20);
            if (image is not null)
            {
                double scale = Math.Min((width - 64.0) / image.PixelWidth, (height - 110.0) / image.PixelHeight);
                double w = image.PixelWidth * scale, h = image.PixelHeight * scale;
                dc.DrawImage(image, new Rect((width - w) / 2, 90, w, h));
            }
            else Text(dc, L("열린 핀 없음", "No open pins"), 220, 205, 25, Brushes.DimGray);
        }
        return Bitmap(visual, width, height);
    }

    private static Window Host(FrameworkElement content, int width, int height) => new()
    {
        Content = content, Resources = Resources(), Width = width, Height = height, Left = -10000, Top = -10000,
        ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
    };
    private static void Drain(FrameworkElement element) => element.Dispatcher.Invoke(element.UpdateLayout, DispatcherPriority.ApplicationIdle);
    internal static BitmapSource Snapshot(FrameworkElement content, int width, int height)
    {
        content.Resources = Resources();
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        return Bitmap(content, width, height);
    }
    private static BitmapSource Bitmap(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    internal static void Write(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
