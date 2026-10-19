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
    private readonly ProgressBar _remaining = new() { Minimum = 0, Maximum = 100, Value = 100, Height = 2, BorderThickness = new Thickness(0) };
    private readonly ToggleButton _keep;
    // Up to two wrapped lines beside the close button, where the card already has room.
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = StatusLineHeight, MaxHeight = StatusLineHeight * 2,
    };
    // Inline with the status text, on the shared 20x20 grid at its inline size. The glyph's own
    // inset on the grid is part of the gap, so the margin is smaller than a filled box needs.
    private readonly SymbolIcon _statusGlyph = new()
    {
        Size = SymbolIcon.InlineSize, Margin = new Thickness(0, 1, 3, 0), VerticalAlignment = VerticalAlignment.Center,
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
    private readonly Border _previewSurface;
    private DpiScale _previewDpi = new(1, 1);
    private readonly TextBlock _firstUseTip = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 2, 6) };
    private readonly TextBlock _dimensions = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly RectD? _capturedRegion;
    private readonly MonitorInfo? _targetMonitor;
    private readonly ReceiptKeyboardRouter? _keyboardRouter;
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

    /// <param name="copyText">
    /// Recognises the capture's text and copies it. Without one, the action reports that text
    /// recognition is unavailable.
    /// </param>
    internal QuickAccessWindow(BitmapSource bitmap, Func<Task<bool>> copy, Action edit,
        Func<Task> pin, Func<Task<bool>> save, Action library, Func<Task<string>> export, bool excludeFromCapture = true,
        RectD? capturedRegion = null, MonitorInfo? targetMonitor = null, Func<Task<TextCaptureOutcome>>? copyText = null,
        bool copiedToClipboard = true, string? firstCaptureTip = null, ReceiptKeyboardRouter? keyboardRouter = null)
    {
        Title = UiText.Get("QuickAccess.Title");
        // Fits a 1366 x 768 work area at 150% scaling.
        Width = 344;
        _capturedRegion = capturedRegion; _targetMonitor = targetMonitor;
        _keyboardRouter = keyboardRouter;
        _previewDpi = targetMonitor is null ? VisualTreeHelper.GetDpi(this)
            : new DpiScale(targetMonitor.ScaleFactor, targetMonitor.ScaleFactor);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = !excludeFromCapture || CaptureWindowExclusion.SupportsLayeredWindows;
        Background = AllowsTransparency ? Brushes.Transparent : CaptureWindowExclusion.OpaqueBackground();
        if (!AllowsTransparency)
        {
            ModernWindowChrome.SetEnabled(this, false);
            Width -= 16; // Remove only the two 8-DIP shadow margins; keep the content width.
        }
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var panel = new StackPanel { Margin = new Thickness(8) };
        var close = Button("Dialog.Close", Close);
        close.Content = Glyph("Icon.Close");
        CommandButtonPresentation.Apply(close, compact: true);
        close.Width = close.Height = close.MinWidth = close.MinHeight = 28;
        close.Padding = new Thickness(4); close.Margin = new Thickness(4);
        close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top;
        close.SetResourceReference(StyleProperty, "Button.Ghost");
        close.SetResourceReference(BackgroundProperty, "Surface.Floating");
        close.ToolTip = UiText.Get("Dialog.Close") + "  Esc";
        AutomationProperties.SetAcceleratorKey(close, "Esc");
        // This button's focus border uses neutral text ink in every theme.
        close.Resources["Border.Focus"] = ThemeBrushes.Follow("Text.Primary", SystemColors.ControlTextBrush);
        ShortToolTip(close);
        var preview = _preview = new Image
        {
            Source = bitmap, Stretch = Stretch.Uniform, ToolTip = UiText.Get("QuickAccess.Drag"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _previewSurface = new Border { Child = preview, ClipToBounds = true };
        _previewSurface.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        UpdatePreview(bitmap);
        var previewArea = new Grid();
        previewArea.Children.Add(_previewSurface); previewArea.Children.Add(close);
        panel.Children.Add(previewArea);
        AutomationProperties.SetAutomationId(preview, "QuickAccess.Preview");
        AutomationProperties.SetName(preview, UiText.Get("QuickAccess.Drag"));
        var receipt = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        receipt.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        receipt.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        receipt.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Caption");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        DockPanel.SetDock(_statusGlyph, Dock.Left);
        _statusRow.Children.Add(_statusGlyph); _statusRow.Children.Add(_status);
        receipt.Children.Add(_statusRow);
        _dimensions.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Caption");
        _dimensions.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _dimensions.Margin = new Thickness(2, 0, 6, 0);
        AutomationProperties.SetAutomationId(_dimensions, "QuickAccess.Dimensions");
        Grid.SetColumn(_dimensions, 1); receipt.Children.Add(_dimensions);
        _keep = new ToggleButton
        {
            Content = Glyph("Icon.Pause"), Focusable = false, ToolTip = UiText.Get("QuickAccess.KeepName"),
        };
        CommandButtonPresentation.Apply(_keep, compact: true);
        _keep.Width = _keep.Height = _keep.MinWidth = _keep.MinHeight = 28;
        _keep.Padding = new Thickness(4); _keep.Margin = new Thickness(0);
        _keyboardTargets.Add(_keep);
        AutomationProperties.SetName(_keep, UiText.Get("QuickAccess.KeepName"));
        AutomationProperties.SetHelpText(_keep, UiText.Get("Onboard_QuickKeep"));
        ShortToolTip(_keep);
        Grid.SetColumn(_keep, 2); receipt.Children.Add(_keep);
        panel.Children.Add(receipt);
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
        _actions["Edit"] = () =>
        {
            try { edit(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The card stays with the reason, so the user can retry instead of losing it.
                ShowStatus(UserFacingError.GetMessage(error) ?? FailureText("Edit"), FeedbackKind.ActionRequired);
                return;
            }
            Close();
        };
        _actions["Pin"] = async () => await RunAsync("Pin", async () => { await pin(); return true; }, "QuickAccess.Pinning", "QuickAccess.Pinned");
        _actions["Save"] = async () => await RunAsync("Save", save, "QuickAccess.Saving", "QuickAccess.Saved");
        _actions["CopyText"] = async () => await RunCoreAsync("CopyText",
            async () => DescribeTextOutcome(copyText is null ? new TextCaptureOutcome(TextCaptureStatus.Unavailable, null, 0) : await copyText()),
            "QuickAccess.CopyingText");
        _actions["Close"] = Close;
        _actions["Activate"] = () => ActivateForKeyboard();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (string action in ActionOrder)
        {
            Button button = Button("QuickAccess." + action, _actions[action]);
            CommandButtonPresentation.Apply(button, compact: true);
            button.Height = button.MinHeight = 32;
            button.Margin = new Thickness(2, 0, 2, 0);
            button.Padding = new Thickness(6, 0, 6, 0);
            if (action == "Edit")
            {
                button.Content = new TextBlock { Text = UiText.Get("QuickAccess.Edit") };
                button.MinWidth = 60;
                button.SetResourceReference(StyleProperty, "Button.Primary");
            }
            else
            {
                button.Content = Glyph(action == "CopyText" ? "Icon.Ocr" : "Icon." + action);
                button.Width = button.MinWidth = 32;
                button.SetResourceReference(StyleProperty, "Button.Ghost");
            }
            string key = ShortcutCatalog.Get(ShortcutScope.QuickAccess, action).Hint;
            button.ToolTip = AutomationProperties.GetName(button) + "  " + key;
            AutomationProperties.SetAcceleratorKey(button, key);
            ShortToolTip(button);
            _actionButtons[action] = button;
            actions.Children.Add(button);
        }
        var libraryButton = Button("QuickAccess.Library", () => { library(); Close(); });
        CommandButtonPresentation.Apply(libraryButton, compact: true);
        libraryButton.SetResourceReference(StyleProperty, "Button.Ghost");
        libraryButton.Height = libraryButton.MinHeight = 32;
        libraryButton.Padding = new Thickness(4, 0, 4, 0);
        libraryButton.Margin = new Thickness(2, 0, 0, 0);
        actions.Children.Add(libraryButton); panel.Children.Add(actions);
        _firstUseTip.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Caption");
        _firstUseTip.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _firstUseTip.Text = firstCaptureTip ?? string.Empty;
        _firstUseTip.Visibility = firstCaptureTip is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_firstUseTip, _firstUseTip.Text);
        AutomationProperties.SetHelpText(_firstUseTip, UiText.Get("Onboard_QuickCardTip"));
        panel.Children.Add(_firstUseTip);
        ShowStatus(UiText.Get(copiedToClipboard ? "QuickAccess.Copied" : "QuickAccess.InitialSaved"), FeedbackKind.Confirmation);
        _keyboardTargets.Remove(close);
        _keyboardTargets.Add(close);
        // The countdown is neutral; colour is kept for the result it reports.
        _remaining.SetResourceReference(BackgroundProperty, "Surface.Hover");
        _remaining.SetResourceReference(ForegroundProperty, "Border.Strong");
        _progressReveal.Tick += (_, _) => RevealProgress();
        _remaining.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_remaining);
        var surface = new Border { Child = panel, Margin = new Thickness(AllowsTransparency ? 8 : 0),
            CornerRadius = new CornerRadius(AllowsTransparency ? 12 : 0), BorderThickness = new Thickness(1) };
        surface.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        surface.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        if (AllowsTransparency) surface.SetResourceReference(EffectProperty, "Shadow.Card");
        Content = surface;
        ToolTip = UiText.Get("Onboard_QuickCardTip");
        if (_keyboardRouter is not null)
        {
            _keyboardRouter.RegistrationChanged += RefreshSharedActivationHint;
            RefreshSharedActivationHint();
        }
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
        Closed += (_, _) =>
        {
            _timer.Stop(); _progressReveal.Stop();
            if (_keyboardRouter is not null)
            {
                _keyboardRouter.RegistrationChanged -= RefreshSharedActivationHint;
                _keyboardRouter.Remove(this);
            }
            else UpdateActivationHotkey();
            _activationKey = null;
        };
        IsVisibleChanged += (_, _) => UpdateActivationHotkey();
        // The toggle's own checked fill shows the state; its word stays the same.
        _keep.Checked += (_, _) => { _timer.Stop(); UpdateActivationHotkey(); };
        _keep.Unchecked += (_, _) =>
        {
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
        if (_keyboardRouter is not null)
        {
            _keyboardRouter.SetTarget(this, new WindowInteropHelper(this).Handle, CanActivateByHotkey,
                priority: 10, ActivateForKeyboard);
            RefreshSharedActivationHint();
            return;
        }
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

    private void RefreshSharedActivationHint() => ToolTip = UiText.Get(
        _keyboardRouter?.IsRegisteredFor(this) == true ? "Onboard_QuickCardTip" : "Onboard_QuickCardTipNoHotkey");

    internal bool HandleShortcut(Key key, ModifierKeys modifiers) => ShortcutCatalog.Execute(ShortcutScope.QuickAccess, key, modifiers, _actions);

    /// <summary>A command glyph on the shared 20x20 grid: close and the five actions.</summary>
    private SymbolIcon Glyph(string key)
    {
        Geometry geometry = TryFindResource(key) as Geometry ?? Geometry.Parse("M4,4 H16 V16 H4 Z");
        SymbolIcon icon = SymbolIcon.Create(geometry, CommandButtonPresentation.IconSize);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Primary");
        return icon;
    }

    private IntPtr NoActivateHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_keyboardRouter?.MatchesMessage(hwnd, message, wParam) == true)
        {
            handled = true;
            _ = Dispatcher.BeginInvoke(new Action(() => _keyboardRouter.Activate()));
            return IntPtr.Zero;
        }
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
        ReflowPreview();
        _dimensions.Text = $"{image.PixelWidth} × {image.PixelHeight}";
    }

    private void ReflowPreview()
    {
        if (_preview?.Source is not BitmapSource image || _previewSurface is null) return;
        // WPF image dimensions are DIPs and can carry arbitrary bitmap DPI metadata.
        // Limit the displayed physical pixels to twice the original pixel dimensions.
        _preview.Width = Math.Min(310, image.PixelWidth * 2.0 / _previewDpi.DpiScaleX);
        _preview.Height = Math.Min(180, image.PixelHeight * 2.0 / _previewDpi.DpiScaleY);
        _previewSurface.Height = _preview.Height;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _previewDpi = newDpi;
        // Resizing after a monitor move must use that monitor rather than snap back
        // through the SizeChanged placement handler to the previous monitor.
        if (IsLoaded && IsVisible)
            _placementMonitor = MonitorEnumerator.GetFromWindow(new WindowInteropHelper(this).Handle);
        ReflowPreview();
        base.OnDpiChanged(oldDpi, newDpi);
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
        _previewDpi = new DpiScale(monitor.ScaleFactor, monitor.ScaleFactor);
        ReflowPreview();
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
        var button = new Button { Content = UiText.Get(key), Focusable = false };
        CommandButtonPresentation.Apply(button);
        _keyboardTargets.Add(button);
        AutomationProperties.SetAutomationId(button, key);
        AutomationProperties.SetName(button, UiText.Get(key));
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// Runs one card action. Its button is disabled and the status says what is happening at
    /// once; work that outlasts <see cref="FeedbackTiming.ProgressRevealDelay"/> also runs the
    /// progress indicator. Other actions stay usable: each renders its own copy of the image.
    /// </summary>
    internal Task RunAsync(string action, Func<Task<bool>> work, string progress, string success) =>
        RunCoreAsync(action, async () => await work()
            ? (UiText.Get(success), FeedbackKind.Confirmation)
            : (FailureText(action), FeedbackKind.ActionRequired), progress);

    /// <summary>The card's actions, left to right.</summary>
    internal static IReadOnlyList<string> ActionOrder { get; } = ["Edit", "Pin", "Save", "CopyText", "Copy"];

    /// <summary>Test hook: the action buttons, left to right.</summary>
    internal IReadOnlyList<Button> ActionButtonsForTest => ActionOrder.Select(action => _actionButtons[action]).ToArray();

    /// <summary>Test hook: the toggle that keeps the card open.</summary>
    internal ToggleButton KeepToggleForTest => _keep;

    /// <summary>
    /// What failed and how to try again: the action's own key, or another drag for the preview.
    /// </summary>
    internal static string FailureText(string action)
    {
        string text = action == "Drag"
            ? UiText.Get("QuickAccess.DragFailed")
            : UiText.Format($"QuickAccess.{action}Failed", ShortcutCatalog.Get(ShortcutScope.QuickAccess, action).Hint);
        // When the line wraps, it breaks before the retry hint rather than inside it.
        int hint = text.LastIndexOf(" · ", StringComparison.Ordinal);
        return hint < 0 ? text : text[..(hint + 3)] + text[(hint + 3)..].Replace(' ', ' ');
    }

    /// <summary>
    /// The card's line for a text copy: copied, nothing to copy (retrying would not change that),
    /// or a failure to retry with the key.
    /// </summary>
    internal static (string Message, FeedbackKind Kind) DescribeTextOutcome(TextCaptureOutcome outcome) => outcome.Status switch
    {
        TextCaptureStatus.Copied => (UiText.Get("QuickAccess.TextCopied"), FeedbackKind.Confirmation),
        TextCaptureStatus.NoText => (UiText.Get("TextCapture_NoText"), FeedbackKind.Warning),
        TextCaptureStatus.Unavailable => (string.IsNullOrWhiteSpace(outcome.Result?.Message)
            ? UiText.Get("TextCapture_Unavailable") : outcome.Result.Message, FeedbackKind.Warning),
        _ => (FailureText("CopyText"), FeedbackKind.ActionRequired),
    };

    private async Task RunCoreAsync(string action, Func<Task<(string Message, FeedbackKind Kind)>> work, string progress)
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
            (message, kind) = await work();
        }
        catch (Exception error)
        {
            (message, kind) = (UserFacingError.GetMessage(error) ?? FailureText(action), FeedbackKind.ActionRequired);
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
        _statusGlyph.SetResourceReference(SymbolIcon.StrokeProperty, failed ? "State.Danger" : "State.Success");
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
        // Reduced motion keeps a steady neutral bar; the status text already says what is running.
        _progressShown = true;
        _remaining.Visibility = Visibility.Visible;
        _remaining.SetResourceReference(ForegroundProperty, "Border.Strong");
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
