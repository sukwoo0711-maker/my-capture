using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;

/// <summary>Product annotation rasterization over a synthetic note; no capture, clipboard or file transaction.</summary>
internal sealed class EditorSceneRenderer : ITutorialSceneRenderer
{
    private readonly string _feature;
    private readonly bool _ko;
    private readonly AnnotationImageStore _images = new();
    private readonly AnnotationRenderer _renderer;
    private readonly BitmapSource _sample;
    private readonly BitmapSource _mosaic;
    private readonly string _mosaicAsset;
    private static readonly ColorRgba Accent = ColorRgba.FromRgb(239, 68, 68);
    private static readonly PointD Origin = new(170, 110), End = new(440, 218);
    public string Id => "image-" + _feature;
    public TutorialStoryboard Storyboard { get; }
    public IReadOnlyList<int> KeyFrames => [0, Math.Min(34, Storyboard.Frames.Count - 1), Storyboard.Frames.Count - 2, Storyboard.Frames.Count - 1];

    internal EditorSceneRenderer(string featureId, string language)
    {
        _feature = featureId.StartsWith("image-", StringComparison.Ordinal) ? featureId[6..] : featureId;
        if (!new[] { "rectangle", "ellipse", "arrow", "pen", "highlighter", "text", "step-number", "mosaic", "blur", "crop", "rotate", "save", "copy" }.Contains(_feature))
            throw new ArgumentOutOfRangeException(nameof(featureId));
        _ko = language == "ko";
        _renderer = new AnnotationRenderer(_images);
        _sample = CreateSample();
        // The mosaic asset is produced from this same fixture, never a second invented result.
        _mosaic = MakeMosaic(_sample);
        _mosaicAsset = _images.RegisterInMemory(_mosaic).AssetFileName;
        int toolIndex = _feature switch { "rectangle" => 0, "ellipse" => 1, "arrow" => 2, "pen" => 3, "highlighter" => 4, "text" => 5, "step-number" => 6, _ => 7 };
        PointD tool = new(30, 78 + toolIndex * 27), start = Origin, finish = End;
        if (_feature == "text") finish = start;
        if (_feature == "arrow") { start = new(170, 218); finish = new(440, 110); }
        if (_feature == "pen") { start = new(170, 170); finish = new(440, 170); }
        if (_feature == "highlighter") { start = new(170, 152); finish = new(440, 152); }
        if (_feature == "step-number") { start = new(198, 141); finish = new(423, 201); }
        if (_feature is "mosaic" or "blur") { start = new(182, 182); finish = new(462, 220); }
        if (_feature == "crop") { start = new(142, 92); finish = new(482, 242); }
        if (_feature == "rotate") { tool = new(320, 160); start = tool; finish = tool; }
        if (_feature is "save" or "copy") { tool = new(_feature == "save" ? 305 : 481, 40); start = tool; finish = tool; }
        var story = new TutorialStoryboard()
            .Hold("ready", 120, new PointD(92, 280))
            .Motion("approach", 25, new PointD(92, 280), tool);
        if (_feature == "rotate") story.Hold("choose", 32, tool);
        else story.Motion("choose", 8, tool, tool, pressed: true);
        story.Hold("chosen", 70, tool);
        PointD tail = finish;
        if (_feature == "rotate")
        {
            tail = tool;
            story.Motion("turn-left", 25, tool, tool).Hold("left-result", 180, tool)
                .Hold("right-press", 40, tail)
                .Motion("turn-right", 25, tail, tail).Hold("result", 160, tail);
        }
        else if (_feature == "save")
        {
            tail = new PointD(490, 226);
            story.Hold("save-dialog", 200, tool).Motion("save-approach", 25, tool, tail)
                .Motion("save-press", 8, tail, tail, pressed: true).Hold("saved-result", 160, tail);
        }
        else if (_feature == "copy")
        {
            tail = new PointD(240, 119);
            story.Hold("editor-closed", 150, tool).Motion("paste-approach", 25, tool, tail)
                .Motion("paste", 8, tail, tail, pressed: true).Hold("pasted-result", 160, tail);
        }
        else
        {
            story.Motion("canvas-approach", 20, tool, start).Motion("canvas-press", 8, start, start, pressed: true);
            if (_feature == "step-number")
                story.Hold("step-one", 130, start).Motion("step-approach", 25, start, finish)
                    .Motion("step-press", 8, finish, finish, pressed: true).Hold("result", 180, finish);
            else if (_feature == "text")
            {
                tail = new PointD(515, 250);
                story.Motion("type", 8, start, start).Hold("typed", 120, start)
                    .Motion("text-away", 24, start, tail).Motion("text-commit", 8, tail, tail, pressed: true).Hold("result", 120, tail);
            }
            else
            {
                story.Motion("draw", 40, start, finish, pressed: true).Hold("result", 180, finish);
                if (_feature == "crop")
                {
                    tail = new PointD(548, 287);
                    story.Motion("apply-approach", 20, finish, tail).Motion("apply", 8, tail, tail, pressed: true).Hold("applied-result", 120, tail);
                }
            }
        }
        story.Hold("complete", Math.Max(100, (11000 - story.DurationMs) / 10), tail);
        Storyboard = story;
    }

    public BitmapSource Render(TutorialFramePlan frame)
    {
        bool chosen = frame.Segment is not "ready" and not "approach";
        bool result = frame.Segment is "result" or "apply-approach" or "apply" or "applied-result" or "typed" or "text-away" or "text-commit" or "complete";
        bool complete = frame.Segment is "apply" or "applied-result" or "text-commit" or "complete";
        double progress = frame.Segment is "draw" or "type" ? TutorialStoryboard.SmoothStep(frame.Progress) : result ? 1 : 0;
        if (_feature is "save" or "copy") progress = 1;
        if (_feature == "step-number") progress = frame.Segment is "canvas-press" or "step-one" or "step-approach" ? .25 : frame.Segment is "step-press" or "result" or "complete" ? 1 : 0;
        PointD cursor = frame.Cursor;
        if (_feature == "pen" && frame.Segment == "draw") cursor = new PointD(170 + 270 * progress, 170 + Math.Sin(progress * Math.PI * 3) * 28);
        BitmapSource scene = TutorialDrawing.DrawScene(dc =>
        {
            if (_feature == "copy" && frame.Segment is "editor-closed" or "paste-approach" or "paste" or "pasted-result" or "complete")
            {
                DrawPasteTarget(dc, frame.Segment is "paste" or "pasted-result" or "complete");
                return;
            }
            DrawChrome(dc, chosen, complete);
            dc.PushClip(new RectangleGeometry(new Rect(72, 65, 500, 205)));
            dc.PushTransform(new TranslateTransform(72, 65));
            if (_feature == "rotate")
            {
                // Q and W use discrete quarter turns. Only the transition picture interpolates.
                double angle = frame.Segment switch
                {
                    "turn-left" => -90 * TutorialStoryboard.SmoothStep(frame.Progress),
                    "left-result" or "right-approach" or "right-press" => -90,
                    "turn-right" => -90 + 90 * TutorialStoryboard.SmoothStep(frame.Progress), _ => 0
                };
                dc.PushTransform(new RotateTransform(angle, 250, 102.5));
                dc.DrawImage(_sample, new Rect(0, 0, 500, 205));
                dc.Pop();
            }
            else dc.DrawImage(_sample, new Rect(0, 0, 500, 205));
            var document = BuildDocument(progress);
            if (_feature == "mosaic" && progress > 0) dc.PushClip(new RectangleGeometry(new Rect(110, 117, Math.Max(1, 280 * progress), Math.Max(1, 38 * progress))));
            _renderer.Render(dc, document, 1, blurSource: _sample);
            if (_feature == "mosaic" && progress > 0) dc.Pop();
            dc.Pop(); dc.Pop();
            if (_feature == "crop" && progress > 0)
            {
                var crop = new Rect(142, 92, Math.Max(1, 340 * progress), Math.Max(1, 150 * progress));
                if (!complete)
                {
                    var mask = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(72, 65, 500, 205)), new RectangleGeometry(crop));
                    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(130, 0, 0, 0)), null, mask);
                    dc.DrawRectangle(null, new Pen(Brushes.White, 2), crop);
                    for (int i = 1; i < 3; i++)
                    {
                        dc.DrawLine(new Pen(Brushes.White, .7), new Point(crop.Left + crop.Width * i / 3, crop.Top), new Point(crop.Left + crop.Width * i / 3, crop.Bottom));
                        dc.DrawLine(new Pen(Brushes.White, .7), new Point(crop.Left, crop.Top + crop.Height * i / 3), new Point(crop.Right, crop.Top + crop.Height * i / 3));
                    }
                }
                else
                {
                    dc.DrawRectangle(TutorialDrawing.Brush("Surface.Base"), null, new Rect(72, 65, 500, 205));
                    dc.DrawImage(new CroppedBitmap(_sample, new Int32Rect(70, 27, 340, 150)), new Rect(152, 90, 340, 150));
                }
            }
            if (_feature == "text" && chosen && !complete)
                dc.DrawRectangle(null, new Pen(TutorialDrawing.Brush("Accent.Default"), 1), new Rect(168, 108, 295, 38));
            if (_feature == "save" && frame.Segment is "chosen" or "save-dialog" or "save-approach" or "save-press") DrawSaveDialog(dc);
            if (_feature == "save" && frame.Segment is "saved-result" or "complete")
            {
                dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Raised"), new Pen(TutorialDrawing.Brush("Border.Control"), 1), new Rect(302, 175, 267, 66), 8, 8);
                TutorialDrawing.Text(dc, _feature == "save" ? L("저장됨", "Saved") : L("편집 완료", "Edits applied"), 321, 184, 18, Brushes.White);
                TutorialDrawing.Text(dc, _feature == "save" ? "capture.png" : L("복사 · 저장 · 편집 종료", "Copy · save · close editor"), 321, 211, 13, Brushes.LightGray);
            }
        });
        return TutorialDrawing.Compose(scene, Caption(frame.Segment), Key(frame.Segment), cursor,
            cursor, frame.Pressed ? frame.Progress : 0);
    }

    private AnnotationDocument BuildDocument(double p)
    {
        var document = AnnotationDocument.CreateFor(500, 205);
        if (p <= 0) return document;
        RectD rect = new(98, 45, Math.Max(1, 270 * p), Math.Max(1, 108 * p));
        switch (_feature)
        {
            case "rectangle": document.Add(new RectangleAnnotation { Rect = rect, Stroke = Accent, StrokeThickness = 4 }); break;
            case "ellipse": document.Add(new EllipseAnnotation { Rect = rect, Stroke = Accent, StrokeThickness = 4 }); break;
            case "arrow": document.Add(new PolylineAnnotation { Points = [new(98, 153), new(98 + 270 * p, 153 - 108 * p)], Stroke = Accent, StrokeThickness = 4, HeadAtEnd = true }); break;
            case "pen":
            case "highlighter":
                var points = new List<PointD>();
                for (int i = 0; i <= Math.Max(1, (int)(60 * p)); i++)
                {
                    double t = i / 60d;
                    points.Add(new PointD(98 + 270 * t, _feature == "pen" ? 105 + Math.Sin(t * Math.PI * 3) * 28 : 87));
                }
                points.Add(new PointD(98 + 270 * p, _feature == "pen" ? 105 + Math.Sin(p * Math.PI * 3) * 28 : 87));
                document.Add(new PenAnnotation { Points = points, Stroke = _feature == "pen" ? Accent : ColorRgba.FromRgb(255, 219, 60), StrokeThickness = _feature == "pen" ? 4 : 22, IsHighlighter = _feature == "highlighter" }); break;
            case "text":
                string value = L("확인할 항목", "Review this item");
                document.Add(new TextAnnotation { Text = value[..Math.Clamp((int)Math.Ceiling(value.Length * p), 1, value.Length)], Rect = new RectD(98, 45, 290, 38), Foreground = Accent, FontSize = 24, Bold = true, HasOutline = true }); break;
            case "step-number":
                int count = p < .5 ? 1 : 2;
                for (int i = 0; i < count; i++) document.Add(new TextAnnotation { IsStepNumber = true, Text = (i + 1).ToString(), Rect = new RectD(110 + i * 225, 60 + i * 60, 32, 32), FontSize = 22, Foreground = ColorRgba.White, Background = Accent, HasOutline = false }); break;
            case "blur": document.Add(new BlurAnnotation { Rect = new RectD(110, 117, Math.Max(1, 280 * p), Math.Max(1, 38 * p)), Radius = 12 }); break;
            case "mosaic": document.Add(new ImageAnnotation { Rect = new RectD(110, 117, 280, 38), SourceWidth = _mosaic.PixelWidth, SourceHeight = _mosaic.PixelHeight, AssetFileName = _mosaicAsset }); break;
            case "save":
            case "copy": document.Add(new RectangleAnnotation { Rect = new RectD(98, 45, 270, 108), Stroke = Accent, StrokeThickness = 4 }); break;
        }
        return document;
    }

    private void DrawChrome(DrawingContext dc, bool chosen, bool complete)
    {
        dc.DrawRectangle(TutorialDrawing.Brush("Surface.Base"), null, new Rect(0, 0, 640, 312));
        TutorialDrawing.Text(dc, L("이미지 편집", "Image editor"), 18, 7, 15, Brushes.White);
        string[] commands = [L("파일로 저장", "Save file"), L("복사", "Copy"), L("완료", "Done"), L("취소", "Cancel")];
        for (int i = 0; i < commands.Length; i++)
        {
            bool active = chosen && ((_feature == "save" && i == 0) || (_feature == "copy" && i == 2));
            dc.DrawRoundedRectangle(active ? TutorialDrawing.Brush("Accent.Default") : TutorialDrawing.Brush("Surface.Raised"), null, new Rect(264 + i * 88, 27, 82, 26), 4, 4);
            TutorialDrawing.Text(dc, commands[i], 270 + i * 88, 32, 12, Brushes.White);
        }
        string special = _feature switch { "mosaic" => "Icon.Mosaic", "blur" => "Icon.Blur", _ => "Icon.Crop" };
        string[] symbols = ["Icon.Rectangle", "Icon.Ellipse", "Icon.Arrow", "Icon.Pen", "Icon.Highlighter", "Icon.Text", "Icon.StepNumber", special];
        string selected = _feature switch { "rectangle" => "Icon.Rectangle", "ellipse" => "Icon.Ellipse", "arrow" => "Icon.Arrow", "pen" => "Icon.Pen", "highlighter" => "Icon.Highlighter", "text" => "Icon.Text", "step-number" => "Icon.StepNumber", _ => special };
        for (int i = 0; i < symbols.Length; i++)
        {
            double y = 66 + i * 27;
            dc.DrawRoundedRectangle(chosen && _feature is not "rotate" and not "save" and not "copy" && selected == symbols[i] ? TutorialDrawing.Brush("Accent.Default") : TutorialDrawing.Brush("Surface.Raised"), null, new Rect(16, y, 30, 25), 4, 4);
            Geometry? icon = Application.Current.TryFindResource(symbols[i]) as Geometry ?? symbols[i] switch
            {
                "Icon.Ellipse" => new EllipseGeometry(new Rect(3, 3, 14, 14)),
                "Icon.Highlighter" => Geometry.Parse("M4,14 L13,3 L17,7 L8,18 Z M2,18 H12"),
                "Icon.StepNumber" => Geometry.Parse("M3,3 H17 V17 H3 Z M8,7 L10,5 V15 M7,15 H13"), _ => null
            };
            if (icon is not null)
            {
                dc.PushTransform(new TranslateTransform(21, y + 2));
                dc.DrawGeometry(null, new Pen(Brushes.White, 1.6), icon); dc.Pop();
            }
        }
        TutorialDrawing.Text(dc, chosen ? ToolName() : L("도구 선택", "Choose a tool"), 72, 282, 14, Brushes.LightGray);
        if (_feature is "crop" or "text") TutorialDrawing.Text(dc, complete ? L("적용됨", "Applied") : L("적용", "Apply"), 525, 280, 14, Brushes.White);
    }

    private void DrawSaveDialog(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Raised"), new Pen(TutorialDrawing.Brush("Border.Control"), 1), new Rect(160, 89, 390, 165), 7, 7);
        TutorialDrawing.Text(dc, L("다른 이름으로 저장", "Save as"), 181, 104, 19, Brushes.White);
        TutorialDrawing.Text(dc, L("파일 이름", "File name"), 182, 150, 14, Brushes.LightGray);
        dc.DrawRoundedRectangle(TutorialDrawing.Brush("Surface.Base"), new Pen(TutorialDrawing.Brush("Border.Control"), 1), new Rect(260, 145, 262, 31), 3, 3);
        TutorialDrawing.Text(dc, "capture.png", 271, 150, 16, Brushes.White);
        TutorialDrawing.Text(dc, "PNG", 181, 185, 14, Brushes.LightGray);
        dc.DrawRoundedRectangle(TutorialDrawing.Brush("Accent.Default"), null, new Rect(445, 210, 78, 30), 4, 4);
        TutorialDrawing.Text(dc, L("저장", "Save"), 464, 216, 15, Brushes.White);
    }

    private void DrawPasteTarget(DrawingContext dc, bool pasted)
    {
        dc.DrawRectangle(TutorialDrawing.Brush("Surface.Base"), null, new Rect(0, 0, 640, 312));
        TutorialDrawing.Text(dc, L("프로젝트 메모", "Project note"), 20, 12, 18, Brushes.White);
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(25, 48, 590, 249), 5, 5);
        TutorialDrawing.Text(dc, L("검토할 화면", "Screen to review"), 48, 67, 20, Brushes.Black);
        if (!pasted)
        {
            TutorialDrawing.Text(dc, L("이미지를 붙여넣을 위치", "Paste the image here"), 48, 111, 16, Brushes.DimGray);
            dc.DrawLine(new Pen(Brushes.Black, 1), new Point(48, 108), new Point(48, 130));
            return;
        }
        dc.PushTransform(new TranslateTransform(65, 98));
        dc.PushTransform(new ScaleTransform(.84, .84));
        dc.DrawImage(_sample, new Rect(0, 0, 500, 205));
        _renderer.Render(dc, BuildDocument(1), 1, blurSource: _sample);
        dc.Pop(); dc.Pop();
    }

    private BitmapSource CreateSample()
    {
        BitmapSource backing = TutorialDrawing.DrawFrame(dc =>
        {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 360));
        TutorialDrawing.Text(dc, L("프로젝트 체크리스트", "Project checklist"), 32, 18, 22, Brushes.Black);
        TutorialDrawing.Text(dc, L("1   요구 사항 확인", "1   Review requirements"), 45, 67, 19, Brushes.Black);
        TutorialDrawing.Text(dc, L("2   변경 사항 검토", "2   Check the changes"), 45, 101, 19, Brushes.Black);
        TutorialDrawing.Text(dc, "demo@example.test", 115, 139, 19, Brushes.DarkSlateGray);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(214, 235, 246)), null, new Rect(390, 45, 82, 100));
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(68, 141, 163)), null, new Point(431, 78), 23, 23);
        });
        var result = new CroppedBitmap(backing, new Int32Rect(0, 0, 500, 205)); result.Freeze(); return result;
    }

    private static BitmapSource MakeMosaic(BitmapSource source)
    {
        // Baked image asset follows the product mosaic model. Sampling is confined to the selected rectangle.
        var patch = new CroppedBitmap(source, new Int32Rect(110, 117, 280, 38));
        var reduced = new TransformedBitmap(patch, new ScaleTransform(1d / 14, 1d / 14));
        var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen()) dc.DrawImage(reduced, new Rect(0, 0, 280, 38));
        var result = new RenderTargetBitmap(280, 38, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze(); return result;
    }
    private string L(string ko, string en) => _ko ? ko : en;
    private string ToolName() => _feature switch
    {
        "rectangle" => L("사각형", "Rectangle"), "ellipse" => L("타원", "Ellipse"), "arrow" => L("화살표", "Arrow"), "pen" => L("펜", "Pen"),
        "highlighter" => L("형광펜", "Highlighter"), "text" => L("텍스트", "Text"), "step-number" => L("순서 번호", "Step number"),
        "mosaic" => L("모자이크", "Mosaic"), "blur" => L("흐림", "Blur"), "crop" => L("자르기", "Crop"), "rotate" => L("회전", "Rotate"),
        "save" => L("파일로 저장", "Save to file"), _ => L("완료", "Done")
    };
    private string Caption(string segment)
    {
        if (segment is "ready" or "approach" or "choose" or "chosen") return _feature is "save" or "copy" or "rotate"
            ? ToolName() : L(ToolName() + " 도구 선택", "Choose " + ToolName().ToLowerInvariant());
        if (_feature == "copy") return segment is "editor-closed" or "paste-approach" ? L("편집 종료 후 붙여넣을 앱으로 이동", "The editor closes; switch to the target app")
            : segment is "paste" or "pasted-result" or "complete" ? L("Ctrl+V로 편집한 이미지 붙여넣기", "Paste the edited image with Ctrl+V") : L("완료: 복사 · 저장 · 편집 종료", "Done: copy, save and close");
        if (_feature == "save") return segment is "saved-result" or "complete" ? L("편집한 이미지 파일 저장됨", "The edited image file is saved") : L("파일 이름 확인 후 저장", "Choose the file name, then save");
        if (segment is "draw" or "type") return _feature switch
        {
            "text" => L("클릭한 위치에 텍스트 입력", "Click, then type the label"), "step-number" => L("클릭할 때마다 순서 번호 추가", "Click to add numbered steps"),
            "rotate" => L("Q로 왼쪽 회전", "Q rotates left"), "save" => L("파일로 저장", "Save the edited image"), "copy" => L("완료: 복사 · 저장 · 편집 종료", "Done: copy, save and close"),
            _ => L("드래그로 영역 지정", "Drag across the target area")
        };
        if (_feature == "step-number") return L("각 위치를 클릭하여 순서 번호 추가", "Click each position to add numbered steps");
        if (_feature == "text") return L("다른 곳을 클릭하여 입력 확정", "Click elsewhere to finish typing");
        if (_feature == "rotate") return segment is "right-approach" or "right-press" or "turn-right" or "result" or "complete" ? L("W로 오른쪽 회전", "W rotates right") : L("Q로 왼쪽 회전", "Q rotates left");
        if (_feature == "crop") return L("자르기 영역 확인 후 적용", "Review the crop, then apply");
        return L(ToolName() + " 결과 확인", "Review the " + ToolName().ToLowerInvariant() + " result");
    }
    private string Key(string segment) => _feature switch
    {
        "rectangle" => "R", "ellipse" => "E", "arrow" => "A", "pen" => "P", "highlighter" => "H", "text" => "T", "step-number" => "N", "mosaic" => "M", "blur" => "B", "crop" => segment is "apply-approach" or "apply" or "applied-result" or "complete" ? "Enter" : "X",
        "rotate" => segment is "right-approach" or "right-press" or "turn-right" or "result" or "complete" ? "W" : "Q", "save" => "Ctrl+S",
        _ => segment is "paste" or "pasted-result" or "complete" ? "Ctrl+V" : "Ctrl+C"
    };
    public void Dispose() { }
}
