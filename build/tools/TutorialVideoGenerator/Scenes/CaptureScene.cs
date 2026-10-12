using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// Capture, recording, color and text-copy clips. The capture selector is the product's
/// CaptureOverlayView over a frozen synthetic desktop (the "monitor" is the 960×488 scene), driven
/// through its own pointer handling; menus, countdown, recording controls, notifications and the
/// editors that follow are the product's own UI.
/// </summary>
internal sealed class CaptureScene : TutorialScene
{
    internal static readonly string[] Features = ["text-copy", "capture-region", "record-region", "record-pause", "capture-window",
        "capture-fullscreen", "capture-scroll", "capture-delay", "color-copy"];

    private const double SceneWidth = Stage.Width, SceneHeight = Stage.SceneHeight, TaskbarHeight = 36;
    private static readonly Point EditorOrigin = new(10, 4 + Stage.TitleBarHeight);

    // The synthetic desktop: a notes app with a terminal block and a colour swatch, and a task list.
    private static readonly Rect NoteClient = new(92, 92, 540, 300);
    private static readonly Rect TaskClient = new(676, 132, 248, 196);
    private static readonly Rect Terminal = new(116, 140, 420, 92);
    private static readonly Rect Swatch = new(232, 262, 128, 32);
    private static readonly Rect TaskInput = new(692, 250, 216, 56);
    private const string SwatchHex = "#7C6CFF";

    private readonly string _feature;
    private readonly BitmapSource _desktop;
    private readonly List<IDisposable> _owned = [];
    private OffscreenWindow? _overlayHost;
    private CaptureOverlayView? _overlay;
    private OffscreenWindow? _editorHost;
    private AnnotationEditorWindow? _editor;
    private OffscreenWindow? _videoHost;
    private PointD _cursor;
    private string? _copiedColor;
    private BitmapSource? _notification;
    private RecordingControlWindow? _recording;
    private PointD _dragStart;

    internal CaptureScene(string id, string language) : base(id, language)
    {
        _feature = id;
        _desktop = Stage.Picture(SceneWidth, SceneHeight, dc => DrawDesktop(dc, pasted: null));
        Story = BuildStory();
    }

    internal override string Provenance =>
        "Real CaptureOverlayView (mode bar, magnifier, guides, window highlight, text mode, colour toast) over a frozen synthetic desktop; "
        + "real tray menu (ThemedShellPresenter.CreateMenu), CountdownWindow, RecordingControlWindow, action notification, "
        + "AnnotationEditorWindow and VideoEditorWindow. Desktop apps, taskbar and the auto-scrolled document are synthetic; "
        + "no screen, clipboard, OCR, file or recording transaction runs.";

    // ---- Storyboards -----------------------------------------------------------------------------

    private static PointD Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private TutorialStoryboard BuildStory()
    {
        var start = new PointD(780, 420);
        var story = new TutorialStoryboard(start);
        switch (_feature)
        {
            case "capture-region":
                story.Hold("desktop", 0.9).Hold("overlay", 1.2);
                Drag(story, new PointD(NoteClient.X + 14, NoteClient.Y + 6), new PointD(Terminal.Right + 14, Terminal.Bottom + 12));
                story.Hold("selected", 0.5).Hold("editor", 0.4);
                break;
            case "capture-window":
                story.Hold("desktop", 0.8).Move("point", new PointD(NoteClient.X + 380, NoteClient.Y + 210)).Hold("pointed", 1.0);
                story.Hold("shortcut", 0.8).Hold("editor", 0.4);
                break;
            case "capture-fullscreen":
                TrayRoute(story, "Text_65BB6610ACA0");
                story.Hold("editor", 0.4);
                break;
            case "capture-delay":
                TrayRoute(story, "Text_38FF9B0AA541");
                story.Hold("countdown-3", 1.0).Hold("countdown-2", 1.0).Hold("countdown-1", 1.0).Hold("overlay", 0.6);
                Drag(story, new PointD(NoteClient.X + 14, NoteClient.Y + 6), new PointD(Terminal.Right + 14, Terminal.Bottom + 12), frames: 26);
                story.Hold("selected", 0.3).Hold("editor", 0.4);
                break;
            case "capture-scroll":
                story.Hold("desktop", 0.8).Move("point", new PointD(NoteClient.X + 300, NoteClient.Y + 200)).Hold("pointed", 0.8);
                story.Hold("shortcut", 0.6).Motion("scrolling", 50, story.End, story.End).Hold("scrolled", 0.4).Hold("editor", 0.4);
                break;
            case "color-copy":
                story.Hold("overlay", 1.2).Move("approach", new PointD(Swatch.X + 14, Swatch.Y + 16)).Hold("pixel", 1.4).Hold("copy-key", 0.2).Hold("copied", 1.6);
                story.Hold("closed", 0.5).Move("paste-move", new PointD(TaskInput.X + 20, TaskInput.Y + 20)).Click("paste-click").Hold("paste-key", 0.5).Hold("pasted", 0.3);
                break;
            case "text-copy":
                story.Hold("overlay", 1.0).Hold("text-key", 0.3).Hold("text-mode", 1.1);
                Drag(story, new PointD(Terminal.X + 10, Terminal.Y + 8), new PointD(Terminal.X + 250, Terminal.Bottom - 8));
                story.Hold("selected", 0.3).Hold("copied", 1.8);
                story.Move("paste-move", new PointD(TaskInput.X + 20, TaskInput.Y + 20)).Click("paste-click").Hold("paste-key", 0.5).Hold("pasted", 0.3);
                break;
            case "record-region":
                story.Hold("record-overlay", 1.0);
                Drag(story, new PointD(NoteClient.X - 4, NoteClient.Y - 36), new PointD(RecordRegion.Right, RecordRegion.Bottom), frames: 28);
                story.Hold("ready", 0.8);
                story.Move("start-move", RecordingButton(primary: true, state: "ready")).Click("start");
                for (int second = 0; second < 3; second++) story.Hold("recording", 1.0);
                story.Move("stop-move", RecordingButton(primary: true, state: "recording")).Click("stop").Hold("video", 0.4);
                break;
            case "record-pause":
                story.Hold("recording", 1.0).Hold("recording", 0.6).Move("pause-move", RecordingButton(primary: false, state: "recording")).Click("pause");
                story.Hold("paused", 2.6).Move("resume-move", RecordingButton(primary: false, state: "paused")).Click("resume");
                for (int second = 0; second < 4; second++) story.Hold("resumed", 1.0);
                break;
        }

        return story.Finish("result", 11);
    }

    private void Drag(TutorialStoryboard story, PointD from, PointD to, int frames = 34)
    {
        _dragStart = from;
        story.Move("approach", from).Motion("drag", frames, from, to, pressed: true);
    }

    /// <summary>Tray icon → menu → 다른 캡처 → the command.</summary>
    private void TrayRoute(TutorialStoryboard story, string commandKey)
    {
        story.Hold("desktop", 0.7).Move("tray-move", TrayIcon).Click("tray-click").Hold("tray", 0.6);
        (BitmapSource root, Rect rootBounds, BitmapSource sub, Rect subBounds) = TrayMenus(UiText.Get(commandKey));
        story.Move("to-more", ItemCenter(rootBounds, root, TrayMenuEntries().FindIndex(e => e.Label == UiText.Get("Onboard_TrayMoreCaptures"))), 14)
            .Hold("submenu", 0.6);
        int index = TrayMenuEntries().First(e => e.Children is not null).Children!.ToList().FindIndex(e => e.Label == UiText.Get(commandKey));
        story.Move("to-command", ItemCenter(subBounds, sub, index), 12).Click("command").Hold("command", 0.2);
        _ = root;
    }

    // ---- Frames ------------------------------------------------------------------------------------

    internal override void Draw(DrawingContext dc, TutorialFramePlan frame)
    {
        _cursor = frame.Cursor;
        string segment = frame.Segment;
        Stage.PointerKind pointer = Stage.PointerKind.Arrow;
        bool showPointer = true;
        switch (_feature)
        {
            case "capture-window":
                if (segment is "editor" or "result")
                    DrawEditor(dc, new Rect(NoteClient.X, NoteClient.Y - Stage.TitleBarHeight, NoteClient.Width, NoteClient.Height + Stage.TitleBarHeight));
                else DrawDesktopPhase(dc, frame);
                break;
            case "capture-region":
            case "capture-delay":
                if (segment is "desktop" or "tray-move" or "tray-click" or "tray" or "to-more" or "submenu" or "to-command" or "command" or "countdown-3" or "countdown-2" or "countdown-1")
                    DrawDesktopPhase(dc, frame);
                else if (segment is "editor" or "result") DrawEditor(dc, CaptureRegion());
                else
                {
                    DriveOverlay(frame);
                    DrawOverlay(dc);
                    pointer = OverlayPointer(); // the selector shows the system crosshair
                }
                break;
            case "capture-fullscreen":
                if (segment is "editor" or "result") DrawEditor(dc, new Rect(0, 0, SceneWidth, SceneHeight));
                else DrawDesktopPhase(dc, frame);
                break;
            case "capture-scroll":
                if (segment is "editor" or "result") DrawEditor(dc, null);
                else DrawScrolling(dc, frame);
                break;
            case "color-copy":
                if (segment is "closed" or "paste-move" or "paste-click" or "paste-key" or "pasted" or "result")
                    DrawPasted(dc, segment is "pasted" or "result" ? (_copiedColor ?? SwatchHex) : null);
                else { DriveOverlay(frame); DrawOverlay(dc); pointer = OverlayPointer(); }
                break;
            case "text-copy":
                if (segment is "copied" or "paste-move" or "paste-click" or "paste-key" or "pasted" or "result")
                {
                    DrawPasted(dc, segment is "pasted" or "result" ? Fixtures.TerminalCommand + "\n" + Fixtures.TerminalResult : null);
                    if (segment is "copied") DrawNotification(dc);
                }
                else { DriveOverlay(frame); DrawOverlay(dc); pointer = OverlayPointer(); }
                break;
            case "record-region":
                if (segment is "record-overlay" or "approach" or "drag") { DriveOverlay(frame); DrawOverlay(dc); pointer = OverlayPointer(); }
                else if (segment is "video" or "result") DrawVideoEditor(dc);
                else DrawRecording(dc, frame);
                break;
            case "record-pause":
                DrawRecording(dc, frame);
                break;
        }

        (string caption, string key) = Caption(frame);
        if (frame.Pressed && showPointer) Stage.DrawClick(dc, frame.Cursor, frame.Progress);
        if (showPointer && segment is not ("shortcut" or "scrolling" or "copy-key" or "text-key" or "paste-key")) Stage.DrawPointer(dc, frame.Cursor, pointer);
        Stage.DrawCaption(dc, caption, key);
    }

    // ---- Desktop -------------------------------------------------------------------------------

    private void DrawDesktop(DrawingContext dc, string? pasted, double scroll = 0)
    {
        Stage.DrawDesktop(dc);
        DrawTaskbar(dc);
        Stage.DrawWindowFrame(dc, TaskClient, L("메모", "Notes"));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF7)), null, TaskClient);
        Fixtures.Text(dc, L("오늘 할 일", "Today"), TaskClient.X + 16, TaskClient.Y + 14, 16, Brushes.Black, bold: true);
        Fixtures.Text(dc, L("· 빌드 결과 공유", "· Share the build result"), TaskClient.X + 16, TaskClient.Y + 48, 13, Brushes.DimGray);
        Fixtures.Text(dc, L("· 브랜드 색 확인", "· Check the brand color"), TaskClient.X + 16, TaskClient.Y + 72, 13, Brushes.DimGray);
        dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE3)), 1), TaskInput, 6, 6);
        if (pasted is null) dc.DrawLine(new Pen(Brushes.Black, 1), new Point(TaskInput.X + 12, TaskInput.Y + 10), new Point(TaskInput.X + 12, TaskInput.Y + 28));
        else
        {
            string[] lines = pasted.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                Fixtures.Text(dc, lines[i], TaskInput.X + 10, TaskInput.Y + 8 + i * 20, 14, Brushes.Black, mono: true);
        }

        Stage.DrawWindowFrame(dc, NoteClient, L("릴리스 메모 - 메모장", "Release notes - Notepad"));
        dc.PushClip(new RectangleGeometry(NoteClient));
        dc.PushTransform(new TranslateTransform(0, -scroll));
        DrawNoteContent(dc);
        dc.Pop();
        dc.Pop();
    }

    private void DrawNoteContent(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFB, 0xFC, 0xFE)), null, new Rect(NoteClient.X, NoteClient.Y, NoteClient.Width, NoteClient.Height + 420));
        Fixtures.Text(dc, L("릴리스 메모", "Release notes"), NoteClient.X + 24, NoteClient.Y + 12, 20, Brushes.Black, bold: true);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x30)), null, Terminal, 8, 8);
        Fixtures.Text(dc, Fixtures.TerminalCommand, Terminal.X + 16, Terminal.Y + 14, 19, Brushes.White, mono: true);
        Fixtures.Text(dc, Fixtures.TerminalResult, Terminal.X + 16, Terminal.Y + 50, 19, new SolidColorBrush(Color.FromRgb(0x9A, 0xE1, 0xB1)), mono: true);
        Fixtures.Text(dc, L("브랜드 색상", "Brand color"), NoteClient.X + 24, Swatch.Y + 7, 14, Brushes.DimGray);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x7C, 0x6C, 0xFF)), null, Swatch, 6, 6);
        Fixtures.Text(dc, SwatchHex, Swatch.X + 30, Swatch.Y + 7, 14, Brushes.White, mono: true);
        string[] more = Korean
            ? ["다음 단계", "1. 변경 사항 검토", "2. 배포 일정 공유", "3. 사용 설명 갱신", "4. 지원 문서 공개", "5. 피드백 정리", "6. 다음 회의 준비", "7. 결과 보고"]
            : ["Next steps", "1. Review the changes", "2. Share the release date", "3. Update the guide", "4. Publish support notes", "5. Collect feedback", "6. Prepare the next meeting", "7. Report the results"];
        for (int i = 0; i < more.Length; i++)
            Fixtures.Text(dc, more[i], NoteClient.X + 24, NoteClient.Y + 222 + i * 46 + (i == 0 ? 0 : 6), i == 0 ? 15 : 15, i == 0 ? Brushes.Black : Brushes.DimGray, bold: i == 0);
    }

    private static readonly Rect TaskbarBounds = new(0, SceneHeight - TaskbarHeight, SceneWidth, TaskbarHeight);
    private static PointD TrayIcon => new(SceneWidth - 118, SceneHeight - TaskbarHeight / 2);

    private static void DrawTaskbar(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1F, 0x26)), null, TaskbarBounds);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x44)), 1), TaskbarBounds.TopLeft, TaskbarBounds.TopRight);
        for (int i = 0; i < 5; i++)
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x50)), null,
                new Rect(SceneWidth / 2 - 100 + i * 40, TaskbarBounds.Y + 8, 22, 20), 4, 4);
        BitmapSource? icon = TrayIconImage.Value;
        if (icon is not null) dc.DrawImage(icon, new Rect(TrayIcon.X - 9, TrayIcon.Y - 9, 18, 18));
        Fixtures.Text(dc, "09:30", SceneWidth - 56, TaskbarBounds.Y + 9, 12, Brushes.White);
    }

    private static readonly Lazy<BitmapSource?> TrayIconImage = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray-idle.ico");
        if (!File.Exists(path)) return null;
        BitmapDecoder decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapFrame best = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).First();
        best.Freeze();
        return best;
    });

    private void DrawDesktopPhase(DrawingContext dc, TutorialFramePlan frame)
    {
        dc.DrawImage(_desktop, new Rect(0, 0, SceneWidth, SceneHeight));
        string segment = frame.Segment;
        if (segment is "tray" or "to-more" or "submenu" or "to-command" or "command" || segment == "tray-click" && frame.IsLast)
        {
            (BitmapSource root, Rect rootBounds, BitmapSource sub, Rect subBounds) = TrayMenus(null);
            dc.DrawImage(root, rootBounds);
            if (segment is "submenu" or "to-command" or "command" || segment == "to-more" && frame.IsLast) dc.DrawImage(sub, subBounds);
        }
        else if (segment.StartsWith("countdown-", StringComparison.Ordinal))
        {
            BitmapSource card = Countdown(int.Parse(segment[^1..], System.Globalization.CultureInfo.InvariantCulture));
            double w = card.PixelWidth / Stage.Scale, h = card.PixelHeight / Stage.Scale;
            dc.DrawImage(card, new Rect((SceneWidth - w) / 2, (SceneHeight - h) / 2, w, h));
        }
    }

    // ---- Tray menu -----------------------------------------------------------------------------

    private List<ThemedShellPresenter.MenuEntry> TrayMenuEntries()
    {
        Action noop = () => { };
        var commands = new TrayMenuCommands(noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop);
        return TrayMenuLayout.Build(commands, new HotkeySettings(), canRestoreClosedPin: false, scrollingActive: false).ToList();
    }

    private (BitmapSource Root, Rect RootBounds, BitmapSource Sub, Rect SubBounds)? _trayMenus;

    private (BitmapSource, Rect, BitmapSource, Rect) TrayMenus(string? _)
    {
        if (_trayMenus is { } cached) return cached;
        var presenter = new ThemedShellPresenter(() => false);
        List<ThemedShellPresenter.MenuEntry> entries = TrayMenuEntries();
        BitmapSource root = MenuBitmap(presenter.CreateMenu(entries));
        BitmapSource sub = MenuBitmap(presenter.CreateMenu(entries.First(e => e.Children is not null).Children!));
        double rw = root.PixelWidth / Stage.Scale, rh = root.PixelHeight / Stage.Scale;
        var rootBounds = new Rect(TrayIcon.X - rw + 30, TaskbarBounds.Y - rh - 4, rw, rh);
        double sw = sub.PixelWidth / Stage.Scale, sh = sub.PixelHeight / Stage.Scale;
        int moreIndex = entries.FindIndex(e => e.Children is not null);
        double itemY = ItemCenter(rootBounds, root, moreIndex).Y;
        var subBounds = new Rect(rootBounds.X - sw + 4, Math.Min(itemY - 20, TaskbarBounds.Y - sh - 4), sw, sh);
        _trayMenus = (root, rootBounds, sub, subBounds);
        return _trayMenus.Value;
    }

    private readonly Dictionary<BitmapSource, IReadOnlyList<Rect>> _menuItems = [];

    /// <summary>Renders a real ContextMenu without opening it (a popup would be kept on a real screen).</summary>
    private BitmapSource MenuBitmap(ContextMenu menu)
    {
        // A ContextMenu cannot have a parent; measured on its own it still takes the app's styles.
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        Stage.Pump(20);
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        var items = new List<Rect>();
        foreach (object item in menu.Items)
            if (menu.ItemContainerGenerator.ContainerFromItem(item) is MenuItem container && container.Visibility == Visibility.Visible)
            {
                Point at = container.TranslatePoint(new Point(), menu);
                items.Add(new Rect(at, new Size(container.ActualWidth, container.ActualHeight)));
            }
            else if (item is MenuItem direct && direct.Visibility == Visibility.Visible)
            {
                Point at = direct.TranslatePoint(new Point(), menu);
                items.Add(new Rect(at, new Size(direct.ActualWidth, direct.ActualHeight)));
            }
            else items.Add(Rect.Empty);

        BitmapSource bitmap = OffscreenWindow.SnapshotElement(menu, menu.ActualWidth, menu.ActualHeight);
        _menuItems[bitmap] = items;
        return bitmap;
    }

    private PointD ItemCenter(Rect bounds, BitmapSource menu, int index)
    {
        IReadOnlyList<Rect> items = _menuItems[menu];
        Rect item = index >= 0 && index < items.Count && !items[index].IsEmpty ? items[index] : new Rect(0, 0, bounds.Width, 30);
        return new PointD(bounds.X + item.X + Math.Min(item.Width / 2, 70), bounds.Y + item.Y + item.Height / 2);
    }

    private BitmapSource Countdown(int seconds)
    {
        var window = new CountdownWindow(seconds);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        window.Close();
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
        return OffscreenWindow.SnapshotElement(content, content.ActualWidth, content.ActualHeight, backdrop: null, opaque: false);
    }

    // ---- Capture selector ----------------------------------------------------------------------

    private CaptureOverlayView Overlay()
    {
        if (_overlay is not null) return _overlay;
        var bounds = new RectD(0, 0, Stage.PixelWidth, Math.Round(SceneHeight * Stage.Scale));
        var monitor = new MonitorInfo("Tutorial", bounds, bounds, (uint)Math.Round(96 * Stage.Scale), true);
        BitmapSource frozen = _desktop;
        var view = new CaptureOverlayView(new FrozenFrame(frozen, bounds, monitor, 0), showMagnifier: true, showSelectionGrid: true)
        {
            ReadCursor = () => new PointD(_cursor.X * Stage.Scale, _cursor.Y * Stage.Scale),
            PlaceCursor = _ => false,
            ReadMonitors = () => [monitor],
            TextCaptureAvailable = true,
            CopyColorText = value => { _copiedColor = value; return Task.FromResult(true); },
            OffersOtherActions = _feature != "record-region",
            ShowModeBar = _feature != "record-region",
            SimulatedPointerInside = true,
            DetectWindows = _feature == "capture-window",
            RecordingSelection = _feature == "record-region",
        };
        view.WindowCandidates =
        [
            new WindowCandidate(new IntPtr(2), Physical(new Rect(TaskClient.X, TaskClient.Y - Stage.TitleBarHeight, TaskClient.Width, TaskClient.Height + Stage.TitleBarHeight)), L("메모", "Notes")),
            new WindowCandidate(new IntPtr(1), Physical(new Rect(NoteClient.X, NoteClient.Y - Stage.TitleBarHeight, NoteClient.Width, NoteClient.Height + Stage.TitleBarHeight)), L("릴리스 메모 - 메모장", "Release notes - Notepad")),
        ];
        view.SelectionConfirmed += (_, e) => _confirmed = e;
        var host = new Window { Content = view, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        _overlayHost = new OffscreenWindow(host, SceneWidth, SceneHeight);
        _overlay = view;
        view.InitializePointer();
        view.FlushFeedbackForTest();
        return view;
    }

    private RegionSelectionEventArgs? _confirmed;

    private static RectD Physical(Rect r) => new(r.X * Stage.Scale, r.Y * Stage.Scale, r.Width * Stage.Scale, r.Height * Stage.Scale);

    private void DriveOverlay(TutorialFramePlan frame)
    {
        CaptureOverlayView view = Overlay();
        switch (frame.Segment)
        {
            case "text-key" when frame.IsLast:
                view.TextCaptureMode = true;
                break;
            case "copy-key" when frame.IsLast:
                view.UpdatePointer(false, false);
                _ = view.CopyColorAtPointerAsync();
                Stage.Pump(30);
                break;
            case "drag":
                if (frame.IsFirst)
                {
                    PointD keep = _cursor;
                    _cursor = _dragStart;
                    view.SimulatePointerMove();
                    view.SimulatePointerDown();
                    _cursor = keep;
                }

                view.SimulatePointerMove();
                if (frame.IsLast)
                {
                    view.SimulatePointerUp();
                    Stage.Pump(30);
                }
                break;
            case "click" when frame.IsLast:
                view.SimulatePointerDown();
                view.SimulatePointerUp();
                Stage.Pump(30);
                break;
            default:
                view.SimulatePointerMove();
                break;
        }

        view.FlushFeedbackForTest();
    }

    /// <summary>The selector sets the crosshair cursor; over its mode bar the pointer is an arrow.</summary>
    private Stage.PointerKind OverlayPointer() =>
        _overlay?.PointerOverModeBarForTest == true ? Stage.PointerKind.Arrow : Stage.PointerKind.Cross;

    private void DrawOverlay(DrawingContext dc)
    {
        BitmapSource bitmap = _overlayHost!.Snapshot();
        dc.DrawImage(bitmap, new Rect(0, 0, SceneWidth, SceneHeight));
    }

    /// <summary>The region the selector confirmed, in scene coordinates.</summary>
    private Rect CaptureRegion()
    {
        if (_confirmed is null) throw new InvalidOperationException("The selector did not confirm a region.");
        RectD r = _confirmed.BitmapRegion;
        return new Rect(r.X / Stage.Scale, r.Y / Stage.Scale, r.Width / Stage.Scale, r.Height / Stage.Scale);
    }

    // ---- Editor after a capture ----------------------------------------------------------------

    private void DrawEditor(DrawingContext dc, Rect? region)
    {
        if (_editor is null)
        {
            BitmapSource image = region is { } r ? Crop(r) : ScrolledDocument();
            if (_overlayHost is not null) { _overlayHost.Dispose(); _overlayHost = null; }
            var pixels = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
            _editor = new AnnotationEditorWindow(new FrozenFrame(image, pixels, null, 0), pixels, image,
                sourceWindowTitle: L("릴리스 메모 - 메모장", "Release notes - Notepad"));
            _editorHost = new OffscreenWindow(_editor, 940, 448);
            _editor.Editor.ShowCaptureReceipt(copiedToClipboard: true);
            Stage.Pump(40);
        }

        dc.DrawImage(_desktop, new Rect(0, 0, SceneWidth, SceneHeight));
        Stage.DrawWindow(dc, _editorHost!.Snapshot(), EditorOrigin, _editor.Title);
    }

    private BitmapSource Crop(Rect region)
    {
        var pixel = new Int32Rect((int)Math.Round(region.X * Stage.Scale), (int)Math.Round(region.Y * Stage.Scale),
            (int)Math.Round(region.Width * Stage.Scale), (int)Math.Round(region.Height * Stage.Scale));
        var cropped = new CroppedBitmap(_desktop, pixel);
        // Captures are 96 DPI images of physical pixels; the editor shows them at one pixel per pixel.
        var copy = BitmapSource.Create(pixel.Width, pixel.Height, 96, 96, PixelFormats.Pbgra32, null, Pixels(cropped), pixel.Width * 4);
        copy.Freeze();
        return copy;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    // ---- Scrolling capture ---------------------------------------------------------------------

    private void DrawScrolling(DrawingContext dc, TutorialFramePlan frame)
    {
        double scroll = frame.Segment switch
        {
            "scrolling" => 360 * Stage.Ease(frame.Progress),
            "scrolled" => 360,
            _ => 0,
        };
        if (scroll == 0) dc.DrawImage(_desktop, new Rect(0, 0, SceneWidth, SceneHeight));
        else DrawDesktop(dc, pasted: null, scroll);
    }

    /// <summary>The stitched result: the notes window's whole document.</summary>
    private BitmapSource ScrolledDocument() => Retag(Stage.Picture(NoteClient.Width, NoteClient.Height + 360, dc =>
    {
        dc.PushTransform(new TranslateTransform(-NoteClient.X, -NoteClient.Y));
        DrawNoteContent(dc);
        dc.Pop();
    }));

    private static BitmapSource Retag(BitmapSource source)
    {
        var copy = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, Pixels(source), source.PixelWidth * 4);
        copy.Freeze();
        return copy;
    }

    // ---- Copy results --------------------------------------------------------------------------

    private void DrawPasted(DrawingContext dc, string? text)
    {
        if (_overlayHost is not null) { _overlayHost.Dispose(); _overlayHost = null; }
        DrawDesktop(dc, text);
    }

    private void DrawNotification(DrawingContext dc)
    {
        if (_notification is null)
        {
            var presenter = new ThemedShellPresenter(() => false);
            int characters = TextCaptureService.CountCharacters(Fixtures.TerminalCommand + "\n" + Fixtures.TerminalResult);
            Window card = presenter.CreateActionNotification(UiText.Get("TextCapture_Copied"),
                UiText.Format("TextCapture_CopiedDetail", characters), UiText.Get("TextCapture_OpenAdvanced"), () => { });
            card.SizeToContent = SizeToContent.Manual;
            using var host = new OffscreenWindow(card, 360, 160);
            card.SizeToContent = SizeToContent.Height;
            Stage.Pump(60);
            _notification = host.Snapshot();
        }

        double w = _notification.PixelWidth / Stage.Scale, h = _notification.PixelHeight / Stage.Scale;
        var bounds = new Rect(SceneWidth - w - 16, SceneHeight - TaskbarHeight - h - 12, w, h);
        Stage.DrawWindowFrame(dc, bounds, string.Empty, caption: false);
        dc.DrawImage(_notification, bounds);
    }

    // ---- Recording -----------------------------------------------------------------------------

    private static readonly Rect RecordRegion = new(NoteClient.X - 4, NoteClient.Y - 36, NoteClient.Width + 8, 296);

    private RecordingControlWindow RecordingControls()
    {
        if (_recording is not null) return _recording;
        _recording = new RecordingControlWindow(Physical(RecordRegion), new RecordingSettings(),
            () => throw new InvalidOperationException("The tutorial never starts a recorder."),
            () => throw new InvalidOperationException("The tutorial never writes a recording."),
            NullLogger.Instance);
        return _recording;
    }

    /// <summary>Lays the recording window's content out as the product places it, without showing the window.</summary>
    private (BitmapSource Picture, Point Origin) RecordingPicture()
    {
        RecordingControlWindow window = RecordingControls();
        var content = (FrameworkElement)window.Content;
        double width = window.Width, height = window.Height;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        BitmapSource picture = OffscreenWindow.SnapshotElement(content, width, height, backdrop: null, opaque: false);
        return (picture, new Point(window.Left / Stage.Scale, window.Top / Stage.Scale));
    }

    private PointD RecordingButton(bool primary, string state)
    {
        RecordingControlWindow window = RecordingControls();
        if (state != "ready") window.PreviewRecordingForTest(paused: state == "paused", TimeSpan.FromSeconds(2));
        (_, Point origin) = RecordingPicture();
        Button button = primary ? window.PrimaryButtonForTest : window.PauseButtonForTest;
        Point center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), (UIElement)window.Content);
        _recording = null; // a fresh window for the clip itself
        return new PointD(origin.X + center.X, origin.Y + center.Y);
    }

    private void DrawRecording(DrawingContext dc, TutorialFramePlan frame)
    {
        RecordingControlWindow window = RecordingControls();
        string segment = frame.Segment;
        // The timer counts recorded time only: it shows the seconds recorded before this picture.
        bool recording, paused = false;
        if (_feature == "record-region")
        {
            recording = segment is "recording" or "stop-move" or "stop" || segment == "start" && frame.IsLast;
            if (recording) window.PreviewRecordingForTest(paused: false, TimeSpan.FromSeconds(Math.Floor(_recordingSeconds)));
        }
        else
        {
            paused = segment is "paused" or "resume-move" || segment == "pause" && frame.IsLast || segment == "resume" && !frame.IsLast;
            recording = !paused;
            window.PreviewRecordingForTest(paused, TimeSpan.FromSeconds(3 + Math.Floor(_recordingSeconds)));
        }

        if (recording) _recordingSeconds += frame.Repeat / (double)TutorialStoryboard.Fps;
        dc.DrawImage(_desktop, new Rect(0, 0, SceneWidth, SceneHeight));
        (BitmapSource picture, Point origin) = RecordingPicture();
        dc.DrawImage(picture, new Rect(origin.X, origin.Y, picture.PixelWidth / Stage.Scale, picture.PixelHeight / Stage.Scale));
    }

    private double _recordingSeconds;

    private void DrawVideoEditor(DrawingContext dc)
    {
        if (_videoHost is null)
        {
            string folder = Path.Combine(Program.WorkFolder, $"{Id}.{(Korean ? "ko" : "en")}");
            Directory.CreateDirectory(folder);
            BitmapSource recorded = Crop(RecordRegion);
            int w = recorded.PixelWidth & ~1, h = recorded.PixelHeight & ~1;
            var even = new CroppedBitmap(recorded, new Int32Rect(0, 0, w, h));
            RecordingResult recording = MyCapture.App.Diagnostics.UxReviewSelfTest.CreateFixtureVideo(Path.Combine(folder, "recording.mp4"), even);
            var paths = MyCapture.Core.Storage.AppPaths.CreateForRoot(Path.Combine(folder, "data"));
            var loggers = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            _owned.Add(loggers);
            var editor = new VideoEditorWindow(recording, paths, loggers);
            _videoHost = new OffscreenWindow(editor, 960, 540);
            Stage.Pump(1500);
        }

        dc.DrawImage(_desktop, new Rect(0, 0, SceneWidth, SceneHeight));
        const double scale = 0.82;
        BitmapSource snapshot = _videoHost.Snapshot(scale);
        double width = snapshot.PixelWidth / Stage.Scale;
        Stage.DrawWindow(dc, snapshot, new Point((SceneWidth - width) / 2, 4 + Stage.TitleBarHeight), _videoHost.Window.Title, caption: true);
    }

    // ---- Captions ------------------------------------------------------------------------------

    private (string Caption, string Key) Caption(TutorialFramePlan frame)
    {
        string s = frame.Segment;
        return _feature switch
        {
            "capture-region" => s switch
            {
                "desktop" => (L("영역 캡처 시작", "Start a region capture"), ""),
                "overlay" or "approach" => (L("캡처 선택 화면", "The capture selector opens"), ""),
                "drag" or "selected" => (L("캡처할 부분을 드래그", "Drag over the area to capture"), ""),
                _ => (L("편집기에서 결과 확인 · 클립보드와 라이브러리에 저장됨", "Review it in the editor · copied and saved to the library"), ""),
            },
            "capture-window" => s switch
            {
                "desktop" or "point" or "pointed" => (L("캡처할 창 위에 포인터를 놓기", "Point at the window to capture"), ""),
                "shortcut" => (L("지정한 창 캡처 단축키 실행", "Use your assigned Window capture shortcut"), ""),
                _ => (L("포인터 아래 창의 캡처 결과 확인", "Review the window under the pointer"), ""),
            },
            "capture-fullscreen" => s switch
            {
                "desktop" or "tray-move" or "tray-click" => (L("트레이 아이콘을 클릭", "Click the tray icon"), ""),
                "tray" or "to-more" or "submenu" or "to-command" or "command" => (L("다른 캡처 › 전체 화면 캡처 선택", "More captures › Full-screen capture"), ""),
                _ => (L("해당 모니터의 전체 화면 캡처 결과 확인", "Review the full-screen capture of that monitor"), ""),
            },
            "capture-delay" => s switch
            {
                "desktop" or "tray-move" or "tray-click" => (L("트레이 아이콘을 클릭", "Click the tray icon"), ""),
                "tray" or "to-more" or "submenu" or "to-command" or "command" => (L("다른 캡처 › 지연 캡처 선택", "More captures › Delayed capture"), ""),
                "countdown-3" or "countdown-2" or "countdown-1" => (L("대기 시간 동안 캡처할 화면 준비", "Prepare the screen during the countdown"), ""),
                "overlay" or "approach" or "drag" or "selected" => (L("선택 화면이 나타나면 영역을 드래그", "When the selector appears, drag the area"), ""),
                _ => (L("편집기에서 결과 확인", "Review it in the editor"), ""),
            },
            "capture-scroll" => s switch
            {
                "desktop" or "point" or "pointed" => (L("스크롤할 창 위에 포인터를 놓기", "Point at the window to scroll"), ""),
                "shortcut" or "scrolling" or "scrolled" => (L("지정한 스크롤 캡처 단축키 실행 · 자동 스크롤 대기", "Use your Scrolling capture shortcut and wait for the scroll"), ""),
                _ => (L("이어 붙인 캡처 확인 · 같은 명령으로 취소", "Review the stitched capture · the same command cancels"), ""),
            },
            "color-copy" => s switch
            {
                "overlay" or "approach" => (L("영역 캡처에서 복사할 색상 위에 포인터 놓기", "In the selector, point at the color"), ""),
                "pixel" => (L("돋보기에서 색상 값 확인", "Check the value in the magnifier"), ""),
                "copy-key" or "copied" => (L("C로 색상 값 복사", "Press C to copy the value"), "C"),
                "closed" or "paste-move" or "paste-click" => (L("붙여 넣을 곳으로 이동", "Go to where you want it"), ""),
                _ => (L("Ctrl+V로 붙여 넣기 · 형식은 색상 형식 설정을 따름", "Paste with Ctrl+V · the format follows your setting"), "Ctrl+V"),
            },
            "text-copy" => s switch
            {
                "overlay" => (L("캡처 선택 화면", "The capture selector"), ""),
                "text-key" or "text-mode" => (L("T로 텍스트 복사로 전환", "Press T to switch to copy text"), "T"),
                "approach" or "drag" or "selected" => (L("복사할 글자 영역을 드래그", "Drag over the words to copy"), ""),
                "copied" => (L("자동으로 인식해 복사 · 텍스트 복사됨 알림", "Recognized and copied · Text copied notification"), ""),
                "paste-move" or "paste-click" => (L("붙여 넣을 곳으로 이동", "Go to where you want the text"), ""),
                _ => (L("Ctrl+V로 붙여 넣기", "Paste with Ctrl+V"), "Ctrl+V"),
            },
            "record-region" => s switch
            {
                "record-overlay" or "approach" or "drag" => (L("녹화할 영역을 드래그", "Drag the area to record"), ""),
                "ready" or "start-move" or "start" => (L("녹화 컨트롤에서 시작 선택", "Click Start in the recording controls"), ""),
                "recording" or "stop-move" => (L("녹화 중 · 화면을 조작", "Recording · work on the screen"), ""),
                "stop" => (L("중지 선택", "Click Stop"), ""),
                _ => (L("녹화 편집기에서 결과 확인", "Review it in the recording editor"), ""),
            },
            _ => s switch
            {
                "recording" or "pause-move" => (L("녹화 중 일시정지 선택", "Click Pause while recording"), ""),
                "pause" or "paused" => (L("일시정지 중에는 녹화되지 않음", "Paused time is not recorded"), ""),
                "resume-move" or "resume" => (L("같은 버튼으로 녹화 이어가기", "Resume with the same button"), ""),
                _ => (L("같은 영역 녹화가 이어짐", "Recording continues in the same area"), ""),
            },
        };
    }

    public override void Dispose()
    {
        _overlay?.ReleaseResources();
        _overlayHost?.Dispose();
        _editorHost?.Dispose();
        _videoHost?.Dispose();
        try { _recording?.Close(); } catch (InvalidOperationException) { }
        foreach (IDisposable owned in Enumerable.Reverse(_owned)) owned.Dispose();
    }
}
