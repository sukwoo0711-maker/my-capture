using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// The image editor clips: the real AnnotationEditorWindow over a synthetic captured page, driven
/// through the editor's own commands, shortcuts and canvas gesture handling.
/// </summary>
internal sealed class EditorScene : TutorialScene
{
    internal static readonly string[] Features = ["image-rectangle", "image-ellipse", "image-arrow", "image-pen", "image-highlighter",
        "image-text", "image-step-number", "image-mosaic", "image-blur", "image-crop", "image-rotate", "image-save", "image-copy"];

    private static readonly Point ClientOrigin = new(10, 4 + Stage.TitleBarHeight);
    private const double ClientWidth = 940, ClientHeight = 448;

    private readonly string _feature;
    private readonly BitmapSource _image;
    private readonly AnnotationEditorWindow _window;
    private readonly AnnotationEditorControl _editor;
    private readonly OffscreenWindow _host;
    private readonly EditorTool _tool;
    private readonly string _toolName, _toolKey;
    private readonly List<PointD> _clicks = [];
    private TaskCompletionSource<bool>? _commit;
    private bool _closed;
    private int _steps;
    private string _typed = string.Empty;
    private PointD _dragStart;
    private readonly PointD _plannedImageOrigin, _plannedImageUnit;

    internal EditorScene(string id, string language) : base(id, language)
    {
        _feature = id["image-".Length..];
        _image = Fixtures.Checklist(Korean);
        var region = new RectD(0, 0, _image.PixelWidth, _image.PixelHeight);
        AnnotationDocument? document = _feature is "save" or "copy" ? Finished(_image) : null;
        _window = new AnnotationEditorWindow(new FrozenFrame(_image, region, null, 0), region, _image, initialDocument: document,
            sourceWindowTitle: L("릴리스 준비", "Release prep"), provenance: Fixtures.ChecklistSource);
        _host = new OffscreenWindow(_window, ClientWidth, ClientHeight);
        _editor = _window.Editor;
        // The tool, its name (the catalog's title for this tutorial) and the key the editor shows for it.
        (EditorTool Tool, string Command) tool = _feature switch
        {
            "rectangle" => (EditorTool.Rectangle, "Rectangle"),
            "ellipse" => (EditorTool.Ellipse, "Ellipse"),
            "arrow" => (EditorTool.Arrow, "Arrow"),
            "pen" => (EditorTool.Pen, "Pen"),
            "highlighter" => (EditorTool.Highlighter, "Highlighter"),
            "text" => (EditorTool.Text, "Text"),
            "step-number" => (EditorTool.StepNumber, "StepNumber"),
            "mosaic" => (EditorTool.Mosaic, "Mosaic"),
            "blur" => (EditorTool.Blur, "Blur"),
            "crop" => (EditorTool.Crop, "Crop"),
            _ => (EditorTool.Select, ""),
        };
        _tool = tool.Tool;
        _toolName = tool.Command.Length == 0 ? string.Empty : Ui(TutorialCatalog.Get(id).TitleKey);
        _toolKey = tool.Command.Length == 0 ? string.Empty : Hint(ShortcutScope.Annotation, tool.Command);
        _plannedImageOrigin = Image(0, 0);
        _plannedImageUnit = Image(1, 1);
        Story = BuildStory();
    }

    internal override string Provenance =>
        "Real AnnotationEditorWindow (command bar, tool rail, property bar above the canvas, crop mode, status line with the page's source chip, tool cursors) over a synthetic page; "
        + "tools, rotation, crop and commits run through the editor's own handlers. Explorer and note windows are synthetic.";

    private static AnnotationDocument Finished(BitmapSource image)
    {
        var document = AnnotationDocument.CreateFor(image.PixelWidth, image.PixelHeight);
        Rect item = Fixtures.ChecklistParts.SecondItem;
        document.Add(new RectangleAnnotation { Rect = new RectD(item.X, item.Y, item.Width, item.Height), Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44), StrokeThickness = 3 });
        return document;
    }

    // ---- Geometry ---------------------------------------------------------------------------

    private PointD Client(Point client) => new(ClientOrigin.X + client.X, ClientOrigin.Y + client.Y);

    private PointD Of(FrameworkElement element) { PointD p = _host.CenterOf(element); return new PointD(ClientOrigin.X + p.X, ClientOrigin.Y + p.Y); }

    private PointD Image(double x, double y)
    {
        Point viewport = _editor.ViewportPointOf(new PointD(x, y));
        return Client(((UIElement)_editor.PointerInputElement).TranslatePoint(viewport, _host.ClientRoot));
    }

    private Point Viewport(PointD stage) =>
        _host.ClientRoot.TranslatePoint(new Point(stage.X - ClientOrigin.X, stage.Y - ClientOrigin.Y), _editor.PointerInputElement);

    // Changing tools changes the height of the property bar and refits the image. Storyboard
    // canvas positions are stored in the opening layout; resolve their image pixels against
    // the current viewport before every gesture instead of drawing at stale screen positions.
    private PointD Reproject(PointD planned) => Image(
        (planned.X - _plannedImageOrigin.X) / (_plannedImageUnit.X - _plannedImageOrigin.X),
        (planned.Y - _plannedImageOrigin.Y) / (_plannedImageUnit.Y - _plannedImageOrigin.Y));

    private bool OverCanvas(PointD stage)
    {
        var viewport = (FrameworkElement)_editor.PointerInputElement;
        Point local = Viewport(stage);
        return local.X >= 0 && local.Y >= 0 && local.X <= viewport.ActualWidth && local.Y <= viewport.ActualHeight;
    }

    private FrameworkElement ByAutomationId(string id) =>
        _host.Find<FrameworkElement>().First(element => element.IsVisible && AutomationProperties.GetAutomationId(element) == id);

    // ---- Storyboard -------------------------------------------------------------------------

    private TutorialStoryboard BuildStory()
    {
        var story = new TutorialStoryboard(Image(560, 290));
        story.Hold("open", 1.0);
        Rect part = Fixtures.ChecklistParts.SecondItem;
        switch (_feature)
        {
            case "rectangle":
            case "ellipse":
                ChooseTool(story);
                Drag(story, Image(part.X - 6, part.Y - 4), Image(part.Right + 8, part.Bottom + 4));
                break;
            case "arrow":
                ChooseTool(story);
                Drag(story, Image(330, 250), Image(part.Right + 6, part.Y + part.Height / 2));
                break;
            case "highlighter":
                ChooseTool(story);
                Drag(story, Image(56, part.Y + part.Height / 2), Image(part.Right + 4, part.Y + part.Height / 2));
                break;
            case "pen":
                ChooseTool(story);
                Drag(story, Image(56, part.Bottom + 2), Image(part.Right + 6, part.Bottom + 2));
                break;
            case "mosaic":
            case "blur":
                Rect email = Fixtures.ChecklistParts.Email;
                ChooseTool(story);
                Drag(story, Image(email.X, email.Y), Image(email.Right, email.Bottom));
                break;
            case "text":
                ChooseTool(story);
                story.Move("canvas", Image(200, 256)).Click("place").Hold("placed", 0.4);
                story.Hold("typing", 1.6).Hold("typed", 0.8);
                story.Move("away", Image(500, 280)).Click("commit");
                story.Hold("result", 0.6);
                break;
            case "step-number":
                ChooseTool(story);
                foreach (PointD at in new[] { Image(290, 62), Image(262, 144), Image(290, 216) })
                    story.Move("step-move", at, 12).Click("step").Hold("stepped", 0.5);
                break;
            case "crop":
                story.Move("command", Of(ByAutomationId("Capture410Crop"))).Click("crop-open").Hold("crop-mode", 1.2);
                story.Move("handle", Image(_image.PixelWidth, _image.PixelHeight));
                story.Motion("crop-drag", 30, story.End, Image(392, 252), pressed: true).Hold("crop-adjusted", 0.4);
                story.Move("handle2", Image(0, 0));
                story.Motion("crop-drag2", 24, story.End, Image(14, 44), pressed: true).Hold("crop-ready", 0.8);
                story.Move("apply-move", MeasureCropApply()).Click("crop-apply");
                break;
            case "rotate":
                // Rotation sits next to Crop in the edit commands; the captions name the keys the
                // tutorial's steps use for the same buttons.
                story.Move("command", Of(ByAutomationId("EditorRotateLeft"))).Click("rotate-left").Hold("rotated-left", 2.0);
                story.Move("command-right", Of(ByAutomationId("EditorRotateRight"))).Click("rotate-right").Hold("rotated-right", 1.4);
                break;
            case "save":
                story.Hold("review", 1.0).Hold("save-key", 0.3).Hold("saving", 0.8).Hold("closed", 0.6);
                break;
            case "copy":
                // Copy and close is the one finishing command (the owner's decision): save, copy and
                // close, then paste into the note app left on screen.
                story.Move("command", Of(ByAutomationId("EditorFlowDone"))).Click("copy").Hold("copying", 0.8).Hold("closed", 0.6);
                story.Move("note", new PointD(330, 300)).Click("note-click").Hold("paste-key", 0.6).Hold("pasted", 0.4);
                break;
        }

        return story.Finish("result", 11);
    }

    /// <summary>The crop commands exist only in crop mode; an identical throwaway editor finds Apply.</summary>
    private PointD MeasureCropApply()
    {
        var region = new RectD(0, 0, _image.PixelWidth, _image.PixelHeight);
        // The source chip sets the status line's height, so the probe carries the same source.
        var probe = new AnnotationEditorWindow(new FrozenFrame(_image, region, null, 0), region, _image,
            provenance: Fixtures.ChecklistSource);
        using var host = new OffscreenWindow(probe, ClientWidth, ClientHeight);
        ShortcutDefinition crop = ShortcutCatalog.Get(ShortcutScope.Annotation, "Crop");
        probe.Editor.HandleShortcut(crop.Key, crop.Modifiers);
        Stage.Pump(60);
        FrameworkElement apply = host.Find<FrameworkElement>().First(element => element.IsVisible
            && AutomationProperties.GetAutomationId(element) == "EditorFlowCropApply");
        PointD center = host.CenterOf(apply);
        return new PointD(ClientOrigin.X + center.X, ClientOrigin.Y + center.Y);
    }

    private void ChooseTool(TutorialStoryboard story) =>
        story.Move("tool-move", Of(_editor.ToolButton(_tool))).Click("tool").Hold("tool-chosen", 0.9);

    private void Drag(TutorialStoryboard story, PointD from, PointD to)
    {
        _dragStart = from;
        story.Move("canvas", from).Motion("drag", _feature == "pen" ? 40 : 34, from, to, pressed: true).Hold("dragged", 0.2);
    }

    // ---- Frames -----------------------------------------------------------------------------

    internal override void Draw(DrawingContext dc, TutorialFramePlan frame)
    {
        PointD cursor = frame.Cursor;
        if (frame.Segment is "canvas" or "drag" or "dragged" or "place" or "placed" or "typing" or "typed"
            or "away" or "commit" or "step-move" or "step" or "stepped" or "handle" or "crop-drag"
            or "crop-adjusted" or "handle2" or "crop-drag2" or "crop-ready"
            || frame.Segment == "result" && _tool is not (EditorTool.Select or EditorTool.Crop))
            cursor = Reproject(cursor);
        Apply(frame, ref cursor);
        Stage.DrawDesktop(dc);
        if (!_closed)
        {
            BitmapSource client = _host.Snapshot();
            Stage.DrawWindow(dc, client, ClientOrigin, _window.Title);
        }
        else DrawAfterClose(dc, frame);

        (string caption, string key) = Caption(frame);
        if (frame.Pressed) Stage.DrawClick(dc, cursor, frame.Progress);
        bool keyboardOnly = _feature is "save" && frame.Segment is not "open";
        if (!keyboardOnly) DrawPointer(dc, cursor);
        Stage.DrawCaption(dc, caption, key);
    }

    private void Apply(TutorialFramePlan frame, ref PointD cursor)
    {
        if (_closed) return;
        if (frame.Segment == "drag" && _feature == "pen")
        {
            double t = Stage.Ease(frame.Progress);
            cursor = new PointD(cursor.X, cursor.Y + Math.Sin(t * Math.PI * 3) * 14);
        }

        Point viewport = Viewport(cursor);
        switch (frame.Segment)
        {
            case "tool" when frame.IsLast:
                _editor.ToolButton(_tool).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "drag":
                if (frame.IsFirst) _editor.SimulatePointerDown(Viewport(Reproject(_dragStart)));
                _editor.SimulatePointerMove(viewport, leftPressed: true);
                if (frame.IsLast) _editor.SimulatePointerUp(viewport);
                break;
            case "place" when frame.IsLast:
                _editor.SimulatePointerDown(viewport);
                _editor.SimulatePointerUp(viewport);
                break;
            case "typing":
            case "typed":
                string full = L("배포 전 확인", "Check before release");
                _typed = frame.Segment == "typed" ? full : full[..Math.Max(1, full.Length * 2 / 3)];
                if (ActiveTextBox() is { } box) { box.Text = _typed; box.CaretIndex = box.Text.Length; }
                break;
            case "commit" when frame.IsLast:
            case "step" when frame.IsLast:
                _editor.SimulatePointerDown(viewport);
                _editor.SimulatePointerUp(viewport);
                if (frame.Segment == "step") _steps++;
                break;
            case "crop-open" when frame.IsLast:
                ((Button)ByAutomationId("Capture410Crop")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "crop-drag":
            case "crop-drag2":
                if (frame.IsFirst) _editor.SimulatePointerDown(Viewport(frame.Segment == "crop-drag" ? Image(_image.PixelWidth, _image.PixelHeight) : Image(0, 0)));
                _editor.SimulatePointerMove(viewport, leftPressed: true);
                if (frame.IsLast) _editor.SimulatePointerUp(viewport);
                break;
            case "crop-apply" when frame.IsLast:
                ((Button)ByAutomationId("EditorFlowCropApply")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                AnnotationDocument cropped = _editor.CreateCurrentImageSnapshot();
                if (Math.Abs(cropped.CanvasWidth - 378) > 1 || Math.Abs(cropped.CanvasHeight - 208) > 1)
                    throw new InvalidOperationException($"The crop missed its image-space corners: {cropped.CanvasWidth}x{cropped.CanvasHeight}.");
                break;
            case "rotate-left" when frame.IsLast:
                ((Button)ByAutomationId("EditorRotateLeft")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "rotate-right" when frame.IsLast:
                ((Button)ByAutomationId("EditorRotateRight")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "save-key":
                BeginCommit(() => Press(ShortcutScope.Annotation, "Save"));
                break;
            case "copy" when frame.IsLast:
                BeginCommit(() => ((Button)ByAutomationId("EditorFlowDone")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
                break;
            case "closed" when _commit is not null:
                _commit.TrySetResult(true);
                Stage.Pump(80);
                _closed = !_window.IsVisible;
                if (!_closed) throw new InvalidOperationException("The editor stayed open after a successful commit.");
                break;
            default:
                if (OverCanvas(cursor)) _editor.SimulatePointerMove(viewport, leftPressed: false);
                break;
        }

        Stage.Pump(15);
    }

    /// <summary>Presses the key the editor routes to <paramref name="command"/> (ShortcutCatalog).</summary>
    private void Press(ShortcutScope scope, string command)
    {
        ShortcutDefinition shortcut = ShortcutCatalog.Get(scope, command);
        if (!_editor.HandleShortcut(shortcut.Key, shortcut.Modifiers))
            throw new InvalidOperationException($"The editor did not handle {shortcut.Hint} ({command}).");
    }

    private void BeginCommit(Action command)
    {
        if (_commit is not null) return;
        _commit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> pending = _commit;
        _window.CommitRequested = _ => pending.Task;
        command();
    }

    private TextBox? ActiveTextBox() => _host.Find<TextBox>().FirstOrDefault(box => box.IsVisible
        && ((UIElement)_editor.PointerInputElement).IsAncestorOf(box));

    private void DrawPointer(DrawingContext dc, PointD cursor)
    {
        if (!_closed && OverCanvas(cursor))
        {
            EditorCursorKind kind = _editor.CanvasCursorKind;
            if (EditorCursorPolicy.IsCustom(kind))
            {
                int size = (int)Math.Round(32 * Stage.Scale);
                EditorCursorImage image = EditorCursors.Render(kind, size, _steps + 1, ColorRgba.FromRgb(0xEF, 0x44, 0x44), EditorCursorStyle.White);
                Stage.DrawCursorBitmap(dc, image.Bitmap, new PointD(image.HotSpotX / Stage.Scale, image.HotSpotY / Stage.Scale), cursor);
                return;
            }

            Stage.DrawPointer(dc, cursor, kind switch
            {
                EditorCursorKind.TextBeam => Stage.PointerKind.Beam,
                EditorCursorKind.Cross => Stage.PointerKind.Cross,
                EditorCursorKind.Move => Stage.PointerKind.Move,
                EditorCursorKind.ResizeNorthwestSoutheast => Stage.PointerKind.SizeNwse,
                EditorCursorKind.ResizeNortheastSouthwest => Stage.PointerKind.SizeNesw,
                EditorCursorKind.ResizeNorthSouth => Stage.PointerKind.SizeNs,
                EditorCursorKind.ResizeWestEast => Stage.PointerKind.SizeWe,
                _ => Stage.PointerKind.Arrow,
            });
            return;
        }

        Stage.DrawPointer(dc, cursor);
    }

    // ---- Other apps: the folder a save lands in, and the note a copy is pasted into ---------------

    private void DrawAfterClose(DrawingContext dc, TutorialFramePlan frame)
    {
        if (_feature == "save") DrawExplorer(dc);
        else DrawNote(dc, pasted: frame.Segment is "pasted" or "result");
    }

    private void DrawExplorer(DrawingContext dc)
    {
        var client = new Rect(120, 70, 720, 360);
        Stage.DrawWindowFrame(dc, client, "MyCapture");
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x26)), null, client);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x37)), null, new Rect(client.X + 12, client.Y + 10, client.Width - 24, 30), 6, 6);
        Stage.Text(dc, L("사진  ›  MyCapture", "Pictures  ›  MyCapture"), client.X + 26, client.Y + 15, 13, Brushes.White);
        Stage.Text(dc, L("이름", "Name"), client.X + 24, client.Y + 54, 12, Brushes.Gray);
        Stage.Text(dc, L("수정한 날짜", "Date modified"), client.X + 470, client.Y + 54, 12, Brushes.Gray);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x44)), null, new Rect(client.X + 12, client.Y + 74, client.Width - 24, 1));
        string name = QuickSaveNaming.BuildStem(QuickSaveNaming.DefaultPattern, new DateTimeOffset(2026, 10, 13, 9, 30, 12, TimeSpan.FromHours(9)),
            L("릴리스 준비", "Release prep")) + ".png";
        var row = new Rect(client.X + 12, client.Y + 82, client.Width - 24, 76);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x3D, 0x5C)), null, row, 4, 4);
        dc.DrawImage(_image, new Rect(row.X + 10, row.Y + 8, 120, 60));
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), 1.2), new Rect(row.X + 10 + 4.4, row.Y + 8 + 26.6, 43, 7));
        Stage.Text(dc, name, row.X + 146, row.Y + 28, 14, Brushes.White);
        Stage.Text(dc, "2026-10-13  09:30", client.X + 470, row.Y + 28, 13, Brushes.LightGray);
    }

    private void DrawNote(DrawingContext dc, bool pasted)
    {
        var client = new Rect(140, 70, 680, 360);
        Stage.DrawWindowFrame(dc, client, L("메모", "Notes"));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF7)), null, client);
        Fixtures.Text(dc, L("주간 보고", "Weekly report"), client.X + 28, client.Y + 22, 20, Brushes.Black, bold: true);
        Fixtures.Text(dc, L("체크리스트 진행 상황", "Checklist progress"), client.X + 28, client.Y + 58, 15, Brushes.DimGray);
        if (pasted)
        {
            var image = new Rect(client.X + 28, client.Y + 90, 480, 240);
            dc.DrawImage(_image, image);
            Rect item = Fixtures.ChecklistParts.SecondItem;
            double k = image.Width / _image.PixelWidth;
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), 3 * k), new Rect(image.X + item.X * k, image.Y + item.Y * k, item.Width * k, item.Height * k));
        }
        else dc.DrawLine(new Pen(Brushes.Black, 1.2), new Point(client.X + 28, client.Y + 96), new Point(client.X + 28, client.Y + 116));
    }

    // ---- Captions ---------------------------------------------------------------------------

    private (string Caption, string Key) Caption(TutorialFramePlan frame)
    {
        string segment = frame.Segment;
        string tool = _toolName;
        switch (_feature)
        {
            case "crop":
                string apply = Ui("Crop.Apply"), cancel = Ui("Text_BE876433993A");
                string applyKey = Hint(ShortcutScope.Crop, "Apply"), cancelKey = Hint(ShortcutScope.Crop, "Cancel");
                return segment switch
                {
                    "open" or "command" or "crop-open" => (L($"위쪽 명령의 {tool} 선택", $"Choose {tool} in the top commands"), _toolKey),
                    "crop-mode" => (L($"{tool} 모드: 명령 막대가 비율·{apply}·{cancel}로 바뀜", $"{tool} mode swaps the commands for ratio, {apply} and {cancel}"), _toolKey),
                    "apply-move" or "crop-apply" => (L($"{apply} 선택 또는 {applyKey}", $"Click {apply} or press {applyKey}"), applyKey),
                    "result" => (L($"{Ui("Tools_CropApplied")} · 적용 전에는 {cancelKey}로 취소", $"{Ui("Tools_CropApplied")} · {cancelKey} cancels a crop not yet applied"), ""),
                    _ => (L("모서리를 끌어 남길 영역 조절", "Drag the corners to the area to keep"), ""),
                };
            case "rotate":
                return segment switch
                {
                    "open" or "command" or "rotate-left" or "rotated-left" =>
                        (L($"{Ui("EditorUx_RotateLeft")}: 이미지를 왼쪽으로 90도", $"{Ui("EditorUx_RotateLeft")} turns the image 90°"), Hint(ShortcutScope.Annotation, "RotateLeft")),
                    _ => (L($"{Ui("EditorUx_RotateRight")}으로 되돌림", $"{Ui("EditorUx_RotateRight")} turns it back"), Hint(ShortcutScope.Annotation, "RotateRight")),
                };
            case "save":
                string save = Hint(ShortcutScope.Annotation, "Save");
                return segment switch
                {
                    "open" or "review" => (L("편집한 이미지 확인", "Review the edited image"), ""),
                    "save-key" or "saving" => (L($"{save}: 빠른 저장 폴더에 바로 저장", $"{save} saves straight to the quick-save folder"), save),
                    _ => (L($"PNG 저장됨 · 다른 위치는 {Hint(ShortcutScope.Annotation, "SaveAs")}", $"PNG saved · {Hint(ShortcutScope.Annotation, "SaveAs")} chooses another place"), ""),
                };
            case "copy":
                string copy = Hint(ShortcutScope.Annotation, "Copy"), copyLabel = Ui("EditorFlow_CopyAndClose");
                return segment switch
                {
                    "open" or "command" => (L($"{copyLabel} 선택 또는 {copy}", $"Click {copyLabel} or press {copy}"), copy),
                    "copy" or "copying" or "closed" => (L("저장하고 클립보드에 복사한 뒤 편집기 닫힘", "Saves, copies to the clipboard and closes the editor"), copy),
                    "note" or "note-click" => (L("붙여 넣을 앱으로 이동", "Switch to the app to paste into"), ""),
                    _ => (L("Ctrl+V로 이미지 붙여 넣기", "Paste the image with Ctrl+V"), "Ctrl+V"),
                };
            case "text":
                return segment switch
                {
                    "open" or "tool-move" or "tool" or "tool-chosen" => (L(tool + " 도구 선택", "Choose " + tool), _toolKey),
                    "canvas" or "place" or "placed" or "typing" or "typed" => (L("이미지를 클릭하고 내용 입력", "Click the image and type"), ""),
                    _ => (L("입력 상자 밖을 클릭하여 확정", "Click outside the box to commit"), ""),
                };
            case "step-number":
                return segment switch
                {
                    "open" or "tool-move" or "tool" or "tool-chosen" => (L(tool + " 도구 선택", "Choose " + tool), _toolKey),
                    "result" => (L("클릭한 순서대로 번호가 늘어남", "Numbers increase in click order"), ""),
                    _ => (L("설명할 위치를 차례로 클릭", "Click each spot in order"), ""),
                };
            default:
                return segment switch
                {
                    "open" or "tool-move" or "tool" or "tool-chosen" => (L(tool + " 도구 선택", "Choose " + tool), _toolKey),
                    "canvas" or "drag" or "dragged" => (_feature switch
                    {
                        "arrow" => L("시작점에서 끝점까지 드래그", "Drag from the start to the tip"),
                        "pen" => L("드래그하여 그리기", "Drag to draw"),
                        "highlighter" => L("강조할 부분을 드래그", "Drag over the text to emphasize"),
                        "mosaic" or "blur" => L("가릴 영역을 드래그", "Drag over the area to hide"),
                        _ => L("표시할 부분을 드래그", "Drag over the area to mark"),
                    }, ""),
                    // Drawn shapes: the words the editor's status line shows for the new shape.
                    _ => (_feature is "mosaic" or "blur"
                        ? L(tool + " 적용됨 · 범위 확인 후 저장", tool + " applied · check the area, then save")
                        : UiText.Format("EditorFlow_Added", tool), ""),
                };
        }
    }

    public override void Dispose()
    {
        _commit?.TrySetResult(false);
        _host.Dispose();
    }
}
