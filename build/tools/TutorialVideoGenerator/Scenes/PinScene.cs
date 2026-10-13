using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// The pin clips: real PinWindows (image clip, frame outline, hover accent, bottom feedback), their
/// PinHoverBar, the pin's own context menu and the tray menu over a synthetic desktop. Commands
/// run through the product's handlers (menu items, the Esc key handler, hide/show, click-through);
/// the clipboard and file writes they request are intercepted and their results reported back the
/// way PinManager reports them.
/// </summary>
/// <remarks>
/// The pins stay far outside every monitor: they are drawn at their stage position, and a drag
/// moves only that drawn position, because the product's drag clamps a pin into the real virtual
/// desktop. Menus are measured and rendered as detached elements, never opened as popups, because
/// WPF keeps a popup inside a monitor and would put it on the user's screen. The feedback and
/// hover-bar timers run on video time, so the clip is the same however fast it renders.
/// </remarks>
internal sealed class PinScene : TutorialScene
{
    internal static readonly string[] Features = ["pin-clipboard", "pin-hide-all", "pin-click-through", "pin-restore", "pin-copy", "pin-save"];

    private const double TaskbarHeight = 48;

    /// <summary>The hover bar is kept beside its offscreen pin, far from every monitor.</summary>
    private static readonly RectD OffscreenWorkArea = new(-30000, -30000, 12000, 12000);

    private static readonly Rect NoteClient = new(36, 66, 540, 360);
    private static readonly Rect NoteInput = new(NoteClient.X + 28, NoteClient.Y + 244, NoteClient.Width - 56, 36);
    private static readonly Rect ExplorerClient = new(28, 64, 556, 370);
    private static readonly Rect TrayIconBounds = new(800, Stage.SceneHeight - TaskbarHeight + 16, 16, 16);
    private static readonly DateTimeOffset SavedAt = new(2026, 10, 13, 9, 30, 12, TimeSpan.FromHours(9));

    private static readonly FieldInfo CountdownField = PrivateField(typeof(PinWindow), "_feedbackCountdown");
    private static readonly FieldInfo CountdownTimerField = PrivateField(typeof(FeedbackCountdown), "_timer");
    private static readonly FieldInfo HoverHideTimerField = PrivateField(typeof(PinWindow), "_hoverHideTimer");
    private static readonly FieldInfo IsMouseOverKeyField = typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(UIElement), "IsMouseOverPropertyKey");
    private static readonly MethodInfo SetHighlighted =typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))?.GetSetMethod(nonPublic: true)
        ?? throw new MissingMethodException(nameof(MenuItem), "set_IsHighlighted");

    private static readonly Brush Ink = Solid(0x1F, 0x24, 0x2E);
    private static readonly Brush Muted = Solid(0x5B, 0x64, 0x73);
    private static readonly Brush Placeholder = Solid(0x8A, 0x92, 0x9F);
    private static readonly Brush Paper = Solid(0xFF, 0xFF, 0xFF);
    private static readonly Brush CaptionLight = Solid(0xEE, 0xF0, 0xF3);
    private static readonly Brush Rule = Solid(0xE3, 0xE6, 0xEB);
    private static readonly Brush FieldBorder = Solid(0xC4, 0xCA, 0xD3);
    private static readonly Brush FocusBlue = Solid(0x2F, 0x6F, 0xEB);
    private static readonly Brush SelectionBlue = Solid(0xC7, 0xDD, 0xFF);

    private readonly string _feature;
    private readonly HotkeySettings _hotkeys = NewInstallDefaults.Create().Hotkeys;
    private readonly Func<HotkeySettings> _previousHotkeys;
    private readonly PinSettings _settings = new();
    private readonly List<LivePin> _pins = [];
    private readonly string[] _noteLines;
    private readonly double _workBottom;
    private BitmapSource? _checklist;
    private MenuView? _pinMenu, _trayMenu, _menu;
    private MenuItem? _menuTarget;
    private LivePin? _focus, _dragged, _copyPending, _savePending;
    private Point _dragPinStart;
    private PointD _dragFrom;
    private PointD? _selectionTo;
    private double _clock;
    private int _lastRepeat;
    private bool _inputFocused, _caret, _pasted, _explorer;
    private string _typed = string.Empty;

    internal PinScene(string id, string language) : base(id, language)
    {
        _feature = id;
        _workBottom = id == "pin-restore" ? Stage.SceneHeight - TaskbarHeight : Stage.SceneHeight;
        _noteLines = Korean
            ? ["배포 점검 순서", "1. 변경 사항 검토", "2. 테스트 결과 확인", "3. 배포 일정 공유"]
            : ["Release check", "1. Review the changes", "2. Confirm the test results", "3. Share the release date"];
        // A new installation: the optional pin shortcuts are unassigned, so the menus show no
        // key for them and no clip invents one.
        _previousHotkeys = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => _hotkeys;
        try
        {
            Story = BuildStory();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal override string Provenance =>
        "Real PinWindow (image clip, frame outline and hover accent, bottom feedback), PinHoverBar, the pin's ContextMenu and the tray "
        + "ContextMenu (ThemedShellPresenter.CreateMenu over TrayMenuLayout.Build) rendered detached at 1:1; Esc, hide/show, "
        + "click-through, copy, quick save and reopen run through the product's handlers and menu items. Clipboard and file writes "
        + "are intercepted and reported with ReportCopyResult/ReportSaveResult. Note app, Explorer, taskbar and wallpaper are synthetic.";

    // ---- Storyboards ----------------------------------------------------------------------------

    private TutorialStoryboard BuildStory() => _feature switch
    {
        "pin-clipboard" => ClipboardStory(),
        "pin-hide-all" => HideAllStory(),
        "pin-click-through" => ClickThroughStory(),
        "pin-restore" => RestoreStory(),
        "pin-copy" => CopyStory(),
        "pin-save" => SaveStory(),
        _ => throw new NotSupportedException(_feature),
    };

    private TutorialStoryboard ClipboardStory()
    {
        PointD start = LineStart(0), end = LineEnd(_noteLines.Length - 1);
        var story = new TutorialStoryboard(new PointD(760, 400));
        story.Hold("open", 0.8).Move("to-text", start);
        story.Motion("select-text", 24, start, end, pressed: true).Hold("selected", 0.4);
        story.Hold("copy", 0.8).Hold("shortcut", 0.9).Hold("pinned", 1.5);
        _dragFrom = end;
        story.Motion("drag", 32, end, new PointD(end.X + 560, end.Y - 90), pressed: true).Hold("moved", 1.0);
        story.Hold("esc", 0.4);
        return story.Finish("closed", 11);
    }

    private TutorialStoryboard HideAllStory()
    {
        _checklist = ChecklistCapture();
        AddPin(PinContent.FromImage(_checklist), new Point(330, 150));
        AddPin(TablePin(), new Point(612, 36));
        var story = new TutorialStoryboard(new PointD(190, 404));
        story.Hold("pins", 2.0).Hold("hide", 0.8).Hold("hidden", 1.3);
        story.Move("to-input", new PointD(440, NoteInput.Y + NoteInput.Height / 2)).Click("focus");
        story.Motion("typing", 22, story.End, story.End).Hold("typed", 0.6);
        story.Move("away", new PointD(190, 404), 12).Hold("show", 0.8);
        return story.Finish("returned", 11);
    }

    private TutorialStoryboard ClickThroughStory()
    {
        _checklist = ChecklistCapture();
        LivePin pin = AddPin(PinContent.FromImage(_checklist), new Point(330, 160));
        var rightClick = new PointD(560, 236);
        // Opening the menu refreshes the reopen command, exactly as its Opened handler does.
        pin.Window.CanRestoreClosedPin = () => false;
        pin.Window.RefreshRestoreClosedMenuItem();
        _pinMenu = new MenuView(pin.Window.ContextMenu, new Point(rightClick.X, rightClick.Y));
        _menuTarget = _pinMenu.Item(UiText.Get("Onboard_PinAllowClickThrough"));
        var story = new TutorialStoryboard(new PointD(820, 430));
        // The pointer is on the menu now, so the pin's hover bar leaves after its short delay.
        story.Hold("open", 0.8).Move("to-pin", rightClick).Hold("hover", 0.5).Click("right-click")
            .Hold("menu", PinWindow.HoverBarHideDelay.TotalSeconds + 0.02).Hold("menu", 0.6);
        story.Move("to-item", _pinMenu.CenterOf(_menuTarget)).Hold("item", 0.3).Click("choose").Hold("through", 1.5);
        story.Move("to-input", new PointD(440, NoteInput.Y + NoteInput.Height / 2)).Click("focus");
        story.Motion("typing", 22, story.End, story.End).Hold("typed", 0.4);
        return story.Finish("result", 11);
    }

    private TutorialStoryboard RestoreStory()
    {
        LivePin pin = AddPin(TablePin(), new Point(600, 140));
        var shell = new ThemedShellPresenter(static () => true);
        var tray = new PointD(TrayIconBounds.X + TrayIconBounds.Width / 2, TrayIconBounds.Y + TrayIconBounds.Height / 2);
        // The tray menu is rebuilt on every open: when this one opens, the only pin is closed (no
        // pin is open, so Hide all pins is absent) and it can come back.
        _trayMenu = new MenuView(shell.CreateMenu(TrayMenu.Entries(new TrayMenuState(CanRestoreClosedPin: true, PinCount: 0), restoreClosedPin: Reopen)),
            new Point(tray.X, tray.Y));
        _menuTarget = _trayMenu.Item(UiText.Get("Onboard_TrayReopenPin"));
        var story = new TutorialStoryboard(new PointD(470, 400));
        story.Hold("open", 1.0).Move("to-pin", Center(pin.Bounds)).Click("select").Hold("selected", 0.5).Hold("esc", 0.5).Hold("closed", 1.0);
        story.Move("to-tray", tray).Click("tray-click").Hold("tray-menu", 1.0);
        story.Move("to-reopen", _trayMenu.CenterOf(_menuTarget)).Hold("reopen-hover", 0.4).Click("reopen");
        return story.Finish("restored", 11);
    }

    private TutorialStoryboard CopyStory()
    {
        _checklist = ChecklistCapture();
        LivePin pin = AddPin(PinContent.FromImage(_checklist), new Point(596, 60));
        var story = new TutorialStoryboard(new PointD(450, 420));
        story.Hold("open", 0.8).Move("to-pin", new PointD(pin.Bounds.X + 150, pin.Bounds.Y + 170)).Click("select").Hold("selected", 0.6);
        story.Hold("copy", 0.3).Hold("copied", FeedbackTiming.ConfirmationOverContent.TotalSeconds);
        story.Move("to-note", new PointD(NoteClient.X + 60, NoteClient.Y + 130)).Click("note-click").Hold("caret", 0.6);
        story.Hold("paste", 0.4);
        return story.Finish("pasted", 11);
    }

    private TutorialStoryboard SaveStory()
    {
        _checklist = ChecklistCapture();
        LivePin pin = AddPin(PinContent.FromImage(_checklist), new Point(596, 80));
        // Measure where the hover bar's Save button lands for this pin, then leave the bar hidden.
        pin.Window.ShowHoverBar();
        Stage.Pump(40);
        PointD save = BarButtonCenter(pin, pin.Window.HoverBarForTest!.SaveButton);
        pin.Window.HideHoverBar();
        var story = new TutorialStoryboard(new PointD(450, 420));
        story.Hold("open", 0.8).Move("to-pin", new PointD(pin.Bounds.X + 150, pin.Bounds.Y + 170)).Click("select").Hold("selected", 0.5);
        story.Hold("save", 0.3).Hold("saved", 1.8);
        story.Move("to-bar-save", save).Hold("bar-save", 1.4);
        story.Hold("explorer", 0.5).Move("to-file", new PointD(ExplorerClient.X + 220, ExplorerClient.Y + 120));
        return story.Finish("explorer-file", 11);
    }

    // ---- Frames ---------------------------------------------------------------------------------

    internal override void Draw(DrawingContext dc, TutorialFramePlan frame)
    {
        // The previous plan was on screen for its frames; spend that video time on the product's
        // own countdowns before this plan acts.
        double elapsed = _lastRepeat / (double)TutorialStoryboard.Fps;
        _clock += elapsed;
        _lastRepeat = frame.Repeat;
        foreach (LivePin pin in _pins.Where(pin => !pin.Window.IsClosed))
            pin.Window.AdvanceFeedbackForTest(TimeSpan.FromSeconds(elapsed));

        Apply(frame);
        foreach (LivePin pin in _pins) FreezeClocks(pin.Window);
        Stage.Pump(15);
        foreach (LivePin pin in _pins) FreezeClocks(pin.Window);

        Stage.DrawDesktop(dc);
        DrawNote(dc);
        if (_explorer) DrawExplorer(dc);
        if (_feature == "pin-restore") DrawTaskbar(dc);
        DrawPins(dc);
        _menu?.Draw(dc);

        (string caption, string key) = Caption(frame.Segment);
        if (frame.Pressed) Stage.DrawClick(dc, frame.Cursor, frame.Progress);
        Stage.DrawPointer(dc, frame.Cursor, PointerAt(frame.Cursor));
        Stage.DrawCaption(dc, caption, key);
    }

    private void Apply(TutorialFramePlan frame)
    {
        PointD cursor = frame.Cursor;
        bool first = frame.IsFirst, last = frame.IsLast;
        switch (frame.Segment)
        {
            case "select-text":
                _selectionTo = cursor;
                break;
            case "pinned" when first && _focus is null:
                PinClipboard(cursor);
                break;
            case "select" when _focus is { } focus:
                // A press starts the pin's drag gesture, which hides the bar; the release brings it back.
                if (first) focus.Window.HideHoverBar();
                if (last) focus.Window.ShowHoverBar();
                break;
            case "drag" when _focus is { } dragged:
                if (first)
                {
                    _dragged = dragged;
                    _dragPinStart = dragged.Position;
                    dragged.Window.HideHoverBar();
                }

                dragged.Position = new Point(_dragPinStart.X + cursor.X - _dragFrom.X, _dragPinStart.Y + cursor.Y - _dragFrom.Y);
                if (last)
                {
                    _dragged = null;
                    dragged.Window.ShowHoverBar();
                }

                break;
            case "closed" when first && _focus is { } closing:
                PressKey(closing.Window, Key.Escape);
                if (!closing.Window.IsClosed) throw new InvalidOperationException("Esc did not close the pin.");
                break;
            case "hidden" when first:
                foreach (LivePin pin in _pins) pin.Window.HidePin();
                break;
            case "returned" when first:
                // PinManager.HideOrShowAll: click-through is cleared before a pin is shown again.
                foreach (LivePin pin in _pins)
                {
                    if (pin.Window.State.IsClickThrough) pin.Window.ApplyClickThrough(enabled: false);
                    pin.Window.ShowPin();
                }

                break;
            case "focus" when last:
                _inputFocused = true;
                break;
            case "typing":
            case "typed":
                string text = L("릴리스 노트 작성", "Write the release notes");
                _typed = frame.Segment == "typed" ? text : text[..Math.Max(1, (int)Math.Ceiling(text.Length * frame.Progress))];
                break;
            case "right-click" when last:
                _menu = _pinMenu;
                break;
            case "choose" when last:
                _menu = null;
                Click(_menuTarget!);
                if (_focus?.Window.State.IsClickThrough != true) throw new InvalidOperationException("The menu item did not turn click-through on.");
                break;
            case "tray-click" when last:
                _menu = _trayMenu;
                break;
            case "reopen" when last:
                _menu = null;
                Click(_menuTarget!);
                break;
            case "copy" when first && _focus is { } copying:
                Click(MenuCommand(copying.Window, "Text_37B3D3B11B26"));
                break;
            case "copied" when first:
                // PinManager reports the clipboard result back to the pin; nothing touches the clipboard.
                (_copyPending ?? throw new InvalidOperationException("The pin did not request a copy.")).Window.ReportCopyResult(true);
                _copyPending = null;
                break;
            case "note-click" when last:
                _caret = true;
                break;
            case "pasted" when first:
                _pasted = true;
                break;
            case "save" when first && _focus is { } saving:
                Click(MenuCommand(saving.Window, "Text_DFC084A111D0"));
                break;
            case "saved" when first:
                // The quick-save result PinManager reports; no file is written.
                string name = QuickSaveNaming.BuildStem(new ExportSettings().FileNamePattern, SavedAt) + ".png";
                (_savePending ?? throw new InvalidOperationException("The pin did not request a save.")).Window
                    .ReportSaveResult(new PinSaveResult(PinSaveStatus.Saved, Path.Combine("MyCapture", name)));
                _savePending = null;
                break;
            case "explorer" when first:
                _explorer = true;
                break;
        }

        if (_dragged is null && frame.Segment != "select") TrackHover(cursor);
        TrackButtonHover(new Point(cursor.X, cursor.Y));
        _menu?.Highlight(_menu.ItemAt(new Point(cursor.X, cursor.Y)) is { IsEnabled: true } item ? item : null);
    }

    private void PinClipboard(PointD cursor)
    {
        // PinManager.OpenPin: the copied text is rendered by the product and centred on the pointer.
        string text = string.Join("\r\n", _noteLines);
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(text, isTabularContent: false), text, isTable: false);
        PinGeometry.Placement placement = PinGeometry.CascadedPlacement(content.Image.PixelWidth, content.Image.PixelHeight,
            0, 0, Stage.Width, _workBottom, cursor.X, cursor.Y, openPinCount: 0);
        AddPin(content, new Point(placement.Left, placement.Top));
    }

    private void Reopen()
    {
        // PinManager.RestoreLastClosed: the same content, zoom and opacity at its last position.
        LivePin closed = _focus ?? throw new InvalidOperationException("No pin was closed.");
        PinViewState old = closed.Window.State;
        var state = new PinViewState(old.ImageWidthDip, old.ImageHeightDip, old.Zoom, old.Opacity, _settings.ZoomStep);
        _focus = AddPin(closed.Window.PinnedContent, closed.Position, state);
    }

    // ---- Pins -----------------------------------------------------------------------------------

    private LivePin AddPin(PinContent content, Point position, PinViewState? state = null)
    {
        state ??= new PinViewState(content.Image.PixelWidth, content.Image.PixelHeight, 1, _settings.InitialOpacity, _settings.ZoomStep);
        var pin = new LivePin(content, state, position, () => _settings);
        pin.Window.CopyRequested += (_, _) => _copyPending = pin;
        pin.Window.SaveRequested += (_, _) => _savePending = pin;
        _pins.Add(pin);
        _focus = pin;
        FreezeClocks(pin.Window);
        return pin;
    }

    private PinContent TablePin()
    {
        string table = Korean
            ? "항목\t담당\t마감\r\n변경 검토\t민수\t10/15\r\n테스트\t지연\t10/16\r\n배포 공지\t하늘\t10/17"
            : "Task\tOwner\tDue\r\nReview\tAlex\t10/15\r\nTesting\tSam\t10/16\r\nAnnounce\tKim\t10/17";
        return PinContent.FromText(ClipboardTextRenderer.Render(table, isTabularContent: true), table, isTable: true);
    }

    /// <summary>The checklist the editor clips edit, captured as far as its owner line.</summary>
    private BitmapSource ChecklistCapture()
    {
        var capture = new CroppedBitmap(Fixtures.Checklist(Korean), new Int32Rect(0, 0, 340, 246));
        capture.Freeze();
        return capture;
    }

    /// <summary>Hover enter and leave as Windows would raise them for the drawn pointer.</summary>
    private void TrackHover(PointD cursor)
    {
        var point = new Point(cursor.X, cursor.Y);
        bool overMenu = _menu?.Bounds.Contains(point) == true;
        LivePin? overBar = overMenu ? null : _pins.LastOrDefault(pin => BarBounds(pin) is { } bar && bar.Contains(point));
        LivePin? over = overMenu || overBar is not null ? null
            : _pins.LastOrDefault(pin => pin.Shown && !pin.Window.State.IsClickThrough && pin.Bounds.Contains(point));
        foreach (LivePin pin in _pins)
        {
            if (!pin.Shown)
            {
                pin.Hovered = false;
                pin.LeftAt = null;
                continue;
            }

            if (ReferenceEquals(pin, over) && !pin.Hovered)
            {
                pin.Hovered = true;
                pin.LeftAt = null;
                pin.Window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            }
            else if (!ReferenceEquals(pin, over) && pin.Hovered)
            {
                pin.Hovered = false;
                pin.LeftAt = _clock;
                pin.Window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            }

            // The bar keeps itself while the pointer is on it and leaves a moment after it goes.
            if (ReferenceEquals(pin, overBar) && pin.LeftAt is not null) pin.LeftAt = _clock;
            if (pin.LeftAt is { } left && _clock - left >= PinWindow.HoverBarHideDelay.TotalSeconds - 0.001)
            {
                pin.Window.ForceHoverBarHideForTest();
                pin.LeftAt = null;
            }
        }
    }

    /// <summary>The pin's feedback and hover timers advance on video time only.</summary>
    private static void FreezeClocks(PinWindow pin)
    {
        ((DispatcherTimer)CountdownTimerField.GetValue(CountdownField.GetValue(pin))!).Stop();
        ((DispatcherTimer)HoverHideTimerField.GetValue(pin)!).Stop();
    }

    private static void PressKey(PinWindow pin, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(pin) ?? throw new InvalidOperationException("The pin has no window yet.");
        pin.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private static MenuItem MenuCommand(PinWindow pin, string headerKey) =>
        pin.ContextMenu.Items.OfType<MenuItem>().First(item => item.Header as string == UiText.Get(headerKey));

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));

    private Rect? BarBounds(LivePin pin)
    {
        if (!pin.Shown || pin.Window.HoverBarForTest is not { IsVisible: true } bar) return null;
        bar.UpdateLayout();
        // PinHoverBar.Place with the stage as the work area: where the bar sits on this screen.
        RectD place = PinHoverBar.Place(new RectD(pin.Position.X, pin.Position.Y, pin.Bounds.Width, pin.Bounds.Height),
            bar.ActualWidth, bar.ActualHeight, new RectD(0, 0, Stage.Width, _workBottom), 1);
        return new Rect(place.X, place.Y, bar.ActualWidth, bar.ActualHeight);
    }

    private PointD BarButtonCenter(LivePin pin, Button button) =>
        Center(ButtonBounds(pin, button) ?? throw new InvalidOperationException("The hover bar is not shown."));

    private static Button[] BarButtons(PinHoverBar bar) => [bar.CopyButton, bar.SaveButton, bar.OpacityButton, bar.CloseButton];

    /// <summary>A hover-bar button's stage bounds while the bar is shown.</summary>
    private Rect? ButtonBounds(LivePin pin, Button button)
    {
        if (BarBounds(pin) is not { } bar) return null;
        var root = (FrameworkElement)VisualTreeHelper.GetChild(pin.Window.HoverBarForTest!, 0);
        Rect local = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
        local.Offset(bar.X, bar.Y);
        return local;
    }

    private bool OverBarButton(LivePin pin, Point point) =>
        pin.Window.HoverBarForTest is { } bar && BarButtons(bar).Any(button => ButtonBounds(pin, button) is { } bounds && bounds.Contains(point));

    /// <summary>The hover wash a bar button shows under the pointer (its template's IsMouseOver trigger).</summary>
    private void TrackButtonHover(Point point)
    {
        var key = (DependencyPropertyKey)IsMouseOverKeyField.GetValue(null)!;
        foreach (LivePin pin in _pins)
        {
            if (pin.Window.HoverBarForTest is not { } bar) continue;
            foreach (Button button in BarButtons(bar))
            {
                bool over = ButtonBounds(pin, button) is { } bounds && bounds.Contains(point) && _menu is null;
                if ((bool)button.GetValue(UIElement.IsMouseOverProperty) != over) button.SetValue(key, over);
            }
        }
    }

    private void DrawPins(DrawingContext dc)
    {
        foreach (LivePin pin in _pins.Where(pin => pin.Shown))
        {
            pin.Window.UpdateLayout();
            var root = (FrameworkElement)VisualTreeHelper.GetChild(pin.Window, 0);
            dc.PushOpacity(pin.Window.Opacity);
            dc.DrawImage(Snapshot(root), new Rect(pin.Position, root.RenderSize));
            dc.Pop();
        }

        // Each hover bar is its own top-most window above every pin.
        foreach (LivePin pin in _pins)
        {
            if (BarBounds(pin) is not { } bounds) continue;
            PinHoverBar bar = pin.Window.HoverBarForTest!;
            var root = (FrameworkElement)VisualTreeHelper.GetChild(bar, 0);
            dc.PushOpacity(bar.Opacity);
            dc.DrawImage(Snapshot(root), new Rect(bounds.Location, root.RenderSize));
            dc.Pop();
        }
    }

    /// <summary>A layered window's client area with its transparency, at output resolution.</summary>
    private static BitmapSource Snapshot(FrameworkElement root)
    {
        double factor = Stage.Scale;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Round(root.ActualWidth * factor)), Math.Max(1, (int)Math.Round(root.ActualHeight * factor)),
            96 * factor, 96 * factor, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private Stage.PointerKind PointerAt(PointD cursor)
    {
        var point = new Point(cursor.X, cursor.Y);
        if (_dragged is not null) return Stage.PointerKind.Arrow;
        // The shared MenuItem style shows the hand over a command it can run.
        if (_menu is { } menu && menu.Bounds.Contains(point))
            return menu.ItemAt(point) is { IsEnabled: true } ? Stage.PointerKind.Hand : Stage.PointerKind.Arrow;
        foreach (LivePin pin in Enumerable.Reverse(_pins))
            if (BarBounds(pin) is { } bar && bar.Contains(point))
                return OverBarButton(pin, point) ? Stage.PointerKind.Hand : Stage.PointerKind.Arrow;
        if (_pins.Any(pin => pin.Shown && !pin.Window.State.IsClickThrough && pin.Bounds.Contains(point))) return Stage.PointerKind.Arrow;
        if (_explorer && ExplorerClient.Contains(point)) return Stage.PointerKind.Arrow;
        if (_feature == "pin-restore" && point.Y >= _workBottom) return Stage.PointerKind.Arrow;
        return NoteClient.Contains(point) ? Stage.PointerKind.Beam : Stage.PointerKind.Arrow;
    }

    // ---- Synthetic desktop ----------------------------------------------------------------------

    private double LineY(int index) => NoteClient.Y + (index == 0 ? 92 : 120 + (index - 1) * 26);

    private double LineWidth(int index) => Stage.Format(_noteLines[index], 15, Ink, bold: index == 0).WidthIncludingTrailingWhitespace;

    private PointD LineStart(int index) => new(NoteClient.X + 27, LineY(index) + 10);

    private PointD LineEnd(int index) => new(NoteClient.X + 28 + LineWidth(index) + 1, LineY(index) + 10);

    private void DrawNote(DrawingContext dc)
    {
        Rect c = NoteClient;
        bool meeting = _feature == "pin-copy";
        DrawLightWindow(dc, c, meeting ? L("회의 메모 - 메모", "Meeting notes - Notes") : L("주간 보고 - 메모", "Weekly report - Notes"));
        Stage.Text(dc, meeting ? L("회의 메모", "Meeting notes") : L("주간 보고", "Weekly report"), c.X + 28, c.Y + 20, 20, Ink, bold: true);
        Stage.Text(dc, meeting ? L("10월 13일 · 릴리스 점검", "October 13 · Release review") : L("10월 둘째 주 · 개발팀", "Second week of October · Dev team"),
            c.X + 28, c.Y + 54, 13, Muted);
        if (meeting)
        {
            Stage.Text(dc, L("체크리스트 현황", "Checklist status"), c.X + 28, c.Y + 92, 15, Ink, bold: true);
            if (_pasted && _checklist is not null)
            {
                var image = new Rect(c.X + 28, c.Y + 124, _checklist.PixelWidth * 0.7, _checklist.PixelHeight * 0.7);
                dc.DrawImage(_checklist, image);
                dc.DrawRectangle(null, new Pen(Rule, 1), image);
                DrawCaret(dc, image.Right + 3, image.Bottom - 20);
            }
            else if (_caret) DrawCaret(dc, c.X + 28, c.Y + 122);

            return;
        }

        if (_selectionTo is { } to) DrawSelection(dc, to);
        for (int i = 0; i < _noteLines.Length; i++) Stage.Text(dc, _noteLines[i], c.X + 28, LineY(i), 15, Ink, bold: i == 0);
        dc.DrawRectangle(Rule, null, new Rect(c.X + 28, c.Y + 206, c.Width - 56, 1));
        Stage.Text(dc, L("할 일", "To-do"), c.X + 28, c.Y + 220, 13, Muted);
        dc.DrawRoundedRectangle(Paper, new Pen(_inputFocused ? FocusBlue : FieldBorder, _inputFocused ? 2 : 1), NoteInput, 6, 6);
        bool empty = _typed.Length == 0;
        Stage.Text(dc, empty ? L("할 일 추가", "Add a to-do") : _typed, NoteInput.X + 12, NoteInput.Y + 8, 15, empty ? Placeholder : Ink);
        if (_inputFocused)
            DrawCaret(dc, NoteInput.X + 12 + (empty ? 0 : Stage.Format(_typed, 15, Ink).WidthIncludingTrailingWhitespace) + 1, NoteInput.Y + 8);
    }

    private void DrawSelection(DrawingContext dc, PointD to)
    {
        double x = NoteClient.X + 26;
        for (int i = 0; i < _noteLines.Length; i++)
        {
            double top = LineY(i) - 2, bottom = top + 24;
            double width = LineWidth(i) + 4;
            if (to.Y < top) break;
            if (to.Y < bottom) width = Math.Clamp(to.X - x, 0, width);
            dc.DrawRectangle(SelectionBlue, null, new Rect(x, top, width, 24));
        }
    }

    private static void DrawCaret(DrawingContext dc, double x, double y) =>
        dc.DrawRectangle(Ink, null, new Rect(Math.Round(x), y, 1.5, 20));

    /// <summary>Another company's app in its light theme, with the Windows 11 caption buttons.</summary>
    private static void DrawLightWindow(DrawingContext dc, Rect client, string title)
    {
        var outer = new Rect(client.X, client.Y - Stage.TitleBarHeight, client.Width, client.Height + Stage.TitleBarHeight);
        for (int i = 1; i <= 6; i++)
        {
            var ring = new Rect(outer.X - i, outer.Y - i + 3, outer.Width + 2 * i, outer.Height + 2 * i);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(34 - i * 5), 0, 0, 0)), 1.2), ring, 8 + i, 8 + i);
        }

        dc.PushClip(new RectangleGeometry(outer, 8, 8));
        dc.DrawRectangle(CaptionLight, null, outer);
        dc.DrawRectangle(Paper, null, client);
        Stage.Text(dc, title, outer.X + 14, outer.Y + 8, 12, Ink);
        var glyphs = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        string[] buttons = ["", "", ""];
        for (int i = 0; i < buttons.Length; i++)
        {
            var glyph = new FormattedText(buttons[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(glyphs, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, Ink, Stage.Scale);
            double cell = outer.Right - (buttons.Length - i) * 46;
            dc.DrawText(glyph, new Point(cell + (46 - glyph.Width) / 2, outer.Y + (Stage.TitleBarHeight - glyph.Height) / 2));
        }

        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x60, 0x68, 0x78)), 1), outer, 8, 8);
    }

    /// <summary>File Explorer on the quick-save folder (the default Pictures\MyCapture) with the new PNG.</summary>
    private void DrawExplorer(DrawingContext dc)
    {
        Rect client = ExplorerClient;
        Stage.DrawWindowFrame(dc, client, "MyCapture");
        dc.DrawRectangle(Solid(0x1C, 0x1F, 0x26), null, client);
        dc.DrawRoundedRectangle(Solid(0x2A, 0x2E, 0x37), null, new Rect(client.X + 12, client.Y + 10, client.Width - 24, 30), 6, 6);
        Stage.Text(dc, L("사진  ›  MyCapture", "Pictures  ›  MyCapture"), client.X + 26, client.Y + 15, 13, Brushes.White);
        Stage.Text(dc, L("이름", "Name"), client.X + 24, client.Y + 54, 12, Brushes.Gray);
        Stage.Text(dc, L("수정한 날짜", "Date modified"), client.X + 360, client.Y + 54, 12, Brushes.Gray);
        dc.DrawRectangle(Solid(0x33, 0x38, 0x44), null, new Rect(client.X + 12, client.Y + 74, client.Width - 24, 1));
        var row = new Rect(client.X + 12, client.Y + 82, client.Width - 24, 76);
        dc.DrawRoundedRectangle(Solid(0x2F, 0x3D, 0x5C), null, row, 4, 4);
        if (_checklist is not null)
        {
            double height = 60, width = height * _checklist.PixelWidth / _checklist.PixelHeight;
            dc.DrawImage(_checklist, new Rect(row.X + 10, row.Y + 8, width, height));
        }

        Stage.Text(dc, QuickSaveNaming.BuildStem(new ExportSettings().FileNamePattern, SavedAt) + ".png", row.X + 110, row.Y + 28, 14, Brushes.White);
        Stage.Text(dc, "2026-10-13  09:30", client.X + 360, row.Y + 28, 13, Brushes.LightGray);
    }

    /// <summary>The Windows 11 taskbar with MyCapture's notification-area icon.</summary>
    private void DrawTaskbar(DrawingContext dc)
    {
        var bar = new Rect(0, _workBottom, Stage.Width, TaskbarHeight);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1F, 0x26)), null, bar);
        dc.DrawRectangle(Solid(0x33, 0x38, 0x44), null, new Rect(0, bar.Y, Stage.Width, 1));
        double middle = bar.Y + TaskbarHeight / 2;
        // Start, search, File Explorer and the note app in the middle.
        double startX = Stage.Width / 2 - 70;
        var blue = Solid(0x4C, 0xA8, 0xF5);
        for (int i = 0; i < 4; i++)
            dc.DrawRectangle(blue, null, new Rect(startX - 9 + i % 2 * 10, middle - 9 + i / 2 * 10, 8.5, 8.5));
        DrawGlyph(dc, "", Stage.Width / 2 - 24, middle, 16, Brushes.White);
        var folder = new Rect(Stage.Width / 2 + 13, middle - 8, 22, 16);
        dc.DrawRoundedRectangle(Solid(0xE8, 0xB3, 0x3A), null, folder, 2, 2);
        dc.DrawRoundedRectangle(Solid(0xF7, 0xCE, 0x5B), null, new Rect(folder.X, folder.Y + 4, folder.Width, folder.Height - 4), 2, 2);
        var note = new Rect(Stage.Width / 2 + 59, middle - 10, 18, 20);
        dc.DrawRoundedRectangle(Paper, null, note, 3, 3);
        for (int i = 0; i < 3; i++) dc.DrawRectangle(Solid(0x5B, 0x8D, 0xEF), null, new Rect(note.X + 4, note.Y + 5 + i * 4.5, 10, 1.6));
        dc.DrawRoundedRectangle(Solid(0x9A, 0xA3, 0xB5), null, new Rect(note.X + 4, bar.Bottom - 5, 10, 3), 1.5, 1.5);
        // Notification area: overflow, MyCapture, network, volume and the clock.
        DrawGlyph(dc, "", TrayIconBounds.X - 22, middle, 11, Brushes.White);
        dc.DrawImage(TrayIcon.Value, TrayIconBounds);
        DrawGlyph(dc, "", TrayIconBounds.Right + 22, middle, 15, Brushes.White);
        DrawGlyph(dc, "", TrayIconBounds.Right + 48, middle, 15, Brushes.White);
        FormattedText time = Stage.Format(L("오전 9:30", "9:30 AM"), 12, Brushes.White);
        FormattedText date = Stage.Format(L("2026-10-13", "10/13/2026"), 12, Brushes.White);
        double right = Stage.Width - 14;
        dc.DrawText(time, new Point(right - time.Width, bar.Y + 7));
        dc.DrawText(date, new Point(right - date.Width, bar.Y + 25));
    }

    private static void DrawGlyph(DrawingContext dc, string glyph, double centerX, double centerY, double size, Brush brush)
    {
        var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, brush, Stage.Scale);
        dc.DrawText(text, new Point(centerX - text.Width / 2, centerY - text.Height / 2));
    }

    /// <summary>The product's own tray icon (the idle state the shell shows), read from the build output.</summary>
    private static readonly Lazy<BitmapSource> TrayIcon = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray-idle.ico");
        Uri source = File.Exists(path) ? new Uri(path) : new Uri("pack://application:,,,/MyCapture;component/Assets/app.ico");
        BitmapDecoder decoder = BitmapDecoder.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames.OrderBy(candidate => Math.Abs(candidate.PixelWidth - 32 * Stage.Scale)).First();
        frame.Freeze();
        return frame;
    });

    // ---- Captions -------------------------------------------------------------------------------

    private (string Caption, string Key) Caption(string segment)
    {
        return _feature switch
        {
            "pin-clipboard" => segment switch
            {
                "open" or "to-text" or "select-text" or "selected" => (L("핀으로 띄울 텍스트 선택", "Select the text to pin"), ""),
                "copy" => (L("Ctrl+C로 클립보드에 복사", "Copy it to the clipboard with Ctrl+C"), "Ctrl+C"),
                "shortcut" => (L($"지정된 {Ui("Text_1FBCF51E2FF5")} 단축키 누름", $"Press your {Ui("Text_1FBCF51E2FF5")} shortcut"), ""),
                "pinned" => (L("클립보드 내용이 핀으로 화면에 표시됨", "The clipboard content appears as a pin"), ""),
                "drag" or "moved" => (L("핀을 드래그하여 원하는 위치로 이동", "Drag the pin where you need it"), ""),
                "esc" => (L($"{CloseGesture}로 핀 닫기", $"Press {CloseGesture} to close the pin"), CloseGesture),
                _ => (L("핀 닫힘", "The pin is closed"), ""),
            },
            "pin-hide-all" => segment switch
            {
                "pins" => (L("화면에 띄운 핀 두 개", "Two pins on the screen"), ""),
                "hide" => (L($"지정된 {Ui("Tray_HideAllPins")} 단축키 누름", $"Press your {Ui("Tray_HideAllPins")} shortcut"), ""),
                "hidden" => (L("모든 핀이 숨겨져 아래 창이 보임", "All pins are hidden and the window below shows"), ""),
                "show" => (L("같은 단축키를 다시 누름", "Press the same shortcut again"), ""),
                "returned" => (L("핀이 같은 위치에 다시 표시됨", "The pins return to the same places"), ""),
                _ => (L("가려졌던 메모를 바로 편집", "Edit the note the pins were covering"), ""),
            },
            "pin-click-through" => segment switch
            {
                "open" or "to-pin" or "hover" or "right-click" => (L("핀에서 오른쪽 클릭", "Right-click the pin"), ""),
                "menu" or "to-item" or "item" or "choose" =>
                    (L($"메뉴에서 {Ui("Onboard_PinAllowClickThrough")} 선택", $"Choose {Ui("Onboard_PinAllowClickThrough")}"), ""),
                "through" => (L($"{Ui("Onboard_PinAllowClickThrough")} 켜짐 · 핀이 클릭을 받지 않음", "Click-through is on · the pin no longer takes clicks"), ""),
                // The way back the pin's own feedback names for the current shortcuts.
                "result" => (L($"핀은 계속 표시됨 · 해제: {PinWindow.ClickThroughRecoveryHint()}",
                    $"The pin stays visible · Turn off: {PinWindow.ClickThroughRecoveryHint()}"), ""),
                _ => (L("핀 아래 앱을 그대로 조작", "Use the app underneath the pin"), ""),
            },
            "pin-restore" => segment switch
            {
                "open" or "to-pin" or "select" or "selected" => (L("닫을 핀을 클릭하여 선택", "Click the pin to select it"), ""),
                "esc" => (L($"{CloseGesture}로 핀 닫기", $"Press {CloseGesture} to close the pin"), CloseGesture),
                "closed" => (L("핀 닫힘", "The pin is closed"), ""),
                "to-tray" or "tray-click" => (L("알림 영역의 MyCapture 아이콘을 오른쪽 클릭", "Right-click the MyCapture icon in the notification area"), ""),
                "restored" => (L("최근에 닫은 핀이 같은 위치에 다시 열림", "The most recently closed pin reopens in the same place"), ""),
                _ => (L($"{Ui("Onboard_TrayReopenPin")} 선택 · 지정된 단축키도 같은 동작", $"Choose {Ui("Onboard_TrayReopenPin")} · your assigned shortcut does the same"), ""),
            },
            "pin-copy" => segment switch
            {
                "open" or "to-pin" or "select" or "selected" => (L("복사할 핀을 클릭하여 선택", "Click the pin to select it"), ""),
                "copy" or "copied" => (L($"{FeatureKeys}로 핀 이미지 복사", $"Press {FeatureKeys} to copy the pin image"), FeatureKeys),
                "to-note" or "note-click" or "caret" => (L("붙여 넣을 앱을 클릭", "Click the app to paste into"), ""),
                "paste" => (L("Ctrl+V로 붙여 넣기", "Paste with Ctrl+V"), "Ctrl+V"),
                _ => (L("이미지 붙여 넣음 · 핀은 그대로 열려 있음", "The image is pasted · the pin stays open"), ""),
            },
            _ => segment switch
            {
                "open" or "to-pin" or "select" or "selected" => (L("저장할 핀을 클릭하여 선택", "Click the pin to select it"), ""),
                "save" or "saved" => (L($"{FeatureKeys}로 빠른 저장 폴더에 바로 저장", $"{FeatureKeys} saves straight to the quick-save folder"), FeatureKeys),
                "to-bar-save" or "bar-save" => (L($"핀 위 도구 막대의 {Ui("QuickAccess.Save")}도 같은 동작", $"{Ui("QuickAccess.Save")} on the pin's toolbar does the same"), ""),
                _ => (L($"빠른 저장 폴더에 PNG 저장됨 · 다른 위치는 {SaveAsGesture}", $"PNG saved to the quick-save folder · {SaveAsGesture} picks another place"), ""),
            },
        };
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>The key the pin's own menu gives Close.</summary>
    private string CloseGesture => MenuCommand(FirstPin, "Dialog.Close").InputGestureText;

    /// <summary>The key the pin's own menu gives Save as.</summary>
    private string SaveAsGesture => MenuCommand(FirstPin, "Text_A81F975C401F").InputGestureText;

    private PinWindow FirstPin => _pins.FirstOrDefault()?.Window ?? throw new InvalidOperationException("No pin is open yet.");

    private static PointD Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static FieldInfo PrivateField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(type.Name, name);

    public override void Dispose()
    {
        foreach (LivePin pin in _pins) pin.Dispose();
        _pins.Clear();
        ShortcutHelpWindow.ReadHotkeys = _previousHotkeys;
    }

    /// <summary>A real PinWindow shown offscreen and drawn at its stage position.</summary>
    private sealed class LivePin : IDisposable
    {
        private readonly OffscreenWindow _host;

        internal LivePin(PinContent content, PinViewState state, Point position, Func<PinSettings> settings)
        {
            Window = new PinWindow(content, state, 0, 0, settings) { ReadWorkArea = _ => OffscreenWorkArea };
            _host = new OffscreenWindow(Window, state.WidthDip, state.HeightDip);
            Position = position;
        }

        internal PinWindow Window { get; }

        internal Point Position { get; set; }

        internal bool Hovered { get; set; }

        /// <summary>Video time the pointer left the pin while its bar was showing.</summary>
        internal double? LeftAt { get; set; }

        internal Rect Bounds => new(Position, new Size(Window.State.WidthDip, Window.State.HeightDip));

        internal bool Shown => !Window.IsClosed && Window.IsVisible;

        public void Dispose() => _host.Dispose();
    }

    /// <summary>
    /// A real ContextMenu measured and rendered as a detached element, placed the way a popup at the
    /// pointer lands (WPF PlacementMode.MousePoint): below and right of the tip, flipped to the side
    /// that fits, and aligned to the top of the screen when it is taller than the space either way.
    /// </summary>
    private sealed class MenuView
    {
        internal MenuView(ContextMenu menu, Point pointer)
        {
            Menu = menu;
            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            menu.Arrange(new Rect(menu.DesiredSize));
            menu.UpdateLayout();
            Items = menu.Items.OfType<MenuItem>().ToArray();
            if (Items.Count == 0 || Items.Any(item => item.ActualHeight <= 0))
                throw new InvalidOperationException("The detached menu did not lay out its items.");
            Size size = menu.RenderSize;
            Rect screen = Stage.SceneBounds;
            double x = pointer.X + size.Width <= screen.Right ? pointer.X
                : pointer.X - size.Width >= screen.Left ? pointer.X - size.Width : screen.Right - size.Width;
            double y = pointer.Y + size.Height <= screen.Bottom ? pointer.Y
                : pointer.Y - size.Height >= screen.Top ? pointer.Y - size.Height : screen.Top;
            Bounds = new Rect(new Point(x, y), size);
        }

        internal ContextMenu Menu { get; }

        internal Rect Bounds { get; }

        internal IReadOnlyList<MenuItem> Items { get; }

        internal MenuItem Item(string header) => Items.First(item => item.Header as string == header);

        internal Rect BoundsOf(MenuItem item)
        {
            Rect local = item.TransformToAncestor(Menu).TransformBounds(new Rect(item.RenderSize));
            local.Offset(Bounds.X, Bounds.Y);
            return local;
        }

        internal PointD CenterOf(MenuItem item) => Center(BoundsOf(item));

        internal MenuItem? ItemAt(Point point) => Items.FirstOrDefault(item => item.Visibility == Visibility.Visible && BoundsOf(item).Contains(point));

        /// <summary>The highlight the menu gives the item under the pointer.</summary>
        internal void Highlight(MenuItem? target)
        {
            foreach (MenuItem item in Items)
            {
                bool on = ReferenceEquals(item, target);
                if (item.IsHighlighted != on) SetHighlighted.Invoke(item, [on]);
            }
        }

        internal void Draw(DrawingContext dc)
        {
            Menu.UpdateLayout();
            dc.DrawImage(Snapshot(Menu), Bounds);
        }
    }
}
