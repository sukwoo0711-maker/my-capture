using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;

/// <summary>Actual trim timeline and frame arithmetic, with deterministic synthetic video frames.</summary>
internal sealed class VideoSceneRenderer : ITutorialSceneRenderer
{
    private readonly string _feature;
    private readonly bool _ko;
    private readonly TwoLineTimeline _timeline = new();
    private const int Fps = 25;
    private const double Duration = 8000;
    public string Id => "video-" + _feature;
    public TutorialStoryboard Storyboard { get; }
    public IReadOnlyList<int> KeyFrames => [0, Math.Min(34, Storyboard.Frames.Count - 1), Storyboard.Frames.Count - 2, Storyboard.Frames.Count - 1];

    internal VideoSceneRenderer(string featureId, string language)
    {
        _feature = featureId.StartsWith("video-", StringComparison.Ordinal) ? featureId[6..] : featureId;
        if (!new[] { "play", "frame-step", "trim", "gif-export" }.Contains(_feature)) throw new ArgumentOutOfRangeException(nameof(featureId));
        _ko = language == "ko";
        _timeline.Initialize(Duration, Fps);
        _timeline.SetDetailVisible(false);
        _timeline.SetTrimMode(_feature is "trim" or "gif-export");
        _timeline.Width = 592; _timeline.Height = 58;
        _timeline.Measure(new Size(592, 58)); _timeline.Arrange(new Rect(0, 0, 592, 58)); _timeline.UpdateLayout();
        var initial = new PointD(92, 282);
        PointD first = _feature switch
        {
            "gif-export" => new(548, 39), "frame-step" => new(108, 201),
            "trim" => new(24 + _timeline.OverviewPxForTest(3000), 251), _ => new(72, 201)
        };
        var story = new TutorialStoryboard().Hold("ready", 120, initial).Motion("approach", 25, initial, first)
            .Motion("press", 8, first, first, pressed: true);
        PointD tail = first;
        switch (_feature)
        {
            case "play":
                story.Motion("playing", 60, first, first).Hold("playing-hold", 100, first)
                    .Motion("pause", 8, first, first, pressed: true).Hold("paused", 180, first); break;
            case "frame-step":
                story.Hold("previous-frame", 160, first).Motion("next-approach", 20, first, new PointD(124, 201));
                tail = new PointD(124, 201);
                story.Motion("next-frame", 8, tail, tail, pressed: true).Hold("stepped", 200, tail); break;
            case "trim":
                PointD inPoint = new(24 + _timeline.OverviewPxForTest(2000), 251);
                tail = new PointD(24 + _timeline.OverviewPxForTest(6000), 251);
                story.Motion("seek-in", 25, first, inPoint).Motion("set-in", 8, inPoint, inPoint, pressed: true)
                    .Hold("in-result", 160, inPoint).Motion("seek-out", 30, inPoint, tail)
                    .Motion("set-out", 8, tail, tail, pressed: true).Hold("trim-result", 160, tail); break;
            case "gif-export":
                tail = new PointD(503, 231);
                story.Hold("export-dialog", 200, first).Motion("export-approach", 25, first, tail)
                    .Motion("export", 8, tail, tail, pressed: true).Motion("exporting", 30, tail, tail)
                    .Hold("exported", 180, tail); break;
        }
        story.Hold("complete", Math.Max(100, (11000 - story.DurationMs) / 10), tail);
        Storyboard = story;
    }

    public BitmapSource Render(TutorialFramePlan frame)
    {
        double position = 3000;
        if (_feature == "play") position = frame.Segment == "playing" ? 3000 + 2400 * frame.Progress
            : frame.Segment is "playing-hold" or "pause" or "paused" or "complete" ? 5400 : 3000;
        if (_feature == "frame-step") position = frame.Segment is "previous-frame" or "next-approach" ? FrameStepCalculator.StepByFrames(3000, -1, Fps, Duration)
            : frame.Segment is "next-frame" or "stepped" or "complete" ? 3000 : 3000;
        if (_feature == "trim")
        {
            position = frame.Segment switch { "seek-in" => 3000 + (2000 - 3000) * frame.Progress, "set-in" or "in-result" => 2000,
                "seek-out" => 2000 + 4000 * frame.Progress, "set-out" or "trim-result" or "complete" => 6000, _ => 3000 };
            bool hasIn = frame.Segment is "set-in" or "in-result" or "seek-out" or "set-out" or "trim-result" or "complete";
            bool hasOut = frame.Segment is "set-out" or "trim-result" or "complete";
            _timeline.SetTrimRange(hasIn ? 2000 : 0, hasOut ? 6000 : Duration);
        }
        _timeline.SetPlayhead(position);
        _timeline.FlushRenderForTest();
        var timelineBitmap = new RenderTargetBitmap(592, 58, 96, 96, PixelFormats.Pbgra32); timelineBitmap.Render(_timeline); timelineBitmap.Freeze();
        bool playing = _feature == "play" && frame.Segment is "playing" or "playing-hold";
        bool exportDialog = _feature == "gif-export" && frame.Segment is "press" or "export-dialog" or "export-approach" or "export" or "exporting";
        BitmapSource scene = TutorialDrawing.DrawScene(dc =>
        {
            dc.DrawRectangle(TutorialDrawing.Brush("Surface.Base"), null, new Rect(0, 0, 640, 312));
            TutorialDrawing.Text(dc, L("동영상 편집", "Video editor"), 18, 9, 17, Brushes.White);
            dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Raised"), null, new Rect(425, 27, 90, 28), 4, 4);
            TutorialDrawing.Text(dc, L("내보내기", "Export"), 438, 32, 14, Brushes.White);
            dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Default"), null, new Rect(522, 27, 100, 28), 4, 4);
            TutorialDrawing.Text(dc, "GIF", 553, 32, 14, Brushes.White);
            DrawVideoFrame(dc, position);
            TutorialDrawing.Text(dc, playing ? "Ⅱ" : "▶", 63, 191, 21, Brushes.White);
            TutorialDrawing.Text(dc, "‹    ›", 99, 191, 20, Brushes.White);
            TutorialDrawing.Text(dc, Time(position) + " / 00:08.000", 180, 197, 15, Brushes.LightGray);
            TutorialDrawing.Text(dc, "25 fps", 529, 197, 14, Brushes.LightGray);
            dc.DrawImage(timelineBitmap, new Rect(24, 226, 592, 58));
            TutorialDrawing.Text(dc, L("시작", "Start") + " " + Time(_timeline.InMs), 28, 289, 12, Brushes.LightGray);
            TutorialDrawing.Text(dc, L("끝", "End") + " " + Time(_timeline.OutMs), 228, 289, 12, Brushes.LightGray);
            TutorialDrawing.Text(dc, L("선택 구간", "Selected") + " " + (_timeline.SelectedDurationMs / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s", 432, 289, 12, Brushes.White);
            if (exportDialog) DrawExportDialog(dc, frame);
            if (_feature == "gif-export" && frame.Segment is "exported" or "complete")
            {
                dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Raised"), new Pen(TutorialDrawing.Brush("Border.Control"), 1), new Rect(304, 122, 286, 78), 7, 7);
                TutorialDrawing.Text(dc, L("GIF 내보내기 완료", "GIF exported"), 322, 137, 19, Brushes.White);
                TutorialDrawing.Text(dc, "clip.gif", 322, 166, 16, Brushes.LightGray);
            }
        });
        return TutorialDrawing.Compose(scene, Caption(frame.Segment), Key(frame.Segment), frame.Cursor, frame.Cursor, frame.Pressed ? frame.Progress : 0);
    }

    private void DrawVideoFrame(DrawingContext dc, double position)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(229, 241, 248)), null, new Rect(100, 65, 440, 118), 4, 4);
        TutorialDrawing.Text(dc, L("작업 흐름 데모", "Workflow demo"), 120, 76, 18, Brushes.Black);
        double x = 140 + 320 * position / Duration;
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(73, 140, 180)), null, new Point(x, 139), 20, 20);
        TutorialDrawing.Text(dc, "frame " + FrameStepCalculator.FrameIndexAt(position, Fps, Duration), 364, 154, 13, Brushes.DarkSlateGray, mono: true);
    }
    private void DrawExportDialog(DrawingContext dc, TutorialFramePlan frame)
    {
        dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Raised"), new Pen(TutorialDrawing.Brush("Border.Control"), 1), new Rect(160, 79, 390, 184), 7, 7);
        TutorialDrawing.Text(dc, L("GIF 내보내기", "Export GIF"), 183, 95, 20, Brushes.White);
        TutorialDrawing.Text(dc, L("선택 구간   8.0초", "Selected range   8.0 seconds"), 183, 134, 16, Brushes.LightGray);
        TutorialDrawing.Text(dc, "640 × 360    ·    25 fps", 183, 166, 16, Brushes.LightGray);
        dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Default"), null, new Rect(445, 214, 84, 32), 4, 4);
        TutorialDrawing.Text(dc, L("내보내기", "Export"), 452, 220, 14, Brushes.White);
        if (frame.Segment == "exporting")
        {
            dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Subtle"), null, new Rect(184, 216, 235, 8), 4, 4);
            dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Default"), null, new Rect(184, 216, Math.Max(1, 235 * frame.Progress), 8), 4, 4);
        }
    }
    private string Caption(string segment) => _feature switch
    {
        "play" => segment is "pause" or "paused" or "complete" ? L("Space로 일시 정지", "Press Space to pause") : L("Space로 동영상 재생", "Press Space to play"),
        "frame-step" => segment is "next-approach" or "next-frame" or "stepped" or "complete" ? L("마침표로 다음 프레임", "Period steps to the next frame") : L("쉼표로 이전 프레임", "Comma steps to the previous frame"),
        "trim" => segment is "seek-out" or "set-out" or "trim-result" or "complete" ? L("끝 위치에서 O: 선택 구간만 유지", "At the end, press O to keep this range") : L("시작 위치에서 I: 자르기 시작점", "At the start, press I to set the in point"),
        _ => segment is "exported" or "complete" ? L("선택 구간의 GIF 파일 확인", "Review the exported GIF file") : L("G로 GIF 내보내기 설정", "Press G to configure GIF export")
    };
    private string Key(string segment) => _feature switch { "play" => "Space", "frame-step" => segment is "next-approach" or "next-frame" or "stepped" or "complete" ? "." : ",", "trim" => segment is "seek-out" or "set-out" or "trim-result" or "complete" ? "O" : "I", _ => "G" };
    private string L(string ko, string en) => _ko ? ko : en;
    private static string Time(double ms) => TimeSpan.FromMilliseconds(ms).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    public void Dispose() => _timeline.Dispose();
}
