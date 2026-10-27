using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// The capture quick-access card: the real QuickAccessWindow content after a region capture of a
/// synthetic page, placed by the product's own QuickAccessWindow.Position, counting down with
/// Advance, and driven with its own HandleShortcut (C copies). Copy goes to a fake delegate, never
/// the clipboard; the paste lands in a synthetic note app.
/// </summary>
/// <remarks>
/// The card window itself is never shown: on Loaded it would place itself topmost on the real
/// monitor and register Alt+Q as a global hotkey. Its content is laid out detached instead, and the
/// window's Alt+Q activation (keyboard focus moves to the first command, the countdown pauses) is
/// shown with the focus look of that first command.
/// </remarks>
internal sealed class QuickAccessScene : TutorialScene
{
    internal static readonly string[] Features = ["quick-access"];

    // The page on the right, the note on the left; the card lands beside the captured region
    // (QuickAccessWindow.Position), between the two.
    private static readonly Rect BrowserClient = new(400, 52, 540, 300);
    private static readonly Rect NoteClient = new(20, 52, 318, 360);
    private const double Toolbar = 36;
    // The captured part of the page, in page pixels: the heading and the first two items.
    private static readonly Int32Rect Region = new(16, 44, 280, 120);

    private readonly BitmapSource _page;
    private readonly BitmapSource _captured;
    private readonly QuickAccessWindow _card;
    private readonly Border _cardRoot;
    private readonly Button _closeButton;
    private readonly ToggleButton _keepButton;
    private Point _cardOrigin;
    private bool _cardClosed, _active, _noteActive, _pasted;
    private int _copies;

    // The card's success marks keep their pressed action's check on the wall clock; the clip ends it on video time.
    private static readonly System.Reflection.FieldInfo SuccessMarksField =
        typeof(QuickAccessWindow).GetField("_successMarks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(QuickAccessWindow), "_successMarks");

    internal QuickAccessScene(string id, string language) : base(id, language)
    {
        _page = Fixtures.Checklist(Korean, (int)BrowserClient.Width, (int)(BrowserClient.Height - Toolbar));
        var crop = new CroppedBitmap(_page, Region);
        crop.Freeze();
        _captured = crop;
        _card = new QuickAccessWindow(_captured,
            copy: () => { _copies++; return Task.FromResult(true); },
            edit: () => { }, pin: () => Task.CompletedTask, save: () => Task.FromResult(true), library: () => { },
            export: () => Task.FromResult(string.Empty), excludeFromCapture: false,
            // Wired like App does; the clip never presses T, and nothing reads or writes the clipboard.
            copyText: () => Task.FromResult(new TextCaptureOutcome(TextCaptureStatus.Unavailable, null, 0)));
        _card.Closed += (_, _) => _cardClosed = true;
        // The card's content, detached from its never-shown window (see remarks).
        var surface = (UIElement)_card.Content;
        _card.Content = null;
        _cardRoot = new Border { Width = _card.Width, Child = surface };
        _cardRoot.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        Size size = Detached.Layout(_cardRoot, _card.Width);
        _closeButton = OffscreenWindow.Descendants(_cardRoot).OfType<Button>()
            .First(button => System.Windows.Automation.AutomationProperties.GetName(button) == UiText.Get("Dialog.Close"));
        _keepButton = OffscreenWindow.Descendants(_cardRoot).OfType<ToggleButton>()
            .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == UiText.Get("QuickAccess.KeepName"));

        // QuickAccessWindow.Position: beside the captured region inside the work area.
        RectD region = StageRegion;
        Rect placed = QuickAccessWindow.Position(new RectD(0, 0, Stage.Width, Taskbar.WorkBottom), 1, size.Width, size.Height, region);
        _cardOrigin = placed.TopLeft;
        Story = BuildStory(new PointD(region.Right, region.Bottom));
    }

    internal override string Provenance =>
        "Real QuickAccessWindow content (large preview, an icon with its name for every action, Keep open toggle, status line, countdown) laid out "
        + "detached; placed by QuickAccessWindow.Position, timed by Advance and driven by HandleShortcut. Copy's success check ends "
        + "after QuickAccessWindow.SuccessCheckTime of video time. Copy calls a fake delegate "
        + "(no clipboard). The captured page, note app and taskbar are synthetic.";

    private static RectD StageRegion => new(BrowserClient.X + Region.X, BrowserClient.Y + Toolbar + Region.Y, Region.Width, Region.Height);

    // Below the text, where a click puts the caret at the end of the note.
    private static PointD NoteBody => new(NoteClient.X + 170, NoteClient.Y + 262);

    private TutorialStoryboard BuildStory(PointD start)
    {
        var story = new TutorialStoryboard(start);
        story.Motion("appear", 32, start, start);
        story.Hold("alt-q", 1.6);
        Point keep = _keepButton.TranslatePoint(new Point(_keepButton.ActualWidth / 2, _keepButton.ActualHeight / 2), _cardRoot);
        story.Move("keep-move", new PointD(_cardOrigin.X + keep.X, _cardOrigin.Y + keep.Y), 12)
            .Click("keep-on").Hold("kept", 0.8).Click("keep-off");
        // Copy's icon shows a check for SuccessCheckTime, then its own icon returns.
        double check = QuickAccessWindow.SuccessCheckTime.TotalSeconds;
        story.Hold("copy", check).Hold("copied", 2.2 - check);
        story.Move("note-move", NoteBody, 18).Click("note-click");
        story.Motion("paste-wait", 10, NoteBody, NoteBody);
        story.Motion("paste", 30, NoteBody, NoteBody);
        story.Motion("result", 40, NoteBody, NoteBody);
        return story.Finish("result", 11);
    }

    internal override void Draw(DrawingContext dc, TutorialFramePlan frame)
    {
        Apply(frame);
        Stage.DrawDesktop(dc);
        Taskbar.Draw(dc, Korean, LibraryFixture.Now);
        DrawBrowser(dc);
        DrawNote(dc);
        if (!_cardClosed)
        {
            Detached.Layout(_cardRoot, _card.Width);
            Detached.Draw(dc, _cardRoot, _cardOrigin);
        }

        (string caption, string key) = Caption(frame.Segment);
        if (frame.Pressed) Stage.DrawClick(dc, frame.Cursor, frame.Progress);
        Stage.DrawPointer(dc, frame.Cursor, NoteClient.Contains(new Point(frame.Cursor.X, frame.Cursor.Y)) && frame.Cursor.Y > NoteClient.Y + 40
            ? Stage.PointerKind.Beam : Stage.PointerKind.Arrow);
        Stage.DrawCaption(dc, caption, key);
    }

    private void Apply(TutorialFramePlan frame)
    {
        switch (frame.Segment)
        {
            case "alt-q" when !_active:
                // Alt+Q: the card takes the keyboard; focus moves to its first command and the
                // countdown pauses while the keyboard is on the card.
                _active = true;
                FocusLook.Apply(_closeButton);
                break;
            case "copy" when _copies == 0:
                ShortcutDefinition copy = ShortcutCatalog.Get(ShortcutScope.QuickAccess, "Copy");
                if (!_card.HandleShortcut(copy.Key, copy.Modifiers)) throw new InvalidOperationException($"{copy.Hint} did not run the card's Copy.");
                // The check is state and shows with motion off too; its period runs on video time.
                foreach (object mark in ((System.Collections.IDictionary)SuccessMarksField.GetValue(_card)!).Values)
                    ((System.Windows.Threading.DispatcherTimer)mark.GetType().GetProperty("Timer", System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.GetValue(mark)!).Stop();
                if (!_card.ShowsSuccessCheckForTest("Copy")) throw new InvalidOperationException("Copy's icon did not turn into a check.");
                break;
            case "copied" when frame.IsFirst && _card.ShowsSuccessCheckForTest("Copy"):
                _card.ElapseSuccessCheckForTest();
                break;
            case "keep-on" when frame.IsLast:
                _card.IsKept = true;
                FocusLook.Apply(_closeButton, focused: false);
                break;
            case "keep-off" when frame.IsLast:
                _card.IsKept = false;
                break;
            case "note-click" when frame.IsLast:
                // Clicking another app deactivates the card: keyboard access ends, the countdown resumes.
                _active = false;
                _noteActive = true;
                FocusLook.Apply(_closeButton, focused: false);
                break;
            case "paste" when frame.IsFirst:
                _pasted = true;
                break;
        }

        if (!_cardClosed) _card.Advance(TimeSpan.FromSeconds(frame.Repeat / (double)TutorialStoryboard.Fps), interacting: _active);
    }

    private void DrawBrowser(DrawingContext dc)
    {
        Stage.DrawWindowFrame(dc, BrowserClient, L("릴리스 준비 - Microsoft Edge", "Release prep - Microsoft Edge"));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF7)), null, new Rect(BrowserClient.X, BrowserClient.Y, BrowserClient.Width, Toolbar));
        dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xD5, 0xD9, 0xE0)), 1),
            new Rect(BrowserClient.X + 64, BrowserClient.Y + 6, BrowserClient.Width - 128, 24), 12, 12);
        Fixtures.Text(dc, "release.example.test/checklist", BrowserClient.X + 80, BrowserClient.Y + 10, 12, new SolidColorBrush(Color.FromRgb(0x44, 0x4B, 0x58)));
        dc.DrawImage(_page, new Rect(BrowserClient.X, BrowserClient.Y + Toolbar, _page.PixelWidth, _page.PixelHeight));
    }

    private void DrawNote(DrawingContext dc)
    {
        Stage.DrawWindowFrame(dc, NoteClient, L("메모", "Notes"));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF7)), null, NoteClient);
        Fixtures.Text(dc, L("회의 준비", "Meeting prep"), NoteClient.X + 18, NoteClient.Y + 14, 18, Brushes.Black, bold: true);
        Fixtures.Text(dc, L("릴리스 체크리스트 공유", "Share the release checklist"), NoteClient.X + 18, NoteClient.Y + 44, 13, Brushes.DimGray);
        double top = NoteClient.Y + 70;
        if (_pasted)
        {
            var image = new Rect(NoteClient.X + 18, top, Region.Width, Region.Height);
            dc.DrawImage(_captured, image);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xDD, 0xE2, 0xEA)), 1), image);
            top = image.Bottom + 6;
        }

        if (_noteActive) dc.DrawRectangle(Brushes.Black, null, new Rect(NoteClient.X + 18, top + 2, 1.2, 16));
    }

    private (string Caption, string Key) Caption(string segment) => segment switch
    {
        "appear" => (L($"캡처하면 {Ui("QuickAccess.Title")} 카드가 나타남", "After a capture, the quick access card appears"), ""),
        "alt-q" => (L($"{Hint(ShortcutScope.QuickAccess, "Activate")}: 키보드로 카드 사용", $"{Hint(ShortcutScope.QuickAccess, "Activate")}: control the card from the keyboard"),
            Hint(ShortcutScope.QuickAccess, "Activate")),
        "keep-move" or "keep-on" or "kept" or "keep-off" => (Ui("QuickAccess.KeepName"), ""),
        "copy" or "copied" => (L($"{Hint(ShortcutScope.QuickAccess, "Copy")}: {Ui("QuickAccess.Copy")} · 카드에 {Ui("QuickAccess.Copied")} 표시",
            $"{Hint(ShortcutScope.QuickAccess, "Copy")}: {Ui("QuickAccess.Copy")} · the card shows {Ui("QuickAccess.Copied")}"), Hint(ShortcutScope.QuickAccess, "Copy")),
        "note-move" or "note-click" or "paste-wait" => (L("붙여 넣을 곳 클릭", "Click where to paste"), ""),
        "paste" => (L("Ctrl+V로 붙여 넣기", "Paste with Ctrl+V"), "Ctrl+V"),
        _ => (L("캡처가 붙여 넣어짐 · 카드는 잠시 후 닫힘", "The capture is pasted · the card closes shortly"), ""),
    };

    public override void Dispose()
    {
        if (!_cardClosed)
        {
            try { _card.Close(); }
            catch (InvalidOperationException) { }
        }
    }
}
