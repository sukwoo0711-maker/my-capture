using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using MyCapture.Tests;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>The practice picture, real pin controls and real pin history, seen through a close camera.</summary>
internal sealed class PracticePinScene : TutorialScene
{
    internal static readonly string[] Features =
        ["pin-resize", "pin-opacity", "practice-image-pin", "practice-pin-close", "pin-restore"];

    private static readonly RectD HiddenArea = new(-26000, -26000, 4000, 4000);
    private static readonly MonitorInfo HiddenMonitor = new("tutorial", HiddenArea, HiddenArea, 96, true);
    private static readonly FieldInfo DragInputField = Field("_dragInput");
    private static readonly FieldInfo HoverTimerField = Field("_hoverHideTimer");
    private static readonly FieldInfo FeedbackField = Field("_feedbackCountdown");
    private static readonly FieldInfo FeedbackTimerField = typeof(FeedbackCountdown).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LandingTimerField = Field("_landingResetTimer");
    private static readonly FieldInfo LandingAccentField = Field("_landingAccentAmount");
    private static readonly MethodInfo UpdateChrome = typeof(PinWindow).GetMethod("UpdateChromeColor", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly DependencyPropertyKey MouseOverKey = (DependencyPropertyKey)typeof(UIElement)
        .GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static readonly MethodInfo HighlightItem = typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!.GetSetMethod(true)!;

    private readonly PinSettings _settings = new() { CloseOnDoubleClick = false, RestoreOpenPinsOnLaunch = false };
    private readonly HotkeySettings _keys = NewInstallDefaults.Create().Hotkeys;
    private readonly Func<HotkeySettings> _oldKeys;
    private readonly PinManager _manager;
    private readonly BitmapSource _picture = PracticePicture.Load();
    private readonly DragInput _input = new();
    private readonly Point _pinOrigin;
    private readonly double _camera;
    private PinWindow? _pin;
    private PinLandingDecoration? _landing;
    private double? _landedAt;
    private WelcomeWindow? _guide;
    private OffscreenWindow? _guideHost;
    private MenuCamera? _opacityMenu, _restoreMenu, _visibleMenu;
    private MenuItem? _opacity60, _reopen;
    private PointD _corner, _cornerEnd, _opacityButton, _closeButton;
    private int _previousRepeat;
    private double _clock;
    private double _closedZoom, _closedOpacity;
    private (int Left, int Top) _closedOrigin;
    private Guid _closedLogicalId;
    private uint _copiedVersion;

    internal PracticePinScene(string id, string language) : base(id, language)
    {
        _oldKeys = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => _keys;
        _camera = id == "pin-resize" ? 1.6 : id == "practice-image-pin" ? 1.45 : 2;
        _pinOrigin = id == "practice-image-pin" ? new Point(578, 178) : new Point(170, 148);
        _manager = new PinManager(() => _settings, NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = () => HiddenMonitor,
            ReadRestoreMonitorsForTest = () => [HiddenMonitor],
            ActivatePinForTest = _ => { },
            BeforeShowForTest = pin =>
            {
                pin.ShowActivated = false;
                pin.ShowInTaskbar = false;
                pin.Topmost = false;
                pin.Focusable = false;
                pin.ReadWorkArea = _ => HiddenArea;
                DragInputField.SetValue(pin, _input);
                pin.Left = -24000;
                pin.Top = -24000;
            },
        };
        try
        {
            if (id == "practice-image-pin") PrepareCopiedGuide();
            else
            {
                _pin = _manager.PinImage(_picture);
                FreezeClocks(_pin);
                LandingAccentField.SetValue(_pin, 0d);
                UpdateChrome.Invoke(_pin, [false]);
                PrepareControls();
            }
            Story = BuildStory();
        }
        catch { Dispose(); throw; }
    }

    internal override string Provenance =>
        "The packaged PracticePicture is shared by all practice clips. Real PinWindow, PinHoverBar and detached product menus "
        + "are rendered directly with RenderTargetBitmap and enlarged for the guide preview. Corner resizing invokes the real "
        + "WPF mouse callbacks with an IPinDragInput that never captures the system mouse. The 60% menu item and close button "
        + "run their product handlers. PinManager receives an injected in-memory clipboard read and owns close/encode/restore; "
        + "the welcome copy uses RecordingClipboard, never the Windows clipboard. The desktop, background lines and taskbar "
        + "are synthetic. Landing uses the product ornament and video time; no popup, resident App or user-profile write runs.";

    private static FieldInfo Field(string name) => typeof(PinWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(PinWindow), name);

    private void PrepareCopiedGuide()
    {
        if (!ClipboardWriter.IsTestOverrideInstalled)
            throw new InvalidOperationException("Practice rendering requires the in-memory clipboard gateway.");
        _guide = new WelcomeWindow(_keys, AfterCaptureAction.QuickAccess, options: new WelcomeOptions
        {
            Replay = true, FeaturePractice = true, ReducedMotion = true,
            HintDelay = TimeSpan.FromHours(1), MonitorProvider = () => HiddenMonitor,
            CopyPracticePicture = PracticePicture.Copy,
        });
        _guide.Focusable = false;
        _guide.PreviewGotKeyboardFocus += (_, e) => e.Handled = true;
        // Use the product's missing-video fallback during rendering, avoiding a recursive thumbnail
        // of this very clip when installed assets from a previous generation are present.
        typeof(WelcomeTutorialPreview).GetProperty(nameof(WelcomeTutorialPreview.AssetDirectory), BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_guide.Preview, System.IO.Path.Combine(Program.WorkFolder, "uninstalled-practice-preview"));
        _guideHost = new OffscreenWindow(_guide, 520, 440);
        if (_guide.Session.Step != PracticeStep.B0 || !_guide.TryNowButton.IsVisible)
            throw new InvalidOperationException("The guide's copy action is not visible before preparing the picture.");
        _guide.TryNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_guide.Session.Step != PracticeStep.B1 || RecordingClipboard.LastWrite is not { } write
            || !write.Formats.TryGetValue(DataFormats.Bitmap, out object? copied) || copied is not BitmapSource bitmap)
            throw new InvalidOperationException("The guide did not copy its practice picture to the isolated clipboard.");
        RequireSamePixels(_picture, bitmap);
        _copiedVersion = _guide.Session.ClipboardSequence;
        if (_copiedVersion == 0) throw new InvalidOperationException("The prepared picture has no clipboard sequence.");
        RequireGuideActionState();
    }

    private void RequireGuideActionState()
    {
        if (_guide is null) return;
        bool externalAction = _guide.Session.Step == PracticeStep.B2 && _guide.Session.WindowPinId is not null;
        Visibility expected = externalAction ? Visibility.Collapsed : Visibility.Visible;
        if (_guide.TryNowButton.Visibility != expected || _guide.TryNowButton.IsDefault == externalAction
            || _guide.TryNowButton.IsTabStop == externalAction)
            throw new InvalidOperationException("The practice guide's primary action does not match the current pin step.");
        if (!_guide.PreviewShown || _guide.ReplayButton.Visibility != Visibility.Visible)
            throw new InvalidOperationException("The practice guide lost its replay control while the pin is open.");
    }

    private void PrepareControls()
    {
        PinWindow pin = RequiredPin;
        pin.UpdateLayout();
        _corner = ToStage(new Point(pin.Width - 1, pin.Height - 1));
        _cornerEnd = new PointD(_corner.X + 120 * _camera, _corner.Y + 70 * _camera);
        pin.ShowHoverBar();
        Stage.Pump(35);
        PinHoverBar bar = pin.HoverBarForTest!;
        _opacityButton = ButtonCenter(bar.OpacityButton);
        _closeButton = ButtonCenter(bar.CloseButton);
        if (Id == "pin-opacity")
        {
            ContextMenu menu = bar.CreateOpacityMenu();
            _opacity60 = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "60%");
            _opacityMenu = new MenuCamera(menu, _opacity60, new Point(454, 106), 2.2, cropToTarget: false);
        }
        if (Id == "pin-restore")
        {
            var shell = new ThemedShellPresenter(static () => true);
            ContextMenu menu = shell.CreateMenu(TrayMenu.Entries(new TrayMenuState(CanRestoreClosedPin: true, PinCount: 0),
                restoreClosedPin: Restore));
            _reopen = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == Ui("Onboard_TrayReopenPin"));
            _restoreMenu = new MenuCamera(menu, _reopen, new Point(138, 66), 2, cropToTarget: true);
        }
        pin.HideHoverBar();
    }

    private TutorialStoryboard BuildStory()
    {
        var story = new TutorialStoryboard(new PointD(860, 402));
        switch (Id)
        {
            case "pin-resize":
                return story.Hold("open", 1).Move("corner", _corner, 20).Hold("corner", .8)
                    .Motion("resize", 44, _corner, _cornerEnd, true).Hold("released", .5)
                    .Move("away", new PointD(890, 445), 12).Finish("result", 8.4);
            case "pin-opacity":
                return story.Hold("open", .8).Move("hover", ToStage(new Point(110, 80)), 17).Hold("hover", .4)
                    .Move("opacity", _opacityButton, 16).Hold("opacity", .4).Click("open-menu")
                    .Hold("menu", .8).Move("sixty", _opacityMenu!.CenterOf(_opacity60!), 17)
                    .Hold("sixty", .7).Click("choose-opacity").Move("away", new PointD(870, 420), 13).Finish("result", 8.4);
            case "practice-pin-close":
                return story.Hold("open", 1).Move("hover", ToStage(new Point(110, 80)), 20).Hold("hover", .8)
                    .Move("close", _closeButton, 18).Hold("close", .7).Click("close-pin").Finish("closed", 8);
            case "practice-image-pin":
                return story.Hold("copied", 2).Move("beside", new PointD(748, 250), 18).Hold("shortcut", 1.2)
                    .Motion("opened", LandingFrames, story.End, story.End).Finish("result", 8.4);
            case "pin-restore":
                return story.Hold("open", .8).Move("hover", ToStage(new Point(110, 80)), 10)
                    .Move("close", _closeButton, 12).Click("close-pin").Hold("closed", .7)
                    .Move("tray", Taskbar.TrayIcon, 18).Click("tray-click").Hold("restore-menu", .7)
                    .Move("reopen", _restoreMenu!.CenterOf(_reopen!), 18).Hold("reopen", .5).Click("restore-pin")
                    .Motion("restored", LandingFrames, story.End, story.End).Finish("result", 8.8);
            default: throw new NotSupportedException(Id);
        }
    }

    internal override void Draw(DrawingContext dc, TutorialFramePlan frame)
    {
        double elapsed = _previousRepeat / (double)TutorialStoryboard.Fps;
        _clock += elapsed;
        _previousRepeat = frame.Repeat;
        if (_pin is { IsClosed: false } pin) pin.AdvanceFeedbackForTest(TimeSpan.FromSeconds(elapsed));
        Apply(frame);
        if (_pin is { IsClosed: false } current) FreezeClocks(current);
        Stage.Pump(8);
        if (_pin is { IsClosed: false } live) FreezeClocks(live);
        AdvanceLanding();
        Stage.DrawDesktop(dc);
        DrawBackground(dc);
        if (_guideHost is { } guideHost)
        {
            RequireGuideActionState();
            var client = new Rect(18, 34, 520, 440);
            Stage.DrawWindowFrame(dc, client, Ui("Help.Welcome"));
            dc.DrawImage(guideHost.Snapshot(), client);
        }
        if (Id == "pin-restore") Taskbar.Draw(dc, Korean, new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.FromHours(9)));
        DrawPin(dc);
        _visibleMenu?.Draw(dc, frame.Cursor);
        string key = Id == "practice-image-pin" && frame.Segment == "shortcut" ? _keys.PasteToScreen.ToString() : string.Empty;
        Stage.PointerKind pointer = Id == "pin-resize" && frame.Segment is "corner" or "resize" or "released"
            ? Stage.PointerKind.SizeNwse
            : frame.Segment is "opacity" or "sixty" or "choose-opacity" or "close" or "close-pin" or "reopen" or "restore-pin"
                ? Stage.PointerKind.Hand : Stage.PointerKind.Arrow;
        PracticeOverlay(dc, frame, Caption(frame.Segment), key, pointer);
    }

    private void Apply(TutorialFramePlan frame)
    {
        switch (frame.Segment)
        {
            case "hover" when frame.IsLast:
                RequiredPin.ShowHoverBar();
                break;
            case "resize":
                PinWindow resizing = RequiredPin;
                if (frame.IsFirst)
                {
                    _input.Down(resizing);
                    if (!resizing.IsPracticeResizeInProgress) throw new InvalidOperationException("The bottom-right corner did not start a resize.");
                }
                _input.Move(resizing, (int)Math.Round((frame.Cursor.X - _corner.X) / _camera), (int)Math.Round((frame.Cursor.Y - _corner.Y) / _camera));
                if (frame.IsLast)
                {
                    _input.Up(resizing);
                    resizing.ForceZoomSettleForTest();
                    if (_input.Captured || resizing.State.Zoom < 1.45 || resizing.State.Zoom > 1.55)
                        throw new InvalidOperationException("The actual edge drag did not finish at the expected larger size.");
                }
                break;
            case "open-menu" when frame.IsLast:
                // The button's handler would open a real popup; render its very same menu detached.
                _visibleMenu = _opacityMenu;
                break;
            case "choose-opacity" when frame.IsLast:
                _opacity60!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, _opacity60));
                if (Math.Abs(RequiredPin.State.Opacity - .6) > .0001 || Math.Abs(RequiredPin.Opacity - .6) > .0001)
                    throw new InvalidOperationException("The 60% product menu item did not apply 60% opacity.");
                _visibleMenu = null;
                RequiredPin.HideHoverBar();
                break;
            case "close-pin" when frame.IsLast:
                PinWindow closing = RequiredPin;
                _closedZoom = closing.State.Zoom;
                _closedOpacity = closing.State.Opacity;
                _closedLogicalId = closing.LogicalPracticeId;
                var beforeClose = closing.PhysicalBounds;
                _closedOrigin = (beforeClose.Left, beforeClose.Top);
                closing.HoverBarForTest!.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Await(_manager.LastClosedEncodingForTest);
                if (!closing.IsClosed || _manager.ClosedCount != 1) throw new InvalidOperationException("Closing the practice pin did not preserve its restore history.");
                break;
            case "shortcut" when frame.IsFirst:
                _guide!.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
                break;
            case "opened" when frame.IsFirst:
                PasteResult pasted = Await(_manager.PasteFromClipboardAsync(() => Task.FromResult(
                    ClipboardImageReader.PinReadAttempt.Success(PinContent.FromImage(_picture)) with { ClipboardSequence = _copiedVersion })));
                if (pasted != PasteResult.Pinned) throw new InvalidOperationException("The prepared picture was not pinned.");
                _pin = _manager.SnapshotOpenPinWindows().Single();
                RequireSamePixels(_picture, _pin.PinnedContent.Image);
                bool accepted = _guide!.Notify(new PracticeEvent(PracticeEventKind.PinOpened, _guide.Session.Id,
                    LogicalPinId: _pin.LogicalPracticeId, WindowPinId: _pin.PracticeId, ClipboardSequence: _copiedVersion));
                if (!accepted || _guide.Session.Step != PracticeStep.B2)
                    throw new InvalidOperationException("The guide did not accept the matching picture pin.");
                RequireGuideActionState();
                StartLanding();
                break;
            case "tray-click" when frame.IsLast:
                _visibleMenu = _restoreMenu;
                break;
            case "restore-pin" when frame.IsLast:
                _reopen!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, _reopen));
                _visibleMenu = null;
                break;
        }
        if (_pin is { IsClosed: false, HoverBarForTest: { } bar })
        {
            foreach (Button button in new[] { bar.CopyButton, bar.SaveButton, bar.OpacityButton, bar.CloseButton })
            {
                Rect bounds = ButtonBounds(button);
                button.SetValue(MouseOverKey, _visibleMenu is null && bounds.Contains(new Point(frame.Cursor.X, frame.Cursor.Y)));
            }
        }
    }

    private void Restore()
    {
        _pin = Await(_manager.RestoreLastClosedAsync(_closedLogicalId)) ?? throw new InvalidOperationException("The practice pin could not be restored.");
        if (_pin.LogicalPracticeId != _closedLogicalId || Math.Abs(_pin.State.Zoom - _closedZoom) > .0001
            || Math.Abs(_pin.State.Opacity - _closedOpacity) > .0001)
            throw new InvalidOperationException("Restoring changed the practice pin's identity, size or opacity.");
        var restoredBounds = _pin.PhysicalBounds;
        if ((restoredBounds.Left, restoredBounds.Top) != _closedOrigin)
            throw new InvalidOperationException($"Restoring moved the practice pin from ({_closedOrigin.Left}, {_closedOrigin.Top}) "
                + $"to ({restoredBounds.Left}, {restoredBounds.Top}).");
        RequireSamePixels(_picture, _pin.PinnedContent.Image);
        StartLanding();
    }

    private string Caption(string segment)
    {
        int step = Id switch
        {
            "pin-resize" => segment is "open" or "corner" ? 1 : segment == "resize" ? 2 : 3,
            "pin-opacity" => segment is "open" or "hover" ? 1 : segment is "opacity" or "open-menu" or "menu" ? 2 : 3,
            "practice-image-pin" => segment is "copied" or "beside" ? 1 : segment == "shortcut" ? 2 : 3,
            "practice-pin-close" => segment is "open" or "hover" ? 1 : segment is "close" or "close-pin" ? 2 : 3,
            "pin-restore" => segment is "open" or "hover" or "close" or "close-pin" or "closed" ? 1
                : segment is "tray" or "tray-click" or "restore-menu" or "reopen" or "restore-pin" ? 2 : 3,
            _ => throw new NotSupportedException(Id),
        };
        return Ui($"Tutorial.{Id}.Step{step}");
    }

    private void DrawBackground(DrawingContext dc)
    {
        if (Id == "practice-image-pin") return;
        // Neutral ruled paper gives the 60% result a clear visible reference behind the picture.
        var paper = new Rect(110, 92, 690, 354);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE9, 0xEE, 0xF5)), null, paper);
        for (double y = paper.Top + 34; y < paper.Bottom; y += 34)
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xB5, 0xC3, 0xD7)), 2), new Point(paper.Left, y), new Point(paper.Right, y));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xBD, 0x8D, 0x90)), 2), new Point(146, paper.Top), new Point(146, paper.Bottom));
    }

    private void DrawPin(DrawingContext dc)
    {
        if (_pin is not { IsClosed: false, IsVisible: true } pin) return;
        var root = (FrameworkElement)VisualTreeHelper.GetChild(pin, 0);
        root.UpdateLayout();
        double reveal = _landedAt is { } at ? Math.Clamp((_clock - at) * 1000 / FluidMotion.FastDuration.TimeSpan.TotalMilliseconds, 0, 1) : 1;
        dc.PushOpacity(pin.Opacity * reveal);
        dc.DrawImage(OffscreenWindow.SnapshotElement(root, root.ActualWidth, root.ActualHeight, _camera, opaque: false),
            new Rect(_pinOrigin, new Size(root.ActualWidth * _camera, root.ActualHeight * _camera)));
        dc.Pop();
        if (_landing is { IsClosed: false, Content: FrameworkElement ornament })
        {
            RectD layout = _landing.Layout.PhysicalBounds;
            ornament.Measure(new Size(layout.Width, layout.Height));
            ornament.Arrange(new Rect(ornament.DesiredSize));
            ornament.UpdateLayout();
            dc.DrawImage(OffscreenWindow.SnapshotElement(ornament, layout.Width, layout.Height, opaque: false),
                new Rect(layout.X, layout.Y, layout.Width, layout.Height));
        }
        if (pin.HoverBarForTest is not { IsVisible: true } bar) return;
        var barRoot = (FrameworkElement)VisualTreeHelper.GetChild(bar, 0);
        dc.DrawImage(OffscreenWindow.SnapshotElement(barRoot, barRoot.ActualWidth, barRoot.ActualHeight, _camera, opaque: false), BarBounds());
    }

    private Rect BarBounds()
    {
        PinWindow pin = RequiredPin;
        PinHoverBar bar = pin.HoverBarForTest!;
        bar.UpdateLayout();
        RectD bounds = PinHoverBar.Place(new RectD(_pinOrigin.X / _camera, _pinOrigin.Y / _camera, pin.Width, pin.Height),
            bar.ActualWidth, bar.ActualHeight, new RectD(0, 0, Stage.Width / _camera, Stage.SceneHeight / _camera), 1);
        return new Rect(bounds.X * _camera, bounds.Y * _camera, bar.ActualWidth * _camera, bar.ActualHeight * _camera);
    }

    private Rect ButtonBounds(Button button)
    {
        Rect bar = BarBounds();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(RequiredPin.HoverBarForTest!, 0);
        Rect local = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
        return new Rect(bar.X + local.X * _camera, bar.Y + local.Y * _camera, local.Width * _camera, local.Height * _camera);
    }
    private PointD ButtonCenter(Button button) { Rect bounds = ButtonBounds(button); return new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); }
    private PointD ToStage(Point local) => new(_pinOrigin.X + local.X * _camera, _pinOrigin.Y + local.Y * _camera);
    private PinWindow RequiredPin => _pin ?? throw new InvalidOperationException("No practice pin is open.");

    private static void FreezeClocks(PinWindow pin)
    {
        ((DispatcherTimer)HoverTimerField.GetValue(pin)!).Stop();
        ((DispatcherTimer)FeedbackTimerField.GetValue(FeedbackField.GetValue(pin))!).Stop();
        (LandingTimerField.GetValue(pin) as DispatcherTimer)?.Stop();
    }

    private static readonly int LandingFrames = (int)Math.Ceiling(PinLandingDecoration.OutlineEndMs * TutorialStoryboard.Fps / 1000);
    private void StartLanding()
    {
        PinWindow pin = RequiredPin;
        var bounds = new RectD(_pinOrigin.X, _pinOrigin.Y, pin.Width * _camera, pin.Height * _camera);
        _landing = new PinLandingDecoration(pin, PinLandingDecoration.LayoutFor(bounds, 1), new Stopwatch());
        _landedAt = _clock;
    }
    private void AdvanceLanding()
    {
        if (_landedAt is not { } start || _pin is not { IsClosed: false } pin) return;
        double ms = (_clock - start) * 1000;
        _landing?.Advance(ms);
        LandingAccentField.SetValue(pin, ms < PinLandingDecoration.OutlineEndMs ? PinLandingDecoration.Render(ms).BorderAccentAmount : 0d);
        UpdateChrome.Invoke(pin, [false]);
        if (ms >= PinLandingDecoration.OutlineEndMs) { _landing = null; _landedAt = null; }
    }

    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(10)) Stage.Pump(10);
        if (!task.IsCompleted) throw new TimeoutException("The practice pin operation did not finish.");
        task.GetAwaiter().GetResult();
    }
    private static void RequireSamePixels(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight)
            throw new InvalidOperationException("The practice picture changed dimensions.");
        int stride = first.PixelWidth * 4;
        var a = new byte[stride * first.PixelHeight]; var b = new byte[a.Length];
        new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0).CopyPixels(a, stride, 0);
        new FormatConvertedBitmap(second, PixelFormats.Bgra32, null, 0).CopyPixels(b, stride, 0);
        if (!a.AsSpan().SequenceEqual(b)) throw new InvalidOperationException("The practice picture pixels changed.");
    }

    public override void Dispose()
    {
        if (_landing is { IsClosed: false } landing) landing.Close();
        _guideHost?.Dispose();
        _manager.CloseAllForExit();
        ShortcutHelpWindow.ReadHotkeys = _oldKeys;
    }

    private sealed class DragInput : IPinDragInput
    {
        private Point _anchor;
        private (int X, int Y) _start;
        public ModifierKeys Modifiers => ModifierKeys.None;
        public (int X, int Y) CursorPosition { get; private set; }
        internal bool Captured { get; private set; }
        public Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner) => _anchor;
        public bool LeftButtonPressed(MouseEventArgs args) => Captured;
        public bool TryCapture(PinWindow owner) => Captured = true;
        public bool HasCapture(PinWindow owner) => Captured;
        public void Release(PinWindow owner) => Captured = false;
        internal void Down(PinWindow pin)
        {
            _anchor = new Point(pin.Width - 1, pin.Height - 1);
            _start = CursorPosition = (pin.PhysicalBounds.Left + (int)Math.Round(_anchor.X), pin.PhysicalBounds.Top + (int)Math.Round(_anchor.Y));
            Invoke(pin, "OnMouseLeftButtonDown", new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        }
        internal void Move(PinWindow pin, int x, int y)
        {
            CursorPosition = (_start.X + x, _start.Y + y);
            Invoke(pin, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        }
        internal void Up(PinWindow pin) => Invoke(pin, "OnMouseLeftButtonUp", new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        private static void Invoke(PinWindow pin, string method, MouseEventArgs args) =>
            typeof(PinWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pin, [args]);
    }

    private sealed class MenuCamera
    {
        private readonly ContextMenu _menu;
        private readonly Rect _source, _target;
        private readonly double _scale;
        internal MenuCamera(ContextMenu menu, MenuItem item, Point origin, double scale, bool cropToTarget)
        {
            _menu = menu;
            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            menu.Arrange(new Rect(menu.DesiredSize));
            menu.UpdateLayout();
            if (item.ActualHeight <= 0) throw new InvalidOperationException("The detached product menu did not lay out.");
            Rect choice = item.TransformToAncestor(menu).TransformBounds(new Rect(item.RenderSize));
            double height = cropToTarget ? Math.Min(164, menu.ActualHeight) : menu.ActualHeight;
            double top = cropToTarget ? Math.Clamp(choice.Y + choice.Height / 2 - height / 2, 0, menu.ActualHeight - height) : 0;
            _source = new Rect(0, top, menu.ActualWidth, height);
            _scale = Math.Min(scale, (Stage.Width - origin.X - 38) / _source.Width);
            _target = new Rect(origin, new Size(_source.Width * _scale, _source.Height * _scale));
            if (_target.Bottom > Stage.SceneHeight) throw new InvalidOperationException("The enlarged menu exceeds the scene.");
        }
        internal PointD CenterOf(MenuItem item)
        {
            Rect local = item.TransformToAncestor(_menu).TransformBounds(new Rect(item.RenderSize));
            return new(_target.X + (local.X + local.Width / 2) * _scale, _target.Y + (local.Y + local.Height / 2 - _source.Y) * _scale);
        }
        internal void Draw(DrawingContext dc, PointD cursor)
        {
            foreach (MenuItem item in _menu.Items.OfType<MenuItem>())
            {
                Rect local = item.TransformToAncestor(_menu).TransformBounds(new Rect(item.RenderSize));
                var bounds = new Rect(_target.X + local.X * _scale, _target.Y + (local.Y - _source.Y) * _scale, local.Width * _scale, local.Height * _scale);
                HighlightItem.Invoke(item, [bounds.Contains(new Point(cursor.X, cursor.Y))]);
            }
            _menu.UpdateLayout();
            dc.PushClip(new RectangleGeometry(_target));
            dc.DrawImage(OffscreenWindow.SnapshotElement(_menu, _menu.ActualWidth, _menu.ActualHeight, _scale, opaque: false),
                new Rect(_target.X, _target.Y - _source.Y * _scale, _menu.ActualWidth * _scale, _menu.ActualHeight * _scale));
            dc.Pop();
        }
    }
}
