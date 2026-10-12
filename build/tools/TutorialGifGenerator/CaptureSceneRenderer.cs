using System.Windows;
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
    private readonly RecordingStripFixture _ready, _recording, _paused, _resumed;
    private readonly RecordingStripFixture[] _recordingSeconds;
    private readonly PointD _target, _end;
    private PointD _cursor;
    private string? _copiedColor;
    private const string ExpectedColor = "#7C6CFF";
    private static readonly Rect SelectedRegion = new(52, 126, 350, 89);
    public string Id { get; }
    public TutorialStoryboard Storyboard { get; }
    public IReadOnlyList<int> KeyFrames { get; }
    public CaptureSceneRenderer(string featureId, string language)
    {
        Id = featureId; _ko = language == "ko"; _desktop = Desktop(_ko);
        _ready = new("ready", "00:00"); _recording = new("recording", "00:03"); _paused = new("paused", "00:03"); _resumed = new("recording", "00:04");
        _recordingSeconds = Enumerable.Range(0, 4).Select(second => new RecordingStripFixture("recording", $"00:{second:00}")).ToArray();
        _target = featureId switch { "color-copy" => new(552, 249), "record-pause" => _recording.PauseCenter, "capture-window" => new(220, 159), "capture-fullscreen" => new(145, 123), _ => new(52, 126) };
        _end = new(402, 215);
        (Storyboard, KeyFrames) = BuildStory();
        var bounds = new RectD(0, 0, SelectorWidth, SelectorHeight);
        var monitor = new MonitorInfo("Tutorial fixture", bounds, bounds, 96, true);
        _cursor = new(550 * SceneScale, 278 * SceneScale);
        _overlay = new CaptureOverlayView(new FrozenFrame(_desktop, bounds, monitor, 0), false, false)
        {
            ReadCursor = () => _cursor, ReadMonitors = () => [monitor], TextCaptureAvailable = true,
            CopyColorText = value => { _copiedColor = value; return Task.FromResult(true); }, OffersOtherActions = featureId != "record-region", ShowModeBar = featureId != "record-region",
            DetectWindows = featureId == "capture-window", RecordingSelection = featureId == "record-region",
        };
        _overlay.WindowCandidates = [new WindowCandidate(new IntPtr(1), new RectD(42 * SceneScale, 72 * SceneScale, 552 * SceneScale, 216 * SceneScale), L(_ko, "개발 기록", "Development notes"))];
        Set(_overlay, "_pointerInitialized", true); Set(_overlay, "_cursorPixel", _cursor);
        string action = UiText.Get(featureId switch { "capture-window" => "Text_EA1FBABE64F3", "capture-fullscreen" => "Text_65BB6610ACA0", "capture-scroll" => "Text_4DBA30197C0A", "capture-delay" => "Text_38FF9B0AA541", "record-region" or "record-pause" => "Text_4F5AF20FAA6E", _ => "Text_2C20ADDA2018" });
        _tray = Tray(_ko, action, featureId is "capture-window" or "capture-fullscreen" or "capture-scroll" or "capture-delay");
        if (Id == "color-copy")
        {
            var pixel = new byte[4]; _desktop.CopyPixels(new Int32Rect((int)(_target.X * SceneScale), (int)(_target.Y * SceneScale), 1, 1), pixel, 4, 0);
            Require(pixel[2] == 124 && pixel[1] == 108 && pixel[0] == 255 && pixel[3] == 255, "Solid swatch exact color");
        }
    }
    private (TutorialStoryboard, IReadOnlyList<int>) BuildStory()
    {
        PointD initial = new(550, 278);
        if (Id == "record-region")
        {
            PointD start = _ready.PrimaryCenter, stop = _recording.PrimaryCenter, status = new(430, 266);
            return (new TutorialStoryboard().Hold("open", 60, initial).Motion("approach", 32, initial, _target).Motion("press", 6, _target, _target, true)
                .Motion("drag", 28, _target, _end, true).Motion("release", 6, _end, _end).Hold("ready", 80, _end)
                .Motion("start-approach", 20, _end, start).Motion("start", 6, start, start, true).Hold("recording", 120, start)
                .Motion("recording-pointer", 22, start, status).Motion("stop-approach", 22, status, stop).Motion("stop", 6, stop, stop, true).Hold("done", 268, stop), [0, 66, 73, 100, 145, 151]);
        }
        if (Id == "record-pause")
        {
            PointD pause = _recording.PauseCenter, resume = _paused.PauseCenter, status = new(455, 267);
            return (new TutorialStoryboard().Hold("open", 100, initial).Motion("approach", 35, initial, pause).Motion("pause", 8, pause, pause, true).Hold("paused", 200, pause)
                .Motion("away", 25, pause, status).Motion("resume-approach", 25, status, resume).Motion("resume", 8, resume, resume, true).Hold("resumed", 200, resume)
                .Motion("follow", 25, resume, new(460, 190)).Hold("done", 116, new(460, 190)), [0, 36, 44, 95, 102, 129]);
        }
        if (Id == "capture-delay")
            return (new TutorialStoryboard().Hold("open", 80, initial).Motion("approach", 25, initial, new(150, 212)).Motion("click", 8, new(150, 212), new(150, 212), true)
                .Hold("countdown-3", 100, new(150, 212)).Hold("countdown-2", 100, new(150, 212)).Hold("countdown-1", 100, new(150, 212))
                .Motion("selector-approach", 25, new(150, 212), _target).Motion("drag", 40, _target, _end, true).Motion("release", 8, _end, _end).Hold("result", 100, _end)
                .Motion("follow", 25, _end, new(530, 272)).Motion("finish", 8, new(530, 272), new(560, 272)).Hold("done", 84, new(560, 272)), [0, 34, 36, 101, 110, 144]);
        if (Id is "capture-window" or "capture-fullscreen")
            return (new TutorialStoryboard().Hold("open", 100, initial).Motion("approach", 35, initial, _target).Motion("click", 8, _target, _target, true).Hold("result", 200, _target)
                .Motion("follow", 50, _target, new(520, 250)).Hold("done", 448, new(520, 250)), [0, 35, 36, 44, 94, 95]);
        if (Id == "color-copy")
            return (new TutorialStoryboard().Hold("open", 100, new(420, 165)).Motion("approach", 35, new(420, 165), _target).Hold("pixel", 150, _target).Motion("copy", 8, _target, _target)
                .Hold("copied", 200, _target).Motion("follow", 50, _target, new(500, 240)).Hold("done", 298, new(500, 240)), [0, 35, 36, 37, 45, 96]);
        return (new TutorialStoryboard().Hold("open", 100, initial).Motion("approach", 35, initial, _target).Motion("press", 8, _target, _target, true).Hold("input", 100, _target)
            .Motion(Id == "capture-scroll" ? "scroll" : "drag", 50, _target, _end, true).Motion("release", 8, _end, _end).Hold("result", 100, _end)
            .Motion("follow", 35, _end, new(520, 250)).Hold("done", 276, new(520, 250)), [0, 44, 94, 95, 103, 139]);
    }
    public BitmapSource Render(TutorialFramePlan frame)
    {
        string key = "", caption;
        BitmapSource scene; bool overlay = false;
        if (Id == "record-pause")
        {
            bool paused = frame.Segment is "pause" or "paused" or "away" or "resume-approach";
            bool resumed = frame.Segment is "resume" or "resumed" or "follow" or "done";
            scene = RecordingScene(paused ? _paused : resumed ? _resumed : _recording, paused);
            caption = paused ? L(_ko, "일시정지 중에는 영상에 포함되지 않습니다", "Paused time is excluded from the clip") : resumed ? L(_ko, "다시 시작하면 같은 영역 녹화가 이어집니다", "Resume recording in the same region") : L(_ko, "녹화 중 일시정지 버튼 선택", "Choose Pause while recording");
        }
        else if (Id == "record-region" && frame.Segment is "release" or "ready" or "start-approach" or "start" or "recording" or "recording-pointer" or "stop-approach" or "stop" or "done")
        {
            bool ready = frame.Segment is "release" or "ready" or "start-approach" or "start", finished = frame.Segment == "done";
            int currentIndex = Storyboard.Frames.ToList().IndexOf(frame);
            int startIndex = Storyboard.Frames.ToList().FindIndex(item => item.Segment == "recording");
            int elapsedSeconds = ready ? 0 : Math.Clamp(Storyboard.Frames.Skip(startIndex).Take(Math.Max(0, currentIndex - startIndex)).Sum(item => item.DelayCentiseconds) / 100, 0, 3);
            scene = finished ? VideoResult() : RecordingScene(ready ? _ready : _recordingSeconds[elapsedSeconds], false);
            caption = finished ? L(_ko, "녹화 중지 후 영상 확인", "Stop recording and review the video") : ready ? L(_ko, "선택한 영역에서 녹화 시작 버튼 선택", "Choose Start recording for the selected region") : L(_ko, "선택한 영역 녹화 중", "Recording the selected region");
        }
        else if (Id == "color-copy")
        {
            scene = Overlay(frame); overlay = true;
            bool copied = frame.Segment is "copy" or "copied" or "follow" or "done";
            if (copied && _copiedColor is null)
            {
                Require(_overlay.CopyColorAtPointerAsync().GetAwaiter().GetResult() == ExpectedColor && _copiedColor == ExpectedColor, "Product color copy matches solid swatch");
                scene = Overlay(frame);
            }
            caption = copied ? L(_ko, "색상 코드 복사됨  ·  " + ExpectedColor, "Color code copied  ·  " + ExpectedColor) : L(_ko, "픽셀 위에서 C 키로 색상 코드 복사", "Press C over the pixel to copy its color code");
            if (copied) key = "C";
        }
        else if (Id == "capture-delay" && (frame.Segment is "open" or "approach" or "click" || frame.Segment.StartsWith("countdown-", StringComparison.Ordinal)))
        {
            if (frame.Segment.StartsWith("countdown-", StringComparison.Ordinal))
            {
                var card = WindowContent(new CountdownWindow(int.Parse(frame.Segment[^1..])), 245);
                scene = OnDesktop(_desktop, card, new Rect(198, 75, 245, card.PixelHeight));
                caption = L(_ko, "카운트다운 후 캡처 선택 화면 표시", "The selector opens after the countdown");
            }
            else { scene = OnDesktop(_desktop, _tray, new Rect(35, 55, 275, Math.Min(250, _tray.PixelHeight))); caption = L(_ko, "지연 캡처 선택", "Choose delayed capture"); }
        }
        else if (Id == "capture-scroll")
        {
            scene = ScrollScene(frame);
            caption = frame.Segment is "release" or "result" or "follow" or "done" ? L(_ko, "스크롤한 내용을 한 이미지로 캡처", "Scrolled content captured as one image") : L(_ko, "창 위에서 스크롤 캡처 실행", "Start scrolling capture over the window");
        }
        else if (frame.Segment is "result" or "follow" or "finish" or "done" or "release" || Id is "capture-window" or "capture-fullscreen" && frame.Segment == "click")
        {
            scene = CaptureResult(Id == "capture-fullscreen" ? new Rect(0, 0, 640, 312) : Id == "capture-window" ? new Rect(42, 72, 552, 216) : SelectedRegion);
            caption = L(_ko, "캡처한 이미지 확인", "Review the captured image");
        }
        else if (Id == "capture-fullscreen")
        {
            scene = OnDesktop(_desktop, _tray, new Rect(35, 55, 275, Math.Min(250, _tray.PixelHeight))); caption = L(_ko, "전체 화면 캡처 선택", "Choose full-screen capture");
        }
        else
        {
            scene = Overlay(frame); overlay = true;
            caption = Id == "capture-window" ? L(_ko, "창 위로 포인터를 옮기고 클릭", "Move over the window and click") : Id == "record-region" ? L(_ko, "녹화할 영역을 드래그", "Drag the region to record") : L(_ko, "캡처할 영역을 드래그", "Drag the region to capture");
        }
        double click = frame.Segment is "press" or "release" or "click" or "start" or "stop" or "pause" or "resume" or "copy" ? frame.Progress : 0;
        return Compose(scene, caption, key, overlay ? null : frame.Cursor, frame.Cursor, click);
    }
    private BitmapSource Overlay(TutorialFramePlan frame)
    {
        _cursor = new(frame.Cursor.X * SceneScale, frame.Cursor.Y * SceneScale);
        bool drag = frame.Segment is "press" or "input" or "drag";
        var field = typeof(CaptureOverlayView).GetField("_interaction", Private)!;
        field.SetValue(_overlay, Enum.Parse(field.FieldType, drag ? "Create" : "None"));
        if (drag) Set(_overlay, "_dragAnchor", new PointD(_target.X * SceneScale, _target.Y * SceneScale));
        Invoke(_overlay, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        if (frame.Segment == "drag" && frame.Progress == 1)
        {
            RectD expected = new RectD(SelectedRegion.X * SceneScale, SelectedRegion.Y * SceneScale, SelectedRegion.Width * SceneScale, SelectedRegion.Height * SceneScale).ToPixelBounds();
            Require(_overlay.Selection is { } selection && selection.ToPixelBounds() == expected, "Captured and recorded region matches selected physical bounds");
        }
        _overlay.Measure(new Size(SelectorWidth, SelectorHeight)); _overlay.Arrange(new Rect(0, 0, SelectorWidth, SelectorHeight)); _overlay.UpdateLayout(); Invoke(_overlay, "RenderLayers");
        var bitmap = new RenderTargetBitmap(SelectorWidth, SelectorHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(_overlay); bitmap.Freeze(); return bitmap;
    }
    private BitmapSource CaptureResult(Rect crop) => DrawScene(dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        Text(dc, L(_ko, "캡처 결과", "Captured image"), 25, 15, 18, Brush("Text.Primary"));
        RectD physical = new RectD(crop.X * SceneScale, crop.Y * SceneScale, crop.Width * SceneScale, crop.Height * SceneScale).ToPixelBounds();
        var source = new CroppedBitmap(_desktop, new Int32Rect((int)physical.X, (int)physical.Y, Math.Min(_desktop.PixelWidth - (int)physical.X, (int)physical.Width), Math.Min(_desktop.PixelHeight - (int)physical.Y, (int)physical.Height)));
        double width = 550, height = crop.Height / crop.Width * width;
        if (height > 200) { height = 200; width = height * crop.Width / crop.Height; }
        dc.DrawImage(source, new Rect((Width - width) / 2, 75, width, height));
    });
    private BitmapSource RecordingScene(RecordingStripFixture strip, bool paused) => DrawScene(dc =>
    {
        dc.DrawImage(_desktop, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRectangle(null, new Pen(paused ? RecordingControlWindow.CreatePausedFrameBrush() : Brush("Border.Accent"), 4), SelectedRegion);
        dc.DrawImage(strip.Bitmap, new Rect(40, 242, 560, 56));
    });
    private BitmapSource VideoResult() => DrawScene(dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        Text(dc, L(_ko, "녹화 결과", "Recorded video"), 25, 15, 18, Brush("Text.Primary"));
        RectD physical = new RectD(SelectedRegion.X * SceneScale, SelectedRegion.Y * SceneScale, SelectedRegion.Width * SceneScale, SelectedRegion.Height * SceneScale).ToPixelBounds();
        var source = new CroppedBitmap(_desktop, new Int32Rect((int)physical.X, (int)physical.Y, (int)physical.Width, (int)physical.Height));
        dc.DrawImage(source, new Rect(45, 75, 550, SelectedRegion.Height / SelectedRegion.Width * 550));
        Text(dc, $"00:03   ·   {(int)physical.Width} × {(int)physical.Height}", 45, 267, 18, Brush("Text.Secondary"), true);
    });
    private BitmapSource ScrollScene(TutorialFramePlan frame) => DrawScene(dc =>
    {
        bool result = frame.Segment is "release" or "result" or "follow" or "done"; double progress = frame.Segment == "scroll" ? frame.Progress : result ? 1 : 0;
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        Text(dc, L(_ko, result ? "스크롤 캡처 결과" : "개발 기록", result ? "Scrolling capture result" : "Development notes"), 42, 20, 18, Brush("Text.Primary"));
        dc.PushClip(new RectangleGeometry(new Rect(42, 65, 552, 220)));
        dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush("Border.Control"), 1), new Rect(42, 65, 552, 420), 8, 8);
        double scale = result ? .53 : 1;
        for (int i = 0; i < 8; i++) Text(dc, $"{i + 1:00}  " + new[] { "npm run build", "Build completed", "npm run test", "All tests passed" }[i % 4], 65, 82 + i * 43 * scale - (result ? 0 : progress * 210), result ? 17 : 22, Brush("Text.Primary"), true);
        dc.Pop();
    });
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    public void Dispose() => _overlay.ReleaseResources();
}
