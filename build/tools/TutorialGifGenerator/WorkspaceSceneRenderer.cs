using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using static TutorialDrawing;
using static TutorialFixtures;

/// <summary>Scripted fixture transactions; product content is measured offscreen and never shown or clicked.</summary>
internal sealed class WorkspaceSceneRenderer : ITutorialSceneRenderer
{
    internal static readonly string[] Features = ["library-open", "quick-access", "folder-create", "folder-rename", "library-move", "library-delete", "pin-clipboard", "pin-hide-all", "pin-click-through", "pin-restore", "pin-copy", "pin-save"];
    private sealed record Cue(string State, string Caption, string Key);
    private readonly Dictionary<string, Cue> _cues = [];
    private readonly List<Window> _owned = [];
    private readonly bool _ko;
    private readonly BitmapSource _desktop, _pinImage, _pin, _bar, _quick, _tray, _pinMenu, _commands;
    private readonly Rect _pinRect, _trayRect, _menuRect, _quickRect, _barRect;
    private readonly PointD _trayTarget, _menuTarget, _quickCopy, _barSave, _newFolder;
    private readonly GalleryFolderEditDialog? _nameDialog, _moveDialog;
    private readonly ThemedDialog? _deleteDialog;
    private readonly FrameworkElement? _dialogRoot;
    private readonly Rect _dialogRect;
    private readonly PointD _nameInput, _confirm, _destination;
    private readonly ListBox? _destinations;
    private AnnotationEditorControl? _editor;
    private BitmapSource? _editorBitmap;
    private int _sequence;
    private static readonly PointD Start = new(560, 275), Item = new(288, 170), Folder = new(88, 154), Note = new(111, 233), PinNote = new(111, 281);
    public string Id { get; }
    public TutorialStoryboard Storyboard { get; } = new();
    public IReadOnlyList<int> KeyFrames => Storyboard.Frames.Select((frame, index) => (frame, index))
        .Where(pair => pair.frame.DelayCentiseconds > 4).Select(pair => pair.index).ToArray();
    private string Name => L(_ko, Id == "folder-rename" ? "빌드 기록" : "새 개발 기록", Id == "folder-rename" ? "Build notes" : "New development notes");

    public WorkspaceSceneRenderer(string featureId, string language)
    {
        if (!Features.Contains(featureId)) throw new ArgumentOutOfRangeException(nameof(featureId));
        Id = featureId; _ko = language == "ko"; _desktop = Desktop(_ko);
        var commands = new StackPanel { Orientation = Orientation.Horizontal };
        Button newFolder = Button(UiText.Get("Folder410.NewFolder"));
        commands.Children.Add(newFolder); commands.Children.Add(Button(UiText.Get("Folder410.MoveCaptures"))); commands.Children.Add(Button(UiText.Get("LibrarySelection_Delete")));
        _commands = Snapshot(commands, 355, 40);
        _newFolder = Add(Center(newFolder, commands), new Point(269, 9));
        _pinImage = ClipboardTextRenderer.Render(TextCopySceneRenderer.Sample, false);
        var pin = new PinWindow(PinContent.FromText(_pinImage, TextCopySceneRenderer.Sample, false),
            new PinViewState(_pinImage.PixelWidth, _pinImage.PixelHeight, 1, 1, .1), 0, 0, dragInput: new FixturePinInput());
        _pinMenu = SnapshotPinMenu(pin, out Point menuPoint);
        _menuRect = new Rect(246, 83, 280, _pinMenu.PixelHeight);
        _menuTarget = Add(menuPoint, _menuRect.Location);
        _pin = WindowContent(pin, _pinImage.PixelWidth, _pinImage.PixelHeight);
        double pinHeight = Math.Min(98, 310d * _pin.PixelHeight / _pin.PixelWidth);
        _pinRect = new Rect(175, Id == "pin-copy" ? 104 : 141, 310, pinHeight);
        var bar = new PinHoverBar();
        FrameworkElement barRoot = (FrameworkElement)bar.Content;
        _bar = Snapshot(barRoot, 335);
        _barRect = new Rect(158, Id == "pin-copy" ? Math.Max(4, _pinRect.Top - _bar.PixelHeight - 6) : 78, 335, _bar.PixelHeight);
        _barSave = Add(Center(bar.SaveButton, barRoot), _barRect.Location);
        bar.Content = null; bar.Close();
        var quick = new QuickAccessWindow(_pinImage, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("capture.png"), false);
        FrameworkElement quickRoot = (FrameworkElement)quick.Content;
        _quick = Snapshot(quickRoot, 344);
        _quickRect = new Rect(278, 13, 344, _quick.PixelHeight);
        Button copy = Descendants(quickRoot).OfType<Button>().Single(button => AutomationProperties.GetName(button) == UiText.Get("QuickAccess.Copy"));
        _quickCopy = Add(Center(copy, quickRoot), _quickRect.Location);
        quick.Content = null; quick.Close();
        _tray = SnapshotTray(Id == "pin-restore", out Point trayPoint);
        _trayRect = new Rect(12, 8, 250, _tray.PixelHeight);
        _trayTarget = Add(trayPoint, _trayRect.Location);

        if (Id is "folder-create" or "folder-rename")
        {
            _nameDialog = GalleryFolderEditDialog.ForName(Id == "folder-create" ? string.Empty : L(_ko, "개발 기록", "Development notes"), Id == "folder-create", _ => Task.CompletedTask);
            _owned.Add(_nameDialog); _dialogRoot = (FrameworkElement)_nameDialog.Content;
            BitmapSource measured = Snapshot(_dialogRoot, 440);
            _dialogRect = DialogBounds(measured);
            _nameInput = Add(Center(_nameDialog.Input, _dialogRoot), _dialogRect.Location);
            _confirm = Add(Center(_nameDialog.SaveButton, _dialogRoot), _dialogRect.Location);
        }
        else if (Id == "library-move")
        {
            _destinations = new ListBox { ItemsSource = new[] { UiText.Get("Folder410.Unassigned"), L(_ko, "개발 기록", "Development notes"), L(_ko, "참고 자료", "References") },
                SelectedIndex = 0, MinHeight = 120, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            // The product dialog's constructor receives inert fixture choices; no CaptureQueue or storage is created.
            _moveDialog = (GalleryFolderEditDialog)Activator.CreateInstance(typeof(GalleryFolderEditDialog), BindingFlags.Instance | BindingFlags.NonPublic,
                null, ["Folder410.MoveCaptures", "Folder410.Destination", _destinations, "Folder410.MoveCaptures", "Folder410.InvalidMove", (Func<Task>)(() => Task.CompletedTask), null], null)!;
            _owned.Add(_moveDialog); _dialogRoot = (FrameworkElement)_moveDialog.Content;
            BitmapSource measured = Snapshot(_dialogRoot, 440);
            _dialogRect = DialogBounds(measured);
            _confirm = Add(Center(_moveDialog.SaveButton, _dialogRoot), _dialogRect.Location);
            var destination = (ListBoxItem)_destinations.ItemContainerGenerator.ContainerFromIndex(2);
            _destination = Add(Center(destination, _dialogRoot), _dialogRect.Location);
        }
        else if (Id == "library-delete")
        {
            _deleteDialog = new ThemedDialog(UiText.Format("LibrarySelection_DeleteConfirm", 1), UiText.Get("LibrarySelection_Delete"), MessageBoxButton.OKCancel, MessageBoxResult.Cancel);
            _owned.Add(_deleteDialog); _dialogRoot = (FrameworkElement)_deleteDialog.Content;
            BitmapSource measured = Snapshot(_dialogRoot, 440);
            _dialogRect = DialogBounds(measured);
            Button confirm = Descendants(_dialogRoot).OfType<Button>().Single(button => Equals(button.Content, UiText.Get("Dialog.OK")));
            _confirm = Add(Center(confirm, _dialogRoot), _dialogRect.Location);
        }
        BuildStoryboard();
    }

    private void Hold(string state, string ko, string en, int delay, PointD cursor, string key = "")
    {
        string segment = $"{++_sequence}:{state}";
        _cues.Add(segment, new(state, L(_ko, ko, en), key)); Storyboard.Hold(segment, delay, cursor);
    }
    private void Move(string state, string ko, string en, PointD from, PointD to, string key = "", bool pressed = false, int? frames = null)
    {
        double distance = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        int count = frames ?? Math.Clamp((int)Math.Ceiling(distance * 1.5 / 22), 12, 36);
        string segment = $"{++_sequence}:{state}";
        _cues.Add(segment, new(state, L(_ko, ko, en), key)); Storyboard.Motion(segment, count, from, to, pressed);
    }
    private void Click(string state, string ko, string en, PointD target) => Move(state, ko, en, target, target, pressed: true, frames: 6);
    private void Finish(string state, string ko, string en, PointD cursor)
    {
        int remaining = 1120 - Storyboard.DurationMs / 10;
        if (remaining < 100 || Storyboard.Frames.Count >= 160) throw new InvalidOperationException($"Storyboard budget: {Id}");
        Hold(state, ko, en, remaining, cursor);
    }

    private void BuildStoryboard()
    {
        switch (Id)
        {
            case "library-open":
                Hold("tray", "트레이 메뉴의 라이브러리 선택", "Choose Library in the tray menu", 90, Start);
                Move("tray", "라이브러리 항목으로 이동", "Point to Library", Start, _trayTarget); Click("tray", "라이브러리 선택", "Click Library", _trayTarget);
                Hold("library", "캡처한 이미지와 녹화 확인", "Browse images and recordings", 110, _trayTarget);
                Move("library", "열 이미지 선택", "Select the image to open", _trayTarget, Item); Click("library", "이미지 항목 선택", "Click the image item", Item);
                Hold("selected", "Enter로 이미지 열기", "Press Enter to open the image", 90, Item, "Enter");
                Move("editor-transition", "이미지 편집기로 열기", "Open in the image editor", Item, new(310, 200), frames: 8);
                Finish("editor", "이미지 편집기에서 결과 확인", "Review the image in the editor", new(310, 200)); break;
            case "quick-access":
                Hold("quick", "캡처 후 빠른 실행 카드 확인", "Find the quick-access card after capture", 85, Start);
                Hold("quick-active", "Alt+Q로 빠른 실행 활성화", "Press Alt+Q to activate Quick Access", 85, Start, "Alt+Q");
                Hold("quick-copied", "C로 이미지 복사", "Press C to copy the image", 95, Start, "C");
                Move("quick-copied", "붙여 넣을 메모로 이동", "Move to the destination note", Start, Note); Click("quick-copied", "메모 입력 영역 선택", "Click the note input area", Note);
                Hold("quick-pasted", "Ctrl+V로 이미지 붙여넣기", "Press Ctrl+V to paste the image", 100, Note, "Ctrl+V");
                Finish("quick-pasted", "복사한 이미지가 메모에 붙여넣어짐", "The copied image appears in the note", Note); break;
            case "folder-create":
            case "folder-rename":
                bool create = Id == "folder-create"; PointD target = create ? _newFolder : Folder;
                Hold("library", create ? "라이브러리에서 새 폴더 선택" : "이름을 바꿀 폴더 선택", create ? "Choose New folder in the library" : "Select the folder to rename", 80, Start);
                Move("library", create ? "새 폴더 버튼으로 이동" : "폴더로 이동", create ? "Point to New folder" : "Point to the folder", Start, target);
                Click("library", create ? "새 폴더 선택" : "폴더 선택", create ? "Click New folder" : "Click the folder", target);
                Hold("name-initial", create ? "빈 이름 입력창 확인" : "F2로 이름 변경 · 기존 이름 선택", create ? "Start with an empty name field" : "Press F2; the current name is selected", 95, target, create ? "" : "F2");
                if (create)
                {
                    Move("name-initial", "이름 입력 영역으로 이동", "Point to the name field", target, _nameInput);
                    Click("name-initial", "이름 입력 영역 선택", "Click the name field", _nameInput);
                }
                Move("name-typing", "새 이름 입력", "Enter the new name", create ? _nameInput : target, _confirm, frames: 24);
                Hold("name-typed", "입력한 이름 확인", "Review the entered name", 90, _confirm);
                Click("name-typed", create ? "만들기 선택" : "저장 선택", create ? "Click Create" : "Click Save", _confirm);
                Finish("name-result", create ? "입력한 이름으로 새 폴더 생성됨" : "선택한 폴더의 이름 변경됨", create ? "The new folder has the entered name" : "The selected folder has its new name", _confirm); break;
            case "library-move":
                Hold("library", "이동할 캡처 선택", "Select the capture to move", 85, Start);
                Move("library", "캡처 항목으로 이동", "Point to the capture", Start, Item); Click("library", "캡처 선택", "Select the capture", Item);
                Hold("move-initial", "M으로 이동 창 열기", "Press M to open Move", 90, Item, "M");
                Move("move-initial", "목적 폴더로 이동", "Point to the destination folder", Item, _destination); Click("move-initial", "참고 자료 폴더 선택", "Select References", _destination);
                Hold("move-selected", "선택한 목적 폴더 확인", "Check the selected destination", 80, _destination);
                Move("move-selected", "이동 버튼으로 이동", "Point to Move", _destination, _confirm); Click("move-selected", "이동 선택", "Click Move", _confirm);
                Hold("move-removed", "기존 목록에서 이동 완료", "The capture has left the previous list", 70, _confirm);
                Move("move-removed", "참고 자료 폴더로 이동", "Point to References in the folder list", _confirm, new(88, 192)); Click("move-removed", "참고 자료 폴더 열기", "Open References", new(88, 192));
                Finish("move-result", "참고 자료 폴더에서 이동한 캡처 확인", "Find the capture in References", new(88, 192)); break;
            case "library-delete":
                Hold("library", "삭제할 캡처 선택", "Select the capture to delete", 90, Start);
                Move("library", "캡처 항목으로 이동", "Point to the capture", Start, Item); Click("library", "캡처 선택", "Select the capture", Item);
                Hold("delete-dialog", "Del로 삭제 확인 창 열기", "Press Del to review deletion", 110, Item, "Del");
                Move("delete-dialog", "삭제 내용을 확인한 뒤 확인 선택", "Review deletion and point to OK", Item, _confirm); Click("delete-dialog", "확인으로 삭제 승인", "Click OK to confirm deletion", _confirm);
                Finish("delete-result", "목록에서 제외됨 · 휴지통에서 복원 가능", "Removed from the list; recoverable in Recycle Bin", _confirm); break;
            case "pin-clipboard":
                Hold("source-selected", "복사할 텍스트 선택", "Select the text to copy", 100, new(110, 180));
                Hold("source-copied", "Ctrl+C로 텍스트 복사", "Press Ctrl+C to copy the text", 100, new(110, 180), "Ctrl+C");
                Hold("source-copied", "설정한 클립보드 핀 단축키 실행", "Use your assigned Clipboard pin shortcut", 100, new(110, 180));
                Move("pin-appear", "복사한 텍스트가 핀으로 표시됨", "The copied text appears as a pin", new(110, 180), new(135, 185), frames: 8);
                Move("pin", "핀을 드래그할 위치로 이동", "Point to the pin to move it", new(135, 185), new(250, 183));
                Move("pin-drag", "핀을 드래그하여 이동", "Drag the pin to move it", new(250, 183), new(370, 230), pressed: true, frames: 28);
                Hold("pin-moved", "이동한 핀 확인", "Review the moved pin", 100, new(370, 230));
                Hold("pin-closed", "Esc로 핀 닫기", "Press Esc to close the pin", 90, new(370, 230), "Esc");
                Finish("pin-closed", "핀 닫힘", "Pin closed", new(370, 230)); break;
            case "pin-hide-all":
                Hold("pins", "화면에 띄운 두 개의 핀", "Two pins on screen", 150, Start);
                Move("pins", "설정한 모든 핀 숨기기 단축키 실행", "Use your assigned Hide all pins shortcut", Start, new(590, 278));
                Hold("pins-hidden", "두 핀이 함께 숨겨짐", "Both pins are hidden", 190, new(590, 278));
                Hold("pins-returned", "같은 단축키를 다시 실행", "Use the same shortcut again", 150, new(590, 278));
                Finish("pins-returned", "두 핀이 다시 표시됨", "Both pins are visible again", new(590, 278)); break;
            case "pin-click-through":
                Hold("underlay-pin", "핀 아래 입력창 확인", "A note input area underneath the pin", 90, Start);
                Move("underlay-pin", "핀으로 이동", "Point to the pin", Start, new(270, 180)); Click("underlay-pin", "핀에서 우클릭", "Right-click the pin", new(270, 180));
                Hold("pin-menu", "우클릭 메뉴의 클릭 통과 선택", "Choose Click through in the pin menu", 100, new(270, 180));
                Move("pin-menu", "클릭 통과 항목으로 이동", "Point to Click through", new(270, 180), _menuTarget); Click("pin-menu", "클릭 통과 켜기", "Enable Click through", _menuTarget);
                Hold("through", "핀이 보이는 상태로 클릭 통과 켜짐", "Click through is on; the pin stays visible", 90, _menuTarget);
                Move("through", "핀과 겹친 아래 입력창 클릭", "Point to the input underneath the pin", _menuTarget, new(270, 180)); Click("through", "핀을 통과하여 아래 입력창 선택", "Click through the pin into the note", new(270, 180));
                Move("underlay-typing", "아래 입력창에 내용 입력", "Type in the underlying note", new(270, 180), new(310, 195), frames: 18);
                Finish("underlay-typed", "핀은 유지됨 · 아래 앱에 입력됨", "The pin remains; the note receives the text", new(310, 195)); break;
            case "pin-restore":
                Hold("pin", "닫았다가 다시 열 핀", "The pin to close and reopen", 110, Start);
                Hold("pin-closed", "Esc로 핀 닫기", "Press Esc to close the pin", 100, Start, "Esc");
                Hold("restore-tray", "트레이 메뉴에서 핀 다시 열기", "Find Reopen pin in the tray menu", 100, Start);
                Move("restore-tray", "핀 다시 열기 항목으로 이동", "Point to Reopen pin", Start, _trayTarget); Click("restore-tray", "핀 다시 열기 선택", "Click Reopen pin", _trayTarget);
                Finish("pin", "닫은 핀이 같은 내용과 위치로 복원됨", "The same pin returns to its previous position", _trayTarget); break;
            case "pin-copy":
                PointD copyTarget = new(_pinRect.X + _pinRect.Width / 3, _pinRect.Y + _pinRect.Height / 2);
                Hold("pin", "복사할 핀 선택", "Select the pin to copy", 90, Start);
                Move("pin", "핀으로 이동", "Point to the pin", Start, copyTarget); Click("pin", "핀 선택", "Focus the pin", copyTarget);
                Hold("pin-copied", "Ctrl+C로 핀 이미지 복사", "Press Ctrl+C to copy the pin image", 120, copyTarget, "Ctrl+C");
                Move("paste-ready", "붙여 넣을 메모로 이동", "Move to the destination note", copyTarget, PinNote); Click("paste-ready", "메모 입력 영역 선택", "Click the note input area", PinNote);
                Hold("pasted", "Ctrl+V로 이미지 붙여넣기", "Press Ctrl+V to paste the image", 100, PinNote, "Ctrl+V");
                Finish("pasted", "핀 이미지를 메모에 붙여넣음 · 핀 유지", "The pin image is pasted; the pin stays open", PinNote); break;
            case "pin-save":
                Hold("pin", "저장할 핀의 도구 모음 확인", "Find Save in the pin toolbar", 110, Start);
                Move("pin", "저장 버튼으로 이동", "Point to Save", Start, _barSave); Click("pin", "저장 선택 · Ctrl+S도 사용 가능", "Click Save; Ctrl+S also works", _barSave);
                Hold("pin-saving", "빠른 저장 폴더에 PNG 저장", "Save a PNG in the quick-save folder", 110, _barSave);
                Move("pin-saved", "새로 저장한 PNG 파일 확인", "Review the new PNG file", _barSave, new(93, 266));
                Finish("pin-saved", "빠른 저장 폴더에 이미지 저장됨", "The image is saved in the quick-save folder", new(93, 266)); break;
        }
    }

    public BitmapSource Render(TutorialFramePlan frame)
    {
        Cue cue = _cues[frame.Segment];
        string state = cue.State;
        BitmapSource scene = state switch
        {
            "tray" or "restore-tray" => OnDesktop(_desktop, _tray, _trayRect),
            "library" or "selected" => Library(selected: state == "selected"),
            "name-initial" or "name-typing" or "name-typed" => NameScene(state, frame.Progress),
            "name-result" => Library(newFolder: Id == "folder-create", renamed: Id == "folder-rename", selectedFolder: true),
            "move-initial" or "move-selected" => MoveScene(state == "move-selected"),
            "move-removed" => Library(movedAway: true),
            "move-result" => Library(moved: true, selected: true, selectedFolder: true),
            "delete-dialog" => Overlay(Library(selected: true), Snapshot(_dialogRoot!, 440), _dialogRect),
            "delete-result" => Library(removed: true),
            "editor-transition" => Blend(Library(selected: true), EditorScene(), frame.Progress),
            "editor" => EditorScene(),
            "quick" or "quick-active" or "quick-copied" or "quick-pasted" => QuickScene(state),
            "source-selected" or "source-copied" => SourceScene(state == "source-selected"),
            "pin-appear" => Blend(SourceScene(false), PinScene("pin"), frame.Progress),
            _ => PinScene(state, frame.Progress),
        };
        return Compose(scene, cue.Caption, cue.Key, frame.Cursor, frame.Cursor,
            frame.Pressed && state != "pin-drag" ? frame.Progress : 0);
    }

    private BitmapSource NameScene(string state, double progress)
    {
        TextBox input = (TextBox)_nameDialog!.Input;
        input.Text = state == "name-initial" ? (Id == "folder-create" ? "" : L(_ko, "개발 기록", "Development notes"))
            : state == "name-typing" ? Name[..Math.Max(1, (int)Math.Ceiling(Name.Length * progress))] : Name;
        input.Select(0, Id == "folder-rename" && state == "name-initial" ? input.Text.Length : 0);
        BitmapSource dialog = Snapshot(_dialogRoot!, 440);
        return DrawScene(dc =>
        {
            DrawOverlay(dc, Library(selectedFolder: Id == "folder-rename"), dialog, _dialogRect);
            if (Id == "folder-rename" && state == "name-initial")
            {
                Point top = SnapshotPoint(input, new Point(6, 7), _dialogRoot!);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(65, 124, 108, 255)), null,
                    new Rect(_dialogRect.X + top.X, _dialogRect.Y + top.Y, Math.Min(320, input.Text.Length * 15), 22));
            }
        });
    }
    private BitmapSource MoveScene(bool selected)
    {
        _destinations!.SelectedIndex = selected ? 2 : 0;
        return Overlay(Library(selected: true), Snapshot(_dialogRoot!, 440), _dialogRect);
    }
    private BitmapSource EditorScene()
    {
        if (_editorBitmap is not null) return _editorBitmap;
        RectD region = new(0, 0, _desktop.PixelWidth, _desktop.PixelHeight);
        _editor = new AnnotationEditorControl(new FrozenFrame(_desktop, region, null, 0), region, _desktop);
        BitmapSource editor = Snapshot(_editor, 864, 421);
        return _editorBitmap = DrawScene(dc => dc.DrawImage(editor, new Rect(0, 0, Width, SceneHeight)));
    }
    private BitmapSource QuickScene(string state) => DrawScene(dc =>
    {
        dc.DrawImage(_desktop, new Rect(0, 0, Width, SceneHeight));
        DrawNote(dc, state == "quick-pasted", new Rect(42, 176, 552, 127));
        dc.DrawImage(_quick, _quickRect);
        if (state is "quick-active" or "quick-copied")
            dc.DrawRoundedRectangle(null, new Pen(Brush("Border.Focus"), 2), new Rect(_quickCopy.X - 23, _quickCopy.Y - 25, 46, 50), 4, 4);
        if (state is "quick-copied" or "quick-pasted") Text(dc, UiText.Get("QuickAccess.Copied"), 293, 27, 13, Brush("Text.Primary"));
    });
    private BitmapSource SourceScene(bool selected) => DrawScene(dc =>
    {
        dc.DrawImage(_desktop, new Rect(0, 0, Width, SceneHeight));
        if (selected) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 124, 108, 255)), null, new Rect(60, 136, 304, 74));
        else Text(dc, L(_ko, "텍스트 복사됨", "Text copied"), 66, 235, 17, Brush("Text.Primary"));
    });
    private BitmapSource PinScene(string state, double progress = 1) => DrawScene(dc =>
    {
        dc.DrawImage(_desktop, new Rect(0, 0, Width, SceneHeight));
        bool underlay = state is "underlay-pin" or "pin-menu" or "through" or "underlay-typing" or "underlay-typed";
        if (underlay)
        {
            dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush(state is "underlay-typing" or "underlay-typed" ? "Border.Focus" : "Border.Control"), 2), new Rect(95, 129, 450, 169), 7, 7);
            Text(dc, L(_ko, "메모 입력", "Note input"), 110, 105, 17, Brush("Text.Primary"));
            if (state is "underlay-typing" or "underlay-typed")
            {
                string text = L(_ko, "확인 완료", "Confirmed");
                int count = state == "underlay-typed" ? text.Length : Math.Max(1, (int)Math.Ceiling(text.Length * progress));
                Text(dc, text[..count], 113, 257, 20, Brush("Text.Primary"));
            }
        }
        if (state is "pins" or "pins-returned")
        {
            dc.DrawImage(_pin, new Rect(60, 137, 245, _pinRect.Height * 245 / 310));
            dc.DrawImage(_pin, new Rect(330, 205, 245, _pinRect.Height * 245 / 310));
        }
        else if (state is not ("pins-hidden" or "pin-closed"))
        {
            Rect pin = _pinRect;
            if (state == "pin-drag") pin.Offset(120 * TutorialStoryboard.SmoothStep(progress), 47 * TutorialStoryboard.SmoothStep(progress));
            if (state == "pin-moved") pin.Offset(120, 47);
            dc.DrawImage(_pin, pin);
            if (!underlay && state is not ("paste-ready" or "pasted" or "pin-saved"))
            {
                Rect bar = _barRect;
                if (state == "pin-drag") bar.Offset(120 * TutorialStoryboard.SmoothStep(progress), 47 * TutorialStoryboard.SmoothStep(progress));
                if (state == "pin-moved") bar.Offset(120, 47);
                dc.DrawImage(_bar, bar);
            }
        }
        if (state == "pin-menu") dc.DrawImage(_pinMenu, _menuRect);
        if (state is "pin-copied" or "pin-saving") Status(dc, L(_ko, state == "pin-copied" ? "이미지 복사됨" : "이미지 저장 중", state == "pin-copied" ? "Image copied" : "Saving image"));
        if (state is "paste-ready" or "pasted") DrawNote(dc, state == "pasted", new Rect(42, 207, 552, 96));
        if (state == "pin-saved")
        {
            dc.DrawRoundedRectangle(Brush("Surface.Floating"), new Pen(Brush("Border.Control"), 1), new Rect(42, 238, 552, 66), 7, 7);
            Text(dc, L(_ko, "빠른 저장 폴더", "Quick-save folder"), 56, 245, 14, Brush("Text.Secondary"));
            dc.DrawImage(_pinImage, new Rect(56, 268, 112, 29));
            Text(dc, "capture.png", 185, 270, 17, Brush("Text.Primary"));
            Text(dc, L(_ko, "저장됨", "Saved"), 460, 270, 16, Brush("Text.Primary"));
        }
    });
    private void DrawNote(DrawingContext dc, bool pasted, Rect bounds)
    {
        dc.DrawRoundedRectangle(Brush("Surface.Floating"), new Pen(Brush("Border.Control"), 1), bounds, 7, 7);
        Text(dc, L(_ko, "메모", "Note"), bounds.X + 14, bounds.Y + 8, 14, Brush("Text.Secondary"));
        if (pasted)
        {
            double scale = Math.Min(Math.Min(310, bounds.Width - 32) / _pinImage.PixelWidth,
                (bounds.Height - 34) / _pinImage.PixelHeight);
            dc.DrawImage(_pinImage, new Rect(bounds.X + 16, bounds.Y + 28,
                _pinImage.PixelWidth * scale, _pinImage.PixelHeight * scale));
        }
        else dc.DrawLine(new Pen(Brush("Text.Primary"), 1.5), new Point(bounds.X + 16, bounds.Y + 32), new Point(bounds.X + 16, bounds.Y + 49));
    }
    private BitmapSource Library(bool newFolder = false, bool renamed = false, bool moved = false, bool removed = false, bool selected = false, bool selectedFolder = false, bool movedAway = false) => DrawScene(dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        Text(dc, UiText.Get("Text_8739F6684453"), 20, 16, 20, Brush("Text.Primary"));
        dc.DrawImage(_commands, new Rect(269, 9, 355, 40));
        dc.DrawLine(new Pen(Brush("Border.Subtle"), 1), new Point(0, 58), new Point(640, 58));
        dc.DrawRectangle(Brush("Surface.Raised"), null, new Rect(0, 59, 166, 253));
        Text(dc, L(_ko, "전체 항목", "All items"), 20, 80, 17, Brush("Text.Primary"));
        Text(dc, UiText.Get("Folder410.Title"), 20, 113, 15, Brush("Text.Secondary"));
        if (selectedFolder) dc.DrawRoundedRectangle(Brush("Surface.Hover"), null, new Rect(15, newFolder || moved ? 176 : 141, 143, 31), 4, 4);
        RailText(dc, L(_ko, renamed ? "빌드 기록" : "개발 기록", renamed ? "Build notes" : "Development notes"), new Rect(25, 146, 133, 27));
        RailText(dc, L(_ko, newFolder ? "새 개발 기록" : "참고 자료", newFolder ? "New development notes" : "References"), new Rect(25, 182, 133, 27));
        Text(dc, L(_ko, moved ? "참고 자료" : newFolder ? "새 개발 기록" : renamed ? "빌드 기록" : "전체 항목", moved ? "References" : newFolder ? "New development notes" : renamed ? "Build notes" : "All items"), 185, 75, 16, Brush("Text.Secondary"));
        if (!removed && !newFolder && !movedAway)
        {
            dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush(selected ? "Border.Accent" : "Border.Subtle"), selected ? 2 : 1), new Rect(187, 109, 208, 175), 8, 8);
            dc.DrawImage(_desktop, new Rect(196, 119, 190, 112)); Text(dc, "Build completed", 202, 243, 17, Brush("Text.Primary"));
        }
        if (!newFolder)
        {
            dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush("Border.Subtle"), 1), new Rect(410, 109, 208, 175), 8, 8);
            Text(dc, L(_ko, "녹화", "Recording"), 430, 158, 22, Brush("Text.Secondary")); Text(dc, "00:12", 430, 198, 20, Brush("Text.Primary"), true);
        }
        if (removed) Status(dc, UiText.Format("LibrarySelection_Removed", 1, 0));
        if (movedAway) Status(dc, UiText.Format("Folder410.MoveDone", 1));
        if (newFolder) Text(dc, L(_ko, "새 폴더 · 항목 없음", "New folder · No items"), 224, 171, 19, Brush("Text.Secondary"));
    });
    private static void RailText(DrawingContext dc, string text, Rect bounds)
    {
        var label = new TextBlock { Text = text, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        dc.DrawImage(Snapshot(label, bounds.Width, bounds.Height), bounds);
    }

    private BitmapSource SnapshotTray(bool restore, out Point target)
    {
        Action noop = () => { };
        var entries = TrayMenuLayout.Build(new TrayMenuCommands(noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop), new HotkeySettings(), restore, false);
        var panel = new StackPanel(); Button? chosen = null;
        string label = UiText.Get(restore ? "Onboard_TrayReopenPin" : "Text_8739F6684453");
        foreach (var entry in entries)
        {
            if (entry.IsSeparator) { panel.Children.Add(new Separator { Margin = new Thickness(4, 2, 4, 2) }); continue; }
            var button = Button(entry.Label, entry.Label == label); button.Height = 29; button.MinHeight = 29;
            button.Margin = new Thickness(2, 1, 2, 1); button.FontSize = 13; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            panel.Children.Add(button); if (entry.Label == label) chosen = button;
        }
        var border = new Border { Child = panel, Padding = new Thickness(5), Background = Brush("Surface.Floating"), BorderBrush = Brush("Border.Control"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
        BitmapSource bitmap = Snapshot(border, 250); target = Center(chosen!, border); return bitmap;
    }
    private static BitmapSource SnapshotPinMenu(PinWindow pin, out Point target)
    {
        // The lower product menu section is the viewport of this compact fixture. Every item is an actual product MenuItem.
        var panel = new StackPanel(); MenuItem? chosen = null;
        MenuItem[] items = pin.ContextMenu.Items.OfType<MenuItem>().Where(item => Equals(item.Header, UiText.Get("Onboard_PinMoreOpaque")) || Equals(item.Header, UiText.Get("Onboard_PinAllowClickThrough")) || Equals(item.Header, UiText.Get("Dialog.Close"))).ToArray();
        foreach (MenuItem item in items)
        {
            pin.ContextMenu.Items.Remove(item); item.InputGestureText = string.Empty; item.MinHeight = 32;
            panel.Children.Add(item); if (Equals(item.Header, UiText.Get("Onboard_PinAllowClickThrough"))) chosen = item;
        }
        var border = new Border { Child = panel, Padding = new Thickness(5), Background = Brush("Surface.Floating"), BorderBrush = Brush("Border.Control"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
        BitmapSource bitmap = Snapshot(border, 280); target = Center(chosen!, border); return bitmap;
    }
    private static Rect DialogBounds(BitmapSource bitmap) => new(100, Math.Max(20, (SceneHeight - bitmap.PixelHeight) / 2d), 440, bitmap.PixelHeight);
    private static Point Center(FrameworkElement element, FrameworkElement root)
    {
        Point point = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), root);
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0 || !new Rect(0, 0, root.ActualWidth, root.ActualHeight).Contains(point))
            throw new InvalidOperationException("Fixture target must be measured inside the displayed control.");
        return SnapshotPoint(element, new Point(element.ActualWidth / 2, element.ActualHeight / 2), root);
    }
    private static Point SnapshotPoint(FrameworkElement element, Point point, FrameworkElement root)
    {
        Point local = element.TranslatePoint(point, root);
        // RenderTargetBitmap includes the arranged root's visual offset (for example
        // the folder dialog content's 24px margin); TranslatePoint stops inside it.
        Vector offset = VisualTreeHelper.GetOffset(root);
        return new Point(local.X + offset.X, local.Y + offset.Y);
    }
    private static PointD Add(Point point, Point origin) => new(point.X + origin.X, point.Y + origin.Y);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency) { yield return dependency; foreach (DependencyObject nested in Descendants(dependency)) yield return nested; }
    }
    private static BitmapSource Overlay(BitmapSource scene, BitmapSource dialog, Rect bounds) => DrawScene(dc => DrawOverlay(dc, scene, dialog, bounds));
    private static void DrawOverlay(DrawingContext dc, BitmapSource scene, BitmapSource dialog, Rect bounds)
    {
        dc.DrawImage(scene, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(125, 0, 0, 0)), null, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRoundedRectangle(Brush("Surface.Floating"), new Pen(Brush("Border.Control"), 1), bounds, 8, 8);
        dc.DrawImage(dialog, bounds);
    }
    private static void Status(DrawingContext dc, string text)
    {
        dc.DrawRoundedRectangle(Brush("Surface.Floating"), new Pen(Brush("Border.Control"), 1), new Rect(30, 269, 580, 34), 7, 7);
        Text(dc, text, 43, 277, 15, Brush("Text.Primary"));
    }
    public void Dispose()
    {
        _editor?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); _editor = null; _editorBitmap = null;
        foreach (Window window in _owned) { window.Content = null; window.Close(); }
        _owned.Clear();
    }
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
