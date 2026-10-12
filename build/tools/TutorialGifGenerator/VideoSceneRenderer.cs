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
            "gif-export" => new(320, 140), "frame-step" => new(108, 201),
            "trim" => new(24 + _timeline.OverviewPxForTest(3000), 251), _ => new(72, 201)
        };
        var story = new TutorialStoryboard().Hold("ready", 120, initial).Motion("approach", _feature == "trim" ? 20 : 25, initial, first);
        if (_feature == "gif-export") story.Hold("press", 32, first);
        else story.Motion("press", _feature == "trim" ? 6 : 8, first, first, pressed: true);
        PointD tail = first;
        switch (_feature)
        {
            case "play":
                story.Motion("playing", 60, first, first)
                    .Motion("pause", 8, first, first, pressed: true).Hold("paused", 180, first); break;
            case "frame-step":
                story.Hold("previous-frame", 160, first).Motion("next-approach", 20, first, new PointD(124, 201));
                tail = new PointD(124, 201);
                story.Motion("next-frame", 8, tail, tail, pressed: true).Hold("stepped", 200, tail); break;
            case "trim":
                PointD inPoint = new(24 + _timeline.OverviewPxForTest(2000), 251);
                tail = new PointD(24 + _timeline.OverviewPxForTest(6000), 251);
                story.Motion("seek-in", 20, first, inPoint).Motion("set-in", 6, inPoint, inPoint, pressed: true)
                    .Hold("in-result", 160, inPoint).Motion("seek-out", 25, inPoint, tail)
                    .Motion("set-out", 6, tail, tail, pressed: true).Hold("trim-result", 160, tail);
                PointD save = new(535, 40);
                story.Motion("library-save-approach", 20, tail, save).Motion("library-save", 6, save, save, pressed: true)
                    .Hold("library-saved", 100, save); tail = save; break;
            case "gif-export":
                tail = new PointD(503, 231);
                story.Hold("export-dialog", 160, first).Motion("export-approach", 25, first, tail)
                    .Motion("export", 8, tail, tail, pressed: true).Hold("file-dialog", 100, tail)
                    .Motion("file-save", 8, tail, tail, pressed: true).Motion("exporting", 30, tail, tail)
                    .Hold("exported", 140, tail); break;
        }
        story.Hold("complete", Math.Max(100, (11000 - story.DurationMs) / 10), tail);
        Storyboard = story;
    }

    public BitmapSource Render(TutorialFramePlan frame)
    {
        double position = 3000;
        if (_feature == "play") position = frame.Segment == "playing" ? 3000 + 2400 * frame.Progress
            : frame.Segment is "pause" or "paused" or "complete" ? 5400 : 3000;
        if (_feature == "frame-step") position = frame.Segment is "press" or "previous-frame" or "next-approach" ? FrameStepCalculator.StepByFrames(3000, -1, Fps, Duration)
            : frame.Segment is "next-frame" or "stepped" or "complete" ? 3000 : 3000;
        if (_feature == "trim")
        {
            position = frame.Segment switch { "seek-in" => 3000 + (2000 - 3000) * frame.Progress, "set-in" or "in-result" => 2000,
                "seek-out" => 2000 + 4000 * frame.Progress, "set-out" or "trim-result" or "library-save-approach" or "library-save" or "library-saved" or "complete" => 6000, _ => 3000 };
            bool hasIn = frame.Segment is "set-in" or "in-result" or "seek-out" or "set-out" or "trim-result" or "library-save-approach" or "library-save" or "library-saved" or "complete";
            bool hasOut = frame.Segment is "set-out" or "trim-result" or "library-save-approach" or "library-save" or "library-saved" or "complete";
            _timeline.SetTrimRange(hasIn ? 2000 : 0, hasOut ? 6000 : Duration);
        }
        _timeline.SetPlayhead(position);
        _timeline.FlushRenderForTest();
        var timelineBitmap = new RenderTargetBitmap(592, 58, 96, 96, PixelFormats.Pbgra32); timelineBitmap.Render(_timeline); timelineBitmap.Freeze();
        bool playing = _feature == "play" && frame.Segment == "playing";
        bool exportDialog = _feature == "gif-export" && frame.Segment is "press" or "export-dialog" or "export-approach" or "export" or "exporting";
        BitmapSource scene = TutorialDrawing.DrawScene(dc =>
        {
            dc.DrawRectangle(TutorialDrawing.Brush("Surface.Base"), null, new Rect(0, 0, 640, 312));
            TutorialDrawing.Text(dc, L("동영상 편집", "Video editor"), 18, 9, 17, Brushes.White);
            dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Raised"), null, new Rect(330, 27, 112, 28), 4, 4);
            TutorialDrawing.Text(dc, L("내보내기", "Export"), 353, 32, 14, Brushes.White);
            dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Default"), null, new Rect(450, 27, 172, 28), 4, 4);
            TutorialDrawing.Text(dc, L("라이브러리에 저장", "Save to library"), 463, 32, 14, Brushes.White);
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
            if (_feature == "gif-export" && frame.Segment is "file-dialog" or "file-save") DrawFileDialog(dc);
            if (_feature == "trim" && frame.Segment is "library-save" or "library-saved" or "complete")
            {
                dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Raised"), new Pen(TutorialDrawing.Brush("Border.Control"), 1), new Rect(309, 118, 280, 80), 7, 7);
                TutorialDrawing.Text(dc, L("편집 내용 저장됨", "Edits saved"), 328, 132, 19, Brushes.White);
                TutorialDrawing.Text(dc, L("선택 구간   4.0초", "Selected range   4.0 seconds"), 328, 163, 15, Brushes.LightGray);
            }
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
        TutorialDrawing.Text(dc, L("형식   GIF", "Format   GIF"), 183, 131, 16, Brushes.White);
        TutorialDrawing.Text(dc, L("선택 구간   8.0초", "Selected range   8.0 seconds"), 183, 157, 16, Brushes.LightGray);
        TutorialDrawing.Text(dc, "640 × 360    ·    25 fps", 183, 183, 16, Brushes.LightGray);
        dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Default"), null, new Rect(445, 214, 84, 32), 4, 4);
        TutorialDrawing.Text(dc, L("내보내기", "Export"), 452, 220, 14, Brushes.White);
        if (frame.Segment == "exporting")
        {
            dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Subtle"), null, new Rect(184, 216, 235, 8), 4, 4);
            dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Default"), null, new Rect(184, 216, Math.Max(1, 235 * frame.Progress), 8), 4, 4);
        }
    }
    private void DrawFileDialog(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Raised"), new Pen(TutorialDrawing.Brush("Border.Control"), 1), new Rect(160, 79, 390, 184), 7, 7);
        TutorialDrawing.Text(dc, L("GIF 파일 저장", "Save GIF file"), 183, 95, 20, Brushes.White);
        TutorialDrawing.Text(dc, L("파일 이름", "File name"), 183, 141, 16, Brushes.LightGray);
        dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Base"), new Pen(TutorialDrawing.Brush("Border.Control"), 1), new Rect(275, 135, 254, 33), 4, 4);
        TutorialDrawing.Text(dc, "clip.gif", 286, 141, 17, Brushes.White);
        dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Default"), null, new Rect(445, 214, 84, 32), 4, 4);
        TutorialDrawing.Text(dc, L("저장", "Save"), 466, 221, 14, Brushes.White);
    }
    private string Caption(string segment) => _feature switch
    {
        "play" => segment is "pause" or "paused" or "complete" ? L("Space로 일시 정지", "Press Space to pause") : L("Space로 동영상 재생", "Press Space to play"),
        "frame-step" => segment is "next-approach" or "next-frame" or "stepped" or "complete" ? L("마침표로 다음 프레임", "Period steps to the next frame") : L("쉼표로 이전 프레임", "Comma steps to the previous frame"),
        "trim" => segment is "library-save-approach" or "library-save" or "library-saved" or "complete" ? L("라이브러리에 선택 구간 저장", "Save the selected range to the library") : segment is "seek-out" or "set-out" or "trim-result" ? L("끝 위치에서 O: 선택 구간만 유지", "At the end, press O to keep this range") : L("시작 위치에서 I: 자르기 시작점", "At the start, press I to set the in point"),
        _ => segment is "exported" or "complete" ? L("선택 구간의 GIF 파일 확인", "Review the exported GIF file") : segment is "file-dialog" or "file-save" ? L("GIF 파일 이름 확인 후 저장", "Choose the GIF file name, then save") : L("G로 GIF 내보내기 설정", "Press G to configure GIF export")
    };
    private string Key(string segment) => _feature switch { "play" => "Space", "frame-step" => segment is "next-approach" or "next-frame" or "stepped" or "complete" ? "." : ",", "trim" => segment is "library-save-approach" or "library-save" or "library-saved" or "complete" ? "" : segment is "seek-out" or "set-out" or "trim-result" ? "O" : "I", _ => segment is "ready" or "approach" or "press" or "export-dialog" ? "G" : "" };
    private string L(string ko, string en) => _ko ? ko : en;
    private static string Time(double ms) => TimeSpan.FromMilliseconds(ms).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    public void Dispose() => _timeline.Dispose();
}
