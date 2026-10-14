using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>One bounded, nonactivating app notification and a themed tray command menu.</summary>
/// <remarks>
/// Notifications share the floating-card chrome (1px <c>Border.Subtle</c>, 8px corners,
/// <c>Shadow.Card</c>) and appear at the bottom right of the monitor the pointer is on, so they
/// show up where the user is working rather than on the primary monitor.
/// </remarks>
internal sealed class ThemedShellPresenter(Func<bool> suppressNotification) : IDisposable
{
    /// <summary>The transparent band around a notification card that its shadow draws into.</summary>
    internal const double ShadowMargin = 12;

    /// <summary>The gap between a notification card and the edges of the work area.</summary>
    internal const double EdgeGap = 16;

    /// <summary>Width of a notification card, without its shadow band.</summary>
    internal const double CardWidth = 360;

    /// <summary>The 8px corner Windows 11 gives rounded windows (DWMWCP_ROUND).</summary>
    internal static readonly CornerRadius CardCorner = new(8);

    private Window? _toast;

    /// <summary>Raised whenever a tray-owned surface (command menu) has just left the screen,
    /// so a capture starting immediately afterwards can wait out the dismissal.</summary>
    internal event Action? ForegroundDismissed;
    private ContextMenu? _menu;
    private FeedbackCountdown? _countdown;

    /// <summary>The monitor notifications appear on: the one with the pointer. Tests replace it.</summary>
    internal Func<MonitorInfo> NotificationMonitor { get; init; } = MonitorEnumerator.GetFromCursor;

    /// <summary>
    /// A command or a submenu branch in the tray tree. A null Action marks a branch. A command
    /// with <c>IsEnabled</c> false stays visible but cannot be invoked; its <c>ToolTip</c> says why.
    /// </summary>
    internal sealed record MenuEntry(string Label, Action? Action, IReadOnlyList<MenuEntry>? Children = null, string Gesture = "",
        bool IsEnabled = true, string ToolTip = "")
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
        content.Children.Add(Heading(title));
        content.Children.Add(Body(message));
        var close = new Button { Content = UiText.Get("Shell.Close"), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Dismiss();
        content.Children.Add(close);
        Window window = CreateNotificationWindow(content, title, new Thickness(20));
        window.SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(window);
        PlaceOnPointerMonitor(window);
        Present(window, KindOf(kind), title + " " + message);
    }

    internal void ShowColorCopy(ColorCopyFeedback feedback)
    {
        Dismiss();
        if (suppressNotification()) return;
        Window window = CreateNotificationWindow(CreateColorCopyContent(feedback),
            UiText.Get("Tools_ColorPicker"), new Thickness(20));
        window.SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(window);
        PlaceOnPointerMonitor(window);
        Present(window, FeedbackKind.Confirmation, feedback.Message);
    }

    internal static FrameworkElement CreateColorCopyContent(ColorCopyFeedback feedback)
    {
        var brush = new SolidColorBrush(feedback.ChipColor);
        brush.Freeze();
        // Two contrasting edges keep even white and black samples distinct in every palette.
        var chip = new Border
        {
            Width = 28, Height = 28, Background = brush,
            BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
        };
        var outline = new Border
        {
            BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Child = chip,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(chip, "MyCapture.ColorCopy.Chip");
        AutomationProperties.SetName(chip, feedback.HexValue);
        var content = new DockPanel { DataContext = feedback };
        DockPanel.SetDock(outline, Dock.Left);
        content.Children.Add(outline);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Heading(feedback.Message));
        if (feedback.Value != feedback.HexValue) text.Children.Add(Body(feedback.HexValue));
        content.Children.Add(text);
        return content;
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

    /// <summary>Test hook: the open notification window.</summary>
    internal Window? NotificationWindowForTest => _toast;

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
        Window window = CreateNotificationWindow(action, UiText.Get("QuickAccess.Title"), new Thickness(8), width: 320);
        window.SourceInitialized += (_, _) =>
        {
            WindowStyleFacade.SetNoActivate(new WindowInteropHelper(window).Handle, true);
            CaptureWindowExclusion.DisableTransitions(window); CaptureWindowExclusion.TryApply(window);
        };
        PlaceOnPointerMonitor(window);
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
                AutomationProperties.SetName(branch, entry.Label);
                AppendEntries(branch.Items, children, root);
                items.Add(branch);
                continue;
            }

            var item = new MenuItem { Header = entry.Label, MinHeight = 36, InputGestureText = entry.Gesture, IsEnabled = entry.IsEnabled };
            if (entry.Gesture.Length > 0) AutomationProperties.SetAcceleratorKey(item, entry.Gesture);
            if (entry.ToolTip.Length > 0)
            {
                // A disabled command says why it cannot run, by pointer and to screen readers.
                item.ToolTip = entry.ToolTip;
                ToolTipService.SetShowOnDisabled(item, true);
                AutomationProperties.SetHelpText(item, entry.ToolTip);
            }
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

    /// <summary>
    /// A borderless, transparent notification window around one floating card: the card has the
    /// shared 1px subtle border, 8px corners and card shadow, and the shadow draws into the
    /// transparent band around it.
    /// </summary>
    internal static Window CreateNotificationWindow(UIElement child, string title, Thickness padding, double width = CardWidth)
    {
        // The shadow sits on its own layer behind the card, so the card's text is not drawn
        // through the shadow effect and keeps its normal rendering.
        var shadow = new Border { CornerRadius = CardCorner, IsHitTestVisible = false };
        shadow.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        shadow.SetResourceReference(UIElement.EffectProperty, "Shadow.Card");
        var card = new Border { CornerRadius = CardCorner, BorderThickness = new Thickness(1), Padding = padding, Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        AutomationProperties.SetAutomationId(card, "MyCapture.Shell.NotificationCard");
        var surface = new Grid { Margin = new Thickness(ShadowMargin) };
        surface.Children.Add(shadow);
        surface.Children.Add(card);
        var window = new Window
        {
            Title = title,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Width = width + (2 * ShadowMargin),
            SizeToContent = SizeToContent.Height,
            ShowActivated = false,
            Topmost = true,
            Content = surface,
            // Placed on the pointer's monitor once its height is known; never flashes elsewhere.
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        window.SetResourceReference(Window.ForegroundProperty, "Text.Primary");
        // The implicit window style turns the modern chrome on, which paints Surface.Base over
        // the transparent background and makes the shadow margin an opaque frame.
        ModernWindowChrome.SetEnabled(window, false);
        return window;
    }

    /// <summary>
    /// Where a notification window of <paramref name="width"/> × <paramref name="height"/> DIPs
    /// goes on a monitor: its visible card <see cref="EdgeGap"/> inside the bottom-right corner of
    /// the work area, with the transparent shadow band around it.
    /// </summary>
    internal static Rect NotificationBounds(RectD workArea, double scale, double width, double height)
    {
        double left = workArea.Left / scale, top = workArea.Top / scale;
        double right = workArea.Right / scale, bottom = workArea.Bottom / scale;
        double x = right - EdgeGap - width + ShadowMargin;
        double y = bottom - EdgeGap - height + ShadowMargin;
        return new Rect(Math.Max(left - ShadowMargin, x), Math.Max(top - ShadowMargin, y), width, height);
    }

    /// <summary>Puts the window on the pointer's monitor once laid out, and again if a DPI change resizes it.</summary>
    private void PlaceOnPointerMonitor(Window window)
    {
        MonitorInfo? monitor = null;
        void Place()
        {
            monitor ??= NotificationMonitor();
            Rect bounds = NotificationBounds(monitor.WorkArea, monitor.ScaleFactor, window.ActualWidth, window.ActualHeight);
            double scale = monitor.ScaleFactor;
            PhysicalWindowPositioner.PlaceTopmostWithoutActivation(new WindowInteropHelper(window).Handle,
                new RectD(bounds.X * scale, bounds.Y * scale, bounds.Width * scale, bounds.Height * scale));
        }
        window.Loaded += (_, _) => Place();
        window.SizeChanged += (_, _) => { if (window.IsLoaded && window.IsVisible) Place(); };
    }

    private static TextBlock Heading(string title) =>
        KoreanWordWrap.Show(new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, title);

    private static TextBlock Body(string message)
    {
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
        // Korean wraps between words, never inside one.
        KoreanWordWrap.SetText(body, message);
        return body;
    }

    private static FeedbackKind KindOf(TrayBalloonKind kind) => kind switch
    {
        TrayBalloonKind.Error => FeedbackKind.ActionRequired,
        TrayBalloonKind.Warning => FeedbackKind.Warning,
        _ => FeedbackKind.Result,
    };

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
    /// the dismissal countdown so the command can be reached before the card disappears. An
    /// error stays until it is closed or acted on.
    /// </summary>
    internal void ShowActionNotification(string title, string message, string actionLabel, Action action,
        TrayBalloonKind kind = TrayBalloonKind.Information)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dismiss();
        if (suppressNotification()) return;
        Window window = CreateActionNotification(title, message, actionLabel, action);
        PlaceOnPointerMonitor(window);
        Present(window, KindOf(kind), title + " " + message);
    }

    /// <summary>
    /// The card <see cref="ShowActionNotification"/> presents, before it is placed or shown. The
    /// tutorial clip generator renders it offscreen.
    /// </summary>
    internal Window CreateActionNotification(string title, string message, string actionLabel, Action action)
    {
        var content = new StackPanel();
        content.Children.Add(Heading(title));
        content.Children.Add(Body(message));
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var run = new Button { Content = actionLabel, MinHeight = 36, Margin = new Thickness(0, 0, 8, 0) };
        run.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        AutomationProperties.SetName(run, actionLabel);
        run.Click += (_, _) => { Dismiss(); action(); };
        var close = new Button { Content = UiText.Get("Shell.Close"), MinHeight = 36 };
        close.Click += (_, _) => Dismiss();
        buttons.Children.Add(run);
        buttons.Children.Add(close);
        content.Children.Add(buttons);
        Window window = CreateNotificationWindow(content, title, new Thickness(20));
        window.SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(window);
        return window;
    }
}
