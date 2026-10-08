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
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>A keyboard-accessible capture receipt. Optional actions never delay durable capture.</summary>
internal sealed class QuickAccessWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ProgressBar _remaining = new() { Minimum = 0, Maximum = 100, Value = 100, Height = 3 };
    private readonly ToggleButton _keep;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
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
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);
    internal static DispatcherOperation Schedule(Dispatcher dispatcher, Action show) =>
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, show);
    internal bool IsKept { get => _keep.IsChecked == true; set => _keep.IsChecked = value; }
    internal string TimeoutStateForTest => $"enabled={_timer.IsEnabled}; elapsed={_elapsed.TotalSeconds:0.000}; keep={IsKept}; keyboard={_keyboardInteracting}/{IsKeyboardFocusWithin}; busy={_busy}";

    internal QuickAccessWindow(BitmapSource bitmap, Func<Task<bool>> copy, Action edit,
        Func<Task> pin, Func<Task<bool>> save, Action library, Func<Task<string>> export, bool excludeFromCapture = true)
    {
        Title = UiText.Get("QuickAccess.Title");
        Width = 340;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var panel = new StackPanel { Margin = new Thickness(16) };
        var header = new DockPanel { LastChildFill = true };
        _keep = new ToggleButton { Content = UiText.Get("QuickAccess.Keep"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 8), Focusable = false };
        _keyboardTargets.Add(_keep);
        AutomationProperties.SetName(_keep, UiText.Get("QuickAccess.Keep"));
        DockPanel.SetDock(_keep, Dock.Right);
        header.Children.Add(_keep);
        _dimensions.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
        header.Children.Add(_dimensions);
        panel.Children.Add(header);
        var preview = _preview = new Image { Source = bitmap, Height = 150, Stretch = Stretch.Uniform,
            ToolTip = UiText.Get("QuickAccess.Drag"), Margin = new Thickness(0, 0, 0, 12) };
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
        panel.Children.Add(preview);
        var actions = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 12) };
        actions.Children.Add(Button("QuickAccess.Copy", async () => await RunAsync(copy, "QuickAccess.Copied")));
        actions.Children.Add(Button("QuickAccess.Edit", () => { edit(); Close(); }));
        actions.Children.Add(Button("QuickAccess.Pin", async () => await RunAsync(async () => { await pin(); return true; }, "QuickAccess.Pinned")));
        actions.Children.Add(Button("QuickAccess.Save", async () => await RunAsync(save, "QuickAccess.Saved")));
        panel.Children.Add(actions);
        _status.Text = UiText.Get("QuickAccess.Ready");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        panel.Children.Add(_status);
        panel.Children.Add(Button("QuickAccess.Library", () => { library(); Close(); }));
        panel.Children.Add(_remaining);
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
            PlaceOnMonitor(MonitorEnumerator.GetFromCursor());
            _lastTick = Stopwatch.GetTimestamp();
            _timer.Start();
        };
        _timer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastTick, now);
            _lastTick = now;
            Advance(elapsed, IsMouseOver || (_keyboardInteracting && IsKeyboardFocusWithin) || _busy);
        };
        Closed += (_, _) => _timer.Stop();
        PreviewKeyDown += (_, e) => { if (!_keyboardAccess) return; _keyboardInteracting = true; if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Deactivated += (_, _) =>
        {
            _keyboardAccess = false;
            _keyboardInteracting = false;
            foreach (Control target in _keyboardTargets) target.Focusable = false;
            if (IsVisible) WindowStyleFacade.SetNoActivate(new WindowInteropHelper(this).Handle, true);
        };
    }

    private IntPtr NoActivateHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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
        Rect position = Position(monitor.WorkArea, monitor.ScaleFactor, ActualWidth, ActualHeight);
        PhysicalWindowPositioner.PlaceTopmostWithoutActivation(new WindowInteropHelper(this).Handle,
            new RectD(position.Left * monitor.ScaleFactor, position.Top * monitor.ScaleFactor,
                ActualWidth * monitor.ScaleFactor, ActualHeight * monitor.ScaleFactor));
    }

    internal static Rect Position(RectD work, double scale, double width, double height) =>
        new(Math.Max(work.Left / scale, work.Right / scale - width - 16),
            Math.Max(work.Top / scale, work.Bottom / scale - height - 16), width, height);

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
        try { _status.Text = UiText.Get(await action() ? success : "QuickAccess.Failed"); }
        catch (Exception error) { _status.Text = UserFacingError.GetMessage(error) ?? UiText.Get("QuickAccess.Failed"); }
        finally { _busy = false; }
    }
}
