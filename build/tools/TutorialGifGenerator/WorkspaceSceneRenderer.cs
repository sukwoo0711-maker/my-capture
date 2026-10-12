using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using static TutorialDrawing;
using static TutorialFixtures;

internal sealed class WorkspaceSceneRenderer : ITutorialSceneRenderer
{
    internal static readonly string[] Features = ["library-open", "quick-access", "folder-create", "folder-rename", "library-move", "library-delete", "pin-clipboard", "pin-hide-all", "pin-click-through", "pin-restore", "pin-copy", "pin-save"];
    private readonly bool _ko;
    private readonly BitmapSource _desktop, _pin, _pinBar, _quick, _dialog, _tray;
    private readonly PointD _target, _end, _follow;
    public string Id { get; }
    public TutorialStoryboard Storyboard { get; }
    public IReadOnlyList<int> KeyFrames => TutorialFixtures.KeyFrames;
    public WorkspaceSceneRenderer(string featureId, string language)
    {
        Id = featureId; _ko = language == "ko"; _desktop = Desktop(_ko);
        _target = featureId switch { "folder-create" => new(322, 28), "folder-rename" => new(90, 152), "library-move" or "library-delete" => new(245, 139), "pin-copy" => new(191, 86), "pin-save" => new(271, 86), "quick-access" => new(370, 196), "library-open" => new(120, 241), _ => new(260, 150) };
        _end = featureId switch { "library-move" => new(230, 163), "folder-create" or "folder-rename" => new(442, 198), "library-delete" => new(391, 193), _ => new(465, 228) };
        _follow = featureId is "pin-hide-all" or "pin-restore" ? new(180, 130) : new(500, 267);
        Storyboard = Story(_target, _end, _follow);
        var pinImage = ClipboardTextRenderer.Render(TextCopySceneRenderer.Sample, false);
        var pinWindow = new PinWindow(PinContent.FromText(pinImage, TextCopySceneRenderer.Sample, false), new PinViewState(pinImage.PixelWidth, pinImage.PixelHeight, 1, 1, .1), 0, 0, dragInput: new FixturePinInput());
        _pin = WindowContent(pinWindow, pinImage.PixelWidth, pinImage.PixelHeight);
        _pinBar = WindowContent(new PinHoverBar(), 335);
        var quick = new QuickAccessWindow(pinImage, () => Task.FromResult(true), () => { }, () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("fixture.png"), false);
        _quick = WindowContent(quick, 344);
        var dialog = GalleryFolderEditDialog.ForName(L(_ko, featureId == "folder-rename" ? "빌드 기록" : "새 개발 기록", featureId == "folder-rename" ? "Build notes" : "New development notes"), featureId == "folder-create", _ => Task.CompletedTask);
        _dialog = WindowContent(dialog, 440);
        _tray = Tray(_ko, UiText.Get(featureId == "pin-restore" ? "Onboard_TrayReopenPin" : "Text_8739F6684453"));
    }
    public BitmapSource Render(TutorialFramePlan frame)
    {
        bool input = Input(frame), result = Result(frame);
        BitmapSource scene;
        string caption, key = "";
        if (Id == "library-open")
        {
            scene = input ? Library(false, false, false) : OnDesktop(_desktop, _tray, new Rect(35, 50, 275, 250));
            caption = input ? L(_ko, "보관함에서 캡처한 항목 확인", "Browse your captures in the library") : L(_ko, "트레이 메뉴에서 보관함 선택", "Choose Library from the tray menu");
        }
        else if (Id == "quick-access")
        {
            scene = DrawScene(dc =>
            {
                dc.DrawImage(_desktop, new Rect(0, 0, Width, SceneHeight));
                dc.DrawImage(_quick, new Rect(275, 145, 344, _quick.PixelHeight));
                if (result) Status(dc, UiText.Get("QuickAccess.Copied"));
            });
            caption = result ? L(_ko, "빠른 접근에서 이미지 복사 완료", "Image copied from Quick Access") : L(_ko, "캡처 후 빠른 접근의 복사 선택", "Choose Copy after a capture");
        }
        else if (Id.StartsWith("folder-", StringComparison.Ordinal))
        {
            scene = Library(result && Id == "folder-create", result && Id == "folder-rename", false);
            if (input && !result) scene = DialogOver(scene, _dialog);
            caption = Id == "folder-create" ? L(_ko, result ? "새 폴더 생성됨" : "새 폴더 선택 후 이름 입력", result ? "New folder created" : "Choose New folder and enter a name") : L(_ko, result ? "폴더 이름 변경됨" : "폴더를 선택하고 이름 변경", result ? "Folder renamed" : "Select the folder and rename it");
            if (!result && input) key = Id == "folder-rename" ? "F2" : "";
        }
        else if (Id == "library-move")
        {
            scene = Library(false, false, result);
            if (input && !result) scene = MoveDialog(scene);
            caption = result ? L(_ko, "선택한 항목이 대상 폴더로 이동됨", "Selected item moved to the destination folder") : L(_ko, "항목 선택 후 이동할 폴더 선택", "Select the item and its destination folder");
            if (input && !result) key = "M";
        }
        else if (Id == "library-delete")
        {
            scene = Library(false, false, false, result);
            if (input && !result) scene = DeleteDialog(scene);
            caption = result ? L(_ko, "선택한 항목이 휴지통으로 이동됨", "Selected item moved to the Recycle Bin") : L(_ko, "항목 삭제 확인 후 휴지통 이동", "Confirm moving the item to the Recycle Bin");
            if (input && !result) key = "Delete";
        }
        else
        {
            bool showPin = Id switch { "pin-clipboard" => input, "pin-hide-all" => !input || frame.Segment is "finish" or "done", "pin-restore" => input, _ => true };
            scene = DrawScene(dc =>
            {
                dc.DrawImage(_desktop, new Rect(0, 0, Width, SceneHeight));
                if (showPin)
                {
                    // Click-through keeps the pin visible; underlying input is illustrated below it.
                    dc.DrawImage(_pin, new Rect(175, 112, _pin.PixelWidth, _pin.PixelHeight));
                    if (Id != "pin-click-through" || !input) dc.DrawImage(_pinBar, new Rect(163, 66, 335, _pinBar.PixelHeight));
                }
                if (Id == "pin-click-through" && input)
                {
                    Text(dc, L(_ko, "아래 창에 입력", "Type in the window underneath"), 67, 242, 18, Brush("Text.Primary"));
                    dc.DrawLine(new Pen(Brush("Border.Focus"), 2), new Point(67, 264), new Point(475, 264));
                }
                if (Id == "pin-copy" && result)
                {
                    dc.DrawRoundedRectangle(Brush("Surface.Floating"), new Pen(Brush("Border.Control"), 1), new Rect(75, 215, 440, 78), 8, 8);
                    Text(dc, L(_ko, "메모에 이미지 붙여넣기", "Image pasted into a note"), 93, 226, 17, Brush("Text.Primary"));
                    dc.DrawImage(_pin, new Rect(350, 238, 150, 50));
                }
                if (Id == "pin-save" && result) Status(dc, L(_ko, "이미지 저장됨  ·  capture.png", "Image saved  ·  capture.png"));
                if (Id == "pin-restore" && !input) dc.DrawImage(_tray, new Rect(35, 42, 275, 250));
            });
            caption = Id switch
            {
                "pin-clipboard" => L(_ko, input ? "클립보드 텍스트가 핀으로 표시됨" : "클립보드 내용 고정 실행", input ? "Clipboard text appears as a pin" : "Run Pin clipboard content"),
                "pin-hide-all" => L(_ko, frame.Segment is "finish" or "done" ? "같은 기능을 다시 실행하면 표시됨" : input ? "모든 핀 숨김" : "모든 핀 숨기기 실행", frame.Segment is "finish" or "done" ? "Run it again to show the pins" : input ? "All pins hidden" : "Run Hide all pins"),
                "pin-click-through" => L(_ko, input ? "핀이 보이는 상태에서 아래 창 사용" : "핀의 클릭 통과 기능 실행", input ? "Use the window underneath the visible pin" : "Turn on click-through for the pin"),
                "pin-restore" => L(_ko, input ? "마지막으로 닫은 핀 복원됨" : "트레이에서 닫은 핀 다시 열기 선택", input ? "Last closed pin restored" : "Choose Reopen closed pin from the tray"),
                "pin-copy" => L(_ko, result ? "핀 이미지 복사 후 붙여넣기" : "핀 도구 모음에서 복사 선택", result ? "Copy the pin image and paste it" : "Choose Copy in the pin toolbar"),
                _ => L(_ko, result ? "핀 이미지 저장 완료" : "핀 도구 모음에서 저장 선택", result ? "Pin image saved" : "Choose Save in the pin toolbar"),
            };
            if (Id == "pin-copy" && input) key = result ? "Ctrl+V" : "Ctrl+C";
            if (Id == "pin-save" && input) key = "Ctrl+S";
        }
        return Frame(scene, frame, caption, key);
    }
    private BitmapSource Library(bool newFolder, bool renamed, bool moved, bool removed = false) => DrawScene(dc =>
    {
        // Condensed fixture follows GalleryWindow.xaml: header commands, folder rail, cards.
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        Text(dc, UiText.Get("Text_8739F6684453"), 20, 16, 20, Brush("Text.Primary"));
        var commands = new StackPanel { Orientation = Orientation.Horizontal };
        commands.Children.Add(Button(UiText.Get("Folder410.NewFolder")));
        commands.Children.Add(Button(UiText.Get("Folder410.MoveCaptures")));
        commands.Children.Add(Button(UiText.Get("LibrarySelection_Delete")));
        dc.DrawImage(Snapshot(commands, 355, 40), new Rect(269, 9, 355, 40));
        dc.DrawLine(new Pen(Brush("Border.Subtle"), 1), new Point(0, 58), new Point(640, 58));
        dc.DrawRectangle(Brush("Surface.Raised"), null, new Rect(0, 59, 166, 253));
        Text(dc, L(_ko, "전체 항목", "All items"), 20, 80, 17, Brush("Text.Primary"));
        Text(dc, UiText.Get("Folder410.Title"), 20, 113, 15, Brush("Text.Secondary"));
        Text(dc, L(_ko, renamed ? "빌드 기록" : "개발 기록", renamed ? "Build notes" : "Development notes"), 30, 146, 17, Brush("Text.Primary"));
        Text(dc, L(_ko, newFolder ? "새 개발 기록" : "참고 자료", newFolder ? "New development notes" : "References"), 30, 182, 17, moved ? Brush("Text.Primary") : Brush("Text.Secondary"));
        Text(dc, L(_ko, moved ? "참고 자료" : "전체 항목", moved ? "References" : "All items"), 185, 75, 17, Brush("Text.Secondary"));
        if (!removed)
        {
            dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush("Border.Accent"), 1.5), new Rect(187, 109, 208, 175), 8, 8);
            dc.DrawImage(_desktop, new Rect(196, 119, 190, 112));
            Text(dc, "Build completed", 202, 243, 17, Brush("Text.Primary"));
        }
        dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush("Border.Subtle"), 1), new Rect(410, 109, 208, 175), 8, 8);
        Text(dc, L(_ko, "녹화", "Recording"), 430, 158, 22, Brush("Text.Secondary"));
        Text(dc, "00:12", 430, 198, 20, Brush("Text.Primary"), true);
        if (removed) Status(dc, UiText.Format("LibrarySelection_Removed", 1, 0));
    });
    private static BitmapSource DialogOver(BitmapSource scene, BitmapSource dialog) => DrawScene(dc =>
    {
        dc.DrawImage(scene, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(130, 0, 0, 0)), null, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRoundedRectangle(Brush("Surface.Floating"), new Pen(Brush("Border.Control"), 1), new Rect(90, 73, 460, dialog.PixelHeight), 8, 8);
        dc.DrawImage(dialog, new Rect(100, 73, 440, dialog.PixelHeight));
    });
    private BitmapSource MoveDialog(BitmapSource scene)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = UiText.Get("Folder410.Destination"), FontSize = 17, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new ListBox { ItemsSource = new[] { L(_ko, "개발 기록", "Development notes"), L(_ko, "참고 자료", "References") }, SelectedIndex = 1, MinHeight = 80 });
        panel.Children.Add(Button(UiText.Get("Folder410.MoveCaptures"), true));
        return DialogOver(scene, Snapshot(panel, 440));
    }
    private BitmapSource DeleteDialog(BitmapSource scene)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = UiText.Format("LibrarySelection_DeleteConfirm", 1), FontSize = 17, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        row.Children.Add(Button(UiText.Get("LibrarySelection_Delete"))); row.Children.Add(Button(UiText.Get("Library.Action.Cancel"), true)); panel.Children.Add(row);
        return DialogOver(scene, Snapshot(panel, 440));
    }
    private static void Status(DrawingContext dc, string text)
    {
        dc.DrawRoundedRectangle(Brush("Surface.Floating"), new Pen(Brush("Border.Control"), 1), new Rect(30, 265, 580, 38), 7, 7);
        Text(dc, text, 43, 275, 16, Brush("Text.Primary"));
    }
    public void Dispose() { }
    private sealed class FixturePinInput : IPinDragInput
    {
        public ModifierKeys Modifiers => ModifierKeys.None;
        public Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner) => new(0, 0);
        public (int X, int Y) CursorPosition => (0, 0);
        public bool LeftButtonPressed(MouseEventArgs args) => false;
        public bool TryCapture(PinWindow owner) => false;
        public bool HasCapture(PinWindow owner) => false;
        public void Release(PinWindow owner) { }
    }
}
