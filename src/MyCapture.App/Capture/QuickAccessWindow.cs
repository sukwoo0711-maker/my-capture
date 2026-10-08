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
    private bool _busy;
    private Point _dragStart;
    private bool _dragArmed;
    private bool _keyboardInteracting;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);
    internal static DispatcherOperation Schedule(Dispatcher dispatcher, Action show) =>
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, show);
    internal bool IsKept { get => _keep.IsChecked == true; set => _keep.IsChecked = value; }

    internal QuickAccessWindow(BitmapSource bitmap, Func<Task<bool>> copy, Action edit,
        Func<Task> pin, Func<Task<bool>> save, Action library, Func<Task<string>> export)
    {
        Title = UiText.Get("QuickAccess.Title");
        Width = 340;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var panel = new StackPanel { Margin = new Thickness(16) };
        var header = new DockPanel { LastChildFill = true };
        _keep = new ToggleButton { Content = UiText.Get("QuickAccess.Keep"), MinHeight = 32, Margin = new Thickness(8, 0, 0, 8) };
        AutomationProperties.SetName(_keep, UiText.Get("QuickAccess.Keep"));
        DockPanel.SetDock(_keep, Dock.Right);
        header.Children.Add(_keep);
        header.Children.Add(new TextBlock { Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);
        var preview = new Image { Source = bitmap, Height = 150, Stretch = Stretch.Uniform,
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
        var actions = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 12) };
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
        Loaded += (_, _) =>
        {
            PlaceOnMonitor(MonitorEnumerator.GetFromCursor());
            _timer.Start();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        };
        _timer.Tick += (_, _) => Advance(_timer.Interval, IsMouseOver || (_keyboardInteracting && IsKeyboardFocusWithin) || _busy);
        Closed += (_, _) => _timer.Stop();
        PreviewKeyDown += (_, e) => { _keyboardInteracting = true; if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    internal void PlaceOnMonitor(MonitorInfo monitor)
    {
        Rect position = Position(monitor.WorkArea, monitor.ScaleFactor, ActualWidth, ActualHeight);
        PhysicalWindowPositioner.PlaceTopmost(new WindowInteropHelper(this).Handle,
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

    private static Button Button(string key, Action action)
    {
        var button = new Button { Content = UiText.Get(key), MinHeight = 36, Padding = new Thickness(6), Margin = new Thickness(2) };
        AutomationProperties.SetName(button, UiText.Get(key));
        button.Click += (_, _) => action();
        return button;
    }

    private async Task RunAsync(Func<Task<bool>> action, string success)
    {
        if (_busy) return;
        _busy = true;
        try { _status.Text = UiText.Get(await action() ? success : "QuickAccess.Failed"); }
        catch (Exception) { _status.Text = UiText.Get("QuickAccess.Failed"); }
        finally { _busy = false; }
    }
}
