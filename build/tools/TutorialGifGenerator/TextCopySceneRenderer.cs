using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

using static TutorialDrawing;

internal sealed class TextCopySceneRenderer : ITutorialSceneRenderer
{
    private const int Width = 640, Height = 360, SceneHeight = 312;
    private const int SelectorWidth = 864, SelectorHeight = 421;
    private const double SceneScale = 1.35;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal const string Sample = "npm run build\nBuild completed";
    private static readonly string[] SampleLines = Sample.Split('\n');
    private static readonly PointD InitialCursor = new(76, 226), DragStart = new(59, 132), DragEnd = new(367, 200), NoteCaret = new(65, 168);
    internal static TutorialStoryboard CreateStoryboard() => new TutorialStoryboard()
        .Hold("capture", 80, InitialCursor).Hold("text-mode", 80, InitialCursor)
        .Motion("approach", 35, InitialCursor, DragStart)
        .Motion("press", 8, DragStart, DragStart, pressed: true)
        .Motion("drag", 50, DragStart, DragEnd, pressed: true)
        .Motion("release", 8, DragEnd, DragEnd)
        .Hold("selection", 40, DragEnd)
        .Motion("copy-transition", 8, DragEnd, DragEnd)
        .Hold("copied", 120, DragEnd)
        .Motion("note-transition", 8, DragEnd, DragEnd)
        .Hold("note-ready", 50, DragEnd)
        .Motion("note-approach", 25, DragEnd, NoteCaret)
        .Motion("note-click", 8, NoteCaret, NoteCaret, pressed: true)
        .Hold("pasted", 150, NoteCaret);
    private static readonly int[] ReviewFrames = [0, 1, 94, 112, 121, 155];
    private readonly bool _ko;
    private readonly CaptureOverlayView _view;
    private readonly BitmapSource _desktop, _copied, _note, _pasted;
    private readonly FieldInfo _interaction;
    private PointD _cursor;
    private BitmapSource? _lastOverlay;
    public string Id => "text-copy";
    public TutorialStoryboard Storyboard { get; } = CreateStoryboard();
    public IReadOnlyList<int> KeyFrames => ReviewFrames;
    public TextCopySceneRenderer(string language)
    {
        _ko = language == "ko";
        _desktop = SourceScene(_ko);
        var bounds = new RectD(0, 0, SelectorWidth, SelectorHeight);
        var monitor = new MonitorInfo("Tutorial fixture", bounds, bounds, 96, true);
        _cursor = new(InitialCursor.X * SceneScale, InitialCursor.Y * SceneScale);
        _view = new CaptureOverlayView(new FrozenFrame(_desktop, bounds, monitor, 0), showMagnifier: false, showSelectionGrid: false)
        {
            ReadCursor = () => _cursor,
            ReadMonitors = () => [monitor],
            TextCaptureAvailable = true,
            CopyColorText = _ => Task.FromResult(true),
            OffersOtherActions = true,
            DetectWindows = false,
            ShowModeBar = true,
        };
        Set(_view, "_pointerInitialized", true);
        Set(_view, "_cursorPixel", _cursor);
        _interaction = typeof(CaptureOverlayView).GetField("_interaction", PrivateInstance)!;
        _copied = CopiedScene(_desktop); _note = NoteScene(_ko, false); _pasted = NoteScene(_ko, true);
    }
    private BitmapSource OverlayScene()
    {
        _view.Measure(new Size(SelectorWidth, SelectorHeight));
        _view.Arrange(new Rect(0, 0, SelectorWidth, SelectorHeight));
        _view.UpdateLayout();
        Invoke(_view, "RenderLayers");
        var scene = new RenderTargetBitmap(SelectorWidth, SelectorHeight, 96, 96, PixelFormats.Pbgra32);
        scene.Render(_view);
        scene.Freeze();
        return scene;
    }

    public BitmapSource Render(TutorialFramePlan plan)
    {
        _cursor = new PointD(plan.Cursor.X * SceneScale, plan.Cursor.Y * SceneScale);
        string caption = _ko ? "복사할 텍스트 영역 선택" : "Drag around the text";
        string key = "";
        BitmapSource scene;
        PointD? demoCursor = null;
        if (plan.Segment is "capture" or "text-mode" or "approach" or "press" or "drag" or "release" or "selection")
        {
            if (plan.Segment == "text-mode")
            {
                _view.SelectMode(CaptureSelectorMode.Text);
                Require(_view.Mode == CaptureSelectorMode.Text, "Text mode transition");
            }
            bool dragging = plan.Segment == "drag";
            _interaction.SetValue(_view, Enum.Parse(_interaction.FieldType, dragging ? "Create" : "None"));
            if (dragging) Set(_view, "_dragAnchor", new PointD(DragStart.X * SceneScale, DragStart.Y * SceneScale));
            Invoke(_view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            scene = OverlayScene();
            _lastOverlay = scene;
            if (plan.Segment == "capture") caption = _ko ? "캡처 선택 화면" : "Open the capture selector";
            if (plan.Segment == "text-mode") { caption = _ko ? "T 키로 텍스트 모드 전환" : "Press T for text mode"; key = "T"; }
            if (plan.Segment is "release" or "selection")
            {
                Require(_view.Selection is { } selection && selection.Width >= 384 && selection.Height >= 84, "Complete two-line sample");
                caption = _ko ? "마우스 버튼을 놓으면 자동 복사" : "Release to recognise and copy";
            }
        }
        else if (plan.Segment is "copy-transition" or "copied")
        {
            scene = plan.Segment == "copied" ? _copied : Blend(_lastOverlay!, _copied, plan.Progress);
            caption = _ko ? "텍스트 복사됨" : "Text copied automatically";
        }
        else
        {
            scene = plan.Segment switch
            {
                "note-transition" => Blend(_copied, _note, plan.Progress),
                "pasted" => _pasted,
                _ => _note,
            };
            caption = _ko ? "메모에 붙여넣기" : "Paste into your note";
            key = "Ctrl+V";
            demoCursor = plan.Cursor;
            if (plan.Segment == "pasted") { caption = _ko ? "선택한 텍스트 붙여넣기 완료" : "Selected text pasted"; key = ""; demoCursor = null; }
        }
        double clickProgress = plan.Segment is "press" or "release" or "note-click" ? plan.Progress : 0;
        return Compose(scene, caption, key, demoCursor, plan.Cursor, clickProgress);
    }
    public void Dispose() => _view.ReleaseResources();
    private static BitmapSource SourceScene(bool ko) => Draw(SceneHeight, dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(24, 28, 37)), new Pen(Brush("Border.Subtle"), 1), new Rect(38, 91, 564, 198), 8, 8);
        Text(dc, ko ? "터미널" : "Terminal", 54, 103, 14, Brush("Text.Secondary"));
        dc.DrawLine(new Pen(Brush("Border.Subtle"), 1), new Point(38, 125), new Point(602, 125));
        Text(dc, SampleLines[0], 125, 140, 22, Brushes.White, mono: true);
        Text(dc, SampleLines[1], 125, 177, 22, new SolidColorBrush(Color.FromRgb(154, 225, 177)), mono: true);
        Text(dc, ko ? "출력" : "Output", 65, 251, 13, Brush("Text.Secondary"));
    });

    private static BitmapSource CopiedScene(BitmapSource desktop)
    {
        // Mirror the product's notification content only; do not show a Window or run its action.
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = UiText.Get("TextCapture_Copied"), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = UiText.Format("TextCapture_CopiedDetail", TextCaptureService.CountCharacters(Sample)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var advanced = new Button { Content = UiText.Get("TextCapture_OpenAdvanced"), MinHeight = 36, Margin = new Thickness(0, 0, 8, 0) };
        advanced.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        buttons.Children.Add(advanced);
        buttons.Children.Add(new Button { Content = UiText.Get("Shell.Close"), MinHeight = 36 });
        content.Children.Add(buttons);
        var border = new Border { Width = 360, Padding = new Thickness(20), Child = content, Background = Brush("Surface.Base"), BorderBrush = Brush("Border.Control"), BorderThickness = new Thickness(1) };
        TextElement.SetForeground(border, Brush("Text.Primary"));
        TextElement.SetFontFamily(border, (FontFamily)Application.Current.Resources["Font.Ui"]);
        border.Measure(new Size(360, double.PositiveInfinity));
        border.Arrange(new Rect(new Point(0, 0), border.DesiredSize));
        border.UpdateLayout();
        var card = new RenderTargetBitmap(360, (int)Math.Ceiling(border.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        card.Render(border);
        return Draw(SceneHeight, dc =>
        {
            dc.DrawImage(desktop, new Rect(0, 0, Width, SceneHeight));
            dc.DrawImage(card, new Rect(264, SceneHeight - card.PixelHeight - 16, card.PixelWidth, card.PixelHeight));
        });
    }

    private static BitmapSource NoteScene(bool ko, bool pasted) => Draw(SceneHeight, dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush("Border.Control"), 1), new Rect(38, 47, 564, 238), 8, 8);
        Text(dc, ko ? "메모" : "Note", 57, 63, 16, Brush("Text.Primary"));
        dc.DrawLine(new Pen(Brush("Border.Subtle"), 1), new Point(38, 97), new Point(602, 97));
        Text(dc, ko ? "빌드 기록" : "Build log", 65, 115, 17, Brush("Text.Secondary"));
        if (pasted)
        {
            Text(dc, SampleLines[0], 65, 158, 22, Brush("Text.Primary"), mono: true);
            Text(dc, SampleLines[1], 65, 195, 22, Brush("Text.Primary"), mono: true);
            dc.DrawLine(new Pen(Brush("Text.Primary"), 1.5), new Point(265, 195), new Point(265, 219));
        }
        else dc.DrawLine(new Pen(Brush("Text.Primary"), 1.5), new Point(65, 158), new Point(65, 182));
    });

    private static void Set(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidDataException(detail); }
}
