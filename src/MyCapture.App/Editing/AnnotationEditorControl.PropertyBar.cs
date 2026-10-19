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
    private Popup _customColorPopup = null!;
    private Button _customColorButton = null!;

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
        _customColorButton = PropertyButton("+", UiText.Get("EditorProperty_CustomColor"));
        _customColorButton.FontSize = 18;
        AutomationProperties.SetAutomationId(_customColorButton, "AnnotationCustomColorOpen");
        _customColorButton.ToolTip = UiText.Get("EditorProperty_CustomColorHint");
        AutomationProperties.SetHelpText(_customColorButton, UiText.Get("EditorProperty_CustomColorHint"));
        panel.Children.Add(_customColorButton);

        _customColorInput = new TextBox
        {
            Text = ColorCode(_controller.StrokeColor), MinHeight = 32,
            FontSize = 12, Margin = new Thickness(0, 0, 0, 8),
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
            Content = UiText.Get("Tools.CustomColor"), Height = 32, MinHeight = 32, FontSize = 12,
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
            Padding = new Thickness(0), FontSize = 12, ToolTip = name,
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

    private void UpdateColorSwatches()
    {
        ColorRgba current = CurrentPropertyColor();
        foreach (Button button in _swatchPanel.Children.OfType<Button>())
        {
            bool selected = button.Tag is ColorRgba color && color == current;
            var ring = (Border)button.Content;
            if (selected) ring.SetResourceReference(Border.BorderBrushProperty, "Text.Primary");
            else ring.BorderBrush = Brushes.Transparent;
            AutomationProperties.SetItemStatus(button, selected ? UiText.Get("EditorProperty_SelectedColor") : string.Empty);
        }
        if (!_customColorPopup.IsOpen) _customColorInput.Text = ColorCode(current);
    }

    private FrameworkElement BuildThicknessSection()
    {
        var panel = PropertyGroup("AnnotationThicknessProperties");
        panel.Children.Add(PropertyLabel(UiText.Get("Text_70EC2D232B4F")));
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
        Button decrease = PropertyButton("−", UiText.Get("EditorProperty_ThicknessDecrease"));
        Button increase = PropertyButton("+", UiText.Get("EditorProperty_ThicknessIncrease"));
        AutomationProperties.SetAutomationId(decrease, "AnnotationThicknessDecrease");
        AutomationProperties.SetAutomationId(increase, "AnnotationThicknessIncrease");
        decrease.Click += (_, _) => _thicknessSlider.Value = Math.Max(_thicknessSlider.Minimum, _thicknessSlider.Value - 1);
        increase.Click += (_, _) => _thicknessSlider.Value = Math.Min(_thicknessSlider.Maximum, _thicknessSlider.Value + 1);
        entry.Box.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.Up or Key.Down)) return;
            e.Handled = true;
            // Combine a pending typed value and the step into a single property change.
            // Committing first would put two entries on a multi-selection's undo stack.
            double value = SliderValueEntry.TryParse(entry.Box.Text, _thicknessSlider.Minimum,
                _thicknessSlider.Maximum, _thicknessSlider.TickFrequency, out double typed, out bool wasClamped)
                ? typed : _thicknessSlider.Value;
            _thicknessSlider.Value = Math.Clamp(value + (e.Key == Key.Up ? 1 : -1),
                _thicknessSlider.Minimum, _thicknessSlider.Maximum);
            entry.Revert();
        };
        panel.Children.Add(decrease);
        panel.Children.Add(entry.Element);
        panel.Children.Add(increase);
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
        panel.Children.Add(PropertyLabel(UiText.Get("EditorProperty_MosaicStrength")));
        _mosaicSlider = new Slider
        {
            Minimum = SettingsRanges.MosaicBlockSize.Min, Maximum = SettingsRanges.MosaicBlockSize.Max,
            Value = Math.Clamp(_mosaicBlockSize, SettingsRanges.MosaicBlockSize.Min, SettingsRanges.MosaicBlockSize.Max),
            SmallChange = 1, LargeChange = 4, TickFrequency = 1, IsSnapToTickEnabled = true,
            Width = 96, MinHeight = 32, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
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

    internal Popup CustomColorPopupForTest => _customColorPopup;
    internal Button CustomColorButtonForTest => _customColorButton;
}
