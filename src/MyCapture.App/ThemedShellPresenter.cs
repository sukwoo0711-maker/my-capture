using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>One bounded, nonactivating app notification and a themed tray command menu.</summary>
/// <remarks>
/// Notifications share the floating-card chrome (1px <c>Border.Subtle</c>, host window corners,
/// <c>Shadow.Card</c>) and appear at the bottom right of the monitor the pointer is on, so they
/// show up where the user is working rather than on the primary monitor.
/// </remarks>
internal sealed class ThemedShellPresenter(Func<bool> suppressNotification, Func<bool>? suppressColorCopy = null, ReceiptKeyboardRouter? keyboardRouter = null) : IDisposable
{
    /// <summary>The transparent band around a notification card that its shadow draws into.</summary>
    internal const double ShadowMargin = 12;

    /// <summary>The gap between a notification card and the edges of the work area.</summary>
    internal const double EdgeGap = 16;

    /// <summary>Width of a notification card, without its shadow band.</summary>
    internal const double CardWidth = 360;

    /// <summary>Card corners follow the host's window frame.</summary>
    internal static readonly CornerRadius CardCorner =
        new(WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build));

    private Window? _toast;
    private CardKind _toastCard;
    // Cards that are already dismissed but still playing their exit.
    private readonly List<Window> _leaving = [];
    private ReceiptKeyboardSession? _receiptKeyboardSession;
    internal bool HasNotification => _toast is not null;
    internal bool HasVisibleNotification => _toast is not null || _leaving.Count > 0;
    internal event Action? NotificationDismissed;

    /// <summary>Raised whenever a tray-owned surface (command menu) has just left the screen,
    /// so a capture starting immediately afterwards can wait out the dismissal.</summary>
    internal event Action? ForegroundDismissed;
    // Separate from the legacy aggregate notification: every dismissed HWND keeps its
    // own exclusion outcome, so one excluded card cannot mask an unexcluded menu.
    internal event Action<bool>? CaptureSurfaceDismissed;
    private ContextMenu? _menu;
    private FeedbackCountdown? _countdown;

    /// <summary>The monitor notifications appear on: the one with the pointer. Tests replace it.</summary>
    internal Func<MonitorInfo> NotificationMonitor { get; init; } = MonitorEnumerator.GetFromCursor;

    /// <summary>
    /// A command or a submenu branch in the tray tree. A null Action marks a branch. A command
    /// with <c>IsEnabled</c> false stays visible but cannot be invoked; its <c>ToolTip</c> says why.
    /// </summary>
    internal sealed record MenuEntry(string Label, Action? Action, IReadOnlyList<MenuEntry>? Children = null, string Gesture = "",
        bool IsEnabled = true, string ToolTip = "", bool LiteralLabel = false,
        ImageSource? Thumbnail = null, bool IsMuted = false, string ItemStatus = "")
    {
        /// <summary>A divider between groups; it runs nothing.</summary>
        internal static MenuEntry Separator { get; } = new(string.Empty, null) { IsSeparator = true };

        internal bool IsSeparator { get; private init; }
    }

    internal void ShowNotification(string title, string message, TrayBalloonKind kind) =>
        ShowNotification(title, message, kind, answersCommand: false);

    /// <param name="answersCommand">
    /// The notice answers a command the user just gave, such as a capture that could not start, so it
    /// shows even during a capture or recording session, which holds other notifications back.
    /// </param>
    internal void ShowNotification(string title, string message, TrayBalloonKind kind, bool answersCommand)
    {
        DismissFor(CardKind.Notice);
        if (!answersCommand && suppressNotification()) return;
        var content = new StackPanel();
        content.Children.Add(Heading(title));
        content.Children.Add(Body(message));
        var close = new Button { Content = UiText.Get("Shell.Close"), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Dismiss();
        content.Children.Add(close);
        Window window = CreateNotificationWindow(content, title, new Thickness(20));
        window.SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(window);
        PlaceOnPointerMonitor(window);
        Present(window, CardKind.Notice, KindOf(kind), title + " " + message);
    }

    internal void ShowColorCopy(ColorCopyFeedback feedback)
    {
        DismissFor(CardKind.ColorCopy);
        if ((suppressColorCopy ?? suppressNotification)()) return;
        Window window = CreateNotificationWindow(CreateColorCopyContent(feedback),
            UiText.Get("Tools_ColorPicker"), new Thickness(20));
        window.SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(window);
        PlaceOnPointerMonitor(window);
        Present(window, CardKind.ColorCopy, FeedbackKind.Confirmation, feedback.Message);
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
    private void Present(Window window, CardKind card, FeedbackKind kind, string announcement)
    {
        window.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || !ReferenceEquals(_toast, window)) return;
            Dismiss();
            e.Handled = true;
        };
        TrackNotification(window);
        _toastCard = card;
        _countdown ??= new FeedbackCountdown(window.Dispatcher,
            () => _toast is { } toast && (toast.IsMouseOver || toast.IsKeyboardFocusWithin), Dismiss);
        _toastKind = kind;
        window.Show();
        _countdown.Start(FeedbackTiming.DisplayTime(kind));
        if (window.Content is UIElement content) FeedbackTiming.Announce(content, announcement, kind, "MyCapture.Shell.Notification");
    }

    internal void TrackNotification(Window window)
    {
        _toast = window;
        window.Closed += (_, _) =>
        {
            CaptureSurfaceDismissed?.Invoke(CaptureWindowExclusion.WasApplied(window));
            if (!ReferenceEquals(_toast, window)) return;
            _toast = null;
            _countdown?.Stop();
            _receiptKeyboardSession = null;
            NotificationDismissed?.Invoke();
        };
    }

    private FeedbackKind _toastKind;

    /// <summary>Test hook: the open notification's kind and the display time it has left.</summary>
    internal (bool Open, FeedbackKind Kind, TimeSpan? Remaining) NotificationForTest =>
        (_toast?.IsVisible == true, _toastKind, _countdown?.Remaining);

    /// <summary>Test hook: the open notification window.</summary>
    internal Window? NotificationWindowForTest => _toast;

    /// <summary>Test hook: spends display time as the countdown's clock would.</summary>
    internal void AdvanceNotificationForTest(TimeSpan elapsed) => _countdown?.Advance(elapsed);

    internal bool ShowCaptureReceipt(string message, Action openLibrary, string? tip = null, Action? pin = null, bool answersCommand = false, string? actionTooltip = null)
    {
        DismissFor(CardKind.Receipt);
        if (!answersCommand && suppressNotification()) return false;
        Window window = CreateCaptureReceipt(message, openLibrary, tip, pin, actionTooltip);
        PlaceOnPointerMonitor(window);
        Present(window, CardKind.Receipt, FeedbackKind.Confirmation, tip is null ? message : message + " " + tip);
        return true;
    }

    internal Window CreateCaptureReceipt(string message, Action openLibrary, string? tip = null, Action? pin = null, string? actionTooltip = null)
    {
        var action = new Button { Content = KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap }, message),
            Height = double.NaN, Padding = new Thickness(12), ToolTip = actionTooltip ?? UiText.Get("QuickAccess.Library") };
        AutomationProperties.SetName(action, message);
        AutomationProperties.SetAutomationId(action, "MyCapture.Receipt.Open");
        action.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost");
        action.SetResourceReference(Control.FontSizeProperty, "FontSize.Body");
        action.Click += (_, _) => { Dismiss(); openLibrary(); };
        var content = new StackPanel();
        content.Children.Add(action);
        if (tip is not null && pin is not null)
        {
            var tipButton = new Button { Content = tip, MinHeight = 32, FontSize = TypeScale.Caption };
            tipButton.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost");
            AutomationProperties.SetName(tipButton, tip);
            AutomationProperties.SetAutomationId(tipButton, "MyCapture.Receipt.Pin");
            tipButton.ToolTip = UiText.Get("QuickAccess.Pin");
            tipButton.Click += (_, _) => { Dismiss(); pin(); };
            content.Children.Add(tipButton);
        }
        else if (tip is not null)
        {
            var locationHint = Body(tip);
            locationHint.Margin = new Thickness(12, 0, 12, 4);
            content.Children.Add(locationHint);
        }
        var hint = KoreanWordWrap.Show(new TextBlock { FontSize = TypeScale.Caption,
            Margin = new Thickness(12, 4, 12, 4), TextWrapping = TextWrapping.Wrap }, UiText.Get("Onboard_ReceiptKeyboard"));
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        content.Children.Add(hint);
        Window window = CreateNotificationWindow(content, UiText.Get("QuickAccess.Title"), new Thickness(8), width: tip is null ? 320 : 360);
        ConfigureReceiptKeyboard(window, content, hint);
        return window;
    }

    private void ConfigureReceiptKeyboard(Window window, StackPanel content, TextBlock hint)
    {
        var actions = content.Children.OfType<Button>().ToArray();
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(content, System.Windows.Input.KeyboardNavigationMode.Cycle);
        nint Handle() => new WindowInteropHelper(window).Handle;
        nint previousActivation = 0, externalReturn = 0;
        bool fallback = false, enteringFromTaskbar = false;
        var session = new ReceiptKeyboardSession(
            () => enteringFromTaskbar && ReceiptForeground.Get() == Handle() ? previousActivation : ReceiptForeground.Get(),
            passive => WindowStyleFacade.SetNoActivate(Handle(), passive), window.Activate,
            () => actions.FirstOrDefault(b => b.IsEnabled)?.Focus() == true, ReceiptForeground.Restore);
        bool Activate()
        {
            if (!ReferenceEquals(_toast, window) || !window.IsVisible) return false;
            _receiptKeyboardSession = session;
            return session.Enter(Handle());
        }
        window.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            e.Handled = session.HandleKey(key, System.Windows.Input.Keyboard.Modifiers,
                forward => MoveReceiptFocus(actions, forward),
                () => (System.Windows.Input.Keyboard.FocusedElement as Button)?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), Dismiss);
        };
        window.SourceInitialized += (_, _) =>
        {
            WindowStyleFacade.SetNoActivate(Handle(), true);
            CaptureWindowExclusion.DisableTransitions(window); CaptureWindowExclusion.TryApply(window);
            HwndSource.FromHwnd(Handle())?.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == 0x0006 && (wParam.ToInt64() & 0xffff) != 0) previousActivation = lParam;
                if (keyboardRouter?.MatchesMessage(hwnd, message, wParam) == true)
                {
                    handled = true;
                    _ = window.Dispatcher.BeginInvoke(new Action(() => keyboardRouter.Activate()));
                }
                return 0;
            });
        };
        window.Loaded += (_, _) =>
        {
            keyboardRouter?.SetTarget(window, Handle(), available: true, priority: 100, Activate);
            if (keyboardRouter?.IsRegisteredFor(window) == true) return;
            // A conflicting global chord must still leave a keyboard-only route through Alt+Tab.
            fallback = true;
            KoreanWordWrap.Show(hint, UiText.Get("Onboard_ReceiptKeyboardFallback"));
            WindowStyleFacade.SetNoActivate(Handle(), false);
            window.ShowInTaskbar = true;
        };
        window.Activated += (_, _) =>
        {
            if (!fallback || enteringFromTaskbar) return;
            enteringFromTaskbar = true;
            try { Activate(); }
            finally { enteringFromTaskbar = false; }
        };
        window.Closing += (_, _) =>
        {
            if (ReferenceEquals(_toast, window)) externalReturn = session.TakeReturnTarget(Handle());
        };
        window.Closed += (_, _) =>
        {
            keyboardRouter?.Remove(window);
            session.Return(externalReturn);
        };
    }

    internal static void MoveReceiptFocus(IReadOnlyList<Button> actions, bool forward)
    {
        var enabled = actions.Where(b => b.IsEnabled && b.Visibility == Visibility.Visible).ToArray();
        if (enabled.Length == 0) return;
        int current = Array.FindIndex(enabled, b => b.IsKeyboardFocused);
        int next = current < 0 ? (forward ? 0 : enabled.Length - 1)
            : (current + (forward ? 1 : enabled.Length - 1)) % enabled.Length;
        enabled[next].Focus();
    }

    /// <summary>
    /// Opens the tray menu at the pointer. The language is chosen in Settings only; the menu
    /// carries just the entries it is given, in their order (see <see cref="TrayMenuLayout"/>).
    /// </summary>
    internal void ShowMenu(IEnumerable<MenuEntry> commands)
    {
        Dismiss();
        ContextMenu menu = CreateMenu(commands);
        TrackMenu(menu);
        menu.IsOpen = true;
    }

    internal void TrackMenu(ContextMenu menu)
    {
        _menu = menu;
        menu.Closed += OnClosed;
        void OnClosed(object sender, RoutedEventArgs e)
        {
            menu.Closed -= OnClosed;
            if (ReferenceEquals(_menu, menu)) _menu = null;
            // This tray popup has no successful TryApply contract. Keep the safe wait.
            CaptureSurfaceDismissed?.Invoke(false);
        }
    }

    internal ContextMenu? MenuForTest => _menu;

    /// <summary>Builds the themed menu without opening it.</summary>
    internal ContextMenu CreateMenu(IEnumerable<MenuEntry> commands, ResourceDictionary? resources = null)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MinWidth = 240 };
        if (resources is not null) menu.Resources = resources;
        AppendEntries(menu.Items, commands, menu);
        return menu;
    }

    private void AppendEntries(ItemCollection items, IEnumerable<MenuEntry> entries, ContextMenu root)
    {
        DataTemplate? literalHeaderTemplate = null;
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

            MenuItem item = entry.Thumbnail is not null ? new ThumbnailMenuItem()
                : entry.LiteralLabel ? new LiteralMenuItem() : new MenuItem();
            if (entry.Thumbnail is not null && root.TryFindResource(typeof(MenuItem)) is Style previewStyle)
                item.Style = previewStyle;
            item.Header = entry.Label;
            item.MinHeight = 36;
            item.InputGestureText = entry.Gesture;
            item.IsEnabled = entry.IsEnabled;
            if (entry.IsMuted) item.SetResourceReference(Control.ForegroundProperty, "Text.Secondary");
            if (entry.ItemStatus.Length > 0) AutomationProperties.SetItemStatus(item, entry.ItemStatus);
            if (entry.Thumbnail is { } thumbnail)
            {
                var preview = new Image
                {
                    Source = thumbnail, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
                var box = new Border
                {
                    Width = 32, Height = 24, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                    Child = preview, Opacity = entry.IsMuted ? .5 : 1,
                };
                box.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
                box.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");
                item.Icon = box;
            }
            if (entry.LiteralLabel)
            {
                item.HeaderTemplate = literalHeaderTemplate ??= (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                        <TextBlock Text="{Binding}" />
                    </DataTemplate>
                    """);
            }
            AutomationProperties.SetName(item, entry.Label);
            if (entry.Gesture.Length > 0 && entry.Gesture != entry.ItemStatus)
                AutomationProperties.SetAcceleratorKey(item, entry.Gesture);
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
                CaptureSurfaceDismissed?.Invoke(false);
                action();
            };
            items.Add(item);
        }
    }

    // Source titles contain literal underscores, not WPF access-key markers.
    private class LiteralMenuItem : MenuItem
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new LiteralMenuItemAutomationPeer(this);
    }

    // The shared menu reserves an 18-DIP glyph. Only preview rows need a wider icon slot.
    private sealed class ThumbnailMenuItem : LiteralMenuItem
    {
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (Template.FindName("IconHost", this) is not ContentPresenter host) return;
            host.Width = 32;
            host.Height = 24;
            if (VisualTreeHelper.GetParent(host) is Grid grid && grid.ColumnDefinitions.Count > 0)
                grid.ColumnDefinitions[0].Width = new GridLength(32);
        }
    }

    private sealed class LiteralMenuItemAutomationPeer(MenuItem owner) : MenuItemAutomationPeer(owner)
    {
        protected override string GetNameCore() => AutomationProperties.GetName(Owner);
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
    /// shared 1px subtle border, host window corners and card shadow, and the shadow draws into the
    /// transparent band around it.
    /// </summary>
    internal static Window CreateNotificationWindow(UIElement child, string title, Thickness padding, double width = CardWidth)
    {
        bool layered = CaptureWindowExclusion.SupportsLayeredWindows;
        CornerRadius corner = layered ? CardCorner : new CornerRadius(0);
        double margin = layered ? ShadowMargin : 0;
        // The shadow sits on its own layer behind the card, so the card's text is not drawn
        // through the shadow effect and keeps its normal rendering.
        var shadow = new Border { CornerRadius = corner, IsHitTestVisible = false };
        shadow.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        if (layered) shadow.SetResourceReference(UIElement.EffectProperty, "Shadow.Card");
        var card = new Border { CornerRadius = corner, BorderThickness = new Thickness(1), Padding = padding, Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        AutomationProperties.SetAutomationId(card, "MyCapture.Shell.NotificationCard");
        var surface = new Grid { Margin = new Thickness(margin) };
        surface.Children.Add(shadow);
        surface.Children.Add(card);
        var window = new Window
        {
            Title = title,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = layered,
            Background = layered ? Brushes.Transparent : CaptureWindowExclusion.OpaqueBackground(),
            Width = width + (2 * margin),
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
    internal static Rect NotificationBounds(RectD workArea, double scale, double width, double height, double shadowMargin = ShadowMargin)
    {
        double left = workArea.Left / scale, top = workArea.Top / scale;
        double right = workArea.Right / scale, bottom = workArea.Bottom / scale;
        double x = right - EdgeGap - width + shadowMargin;
        double y = bottom - EdgeGap - height + shadowMargin;
        return new Rect(Math.Max(left - shadowMargin, x), Math.Max(top - shadowMargin, y), width, height);
    }

    /// <summary>Puts the window on the pointer's monitor once laid out, and again if a DPI change resizes it.</summary>
    private void PlaceOnPointerMonitor(Window window)
    {
        MonitorInfo? monitor = null;
        void Place()
        {
            monitor ??= NotificationMonitor();
            Rect bounds = NotificationBounds(monitor.WorkArea, monitor.ScaleFactor, window.ActualWidth, window.ActualHeight,
                window.AllowsTransparency ? ShadowMargin : 0);
            double scale = monitor.ScaleFactor;
            PhysicalWindowPositioner.PlaceTopmostWithoutActivation(new WindowInteropHelper(window).Handle,
                new RectD(bounds.X * scale, bounds.Y * scale, bounds.Width * scale, bounds.Height * scale));
        }
        window.Loaded += (_, _) => Place();
        window.SizeChanged += (_, _) => { if (window.IsLoaded && window.IsVisible) Place(); };
    }

    private static TextBlock Heading(string title) =>
        KoreanWordWrap.Show(new TextBlock { FontSize = TypeScale.BodyLarge, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, title);

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

    /// <summary>
    /// Closes the card on screen (its timeout, its close button, Esc or one of its actions) and the
    /// tray menu. The card is gone at once for everyone who asks; only its pixels leave with the
    /// shared exit, sliding toward the screen edge it is docked at.
    /// </summary>
    internal void Dismiss() => Dismiss(immediate: false);

    // The kinds of card the presenter shows. A newer card of the kind on screen replaces it at
    // once; a card of another kind lets the old one play its exit while the new one enters.
    private enum CardKind
    {
        Notice,
        ColorCopy,
        Receipt,
        Action,
    }

    private void DismissFor(CardKind next) => Dismiss(immediate: _toast is not null && _toastCard == next);

    private void Dismiss(bool immediate)
    {
        _countdown?.Stop();
        Window? toast = _toast;
        ReceiptKeyboardSession? session = _receiptKeyboardSession;
        nint returnTo = toast is null ? 0 : session?.TakeReturnTarget(new WindowInteropHelper(toast).Handle) ?? 0;
        _toast = null;
        _receiptKeyboardSession = null;
        if (toast is not null)
        {
            keyboardRouter?.Remove(toast);
            Leave(toast, immediate);
            session?.Return(returnTo);
            NotificationDismissed?.Invoke();
        }
        if (_menu is not null) _menu.IsOpen = false;
        _menu = null;
    }

    // Every card docks at the bottom right of its monitor, so it leaves to the right. Without
    // Windows animations, or as an opaque window, it closes at once.
    private void Leave(Window card, bool immediate)
    {
        if (immediate)
        {
            card.Close();
            return;
        }

        _leaving.Add(card);
        card.Closed += (_, _) => _leaving.Remove(card);
        FluidMotion.BeginExit(card, ExitMotion.FadeRight, card.Close);
    }

    /// <summary>Test hook: dismissed cards whose exit is still on the screen.</summary>
    internal IReadOnlyList<Window> LeavingCardsForTest => _leaving;

    internal void DismissForCapture()
    {
        bool visible = _toast?.IsVisible == true || _menu?.IsOpen == true || _leaving.Count > 0;
        Dismiss(immediate: true);
        // A card still playing its exit leaves now, before the capture's first frame.
        foreach (Window card in _leaving.ToArray()) FluidMotion.CompleteExit(card);
        bool helpDismissed = ShortcutHelpWindow.DismissForCapture();
        if (helpDismissed) CaptureSurfaceDismissed?.Invoke(false);
        if (helpDismissed || visible) ForegroundDismissed?.Invoke();
    }
    public void Dispose() => DismissForCapture();

    /// <summary>
    /// The bounded notification with one follow-up command beside Close. Pointing at it pauses
    /// the dismissal countdown so the command can be reached before the card disappears. An
    /// error stays until it is closed or acted on.
    /// </summary>
    internal void ShowActionNotification(string title, string message, string actionLabel, Action action,
        TrayBalloonKind kind = TrayBalloonKind.Information, bool answersCommand = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        DismissFor(CardKind.Action);
        if (!answersCommand && suppressNotification()) return;
        Window window = CreateActionNotification(title, message, actionLabel, action);
        PlaceOnPointerMonitor(window);
        Present(window, CardKind.Action, KindOf(kind), title + " " + message);
    }

    /// <summary>
    /// The card an editor quick save shows while "Notify after quick save" is on: Saved, the file's
    /// name and a way to its folder. It answers the save command, so an open capture session does
    /// not hold it back.
    /// </summary>
    internal void ShowQuickSaved(string path, Action<string> openFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(openFolder);
        string folder = System.IO.Path.GetDirectoryName(path) ?? path;
        ShowActionNotification(UiText.Get("QuickAccess.Saved"), System.IO.Path.GetFileName(path),
            UiText.Get("QuickAccess.OpenFolder"), () => openFolder(folder), answersCommand: true);
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
