using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

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

/// <summary>
/// The capture selector's mode bar: icon-and-name buttons at the top of the monitor under the pointer,
/// with the current mode filled in. It follows the Windows Snipping Tool convention of choosing a
/// capture type from a bar instead of reading a list of keys, and keeps MyCapture's own modes.
/// </summary>
/// <remarks>
/// The bar is drawn from the selector's capture-guide palette (all six themes and the guide colour
/// override), not from the chrome tokens, so it matches the chips and the highlight around it. Every
/// button has a tooltip and an automation name; the optional key badge follows the shortcut-hint
/// setting. The bar only raises requests; the overlay view decides what they do. It is never part of
/// a capture: the selection is cropped from the frozen frame, not from what the overlay draws.
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

    private readonly RadioButton _capture;
    private readonly RadioButton _text;
    private readonly RadioButton _color;
    private readonly Button _fullScreen;
    private readonly Button _help;
    private readonly Button _cancel;
    private readonly TextBlock _captureLabel;
    private readonly FrameworkElement _textSeparator;
    private readonly List<FrameworkElement> _keyBadges = [];
    private bool _syncing;
    private bool _detectWindows = true;
    private bool _showShortcutHints = true;

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

        ControlTemplate modeTemplate = CreateTemplate(typeof(RadioButton), checkable: true);
        ControlTemplate actionTemplate = CreateTemplate(typeof(Button), checkable: false);
        _captureLabel = new TextBlock();
        _capture = ModeButton(CaptureSelectorMode.Capture, RegionIcon, _captureLabel, key: null, modeTemplate);
        _fullScreen = ActionButton(FullScreenIcon, UiText.Get("CaptureGuide2_FullScreen"), key: null, actionTemplate);
        _text = ModeButton(CaptureSelectorMode.Text, TextIcon, new TextBlock { Text = UiText.Get("CaptureGuide2_Text") }, "T", modeTemplate);
        _color = ModeButton(CaptureSelectorMode.Color, ColorIcon, new TextBlock { Text = UiText.Get("CaptureGuide2_Color") }, "C", modeTemplate);
        _help = ActionButton(HelpIcon, label: null, key: null, actionTemplate);
        _cancel = ActionButton(CancelIcon, label: null, "Esc", actionTemplate);
        _fullScreen.Click += (_, _) => FullScreenRequested?.Invoke();
        _help.Click += (_, _) => HelpRequested?.Invoke();
        _cancel.Click += (_, _) => CancelRequested?.Invoke();
        AutomationProperties.SetName(_help, UiText.Get("CaptureGuide2_Help"));
        AutomationProperties.SetName(_cancel, UiText.Get("CaptureGuide2_Cancel"));

        _textSeparator = Divider();
        var items = new StackPanel { Orientation = Orientation.Horizontal };
        KeyboardNavigation.SetDirectionalNavigation(items, KeyboardNavigationMode.Cycle);
        foreach (UIElement item in new UIElement[] { _capture, _fullScreen, _textSeparator, _text, _color, Divider(), _help, _cancel })
            items.Children.Add(item);
        Child = items;
        ApplyTexts();
    }

    /// <summary>A mode button was chosen by click, keyboard or UI Automation.</summary>
    internal event Action<CaptureSelectorMode>? ModeRequested;

    internal event Action? FullScreenRequested;

    internal event Action? HelpRequested;

    internal event Action? CancelRequested;

    internal RadioButton CaptureButton => _capture;

    internal RadioButton TextButton => _text;

    internal RadioButton ColorButton => _color;

    internal Button FullScreenButton => _fullScreen;

    internal Button HelpButton => _help;

    internal Button CancelButton => _cancel;

    /// <summary>Visible key badges, in bar order; empty when shortcut hints are off.</summary>
    internal IReadOnlyList<string> VisibleKeyBadgesForTest => _keyBadges
        .Where(badge => badge.Visibility == Visibility.Visible && IsShown(badge))
        .Select(badge => ((TextBlock)((Border)badge).Child).Text).ToArray();

    /// <summary>
    /// Shows only what the owner enabled. Without window detection the first mode is a plain region;
    /// text and colour need their owners' handlers. Shortcut hints add the key badges and tooltip keys.
    /// </summary>
    internal void Configure(bool detectWindows, bool textAvailable, bool colorAvailable, bool showShortcutHints)
    {
        _detectWindows = detectWindows;
        _showShortcutHints = showShortcutHints;
        _text.Visibility = textAvailable ? Visibility.Visible : Visibility.Collapsed;
        _color.Visibility = colorAvailable ? Visibility.Visible : Visibility.Collapsed;
        _textSeparator.Visibility = textAvailable || colorAvailable ? Visibility.Visible : Visibility.Collapsed;
        foreach (FrameworkElement badge in _keyBadges)
            badge.Visibility = showShortcutHints ? Visibility.Visible : Visibility.Collapsed;
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
        Describe(_help, "CaptureGuide2_HelpTip", key: null);
        Describe(_cancel, "CaptureGuide2_CancelTip", "Esc");
    }

    /// <summary>Tooltip and automation help; the key is appended like other MyCapture tooltips.</summary>
    private void Describe(ButtonBase button, string helpKey, string? key)
    {
        string help = UiText.Get(helpKey);
        AutomationProperties.SetHelpText(button, help);
        if (key is not null) AutomationProperties.SetAcceleratorKey(button, key);
        string tip = key is not null && _showShortcutHints ? $"{help} ({key})" : help;
        string name = AutomationProperties.GetName(button);
        button.ToolTip = string.IsNullOrEmpty(name) ? tip : $"{name}\n{tip}";
    }

    private RadioButton ModeButton(CaptureSelectorMode mode, Geometry icon, TextBlock label, string? key, ControlTemplate template)
    {
        var button = new RadioButton { Template = template };
        Decorate(button);
        button.Content = ButtonContent(button, icon, label, key);
        button.Checked += (_, _) =>
        {
            if (!_syncing) ModeRequested?.Invoke(mode);
        };
        return button;
    }

    private Button ActionButton(Geometry icon, string? label, string? key, ControlTemplate template)
    {
        var button = new Button { Template = template };
        Decorate(button);
        button.Content = ButtonContent(button, icon, label is null ? null : new TextBlock { Text = label }, key);
        if (label is null) button.Padding = new Thickness(8, 0, 8, 0);
        return button;
    }

    private static void Decorate(ButtonBase button)
    {
        button.Height = 36;
        button.MinWidth = 36;
        button.Margin = new Thickness(1, 0, 1, 0);
        button.Padding = new Thickness(10, 0, 11, 0);
        button.Cursor = Cursors.Hand;
        button.FocusVisualStyle = null;
        button.Background = Brushes.Transparent;
        // A style setter, not a local value, so the checked template trigger can switch to the accent text.
        var style = new Style(button.GetType());
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(TextKey)));
        button.Style = style;
        ToolTipService.SetInitialShowDelay(button, 400);
    }

    private FrameworkElement ButtonContent(ButtonBase owner, Geometry icon, TextBlock? label, string? key)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new Path
        {
            Data = icon,
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = owner });
        panel.Children.Add(glyph);
        if (label is not null)
        {
            label.FontSize = 13;
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(7, 0, 0, 1);
            label.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(label);
        }

        if (key is not null)
        {
            var text = new TextBlock
            {
                Text = key,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var badge = new Border
            {
                Child = text,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 0, 4, 1),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.85,
            };
            badge.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.Foreground)) { Source = owner });
            AutomationProperties.SetName(badge, key);
            _keyBadges.Add(badge);
            panel.Children.Add(badge);
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

    private bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null && !ReferenceEquals(current, this);
             current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    /// <summary>
    /// Chrome, content and an inset focus ring with no layout footprint. The checked mode is filled
    /// with the guide accent and takes the accent's readable text colour; focus never moves anything.
    /// </summary>
    private static ControlTemplate CreateTemplate(Type type, bool checkable)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var ring = new FrameworkElementFactory(typeof(Border), "FocusRing");
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
        ring.SetValue(UIElement.OpacityProperty, 0.0);
        ring.SetResourceReference(Border.BorderBrushProperty, TextKey);
        var root = new FrameworkElementFactory(typeof(Grid));
        root.AppendChild(chrome);
        root.AppendChild(ring);

        var template = new ControlTemplate(type) { VisualTree = root };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(HoverKey), "Chrome"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(HoverKey), "Chrome"));
        template.Triggers.Add(pressed);
        if (checkable)
        {
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(AccentKey), "Chrome"));
            on.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(AccentTextKey)));
            template.Triggers.Add(on);
        }

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "FocusRing"));
        template.Triggers.Add(focus);
        if (checkable)
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
}
