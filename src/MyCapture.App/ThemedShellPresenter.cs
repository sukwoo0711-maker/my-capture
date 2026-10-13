using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>One bounded, nonactivating app notification and a themed tray command menu.</summary>
internal sealed class ThemedShellPresenter(Func<bool> suppressNotification) : IDisposable
{
    private Window? _toast;


    /// <summary>Raised whenever a tray-owned surface (command menu) has just left the screen,
    /// so a capture starting immediately afterwards can wait out the dismissal.</summary>
    internal event Action? ForegroundDismissed;
    private ContextMenu? _menu;
    private FeedbackCountdown? _countdown;

    /// <summary>
    /// A command or a submenu branch in the tray tree. A null Action marks a branch. A command
    /// with <c>IsEnabled</c> false stays visible but cannot be invoked.
    /// </summary>
    internal sealed record MenuEntry(string Label, Action? Action, IReadOnlyList<MenuEntry>? Children = null, string Gesture = "", bool IsEnabled = true)
    {
        /// <summary>A divider between groups; it runs nothing.</summary>
        internal static MenuEntry Separator { get; } = new(string.Empty, null) { IsSeparator = true };

        internal bool IsSeparator { get; private init; }
    }

    internal void ShowNotification(string title, string message, TrayBalloonKind kind)
    {
        Dismiss();
        if (suppressNotification()) return;
        var content = new StackPanel();
        content.Children.Add(KoreanWordWrap.Show(new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, title));
        content.Children.Add(KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }, message));
        var close = new Button { Content = UiText.Get("Shell.Close"), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Dismiss();
        content.Children.Add(close);
        var window = CreateWindow(content, title);
        window.Width = 360;
        window.ShowActivated = false;
        window.Topmost = true;
        window.WindowStyle = WindowStyle.None;
        window.SizeToContent = SizeToContent.Height;
        window.SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(window);
        window.Loaded += (_, _) =>
        {
            Rect work = SystemParameters.WorkArea;
            window.Left = Math.Max(work.Left, work.Right - window.ActualWidth - 16);
            window.Top = Math.Max(work.Top, work.Bottom - window.ActualHeight - 16);
        };
        // An error stays until closed; information and warnings leave after the result time.
        Present(window, kind switch
        {
            TrayBalloonKind.Error => FeedbackKind.ActionRequired,
            TrayBalloonKind.Warning => FeedbackKind.Warning,
            _ => FeedbackKind.Result,
        }, title + " " + message);
    }

    /// <summary>
    /// Shows a notification window for its kind's display time from <see cref="FeedbackTiming"/>.
    /// Pointing at it or keyboard focus inside it pauses the countdown, and Esc closes it.
    /// </summary>
    private void Present(Window window, FeedbackKind kind, string announcement)
    {
        window.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || !ReferenceEquals(_toast, window)) return;
            Dismiss();
            e.Handled = true;
        };
        _toast = window;
        _countdown ??= new FeedbackCountdown(window.Dispatcher,
            () => _toast is { } toast && (toast.IsMouseOver || toast.IsKeyboardFocusWithin), Dismiss);
        _toastKind = kind;
        window.Show();
        _countdown.Start(FeedbackTiming.DisplayTime(kind));
        if (window.Content is UIElement content) FeedbackTiming.Announce(content, announcement, kind, "MyCapture.Shell.Notification");
    }

    private FeedbackKind _toastKind;

    /// <summary>Test hook: the open notification's kind and the display time it has left.</summary>
    internal (bool Open, FeedbackKind Kind, TimeSpan? Remaining) NotificationForTest =>
        (_toast?.IsVisible == true, _toastKind, _countdown?.Remaining);

    /// <summary>Test hook: spends display time as the countdown's clock would.</summary>
    internal void AdvanceNotificationForTest(TimeSpan elapsed) => _countdown?.Advance(elapsed);

    internal void ShowCaptureReceipt(string message, Action openLibrary)
    {
        Dismiss();
        if (suppressNotification()) return;
        var action = new Button { Content = KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap }, message),
            Padding = new Thickness(12), ToolTip = UiText.Get("QuickAccess.Library") };
        System.Windows.Automation.AutomationProperties.SetName(action, message);
        action.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost");
        action.Click += (_, _) => { Dismiss(); openLibrary(); };
        var window = CreateWindow(action, UiText.Get("QuickAccess.Title"));
        window.Width = 320; window.SizeToContent = SizeToContent.Height;
        window.ShowActivated = false; window.Topmost = true; window.WindowStyle = WindowStyle.None;
        window.SourceInitialized += (_, _) =>
        {
            MyCapture.Platform.Display.WindowStyleFacade.SetNoActivate(new System.Windows.Interop.WindowInteropHelper(window).Handle, true);
            CaptureWindowExclusion.DisableTransitions(window); CaptureWindowExclusion.TryApply(window);
        };
        window.Loaded += (_, _) =>
        {
            var monitor = MyCapture.Platform.Display.MonitorEnumerator.GetFromCursor();
            Rect position = QuickAccessWindow.Position(monitor.WorkArea, monitor.ScaleFactor, window.ActualWidth, window.ActualHeight);
            MyCapture.Platform.Display.PhysicalWindowPositioner.PlaceTopmostWithoutActivation(new System.Windows.Interop.WindowInteropHelper(window).Handle,
                new MyCapture.Core.Primitives.RectD(position.X * monitor.ScaleFactor, position.Y * monitor.ScaleFactor, position.Width * monitor.ScaleFactor, position.Height * monitor.ScaleFactor));
        };
        Present(window, FeedbackKind.Confirmation, message);
    }

    /// <summary>
    /// Opens the tray menu at the pointer. The language is chosen in Settings only; the menu
    /// carries just the entries it is given, in their order (see <see cref="TrayMenuLayout"/>).
    /// </summary>
    internal void ShowMenu(IEnumerable<MenuEntry> commands)
    {
        Dismiss();
        ContextMenu menu = CreateMenu(commands);
        _menu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Builds the themed menu without opening it.</summary>
    internal ContextMenu CreateMenu(IEnumerable<MenuEntry> commands)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MinWidth = 240 };
        AppendEntries(menu.Items, commands, menu);
        return menu;
    }

    private void AppendEntries(ItemCollection items, IEnumerable<MenuEntry> entries, ContextMenu root)
    {
        foreach (MenuEntry entry in entries)
        {
            if (entry.IsSeparator)
            {
                items.Add(new Separator());
                continue;
            }

            if (entry.Action is null && entry.Children is { Count: > 0 } children)
            {
                // The shared MenuItem template draws a flat command row with no submenu popup,
                // so a branch carries its own template that opens its children beside it.
                var branch = new MenuItem { Header = entry.Label, MinHeight = 36, Template = CreateSubmenuTemplate() };
                System.Windows.Automation.AutomationProperties.SetName(branch, entry.Label);
                AppendEntries(branch.Items, children, root);
                items.Add(branch);
                continue;
            }

            var item = new MenuItem { Header = entry.Label, MinHeight = 36, InputGestureText = entry.Gesture, IsEnabled = entry.IsEnabled };
            if (entry.Gesture.Length > 0) System.Windows.Automation.AutomationProperties.SetAcceleratorKey(item, entry.Gesture);
            Action action = entry.Action ?? (() => { });
            item.Click += (_, _) =>
            {
                root.IsOpen = false;
                // The menu is visually dismissing while the command runs; a capture that
                // starts from here must let the dismissal leave the screen before BitBlt.
                ForegroundDismissed?.Invoke();
                action();
            };
            items.Add(item);
        }
    }

    /// <summary>
    /// A branch row in the shared menu look, with a chevron and a popup that lists its children to
    /// the right. WPF opens it on hover, click or the right arrow key.
    /// </summary>
    /// <remarks>Parsed per menu: a template belongs to the dispatcher that first uses it.</remarks>
    internal static ControlTemplate CreateSubmenuTemplate() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type MenuItem}">
            <Grid>
                <Border x:Name="Chrome" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"
                        CornerRadius="{DynamicResource Radius.Sm}" SnapsToDevicePixels="True">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="20" />
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <ContentPresenter Grid.Column="1" Margin="8,0,22,0" ContentSource="Header"
                                          RecognizesAccessKey="True" VerticalAlignment="Center" />
                        <Path Grid.Column="2" Data="M0,0 L4.5,4.5 L0,9" Stroke="{TemplateBinding Foreground}"
                              StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                              VerticalAlignment="Center" Margin="0,0,4,0" />
                    </Grid>
                </Border>
                <Popup x:Name="PART_Popup" Placement="Right" HorizontalOffset="4" VerticalOffset="-6"
                       AllowsTransparency="True" Focusable="False" PopupAnimation="None"
                       IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}">
                    <Border Background="{DynamicResource Surface.Floating}" BorderBrush="{DynamicResource Border.Strong}"
                            BorderThickness="1" CornerRadius="{DynamicResource Radius.Md}" Padding="5" MinWidth="220"
                            SnapsToDevicePixels="True">
                        <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle" />
                    </Border>
                </Popup>
            </Grid>
            <ControlTemplate.Triggers>
                <Trigger Property="IsHighlighted" Value="True">
                    <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource Surface.Hover}" />
                </Trigger>
                <Trigger Property="IsSubmenuOpen" Value="True">
                    <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource Surface.Pressed}" />
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Opacity" Value="0.46" />
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private static Window CreateWindow(UIElement child, string title)
    {
        var window = new Window { Title = title, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
            Content = new Border { Padding = new Thickness(20), Child = child } };
        window.SetResourceReference(Window.BackgroundProperty, "Surface.Base");
        window.SetResourceReference(Window.ForegroundProperty, "Text.Primary");
        return window;
    }

    internal void Dismiss()
    {
        _countdown?.Stop();
        _toast?.Close();
        _toast = null;
        if (_menu is not null) _menu.IsOpen = false;
        _menu = null;
    }

    internal void DismissForCapture()
    {
        bool visible = _toast?.IsVisible == true || _menu?.IsOpen == true;
        Dismiss();
        if (ShortcutHelpWindow.DismissForCapture() || visible) ForegroundDismissed?.Invoke();
    }
    public void Dispose() => DismissForCapture();

    /// <summary>
    /// The bounded notification with one follow-up command beside Close. Pointing at it pauses
    /// the dismissal countdown so the command can be reached before the card disappears.
    /// </summary>
    internal void ShowActionNotification(string title, string message, string actionLabel, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dismiss();
        if (suppressNotification()) return;
        Window window = CreateActionNotification(title, message, actionLabel, action);
        window.Loaded += (_, _) =>
        {
            Rect work = SystemParameters.WorkArea;
            window.Left = Math.Max(work.Left, work.Right - window.ActualWidth - 16);
            window.Top = Math.Max(work.Top, work.Bottom - window.ActualHeight - 16);
        };
        Present(window, FeedbackKind.Result, title + " " + message);
    }

    /// <summary>
    /// The card <see cref="ShowActionNotification"/> presents, before it is placed or shown. The
    /// tutorial clip generator renders it offscreen.
    /// </summary>
    internal Window CreateActionNotification(string title, string message, string actionLabel, Action action)
    {
        var content = new StackPanel();
        content.Children.Add(KoreanWordWrap.Show(new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, title));
        content.Children.Add(KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }, message));
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var run = new Button { Content = actionLabel, MinHeight = 36, Margin = new Thickness(0, 0, 8, 0) };
        run.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        System.Windows.Automation.AutomationProperties.SetName(run, actionLabel);
        run.Click += (_, _) => { Dismiss(); action(); };
        var close = new Button { Content = UiText.Get("Shell.Close"), MinHeight = 36 };
        close.Click += (_, _) => Dismiss();
        buttons.Children.Add(run);
        buttons.Children.Add(close);
        content.Children.Add(buttons);
        var window = CreateWindow(content, title);
        window.Width = 360;
        window.ShowActivated = false;
        window.Topmost = true;
        window.WindowStyle = WindowStyle.None;
        window.SizeToContent = SizeToContent.Height;
        window.SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(window);
        return window;
    }
}
