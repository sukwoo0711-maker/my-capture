using MyCapture.App.Themes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private static readonly Thickness PropertyMargin = new(0, 3, 12, 3);
    private FrameworkElement _fillSection = null!;
    private FrameworkElement _mosaicSection = null!;
    private Slider _mosaicSlider = null!;
    private EditorTool _obscureStyle = EditorTool.Mosaic;
    private Popup _customColorPopup = null!;
    private Button _customColorButton = null!;
    private FrameworkElement _arrowHeadSection = null!;
    private readonly Dictionary<ArrowHeadStyle, ToggleButton> _arrowHeadButtons = [];

    private FrameworkElement BuildArrowHeadSection()
    {
        var group = PropertyGroup("AnnotationArrowHeadProperties");
        group.Children.Add(PropertyLabel(UiText.Get("EditorProperty_ArrowHead")));
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (ArrowHeadStyle style in Enum.GetValues<ArrowHeadStyle>())
        {
            var button = new ToggleButton
            {
                Content = UiText.Get("EditorProperty_ArrowHead" + style), MinHeight = 32, MinWidth = 44,
                FontSize = TypeScale.Caption, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(1, 0, 1, 0),
            };
            AutomationProperties.SetName(button, UiText.Get("EditorProperty_ArrowHead") + ": " + button.Content);
            AutomationProperties.SetAutomationId(button, "AnnotationArrowHead" + style);
            PreserveEditorMouseFocus(button);
            button.Click += (_, _) =>
            {
                _controller.ApplyArrowHeads(style);
                RememberPreferences();
                RefreshArrowHeads();
            };
            _arrowHeadButtons.Add(style, button);
            choices.Children.Add(button);
        }
        group.Children.Add(choices);
        return group;
    }

    private void RefreshArrowHeads()
    {
        PolylineAnnotation[] lines = _controller.SelectedItems.OfType<PolylineAnnotation>().ToArray();
        foreach ((ArrowHeadStyle style, ToggleButton button) in _arrowHeadButtons)
            button.IsChecked = lines.Length == 0 ? _controller.ArrowHeads == style
                : lines.All(line => line.HeadAtStart == (style == ArrowHeadStyle.Both)
                    && line.HeadAtEnd == (style != ArrowHeadStyle.None));
    }

    private static StackPanel PropertyGroup(string id) => new()
    {
        Name = id,
        Orientation = Orientation.Horizontal,
        Margin = PropertyMargin,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock PropertyLabel(string text)
    {
        TextBlock label = SectionLabel(text);
        label.Margin = new Thickness(0, 0, 8, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    private FrameworkElement BuildColorSection()
    {
        var panel = PropertyGroup("AnnotationColorProperties");
        _swatchPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        ColorRgba[] palette =
        [
            ColorRgba.FromRgb(0xEF, 0x44, 0x44),
            ColorRgba.FromRgb(0xFB, 0xBF, 0x24),
            ColorRgba.FromRgb(0x34, 0xD3, 0x99),
            ColorRgba.FromRgb(0x3B, 0x82, 0xF6),
            ColorRgba.FromRgb(0x11, 0x18, 0x27),
            ColorRgba.White,
        ];
        foreach (ColorRgba color in palette)
        {
            var dot = new Ellipse { Width = 18, Height = 18, Fill = color.ToBrush(), StrokeThickness = 1 };
            dot.SetResourceReference(Shape.StrokeProperty, "Border.Control");
            var ring = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                Child = dot,
            };
            var button = PropertyButton(ring, UiText.Format("Text_151C77170A94", ColorCode(color)));
            button.Tag = color;
            AutomationProperties.SetAutomationId(button, "AnnotationColorSwatch" + ColorCode(color).TrimStart('#'));
            PreserveEditorMouseFocus(button);
            button.Click += (_, _) =>
            {
                _controller.ApplyStrokeColor(color);
                RememberPreferences();
                RefreshStepFeedback();
                UpdateColorSwatches();
                SetStatus(UiText.Format("Text_67CAE5B118A3", ColorCode(color)));
            };
            _swatchPanel.Children.Add(button);
        }
        panel.Children.Add(_swatchPanel);
        var customDot = new Ellipse
        {
            Width = 18, Height = 18, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
        };
        customDot.SetResourceReference(Shape.StrokeProperty, "Border.Control");
        var customGlyph = new Grid { Width = 26, Height = 26 };
        customGlyph.Children.Add(customDot);
        var plus = new TextBlock
        {
            Text = "+", FontSize = TypeScale.Caption, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        plus.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        customGlyph.Children.Add(plus);
        _customColorButton = PropertyButton(customGlyph, UiText.Get("EditorProperty_CustomColor"));
        AutomationProperties.SetAutomationId(_customColorButton, "AnnotationCustomColorOpen");
        _customColorButton.ToolTip = UiText.Get("EditorProperty_CustomColorHint");
        AutomationProperties.SetHelpText(_customColorButton, UiText.Get("EditorProperty_CustomColorHint"));
        panel.Children.Add(_customColorButton);

        _customColorInput = new TextBox
        {
            Text = ColorCode(_controller.StrokeColor), MinHeight = 32,
            FontSize = TypeScale.Caption, Margin = new Thickness(0, 0, 0, 8),
        };
        AutomationName(_customColorInput, UiText.Get("Tools.CustomColor"));
        AutomationProperties.SetAutomationId(_customColorInput, "AnnotationCustomColorHex");
        InputMethod.SetIsInputMethodEnabled(_customColorInput, true);
        _customColorInput.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (ApplyCustomColor(_customColorInput.Text)) FinishCustomColorInput();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _customColorInput.Text = ColorCode(CurrentPropertyColor());
                FinishCustomColorInput();
            }
        };
        var apply = new Button
        {
            Content = UiText.Get("Tools.CustomColor"), Height = 32, MinHeight = 32, FontSize = TypeScale.Caption,
        };
        apply.SetResourceReference(StyleProperty, "Button.Compact");
        AutomationName(apply, UiText.Get("Tools.CustomColor"));
        AutomationProperties.SetAutomationId(apply, "AnnotationCustomColorApply");
        apply.Click += (_, _) =>
        {
            if (ApplyCustomColor(_customColorInput.Text)) FinishCustomColorInput();
        };
        var content = new StackPanel();
        content.Children.Add(SectionLabel(UiText.Get("EditorProperty_Hex")));
        content.Children.Add(_customColorInput);
        content.Children.Add(apply);
        var flyout = new Border
        {
            Child = content, Padding = new Thickness(12), Width = 232,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        };
        flyout.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        flyout.SetResourceReference(Border.BorderBrushProperty, "Border.Control");
        KeyboardNavigation.SetTabNavigation(flyout, KeyboardNavigationMode.Cycle);
        flyout.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            _customColorInput.Text = ColorCode(CurrentPropertyColor());
            CloseCustomColorFlyout(returnFocus: true);
        };
        _customColorPopup = new Popup
        {
            PlacementTarget = _customColorButton, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, Child = flyout,
        };
        DisableImeOutsideTextInputs(flyout);
        _customColorPopup.Opened += (_, _) =>
        {
            _customColorInput.Focus();
            _customColorInput.SelectAll();
        };
        _customColorButton.Click += (_, _) =>
        {
            if (_customColorPopup.IsOpen) CloseCustomColorFlyout(returnFocus: true);
            else
            {
                _customColorInput.Text = ColorCode(CurrentPropertyColor());
                _customColorPopup.IsOpen = true;
            }
        };
        panel.Children.Add(_customColorPopup);
        return panel;
    }

    private static Button PropertyButton(object content, string name)
    {
        var button = new Button
        {
            Content = content, Width = 32, Height = 32, MinWidth = 32, MinHeight = 32,
            Padding = new Thickness(0), FontSize = TypeScale.Caption, ToolTip = name,
        };
        button.SetResourceReference(StyleProperty, "Button.GhostCompact");
        AutomationName(button, name);
        return button;
    }

    private void FinishCustomColorInput()
    {
        if (_customColorPopup.IsOpen) CloseCustomColorFlyout(returnFocus: true);
        else Focus();
    }

    private void CloseCustomColorFlyout(bool returnFocus)
    {
        if (_customColorPopup is null || !_customColorPopup.IsOpen) return;
        _customColorPopup.IsOpen = false;
        if (returnFocus) _customColorButton.Focus();
    }

    private ColorRgba CurrentPropertyColor() => _controller.Selected switch
    {
        ShapeAnnotation shape => shape.Stroke,
        PolylineAnnotation line => line.Stroke,
        PenAnnotation pen => pen.Stroke,
        TextAnnotation { IsStepNumber: true } step => step.Background,
        TextAnnotation text => text.Foreground,
        _ => _controller.StrokeColor,
    };

    /// <remarks>
    /// Runs on every inspector refresh, including each selection change of a marquee drag, so
    /// a ring is re-pointed only when its swatch becomes or stops being the current colour.
    /// A ring that already follows the <c>Text.Primary</c> resource keeps following it, so a
    /// theme change still reaches it.
    /// </remarks>
    private void UpdateColorSwatches()
    {
        if (_swatchPanel is null) return;
        ColorRgba current = CurrentPropertyColor();
        foreach (Button button in _swatchPanel.Children.OfType<Button>())
        {
            bool selected = button.Tag is ColorRgba color && color == current;
            var ring = (Border)button.Content;
            object ringBrush = ring.ReadLocalValue(Border.BorderBrushProperty);
            if (selected && ringBrush is not Expression) ring.SetResourceReference(Border.BorderBrushProperty, "Text.Primary");
            else if (!selected && !ReferenceEquals(ringBrush, Brushes.Transparent)) ring.BorderBrush = Brushes.Transparent;
            AutomationProperties.SetItemStatus(button, selected ? UiText.Get("EditorProperty_SelectedColor") : string.Empty);
        }
        if (!_customColorPopup.IsOpen) _customColorInput.Text = ColorCode(current);
    }

    private FrameworkElement BuildThicknessSection()
    {
        var panel = PropertyGroup("AnnotationThicknessProperties");
        panel.Children.Add(PropertyLabel(UiText.Get("EditorProperty_StrokeWidthLabel")));
        // The existing slider owns range, snapping and change notification. Its number box
        // and step buttons are the visible control; the right panel's duplicate track is gone.
        _thicknessSlider = new Slider
        {
            Minimum = SettingsRanges.StrokeThickness.Min, Maximum = SettingsRanges.StrokeThickness.Max,
            Value = _controller.StrokeThickness, SmallChange = 1, LargeChange = 4,
            TickFrequency = 1, IsSnapToTickEnabled = true,
        };
        AutomationName(_thicknessSlider, UiText.Get("Text_70EC2D232B4F"));
        InputMethod.SetIsInputMethodEnabled(_thicknessSlider, false);
        SliderValueEntry entry = ValueEntry(_thicknessSlider, UiText.Get("Editor_UnitPx"), UiText.Get("Text_70EC2D232B4F"));
        AutomationProperties.SetAutomationId(entry.Box, "AnnotationThicknessValue");
        var stepper = new NumberStepperField(_thicknessSlider, entry,
            UiText.Get("EditorProperty_ThicknessDecrease"), UiText.Get("EditorProperty_ThicknessIncrease"));
        AutomationProperties.SetAutomationId(stepper.Element, "AnnotationThicknessField");
        AutomationProperties.SetAutomationId(stepper.DecreaseButton, "AnnotationThicknessDecrease");
        AutomationProperties.SetAutomationId(stepper.IncreaseButton, "AnnotationThicknessIncrease");
        panel.Children.Add(stepper.Element);
        _thicknessSlider.ValueChanged += (_, args) =>
        {
            if (_syncingInspector) return;
            _controller.ApplyStrokeThickness(args.NewValue);
            RememberPreferences();
            if (_controller.Selected is not null) SetStatus(UiText.Format("Text_DBAF1D995258", args.NewValue));
        };
        return panel;
    }

    private FrameworkElement BuildMosaicSection()
    {
        var panel = PropertyGroup("AnnotationMosaicProperties");
        AddObscureStyleChoice(panel, EditorTool.Mosaic);
        panel.Children.Add(PropertyLabel(UiText.Get("EditorProperty_MosaicStrength")));
        _mosaicSlider = new Slider
        {
            Minimum = SettingsRanges.MosaicBlockSize.Min, Maximum = SettingsRanges.MosaicBlockSize.Max,
            Value = Math.Clamp(_mosaicBlockSize, SettingsRanges.MosaicBlockSize.Min, SettingsRanges.MosaicBlockSize.Max),
            SmallChange = 1, LargeChange = 4, TickFrequency = 1, IsSnapToTickEnabled = true,
            Width = 64, MinHeight = 32, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationName(_mosaicSlider, UiText.Get("EditorProperty_MosaicStrength"));
        panel.Children.Add(ValueEntry(_mosaicSlider, UiText.Get("Editor_UnitPx"), UiText.Get("EditorProperty_MosaicStrength")).Element);
        panel.Children.Add(_mosaicSlider);
        _mosaicSlider.ValueChanged += (_, args) =>
        {
            if (_syncingInspector) return;
            _mosaicBlockSize = (int)Math.Round(args.NewValue);
            RememberPreferences();
        };
        return panel;
    }

    private void AddObscureStyleChoice(Panel panel, EditorTool style)
    {
        panel.Children.Add(PropertyLabel(UiText.Get("EditorObscure_Style")));
        var choice = new ComboBox
        {
            MinWidth = 100, MinHeight = 32, Margin = new Thickness(0, 0, 16, 0),
            ItemsSource = new[] { UiText.Get("EditorObscure_Pixelate"), UiText.Get("EditorBlur_Name") },
            SelectedIndex = style == EditorTool.Mosaic ? 0 : 1,
        };
        AutomationName(choice, UiText.Get("EditorObscure_Style"));
        AutomationProperties.SetAutomationId(choice, "AnnotationObscureStyle" + style);
        choice.DropDownClosed += (_, _) =>
        {
            // Cancellation and reselecting the current item do not raise SelectionChanged.
            // WPF restores focus to the ComboBox after this event. Finish that input
            // before returning shortcuts, without taking focus from another control.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (!choice.IsDropDownOpen && choice.IsKeyboardFocusWithin) Focus();
            }));
        };
        choice.SelectionChanged += (_, _) =>
        {
            EditorTool next = choice.SelectedIndex == 0 ? EditorTool.Mosaic : EditorTool.Blur;
            if (next == style) return;
            bool returnFocus = choice.IsKeyboardFocusWithin;
            SelectTool(next);
            // Each cached section represents one style; switching does not change its model.
            choice.SelectedIndex = style == EditorTool.Mosaic ? 0 : 1;
            if (returnFocus) Focus();
        };
        panel.Children.Add(choice);
    }

    internal Popup CustomColorPopupForTest => _customColorPopup;
    internal Button CustomColorButtonForTest => _customColorButton;
}
