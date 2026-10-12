using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using static TutorialDrawing;
using static TutorialFixtures;

internal sealed class CaptureSceneRenderer : ITutorialSceneRenderer
{
    internal static readonly string[] Features = ["capture-region", "record-region", "record-pause", "capture-window", "capture-fullscreen", "capture-scroll", "capture-delay", "color-copy"];
    private readonly bool _ko;
    private readonly CaptureOverlayView _overlay;
    private readonly BitmapSource _desktop, _tray;
    private readonly PointD _target, _end;
    private PointD _cursor;
    public string Id { get; }
    public TutorialStoryboard Storyboard { get; }
    public IReadOnlyList<int> KeyFrames => Id == "capture-delay" ? new[] { 0, 34, 36, 101, 110, 144 } : TutorialFixtures.KeyFrames;
    public CaptureSceneRenderer(string featureId, string language)
    {
        Id = featureId; _ko = language == "ko"; _desktop = Desktop(_ko);
        _target = featureId == "color-copy" ? new(490, 249) : featureId == "record-pause" ? new(368, 268) : new(52, 126);
        _end = featureId is "capture-region" or "record-region" or "capture-delay" ? new(402, 215) : new(515, 215);
        Storyboard = Story(_target, _end, new PointD(530, 272));
        if (featureId == "capture-delay")
            Storyboard = new TutorialStoryboard().Hold("open", 80, new(550, 278))
                .Motion("approach", 25, new(550, 278), new(150, 212)).Motion("click", 8, new(150, 212), new(150, 212), true)
                .Hold("countdown-3", 100, new(150, 212)).Hold("countdown-2", 100, new(150, 212)).Hold("countdown-1", 100, new(150, 212))
                .Motion("selector-approach", 25, new(150, 212), _target).Motion("action", 40, _target, _end, true)
                .Motion("release", 8, _end, _end).Hold("result", 100, _end)
                .Motion("follow", 25, _end, new(530, 272)).Motion("finish", 8, new(530, 272), new(530, 272), true).Hold("done", 84, new(530, 272));
        var bounds = new RectD(0, 0, SelectorWidth, SelectorHeight);
        var monitor = new MonitorInfo("Tutorial fixture", bounds, bounds, 96, true);
        _cursor = new(550 * SceneScale, 278 * SceneScale);
        _overlay = new CaptureOverlayView(new FrozenFrame(_desktop, bounds, monitor, 0), false, false)
        {
            ReadCursor = () => _cursor, ReadMonitors = () => [monitor], TextCaptureAvailable = true,
            CopyColorText = _ => Task.FromResult(true), OffersOtherActions = true, ShowModeBar = true,
            DetectWindows = featureId == "capture-window", RecordingSelection = featureId == "record-region",
        };
        _overlay.WindowCandidates = [new WindowCandidate(new IntPtr(1), new RectD(42 * SceneScale, 72 * SceneScale, 552 * SceneScale, 216 * SceneScale), L(_ko, "개발 기록", "Development notes"))];
        Set(_overlay, "_pointerInitialized", true); Set(_overlay, "_cursorPixel", _cursor);
        string action = UiText.Get(featureId switch
        {
            "capture-window" => "Text_EA1FBABE64F3", "capture-fullscreen" => "Text_65BB6610ACA0", "capture-scroll" => "Text_4DBA30197C0A", "capture-delay" => "Text_38FF9B0AA541",
            "record-region" or "record-pause" => "Text_4F5AF20FAA6E", _ => "Text_2C20ADDA2018",
        });
        _tray = Tray(_ko, action, featureId is "capture-window" or "capture-fullscreen" or "capture-scroll" or "capture-delay");
    }
    public BitmapSource Render(TutorialFramePlan frame)
    {
        string key = "";
        string caption = L(_ko, "캡처 영역 선택", "Select the capture region");
        BitmapSource scene;
        bool result = Result(frame), input = Input(frame), overlayDisplayed = false;
        if (Id == "record-pause")
        {
            bool paused = input && frame.Segment is not ("finish" or "done");
            scene = RecordingScene(paused, true);
            caption = paused ? L(_ko, "일시정지 중에는 영상에 포함되지 않습니다", "Paused time is excluded from the clip") : L(_ko, "녹화 일시정지와 다시 시작", "Pause and resume recording");
        }
        else if (Id == "capture-scroll")
        {
            scene = ScrollScene(frame);
            caption = result ? L(_ko, "스크롤한 내용을 한 이미지로 캡처", "Scrolled content captured as one image") : L(_ko, "창 위에서 스크롤 캡처 실행", "Start scrolling capture over the window");
        }
        else if (Id == "capture-delay" && (frame.Segment is "open" or "approach" or "click" || frame.Segment.StartsWith("countdown-", StringComparison.Ordinal)))
        {
            if (frame.Segment.StartsWith("countdown-", StringComparison.Ordinal))
            {
                var countdown = new CountdownWindow(int.Parse(frame.Segment[^1..]));
                var card = WindowContent(countdown, 245);
                scene = OnDesktop(_desktop, card, new Rect(198, 75, 245, card.PixelHeight));
                caption = L(_ko, "3초 후 캡처 선택 화면 표시", "The selector opens after 3 seconds");
            }
            else { scene = OnDesktop(_desktop, _tray, new Rect(35, 55, 275, Math.Min(250, _tray.PixelHeight))); caption = L(_ko, "지연 캡처 선택", "Choose delayed capture"); }
        }
        else if (result)
        {
            if (Id == "record-region") { scene = RecordingScene(false, true); caption = L(_ko, "선택한 영역 녹화 시작", "Recording starts in the selected region"); }
            else if (Id == "color-copy") { scene = ColorNote(); caption = L(_ko, "선택한 색상 코드 복사됨", "Selected color code copied"); }
            else { scene = CaptureResult(Id == "capture-fullscreen" ? new Rect(0, 0, 640, 312) : Id == "capture-window" ? new Rect(42, 72, 552, 216) : new Rect(52, 126, 350, 89)); caption = L(_ko, "캡처한 이미지 확인", "Review the captured image"); }
        }
        else if (Id == "capture-fullscreen")
        {
            scene = input ? DrawScene(dc => { dc.DrawImage(_desktop, new Rect(0, 0, Width, SceneHeight)); dc.DrawRectangle(null, new Pen(Brush("Border.Accent"), 3), new Rect(2, 2, 636, 308)); }) : OnDesktop(_desktop, _tray, new Rect(35, 55, 275, Math.Min(250, _tray.PixelHeight)));
            caption = L(_ko, "전체 화면 캡처 선택", "Choose full-screen capture");
        }
        else
        {
            overlayDisplayed = true;
            _cursor = new(frame.Cursor.X * SceneScale, frame.Cursor.Y * SceneScale);
            if (Id == "color-copy" && input) { _overlay.SelectMode(CaptureSelectorMode.Color); key = "C"; }
            bool drag = Id is "capture-region" or "record-region" or "capture-delay" && frame.Segment == "action";
            var field = typeof(CaptureOverlayView).GetField("_interaction", Private)!;
            field.SetValue(_overlay, Enum.Parse(field.FieldType, drag ? "Create" : "None"));
            if (drag) Set(_overlay, "_dragAnchor", new PointD(_target.X * SceneScale, _target.Y * SceneScale));
            Invoke(_overlay, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            _overlay.Measure(new Size(SelectorWidth, SelectorHeight)); _overlay.Arrange(new Rect(0, 0, SelectorWidth, SelectorHeight)); _overlay.UpdateLayout(); Invoke(_overlay, "RenderLayers");
            var bitmap = new RenderTargetBitmap(SelectorWidth, SelectorHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(_overlay); bitmap.Freeze(); scene = bitmap;
            if (Id == "capture-window") caption = L(_ko, "창 위로 포인터를 옮기고 클릭", "Move over the window and click");
            if (Id == "color-copy") caption = L(_ko, "색상 모드에서 픽셀 선택", "Pick a pixel in color mode");
            if (Id == "record-region") caption = L(_ko, "녹화할 영역을 드래그", "Drag the region to record");
        }
        return Compose(scene, caption, key, overlayDisplayed ? null : frame.Cursor, frame.Cursor, frame.Segment is "click" or "release" or "finish" ? frame.Progress : 0);
    }
    private BitmapSource CaptureResult(Rect crop) => DrawScene(dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        Text(dc, L(_ko, "이미지 편집", "Image editor"), 25, 15, 18, Brush("Text.Primary"));
        var source = new CroppedBitmap(_desktop, new Int32Rect((int)(crop.X * SceneScale), (int)(crop.Y * SceneScale), Math.Min(_desktop.PixelWidth - (int)(crop.X * SceneScale), (int)(crop.Width * SceneScale)), Math.Min(_desktop.PixelHeight - (int)(crop.Y * SceneScale), (int)(crop.Height * SceneScale))));
        dc.DrawImage(source, new Rect(45, 75, 550, Math.Min(190, crop.Height / crop.Width * 550)));
        Text(dc, L(_ko, "저장", "Save"), 527, 268, 16, Brush("Text.Primary"));
    });
    private BitmapSource ColorNote() => DrawScene(dc =>
    {
        dc.DrawImage(_desktop, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRoundedRectangle(Brush("Surface.Floating"), new Pen(Brush("Border.Control"), 1), new Rect(75, 125, 420, 110), 8, 8);
        Text(dc, L(_ko, "색상 코드", "Color code"), 96, 144, 17, Brush("Text.Secondary"));
        Text(dc, "#7C6CFF", 96, 179, 25, Brush("Text.Primary"), true);
    });
    private BitmapSource RecordingScene(bool paused, bool recording) => DrawScene(dc =>
    {
        dc.DrawImage(_desktop, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRectangle(null, new Pen(paused ? RecordingControlWindow.CreatePausedFrameBrush() : Brush("Border.Accent"), 4), new Rect(47, 124, 548, 100));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Button(UiText.Get("Text_88DAAEEDFE4C")));
        row.Children.Add(Button(UiText.Get(paused ? "RecordingPause_Resume" : "RecordingPause_Pause"), paused));
        row.Children.Add(new TextBlock { Text = paused ? UiText.Get("RecordingPause_Status") : L(_ko, "녹화 중  00:03", "Recording  00:03"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        var strip = Snapshot(new Border { Child = row, Padding = new Thickness(7), Background = Brush("Surface.Floating"), BorderBrush = Brush("Border.Control"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) }, 550, 55);
        dc.DrawImage(strip, new Rect(45, 240, 550, 55));
    });
    private BitmapSource ScrollScene(TutorialFramePlan frame) => DrawScene(dc =>
    {
        bool result = Result(frame); double progress = frame.Segment == "action" ? frame.Progress : result ? 1 : 0;
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        Text(dc, L(_ko, result ? "스크롤 캡처 결과" : "개발 기록", result ? "Scrolling capture result" : "Development notes"), 42, 20, 18, Brush("Text.Primary"));
        dc.PushClip(new RectangleGeometry(new Rect(42, 65, 552, 220)));
        dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush("Border.Control"), 1), new Rect(42, 65, 552, 420), 8, 8);
        double scale = result ? .53 : 1;
        for (int i = 0; i < 8; i++) Text(dc, $"{i + 1:00}  " + new[] { "npm run build", "Build completed", "npm run test", "All tests passed" }[i % 4], 65, 82 + i * 43 * scale - (result ? 0 : progress * 210), result ? 17 : 22, Brush("Text.Primary"), true);
        dc.Pop();
        if (!Input(frame)) dc.DrawImage(_tray, new Rect(330, 55, 275, Math.Min(250, _tray.PixelHeight)));
    });
    public void Dispose() => _overlay.ReleaseResources();
}
