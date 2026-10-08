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
    private readonly ProgressBar _remaining = new() { Minimum = 0, Maximum = 100, Value = 100, Height = 3 };
    private readonly ToggleButton _keep;
    private readonly TextBlock _status = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
    private TimeSpan _elapsed;
    private long _lastTick;
    private bool _busy;
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
    private TransientKeyboardListener? _keys;
    private NativeHotkeyRegistrar? _activationKey;
    private const int ActivationKeyId = 0x7102;
    private readonly Dictionary<string, Action> _actions = [];
    internal IReadOnlyDictionary<string, Action> ShortcutActions => _actions;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    internal static DispatcherOperation Schedule(Dispatcher dispatcher, Action show) =>
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, show);
    internal bool IsKept { get => _keep.IsChecked == true; set => _keep.IsChecked = value; }
    internal bool IsPointerOver => IsVisible && WindowStyleFacade.IsPointerOverWindow(new WindowInteropHelper(this).Handle);
    internal string TimeoutStateForTest => $"enabled={_timer.IsEnabled}; elapsed={_elapsed.TotalSeconds:0.000}; keep={IsKept}; keyboard={_keyboardInteracting}/{IsKeyboardFocusWithin}; busy={_busy}";

    internal QuickAccessWindow(BitmapSource bitmap, Func<Task<bool>> copy, Action edit,
        Func<Task> pin, Func<Task<bool>> save, Action library, Func<Task<string>> export, bool excludeFromCapture = true,
        RectD? capturedRegion = null, MonitorInfo? targetMonitor = null)
    {
        Title = UiText.Get("QuickAccess.Title");
        Width = 280;
        _capturedRegion = capturedRegion; _targetMonitor = targetMonitor;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var panel = new StackPanel { Margin = new Thickness(8) };
        var row = new DockPanel { LastChildFill = true };
        _keep = new ToggleButton { Content = Glyph("Icon.Pin"), Width = 24, Height = 30, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(2), Focusable = false, ToolTip = UiText.Get("QuickAccess.Keep") };
        _keyboardTargets.Add(_keep);
        AutomationProperties.SetName(_keep, UiText.Get("QuickAccess.Keep"));
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
            if (!_dragArmed || _busy || e.LeftButton != MouseButtonState.Pressed
                || (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
            _dragArmed = false;
            await RunAsync(async () =>
            {
                string path = await export();
                if (IsVisible && Mouse.LeftButton == MouseButtonState.Pressed)
                    DragDrop.DoDragDrop(preview, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
                return true;
            }, "QuickAccess.Ready");
        };
        _actions["Copy"] = async () => await RunAsync(copy, "QuickAccess.Copied");
        _actions["Edit"] = () => { edit(); Close(); };
        _actions["Pin"] = async () => await RunAsync(async () => { await pin(); return true; }, "QuickAccess.Pinned");
        _actions["Save"] = async () => await RunAsync(save, "QuickAccess.Saved");
        _actions["Close"] = Close;
        _actions["Activate"] = () => ActivateForKeyboard();
        var actions = new UniformGrid { Columns = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (string action in new[] { "Copy", "Edit", "Pin", "Save" })
        {
            Button button = Button("QuickAccess." + action, _actions[action]);
            button.Content = Glyph("Icon." + action);
            button.MinWidth = 0; button.MinHeight = 0; button.Height = 42; button.Padding = new Thickness(1); button.Margin = new Thickness(1);
            ShortcutPresentation.Attach(button, ShortcutScope.QuickAccess, action, corner: true);
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
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        panel.Children.Add(_status); panel.Children.Add(_remaining);
        Content = panel;
        ToolTip = UiText.Get("QuickAccess.KeyboardHint");
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
            if (_activationKey is null)
            {
                _activationKey = new NativeHotkeyRegistrar(new WindowInteropHelper(this).Handle);
                ShortcutDefinition activation = ShortcutCatalog.Get(ShortcutScope.QuickAccess, "Activate");
                if (!_activationKey.TryRegister(ActivationKeyId,
                    new Hotkey((HotkeyModifiers)activation.Modifiers, (uint)KeyInterop.VirtualKeyFromKey(activation.Key)), out _, out _))
                    ToolTip = UiText.Get("QuickAccess.ActivationUnavailable");
            }
            try
            {
                _keys ??= new TransientKeyboardListener(virtualKey =>
                {
                    var key = KeyInterop.KeyFromVirtualKey(virtualKey);
                    ModifierKeys modifiers = ModifierKeys.None;
                    if (TransientKeyboardListener.IsKeyDown(0x10)) modifiers |= ModifierKeys.Shift;
                    if (TransientKeyboardListener.IsKeyDown(0x11)) modifiers |= ModifierKeys.Control;
                    if (TransientKeyboardListener.IsKeyDown(0x12)) modifiers |= ModifierKeys.Alt;
                    if (TransientKeyboardListener.IsKeyDown(0x5B) || TransientKeyboardListener.IsKeyDown(0x5C)) modifiers |= ModifierKeys.Windows;
                    string? command = ShortcutCatalog.Match(ShortcutScope.QuickAccess, key, modifiers);
                    // Alt+Q uses RegisterHotKey so the foreground app cannot interpret
                    // the remaining Alt press/release as entering its native menu loop.
                    if (command is null or "Activate" || !IsVisible || !IsPointerOver || _keyboardAccess) return false;
                    _ = Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) _actions[command](); }));
                    return true;
                });
            }
            catch (System.ComponentModel.Win32Exception) { ToolTip = UiText.Get("QuickAccess.KeyboardUnavailable"); }
            _lastTick = Stopwatch.GetTimestamp();
            _timer.Start();
        };
        _timer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastTick, now);
            _lastTick = now;
            Advance(elapsed, IsPointerOver || (_keyboardInteracting && IsKeyboardFocusWithin) || _busy);
        };
        SizeChanged += (_, _) => { if (IsLoaded && IsVisible && _placementMonitor is { } monitor) PlaceOnMonitor(monitor); };
        Closed += (_, _) => { _timer.Stop(); _keys?.Dispose(); _keys = null; _activationKey?.Unregister(ActivationKeyId); _activationKey = null; };
        PreviewKeyDown += (_, e) =>
        {
            if (!_keyboardAccess) return;
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
        if (message == 0x0312 && wParam.ToInt32() == ActivationKeyId)
        {
            handled = true;
            _ = Dispatcher.BeginInvoke(new Action(() => ActivateForKeyboard()));
            return IntPtr.Zero;
        }
        if (message == 0x0021 && !_keyboardAccess) // WM_MOUSEACTIVATE / MA_NOACTIVATE
        { handled = true; return new IntPtr(3); }
        return IntPtr.Zero;
    }

    internal bool ActivateForKeyboard()
    {
        if (!IsVisible) return false;
        _keyboardAccess = true;
        _keyboardInteracting = true;
        WindowStyleFacade.SetNoActivate(new WindowInteropHelper(this).Handle, false);
        foreach (Control target in _keyboardTargets) target.Focusable = true;
        if (!Activate()) return false;
        return MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
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
        if (IsKept || interacting) return;
        _elapsed += time;
        _remaining.Value = Math.Max(0, 100 * (1 - _elapsed.TotalSeconds / Lifetime.TotalSeconds));
        if (_elapsed >= Lifetime) Close();
    }

    private Button Button(string key, Action action)
    {
        var button = new Button { Content = UiText.Get(key), MinHeight = 36, Padding = new Thickness(6), Margin = new Thickness(2), Focusable = false };
        _keyboardTargets.Add(button);
        AutomationProperties.SetName(button, UiText.Get(key));
        button.Click += (_, _) => action();
        return button;
    }

    private async Task RunAsync(Func<Task<bool>> action, string success)
    {
        if (_busy) return;
        _busy = true;
        _status.Visibility = Visibility.Visible;
        try { _status.Text = UiText.Get(await action() ? success : "QuickAccess.Failed"); }
        catch (Exception error) { _status.Text = UserFacingError.GetMessage(error) ?? UiText.Get("QuickAccess.Failed"); }
        finally { _busy = false; }
    }
}
