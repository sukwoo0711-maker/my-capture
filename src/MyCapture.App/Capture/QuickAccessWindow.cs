using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Capture;

/// <summary>A keyboard-accessible capture receipt. Optional actions never delay durable capture.</summary>
internal sealed class QuickAccessWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ProgressBar _remaining = new() { Minimum = 0, Maximum = 100, Value = 100, Height = 3, BorderThickness = new Thickness(0) };
    private readonly ToggleButton _keep;
    // Up to two wrapped lines beside the close button, where the card already has room.
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = StatusLineHeight, MaxHeight = StatusLineHeight * 2,
    };
    private readonly System.Windows.Shapes.Path _statusGlyph = new()
    {
        Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.6, Margin = new Thickness(0, 1, 6, 0),
        VerticalAlignment = VerticalAlignment.Center, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };
    private readonly DockPanel _statusRow = new() { Margin = new Thickness(2, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _progressReveal = new() { Interval = FeedbackTiming.ProgressRevealDelay };
    private readonly HashSet<string> _running = [];
    private readonly Dictionary<string, Button> _actionButtons = [];
    private FeedbackKind _statusKind = FeedbackKind.Echo;
    private bool _heldForFailure;
    private bool _progressShown;
    private TimeSpan _elapsed;
    private long _lastTick;
    private bool IsBusy => _running.Count > 0;
    private Point _dragStart;
    private bool _dragArmed;
    private bool _keyboardInteracting;
    private readonly List<Control> _keyboardTargets = [];
    private bool _keyboardAccess;
    private readonly Image _preview;
    private readonly TextBlock _dimensions = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly RectD? _capturedRegion;
    private readonly MonitorInfo? _targetMonitor;
    private MonitorInfo? _placementMonitor;
    private NativeHotkeyRegistrar? _activationKey;
    private bool _activationRegistered;
    private const int ActivationKeyId = 0x7102;
    private readonly Dictionary<string, Action> _actions = [];
    internal IReadOnlyDictionary<string, Action> ShortcutActions => _actions;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    private const double StatusLineHeight = 16;
    internal static DispatcherOperation Schedule(Dispatcher dispatcher, Action show) =>
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, show);
    internal bool IsKept { get => _keep.IsChecked == true; set => _keep.IsChecked = value; }
    internal bool CanActivateByHotkey => IsVisible && _timer.IsEnabled && !IsKept;
    internal bool IsPointerOver => IsVisible && WindowStyleFacade.IsPointerOverWindow(new WindowInteropHelper(this).Handle);
    internal string TimeoutStateForTest => $"enabled={_timer.IsEnabled}; elapsed={_elapsed.TotalSeconds:0.000}; keep={IsKept}; keyboard={_keyboardInteracting}/{IsKeyboardFocusWithin}; busy={IsBusy}; held={_heldForFailure}";

    /// <summary>Test hook: the status line, its kind, and whether the progress indicator runs.</summary>
    internal (string Text, FeedbackKind Kind, bool Visible, bool Progress) StatusForTest =>
        (AutomationProperties.GetName(_status), _statusKind, _statusRow.Visibility == Visibility.Visible, _progressShown);

    /// <summary>Test hook: display time the card has left before it closes itself.</summary>
    internal TimeSpan RemainingForTest => Lifetime - _elapsed;

    /// <summary>Test hook: the progress reveal delay passing while an action still runs.</summary>
    internal void ElapseProgressDelayForTest() => RevealProgress();

    internal QuickAccessWindow(BitmapSource bitmap, Func<Task<bool>> copy, Action edit,
        Func<Task> pin, Func<Task<bool>> save, Action library, Func<Task<string>> export, bool excludeFromCapture = true,
        RectD? capturedRegion = null, MonitorInfo? targetMonitor = null)
    {
        Title = UiText.Get("QuickAccess.Title");
        // Wide enough for an icon with its short name on each of the four actions.
        Width = 344;
        _capturedRegion = capturedRegion; _targetMonitor = targetMonitor;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var panel = new StackPanel { Margin = new Thickness(8) };
        var close = Button("Dialog.Close", Close);
        close.Content = Glyph("Icon.Close");
        close.Width = 26; close.Height = 26; close.MinHeight = 0; close.MinWidth = 0;
        close.Padding = new Thickness(4); close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.SetResourceReference(StyleProperty, "Button.Ghost");
        // The status shares the close button's row, so a one-line message never moves the actions.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Caption");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        DockPanel.SetDock(_statusGlyph, Dock.Left);
        _statusRow.Children.Add(_statusGlyph);
        _statusRow.Children.Add(_status);
        header.Children.Add(_statusRow);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        panel.Children.Add(header);
        var row = new DockPanel { LastChildFill = true };
        _keep = new ToggleButton { Content = Glyph("Icon.Unlock"), Width = 24, Height = 30, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(2), Focusable = false, ToolTip = UiText.Get("Onboard_QuickKeep") };
        _keyboardTargets.Add(_keep);
        AutomationProperties.SetName(_keep, UiText.Get("Onboard_QuickKeep"));
        ShortToolTip(_keep);
        DockPanel.SetDock(_keep, Dock.Right); row.Children.Add(_keep);
        var preview = _preview = new Image { Source = bitmap, Width = 52, Height = 48, Stretch = Stretch.Uniform,
            ToolTip = UiText.Get("QuickAccess.Drag"), Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(preview, Dock.Left); row.Children.Add(preview);
        AutomationProperties.SetName(preview, UiText.Get("QuickAccess.Drag"));
        preview.MouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(preview); _dragArmed = true; };
        preview.MouseLeftButtonUp += (_, _) => _dragArmed = false;
        preview.MouseMove += async (_, e) =>
        {
            Point p = e.GetPosition(preview);
            if (!_dragArmed || _running.Contains("Drag") || e.LeftButton != MouseButtonState.Pressed
                || (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
            _dragArmed = false;
            await RunAsync("Drag", async () =>
            {
                string path = await export();
                // The file is ready; the drag itself is the user's own gesture, not work to wait on.
                if (_running.Count == 1) EndProgressDisplay();
                if (IsVisible && Mouse.LeftButton == MouseButtonState.Pressed)
                    DragDrop.DoDragDrop(preview, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
                return true;
            }, "QuickAccess.Preparing", "QuickAccess.Ready");
        };
        _actions["Copy"] = async () => await RunAsync("Copy", copy, "QuickAccess.Copying", "QuickAccess.Copied");
        _actions["Edit"] = () => { edit(); Close(); };
        _actions["Pin"] = async () => await RunAsync("Pin", async () => { await pin(); return true; }, "QuickAccess.Pinning", "QuickAccess.Pinned");
        _actions["Save"] = async () => await RunAsync("Save", save, "QuickAccess.Saving", "QuickAccess.Saved");
        _actions["Close"] = Close;
        _actions["Activate"] = () => ActivateForKeyboard();
        var actions = new UniformGrid { Columns = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (string action in new[] { "Copy", "Edit", "Pin", "Save" })
        {
            Button button = Button("QuickAccess." + action, _actions[action]);
            // An icon over its short name; the key stays in the tooltip and the accelerator key.
            var face = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            face.Children.Add(Glyph("Icon." + action));
            face.Children.Add(new TextBlock
            {
                Text = AutomationProperties.GetName(button), FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            button.Content = face;
            button.MinWidth = 0; button.MinHeight = 46; button.Padding = new Thickness(1, 4, 1, 3); button.Margin = new Thickness(1);
            ShortcutPresentation.Attach(button, ShortcutScope.QuickAccess, action);
            ShortToolTip(button);
            _actionButtons[action] = button;
            actions.Children.Add(button);
        }
        row.Children.Add(actions); panel.Children.Add(row);
        var footer = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var libraryButton = Button("QuickAccess.Library", () => { library(); Close(); });
        libraryButton.MinHeight = 0; libraryButton.Padding = new Thickness(2); libraryButton.FontSize = 11;
        libraryButton.SetResourceReference(StyleProperty, "Button.Ghost");
        DockPanel.SetDock(libraryButton, Dock.Right); footer.Children.Add(libraryButton);
        _dimensions.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
        _dimensions.FontSize = 10; _dimensions.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        footer.Children.Add(_dimensions); panel.Children.Add(footer);
        _keyboardTargets.Remove(close);
        _keyboardTargets.Add(close);
        // The countdown is neutral; colour is kept for the result it reports.
        _remaining.SetResourceReference(BackgroundProperty, "Surface.Hover");
        _remaining.SetResourceReference(ForegroundProperty, "Border.Strong");
        _progressReveal.Tick += (_, _) => RevealProgress();
        panel.Children.Add(_remaining);
        var surface = new Border { Child = panel, Margin = new Thickness(8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        surface.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        surface.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        surface.SetResourceReference(EffectProperty, "Shadow.Card");
        Content = surface;
        ToolTip = UiText.Get("Onboard_QuickCardTip");
        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            WindowStyleFacade.SetNoActivate(hwnd, true);
            // Native close fades can outlive the HWND and appear in the next screenshot.
            CaptureWindowExclusion.DisableTransitions(this);
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivateHook);
            if (excludeFromCapture) CaptureWindowExclusion.TryApply(this);
        };
        Loaded += (_, _) =>
        {
            PlaceOnMonitor(_targetMonitor ?? MonitorEnumerator.GetFromCursor());
            _lastTick = Stopwatch.GetTimestamp();
            if (!IsKept) _timer.Start();
            UpdateActivationHotkey();
        };
        _timer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastTick, now);
            _lastTick = now;
            Advance(elapsed, IsPointerOver || IsMouseCaptured || (_keyboardInteracting && IsKeyboardFocusWithin) || IsBusy);
        };
        SizeChanged += (_, _) => { if (IsLoaded && IsVisible && _placementMonitor is { } monitor) PlaceOnMonitor(monitor); };
        Closed += (_, _) => { _timer.Stop(); _progressReveal.Stop(); UpdateActivationHotkey(); _activationKey = null; };
        IsVisibleChanged += (_, _) => UpdateActivationHotkey();
        _keep.Checked += (_, _) => { _keep.Content = Glyph("Icon.Lock"); _timer.Stop(); UpdateActivationHotkey(); };
        _keep.Unchecked += (_, _) =>
        {
            _keep.Content = Glyph("Icon.Unlock");
            if (IsVisible) { _lastTick = Stopwatch.GetTimestamp(); _timer.Start(); }
            UpdateActivationHotkey();
        };
        Point? cardDrag = null;
        Point cardOrigin = default;
        bool cardMoved = false;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!IsKeyboardEntryBackground(e.OriginalSource as DependencyObject)) return;
            cardDrag = PointToScreen(e.GetPosition(this));
            cardOrigin = new Point(Left, Top);
            cardMoved = false;
            CaptureMouse();
            e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (cardDrag is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            Point screen = PointToScreen(e.GetPosition(this));
            var dpi = VisualTreeHelper.GetDpi(this);
            Vector delta = screen - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
            Left = cardOrigin.X + delta.X / dpi.DpiScaleX;
            Top = cardOrigin.Y + delta.Y / dpi.DpiScaleY;
            cardMoved = true;
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (cardDrag is null) return;
            cardDrag = null; ReleaseMouseCapture(); e.Handled = true;
            if (!cardMoved && !_keyboardAccess) ActivateForKeyboard();
        };
        LostMouseCapture += (_, _) => cardDrag = null;
        PreviewKeyDown += (_, e) =>
        {
            if (!_keyboardAccess || !IsKeyboardFocusWithin) return;
            _keyboardInteracting = true;
            e.Handled = HandleShortcut(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        };
        Deactivated += (_, _) =>
        {
            _keyboardAccess = false;
            _keyboardInteracting = false;
            foreach (Control target in _keyboardTargets) target.Focusable = false;
            if (IsVisible) WindowStyleFacade.SetNoActivate(new WindowInteropHelper(this).Handle, true);
        };
    }

    private static void ShortToolTip(DependencyObject control)
    {
        ToolTipService.SetInitialShowDelay(control, 100);
        ToolTipService.SetBetweenShowDelay(control, 0);
        ToolTipService.SetShowOnDisabled(control, true);
    }

    private void UpdateActivationHotkey()
    {
        if (!CanActivateByHotkey)
        {
            if (_activationRegistered) _activationKey?.Unregister(ActivationKeyId);
            _activationRegistered = false;
            return;
        }
        if (_activationRegistered) return;
        _activationKey ??= new NativeHotkeyRegistrar(new WindowInteropHelper(this).Handle);
        ShortcutDefinition activation = ShortcutCatalog.Get(ShortcutScope.QuickAccess, "Activate");
        _activationRegistered = _activationKey.TryRegister(ActivationKeyId,
            new Hotkey((HotkeyModifiers)activation.Modifiers, (uint)KeyInterop.VirtualKeyFromKey(activation.Key)), out _, out _);
        ToolTip = UiText.Get(_activationRegistered ? "Onboard_QuickCardTip" : "Onboard_QuickCardTipNoHotkey");
    }

    internal bool HandleShortcut(Key key, ModifierKeys modifiers) => ShortcutCatalog.Execute(ShortcutScope.QuickAccess, key, modifiers, _actions);

    private FrameworkElement Glyph(string key)
    {
        Geometry geometry = TryFindResource(key) as Geometry ?? Geometry.Parse("M4,4 H16 V16 H4 Z");
        var path = new System.Windows.Shapes.Path { Data = geometry, StrokeThickness = 1.6, Stretch = Stretch.Uniform, Width = 18, Height = 18 };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text.Primary");
        return path;
    }

    private IntPtr NoActivateHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == ActivationKeyId && CanActivateByHotkey)
        {
            handled = true;
            _ = Dispatcher.BeginInvoke(new Action(() => { if (CanActivateByHotkey) ActivateForKeyboard(); }));
            return IntPtr.Zero;
        }
        if (message == 0x0021 && !_keyboardAccess) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE: action clicks and drag keep the destination active.
        }
        return IntPtr.Zero;
    }

    internal bool ActivateForKeyboard()
    {
        if (!IsVisible) return false;
        EnableKeyboardAccess();
        if (!Activate()) return false;
        return MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private bool IsKeyboardEntryBackground(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null && current != this;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is ButtonBase || ReferenceEquals(current, _preview)) return false;
        }
        return source is not null;
    }

    private void EnableKeyboardAccess()
    {
        _keyboardAccess = true;
        _keyboardInteracting = true;
        WindowStyleFacade.SetNoActivate(new WindowInteropHelper(this).Handle, false);
        foreach (Control target in _keyboardTargets) target.Focusable = true;
    }

    internal void UpdatePreview(BitmapSource image)
    {
        _preview.Source = image;
        _dimensions.Text = $"{image.PixelWidth} × {image.PixelHeight}";
    }

    internal void DismissForCapture(Action foregroundDismissed)
    {
        if (!IsVisible) return;
        Close();
        foregroundDismissed();
    }

    internal void PlaceOnMonitor(MonitorInfo monitor)
    {
        _placementMonitor = monitor;
        Rect position = Position(monitor.WorkArea, monitor.ScaleFactor, ActualWidth, ActualHeight, _capturedRegion);
        PhysicalWindowPositioner.PlaceTopmostWithoutActivation(new WindowInteropHelper(this).Handle,
            new RectD(position.Left * monitor.ScaleFactor, position.Top * monitor.ScaleFactor,
                ActualWidth * monitor.ScaleFactor, ActualHeight * monitor.ScaleFactor));
    }

    internal static Rect Position(RectD work, double scale, double width, double height, RectD? capturedRegion = null)
    {
        double left = work.Left / scale, top = work.Top / scale, right = work.Right / scale, bottom = work.Bottom / scale;
        double x = right - width - 12, y = bottom - height - 12;
        if (capturedRegion is { } region && (region.Width < work.Width || region.Height < work.Height))
        {
            x = region.Right / scale + 8;
            y = region.Bottom / scale + 8;
            if (x + width > right - 4) x = region.Right / scale - width;
            if (y + height > bottom - 4) y = region.Bottom / scale - height - 8;
        }
        return new Rect(Math.Clamp(x, left, Math.Max(left, right - width)), Math.Clamp(y, top, Math.Max(top, bottom - height)), width, height);
    }

    internal void Advance(TimeSpan time, bool interacting)
    {
        // A failure keeps the card until the user closes it or a retry succeeds.
        if (IsKept || interacting || _heldForFailure) return;
        _elapsed += time;
        UpdateRemaining();
        if (_elapsed >= Lifetime) Close();
    }

    private void UpdateRemaining() => _remaining.Value = Math.Max(0, 100 * (1 - _elapsed.TotalSeconds / Lifetime.TotalSeconds));

    private Button Button(string key, Action action)
    {
        var button = new Button { Content = UiText.Get(key), MinHeight = 36, Padding = new Thickness(6), Margin = new Thickness(2), Focusable = false };
        _keyboardTargets.Add(button);
        AutomationProperties.SetName(button, UiText.Get(key));
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// Runs one card action. Its button is disabled and the status says what is happening at
    /// once; work that outlasts <see cref="FeedbackTiming.ProgressRevealDelay"/> also runs the
    /// progress indicator. Other actions stay usable: each renders its own copy of the image.
    /// </summary>
    internal async Task RunAsync(string action, Func<Task<bool>> work, string progress, string success)
    {
        if (!_running.Add(action)) return;
        Button? button = _actionButtons.GetValueOrDefault(action);
        bool refocus = button?.IsKeyboardFocused == true;
        if (button is not null) button.IsEnabled = false;
        ShowStatus(UiText.Get(progress), FeedbackKind.Progress);
        if (_running.Count == 1) _progressReveal.Start();
        string message;
        FeedbackKind kind;
        try
        {
            (message, kind) = await work()
                ? (UiText.Get(success), FeedbackKind.Confirmation)
                : (UiText.Get("QuickAccess.Failed"), FeedbackKind.ActionRequired);
        }
        catch (Exception error)
        {
            (message, kind) = (UserFacingError.GetMessage(error) ?? UiText.Get("QuickAccess.Failed"), FeedbackKind.ActionRequired);
        }
        finally
        {
            _running.Remove(action);
            if (button is not null) button.IsEnabled = true;
            if (_running.Count == 0) EndProgressDisplay();
        }

        if (refocus && IsActive) button!.Focus();
        ShowStatus(message, kind);
    }

    /// <summary>
    /// Shows the status line. A success keeps the card for at least
    /// <see cref="FeedbackTiming.HoldAfterResult"/>; a failure keeps it until it is closed.
    /// </summary>
    private void ShowStatus(string text, FeedbackKind kind)
    {
        _statusKind = kind;
        KoreanWordWrap.SetText(_status, text);
        _status.ToolTip = text;
        bool failed = FeedbackTiming.StaysUntilDismissed(kind);
        _status.SetResourceReference(TextBlock.ForegroundProperty, failed ? "State.Danger" : kind == FeedbackKind.Progress ? "Text.Secondary" : "Text.Primary");
        _statusGlyph.Data = kind switch
        {
            FeedbackKind.Confirmation => TryFindResource("Icon.Check") as Geometry,
            FeedbackKind.ActionRequired => FailureGlyph,
            _ => null,
        };
        _statusGlyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, failed ? "State.Danger" : "State.Success");
        _statusGlyph.Visibility = _statusGlyph.Data is null ? Visibility.Collapsed : Visibility.Visible;
        _statusRow.Visibility = Visibility.Visible;
        if (kind != FeedbackKind.Progress)
        {
            _heldForFailure = failed;
            // While held, the countdown bar would promise a close that is not coming.
            _remaining.Visibility = failed && !IsBusy ? Visibility.Hidden : Visibility.Visible;
            if (!failed)
            {
                TimeSpan hold = Lifetime - FeedbackTiming.HoldAfterResult;
                if (_elapsed > hold) _elapsed = hold;
                UpdateRemaining();
            }
        }

        FeedbackTiming.Announce(_status, text, kind, "MyCapture.QuickAccess.Status");
    }

    // A circled exclamation mark in the 20-unit icon grid; frozen, because cards live on any UI thread.
    private static readonly Geometry FailureGlyph = Frozen(Geometry.Parse("M10,2.5 A7.5,7.5 0 1 1 9.99,2.5 Z M10,6.2 V10.8 M10,13.6 V13.7"));

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private void RevealProgress()
    {
        _progressReveal.Stop();
        if (!IsBusy) return;
        // Reduced motion keeps a steady accent bar; the status text already says what is running.
        _progressShown = true;
        _remaining.Visibility = Visibility.Visible;
        _remaining.SetResourceReference(ForegroundProperty, "Accent.Default");
        _remaining.IsIndeterminate = FluidMotion.AnimationsEnabled;
        if (!FluidMotion.AnimationsEnabled) _remaining.Value = 100;
    }

    private void EndProgressDisplay()
    {
        _progressReveal.Stop();
        _progressShown = false;
        _remaining.IsIndeterminate = false;
        _remaining.SetResourceReference(ForegroundProperty, "Border.Strong");
        UpdateRemaining();
        if (_statusKind == FeedbackKind.Progress) _statusRow.Visibility = Visibility.Collapsed;
    }
}
