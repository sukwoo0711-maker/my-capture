using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>What a click or drag in the capture selector produces.</summary>
internal enum CaptureSelectorMode
{
    /// <summary>A click takes the window (or monitor) under the pointer, a drag takes a region.</summary>
    Capture,

    /// <summary>The same click or drag, recognised as text and copied instead of saved as an image.</summary>
    Text,

    /// <summary>A click copies the colour of the frozen pixel under the pointer; nothing is captured.</summary>
    Color,
}

/// <summary>Something the selector hands back to the app instead of capturing: it closes and the app runs it.</summary>
internal enum CaptureSelectorAction
{
    /// <summary>The record half of the capture/record switch: the recording region selector opens.</summary>
    Record,

    /// <summary>A delayed region capture after <see cref="CaptureSelectorRequest.DelaySeconds"/>.</summary>
    DelayedCapture,

    /// <summary>Scrolling capture of the window under the pointer.</summary>
    ScrollingCapture,

    /// <summary>The previous region again, without a selector.</summary>
    RepeatLastRegion,
}

/// <summary>One request from the selector's switch or ⋯ menu; the app runs its existing command for it.</summary>
internal sealed record CaptureSelectorRequest(CaptureSelectorAction Action, int DelaySeconds = 0);

/// <summary>
/// The capture selector's mode bar: icon-and-name buttons at the top of the monitor under the pointer,
/// with the current mode filled in. It follows the Windows Snipping Tool convention of a capture/record
/// switch beside the capture types, with the rarer commands (delay, scrolling, previous region) in a
/// ⋯ menu, and keeps MyCapture's own modes.
/// </summary>
/// <remarks>
/// The bar is drawn from the selector's capture-guide palette (all six themes and the guide colour
/// override), not from the chrome tokens, so it matches the highlight around it. Every button has a
/// tooltip, an automation name and, where one exists, its key as tooltip text and accelerator key; the
/// bar draws no key badges. The bar only raises requests; the overlay view decides what they do. It
/// is never part of a capture: the selection is cropped from the frozen frame, not from what the
/// overlay draws.
/// </remarks>
internal sealed class CaptureModeBar : Border
{
    internal const string SurfaceKey = "CaptureBar.Surface";
    internal const string BorderKey = "CaptureBar.Border";
    internal const string TextKey = "CaptureBar.Text";
    internal const string MutedKey = "CaptureBar.Muted";
    internal const string AccentKey = "CaptureBar.Accent";
    internal const string AccentTextKey = "CaptureBar.AccentText";
    internal const string HoverKey = "CaptureBar.Hover";

    /// <summary>The delays the ⋯ menu offers, in seconds.</summary>
    internal static readonly IReadOnlyList<int> DelayChoices = [3, 5, 10];

    // 20x20 outline glyphs on the shared symbol grid, stroked like Icon.Glyph.
    private static readonly Geometry RegionIcon = Geometry.Parse(
        "M3.5,7 L3.5,3.5 L7,3.5 M13,3.5 L16.5,3.5 L16.5,7 M16.5,13 L16.5,16.5 L13,16.5 M7,16.5 L3.5,16.5 L3.5,13 M8,8 L12,8 L12,12 L8,12 Z");
    private static readonly Geometry FullScreenIcon = Geometry.Parse(
        "M3.25,3.75 L16.75,3.75 A1,1 0 0 1 17.75,4.75 L17.75,13 A1,1 0 0 1 16.75,14 L3.25,14 A1,1 0 0 1 2.25,13 L2.25,4.75 A1,1 0 0 1 3.25,3.75 Z M10,14 L10,16.5 M6.5,16.5 L13.5,16.5");
    private static readonly Geometry TextIcon = Geometry.Parse(
        "M3.5,7 L3.5,3.5 L7,3.5 M13,3.5 L16.5,3.5 L16.5,7 M16.5,13 L16.5,16.5 L13,16.5 M7,16.5 L3.5,16.5 L3.5,13 M7,7 L13,7 M10,7 L10,13.5");
    private static readonly Geometry ColorIcon = Geometry.Parse(
        "M12.4,3.6 A2.1,2.1 0 0 1 16.4,7.6 L14.6,9.4 L10.6,5.4 Z M11.6,8.4 L5.2,14.8 L4.2,16.8 L6.2,15.8 L12.6,9.4 M9.6,4.4 L15.6,10.4");
    private static readonly Geometry HelpIcon = Geometry.Parse(
        "M10,2.75 A7.25,7.25 0 1 1 9.99,2.75 M7.6,7.9 A2.45,2.45 0 1 1 10.9,10.2 C10.25,10.5 10,10.95 10,11.7 L10,12.2 M10,14.6 L10,14.7");
    private static readonly Geometry CancelIcon = Geometry.Parse("M5.25,5.25 L14.75,14.75 M14.75,5.25 L5.25,14.75");
    private static readonly Geometry CameraIcon = Geometry.Parse(
        "M3.5,6.5 L6.25,6.5 L7.5,4.5 L12.5,4.5 L13.75,6.5 L16.5,6.5 A1,1 0 0 1 17.5,7.5 L17.5,15 A1,1 0 0 1 16.5,16 L3.5,16 A1,1 0 0 1 2.5,15 L2.5,7.5 A1,1 0 0 1 3.5,6.5 Z M10,8.25 A2.75,2.75 0 1 1 9.99,8.25");
    private static readonly Geometry RecordIcon = Geometry.Parse(
        "M10,3.25 A6.75,6.75 0 1 1 9.99,3.25 M10,7.25 A2.75,2.75 0 1 1 9.99,7.25");
    private static readonly Geometry MoreIcon = Geometry.Parse(
        "M5,9 A1,1 0 1 0 4.99,9 M10,9 A1,1 0 1 0 9.99,9 M15,9 A1,1 0 1 0 14.99,9");

    private readonly RadioButton _capture;
    private readonly RadioButton _text;
    private readonly RadioButton _color;
    private readonly RadioButton _captureKind;
    private readonly RadioButton _recordKind;
    private readonly Button _fullScreen;
    private readonly Button _more;
    private readonly Button _help;
    private readonly Button _cancel;
    private readonly TextBlock _captureLabel;
    private readonly FrameworkElement _textSeparator;
    private readonly FrameworkElement _kindSwitch;
    private readonly FrameworkElement _kindSeparator;
    private ContextMenu? _moreMenu;
    private bool _syncing;
    private bool _detectWindows = true;
    private bool _canRepeatLastRegion = true;

    internal CaptureModeBar()
    {
        Focusable = false;
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(4);
        SetResourceReference(BackgroundProperty, SurfaceKey);
        SetResourceReference(BorderBrushProperty, BorderKey);
        AutomationProperties.SetName(this, UiText.Get("CaptureGuide2_BarName"));

        ControlTemplate modeTemplate = CreateTemplate(typeof(RadioButton), checkable: true, subtleCheck: false);
        ControlTemplate kindTemplate = CreateTemplate(typeof(RadioButton), checkable: true, subtleCheck: true);
        ControlTemplate actionTemplate = CreateTemplate(typeof(Button), checkable: false, subtleCheck: false);

        // Capture | Record. Capture is where the user already is; Record leaves for the recording selector.
        _captureKind = KindButton(CameraIcon, UiText.Get("Onboard_BarCapture"), kindTemplate);
        _captureKind.IsChecked = true;
        _recordKind = KindButton(RecordIcon, UiText.Get("Onboard_BarRecord"), kindTemplate);
        _recordKind.Checked += (_, _) =>
        {
            if (_syncing) return;
            ActionRequested?.Invoke(new CaptureSelectorRequest(CaptureSelectorAction.Record));
            ShowCaptureKind();
        };
        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        kinds.Children.Add(_captureKind);
        kinds.Children.Add(_recordKind);
        AutomationProperties.SetName(kinds, UiText.Get("Onboard_BarKindName"));
        _kindSwitch = kinds;
        _kindSeparator = Divider();

        _captureLabel = new TextBlock();
        _capture = ModeButton(CaptureSelectorMode.Capture, RegionIcon, _captureLabel, modeTemplate);
        _fullScreen = ActionButton(FullScreenIcon, UiText.Get("CaptureGuide2_FullScreen"), actionTemplate);
        _text = ModeButton(CaptureSelectorMode.Text, TextIcon, new TextBlock { Text = UiText.Get("CaptureGuide2_Text") }, modeTemplate);
        _color = ModeButton(CaptureSelectorMode.Color, ColorIcon, new TextBlock { Text = UiText.Get("CaptureGuide2_Color") }, modeTemplate);
        _more = ActionButton(MoreIcon, label: null, actionTemplate);
        _help = ActionButton(HelpIcon, label: null, actionTemplate);
        _cancel = ActionButton(CancelIcon, label: null, actionTemplate);
        _fullScreen.Click += (_, _) => FullScreenRequested?.Invoke();
        _more.Click += (_, _) => OpenMoreMenu();
        _help.Click += (_, _) => HelpRequested?.Invoke();
        _cancel.Click += (_, _) => CancelRequested?.Invoke();
        AutomationProperties.SetName(_more, UiText.Get("Onboard_BarMore"));
        AutomationProperties.SetName(_help, UiText.Get("CaptureGuide2_Help"));
        AutomationProperties.SetName(_cancel, UiText.Get("CaptureGuide2_Cancel"));

        _textSeparator = Divider();
        var items = new StackPanel { Orientation = Orientation.Horizontal };
        KeyboardNavigation.SetDirectionalNavigation(items, KeyboardNavigationMode.Cycle);
        foreach (UIElement item in new UIElement[]
                 {
                     _kindSwitch, _kindSeparator, _capture, _fullScreen, _textSeparator, _text, _color, Divider(), _more, _help, _cancel,
                 })
            items.Children.Add(item);
        Child = items;
        ApplyTexts();
    }

    /// <summary>A mode button was chosen by click, keyboard or UI Automation.</summary>
    internal event Action<CaptureSelectorMode>? ModeRequested;

    /// <summary>The record switch or a ⋯ menu command was chosen.</summary>
    internal event Action<CaptureSelectorRequest>? ActionRequested;

    internal event Action? FullScreenRequested;

    internal event Action? HelpRequested;

    internal event Action? CancelRequested;

    internal RadioButton CaptureButton => _capture;

    internal RadioButton TextButton => _text;

    internal RadioButton ColorButton => _color;

    internal RadioButton CaptureKindButton => _captureKind;

    internal RadioButton RecordKindButton => _recordKind;

    internal Button FullScreenButton => _fullScreen;

    internal Button MoreButton => _more;

    internal Button HelpButton => _help;

    internal Button CancelButton => _cancel;

    /// <summary>The ⋯ menu while it is open.</summary>
    internal ContextMenu? MoreMenuForTest => _moreMenu;

    /// <summary>
    /// Shows only what the owner enabled. Without window detection the first mode is a plain region;
    /// text and colour need their owners' handlers; the capture/record switch and the ⋯ menu need an
    /// owner that runs their commands.
    /// </summary>
    internal void Configure(bool detectWindows, bool textAvailable, bool colorAvailable, bool otherActions = false,
        bool canRepeatLastRegion = true)
    {
        _detectWindows = detectWindows;
        _canRepeatLastRegion = canRepeatLastRegion;
        _text.Visibility = textAvailable ? Visibility.Visible : Visibility.Collapsed;
        _color.Visibility = colorAvailable ? Visibility.Visible : Visibility.Collapsed;
        _textSeparator.Visibility = textAvailable || colorAvailable ? Visibility.Visible : Visibility.Collapsed;
        Visibility others = otherActions ? Visibility.Visible : Visibility.Collapsed;
        _kindSwitch.Visibility = _kindSeparator.Visibility = _more.Visibility = others;
        ApplyTexts();
    }

    /// <summary>Fills in the current mode without raising <see cref="ModeRequested"/>.</summary>
    internal void ShowMode(CaptureSelectorMode mode)
    {
        _syncing = true;
        try
        {
            _capture.IsChecked = mode == CaptureSelectorMode.Capture;
            _text.IsChecked = mode == CaptureSelectorMode.Text;
            _color.IsChecked = mode == CaptureSelectorMode.Color;
        }
        finally { _syncing = false; }
    }

    /// <summary>Takes the selector's guide brushes, so the bar follows the theme and the guide colour.</summary>
    internal void ApplyPalette(IReadOnlyDictionary<string, Brush> palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Resources[SurfaceKey] = palette["CaptureGuide.ChipSurface"];
        Resources[BorderKey] = palette["CaptureGuide.ChipBorder"];
        Resources[TextKey] = palette["CaptureGuide.Text"];
        Resources[MutedKey] = palette["CaptureGuide.MutedText"];
        Resources[AccentKey] = palette["CaptureGuide.SelectionBorder"];
        Resources[AccentTextKey] = palette["CaptureGuide.AccentText"];
        Resources[HoverKey] = palette["CaptureGuide.InnerGlow"];
    }

    /// <summary>True for this bar and anything inside it.</summary>
    internal bool Holds(DependencyObject? element)
    {
        for (DependencyObject? current = element; current is not null; current = Parent(current))
            if (ReferenceEquals(current, this)) return true;
        return false;

        static DependencyObject? Parent(DependencyObject child) =>
            child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
    }

    /// <summary>
    /// The ⋯ menu: delayed capture after 3, 5 or 10 seconds, scrolling capture and the previous
    /// region. Each entry closes the selector and runs the app's existing command.
    /// </summary>
    internal ContextMenu CreateMoreMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _more, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
        AutomationProperties.SetName(menu, UiText.Get("Onboard_BarMore"));
        foreach (int seconds in DelayChoices)
            menu.Items.Add(MenuItemFor(menu, UiText.Format("Onboard_DelayCapture", seconds),
                new CaptureSelectorRequest(CaptureSelectorAction.DelayedCapture, seconds)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor(menu, UiText.Get("Text_4DBA30197C0A"), new CaptureSelectorRequest(CaptureSelectorAction.ScrollingCapture)));
        MenuItem repeat = MenuItemFor(menu, UiText.Get("Text_878A28AF568C"), new CaptureSelectorRequest(CaptureSelectorAction.RepeatLastRegion));
        repeat.IsEnabled = _canRepeatLastRegion;
        menu.Items.Add(repeat);
        return menu;
    }

    private void OpenMoreMenu()
    {
        if (_moreMenu is { IsOpen: true }) return;
        ContextMenu menu = CreateMoreMenu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_moreMenu, menu)) _moreMenu = null; };
        _moreMenu = menu;
        menu.IsOpen = true;
    }

    private MenuItem MenuItemFor(ContextMenu menu, string header, CaptureSelectorRequest request)
    {
        var item = new MenuItem { Header = header, MinHeight = 36 };
        AutomationProperties.SetName(item, header);
        item.Click += (_, _) =>
        {
            menu.IsOpen = false;
            ActionRequested?.Invoke(request);
        };
        return item;
    }

    private void ShowCaptureKind()
    {
        _syncing = true;
        try { _captureKind.IsChecked = true; }
        finally { _syncing = false; }
    }

    private void ApplyTexts()
    {
        _captureLabel.Text = UiText.Get(_detectWindows ? "CaptureGuide2_Region" : "CaptureGuide2_RegionOnly");
        AutomationProperties.SetName(_capture, _captureLabel.Text);
        Describe(_capture, _detectWindows ? "CaptureGuide2_RegionHelp" : "CaptureGuide2_RegionOnlyHelp", key: null);
        AutomationProperties.SetName(_fullScreen, UiText.Get("CaptureGuide2_FullScreen"));
        Describe(_fullScreen, "CaptureGuide2_FullScreenHelp", key: null);
        AutomationProperties.SetName(_text, UiText.Get("CaptureGuide2_Text"));
        Describe(_text, "CaptureGuide2_TextHelp", "T");
        AutomationProperties.SetName(_color, UiText.Get("CaptureGuide2_Color"));
        Describe(_color, "CaptureGuide2_ColorHelp", "C");
        Describe(_captureKind, "Onboard_BarCaptureHelp", key: null);
        Describe(_recordKind, "Onboard_BarRecordHelp", key: null);
        Describe(_more, "Onboard_BarMoreHelp", key: null);
        Describe(_help, "CaptureGuide2_HelpTip", key: null);
        Describe(_cancel, "CaptureGuide2_CancelTip", "Esc");
    }

    /// <summary>Tooltip and automation help; a key goes into the tooltip and the accelerator key, never onto the bar.</summary>
    private static void Describe(ButtonBase button, string helpKey, string? key)
    {
        string help = UiText.Get(helpKey);
        AutomationProperties.SetHelpText(button, help);
        if (key is not null) AutomationProperties.SetAcceleratorKey(button, key);
        string tip = key is not null ? $"{help} ({key})" : help;
        string name = AutomationProperties.GetName(button);
        button.ToolTip = string.IsNullOrEmpty(name) ? tip : $"{name}\n{tip}";
    }

    private RadioButton ModeButton(CaptureSelectorMode mode, Geometry icon, TextBlock label, ControlTemplate template)
    {
        var button = new RadioButton { Template = template };
        Decorate(button);
        button.Content = ButtonContent(button, icon, label);
        button.Checked += (_, _) =>
        {
            if (!_syncing) ModeRequested?.Invoke(mode);
        };
        return button;
    }

    private static RadioButton KindButton(Geometry icon, string label, ControlTemplate template)
    {
        var button = new RadioButton { Template = template };
        Decorate(button);
        button.Content = ButtonContent(button, icon, new TextBlock { Text = label });
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static Button ActionButton(Geometry icon, string? label, ControlTemplate template)
    {
        var button = new Button { Template = template };
        Decorate(button);
        button.Content = ButtonContent(button, icon, label is null ? null : new TextBlock { Text = label });
        return button;
    }

    private static void Decorate(ButtonBase button)
    {
        CommandButtonPresentation.Apply(button, compact: true);
        button.Cursor = Cursors.Hand;
        button.FocusVisualStyle = null;
        button.Background = Brushes.Transparent;
        // A style setter, not a local value, so the checked template trigger can switch to the accent text.
        var style = new Style(button.GetType());
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(TextKey)));
        button.Style = style;
        ToolTipService.SetInitialShowDelay(button, 400);
    }

    private static FrameworkElement ButtonContent(ButtonBase owner, Geometry icon, TextBlock? label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new SymbolIcon
        {
            Size = CommandButtonPresentation.IconSize,
            Data = icon,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetBinding(SymbolIcon.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = owner });
        panel.Children.Add(glyph);
        if (label is not null)
        {
            label.FontSize = 13;
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(CommandButtonPresentation.IconLabelGap, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(label);
        }

        return panel;
    }

    private static FrameworkElement Divider()
    {
        var line = new Rectangle
        {
            Width = 1,
            Height = 20,
            Margin = new Thickness(5, 0, 5, 0),
            Opacity = 0.45,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        line.SetResourceReference(Shape.FillProperty, MutedKey);
        return line;
    }

    /// <summary>
    /// Chrome, content and an inset focus ring with no layout footprint. A checked mode is filled
    /// with the guide accent and takes the accent's readable text colour; the capture/record switch
    /// marks its side with the quieter hover wash so the bar keeps one accent fill. Hover and the
    /// checked fill are layers that fade (FluidMotion.FadeTo) rather than background swaps, so a
    /// mode change glides like the shared controls. Focus never moves anything.
    /// </summary>
    private static ControlTemplate CreateTemplate(Type type, bool checkable, bool subtleCheck)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        var hoverLayer = StateLayer("HoverLayer", MotionSpeed.Fast);
        hoverLayer.SetResourceReference(Border.BackgroundProperty, HoverKey);
        FrameworkElementFactory? checkedLayer = null;
        if (checkable)
        {
            checkedLayer = StateLayer("CheckedLayer", MotionSpeed.Normal);
            if (subtleCheck)
            {
                // The quiet selected side: a wash and an outline in the text colour, which also reads in high contrast.
                checkedLayer.SetResourceReference(Border.BackgroundProperty, HoverKey);
                checkedLayer.SetResourceReference(Border.BorderBrushProperty, MutedKey);
            }
            else
            {
                checkedLayer.SetResourceReference(Border.BackgroundProperty, AccentKey);
            }
        }

        // The presenter keeps the one-pixel chrome inset it had as the chrome's child.
        var content = new FrameworkElementFactory(typeof(Border));
        content.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.AppendChild(presenter);
        var ring = new FrameworkElementFactory(typeof(Border), "FocusRing");
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
        ring.SetValue(UIElement.OpacityProperty, 0.0);
        ring.SetResourceReference(Border.BorderBrushProperty, TextKey);
        var root = new FrameworkElementFactory(typeof(Grid));
        root.AppendChild(chrome);
        if (checkedLayer is not null)
        {
            root.AppendChild(checkedLayer);
        }

        root.AppendChild(hoverLayer);
        root.AppendChild(content);
        root.AppendChild(ring);

        var template = new ControlTemplate(type) { VisualTree = root };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(FluidMotion.FadeToProperty, 1.0, "HoverLayer"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(FluidMotion.FadeToProperty, 1.0, "HoverLayer"));
        template.Triggers.Add(pressed);
        if (checkable)
        {
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(FluidMotion.FadeToProperty, 1.0, "CheckedLayer"));
            if (!subtleCheck)
            {
                on.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(AccentTextKey)));
            }

            template.Triggers.Add(on);
        }

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "FocusRing"));
        template.Triggers.Add(focus);
        if (checkable && !subtleCheck)
        {
            // On the accent fill the ring takes the fill's text colour, which stays visible in high contrast.
            var focusOn = new MultiTrigger();
            focusOn.Conditions.Add(new System.Windows.Condition(ToggleButton.IsCheckedProperty, true));
            focusOn.Conditions.Add(new System.Windows.Condition(UIElement.IsKeyboardFocusedProperty, true));
            focusOn.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(AccentTextKey), "FocusRing"));
            template.Triggers.Add(focusOn);
        }

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.46));
        template.Triggers.Add(disabled);
        template.Seal();
        return template;
    }

    /// <summary>A hidden, hit-test-transparent wash with the chrome's shape that fades in on a state.</summary>
    private static FrameworkElementFactory StateLayer(string name, MotionSpeed speed)
    {
        var layer = new FrameworkElementFactory(typeof(Border), name);
        layer.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        layer.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        layer.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        layer.SetValue(UIElement.OpacityProperty, 0.0);
        layer.SetValue(UIElement.IsHitTestVisibleProperty, false);
        layer.SetValue(FluidMotion.TransitionProperty, speed);
        return layer;
    }
}
