using MyCapture.App.Themes;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Core.Annotations;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private TextBox? _stepNextInput;
    private TextBox? _stepNumberInput;
    private TextBox? _stepSizeInput;
    private FrameworkElement? _stepSelectedProperties;
    private Button? _stepRenumberButton;
    private Guid? _stepInputSelection;
    private bool _syncingStepProperties;

    private FrameworkElement AppendStepNumberProperties(StackPanel panel)
    {
        _stepNextInput = StepInput("AnnotationStepNext", UiText.Get("EditorUx_StepNumberNext"),
            value => int.TryParse(value, out int number) && _controller.TrySetNextStepNumber(number),
            () => _controller.NextStepNumber.ToString(CultureInfo.CurrentCulture),
            () => UiText.Get("EditorStep_NumberRange"));
        _stepNextInput.Margin = new Thickness(6, 0, 12, 0);
        panel.Children.Add(_stepNextInput);

        var selected = new StackPanel { Orientation = Orientation.Horizontal };
        selected.Children.Add(PropertyLabel(UiText.Get("EditorStep_Number")));
        _stepNumberInput = StepInput("AnnotationStepNumber", UiText.Get("EditorStep_Number"),
            value => int.TryParse(value, out int number) && _controller.ApplySelectedStepNumber(number),
            () => (_controller.Selected as TextAnnotation)?.Text ?? string.Empty,
            () => UiText.Get("EditorStep_NumberRange"));
        _stepNumberInput.Margin = new Thickness(0, 0, 12, 0);
        selected.Children.Add(_stepNumberInput);
        panel.Children.Add(selected);
        _stepSelectedProperties = selected;

        panel.Children.Add(PropertyLabel(UiText.Get("EditorStep_Size")));
        _stepSizeInput = StepInput("AnnotationStepSize", UiText.Get("EditorStep_Size"),
            value => double.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out double size)
                && double.IsFinite(size) && size >= 24 && size <= _controller.MaximumStepSize
                && _controller.ApplyStepSize(size),
            () => (_controller.Selected is TextAnnotation { IsStepNumber: true } step ? step.Rect.Width : _controller.StepSize)
                .ToString("0.##", CultureInfo.CurrentCulture),
            () => UiText.Format("EditorStep_SizeRange", _controller.MaximumStepSize));
        panel.Children.Add(_stepSizeInput);
        var unit = PropertyLabel("px");
        unit.Margin = new Thickness(4, 0, 12, 0);
        panel.Children.Add(unit);

        _stepRenumberButton = new Button
        {
            Content = UiText.Get("EditorStep_Renumber"), MinHeight = 32, Padding = new Thickness(8, 2, 8, 2), FontSize = TypeScale.Caption,
        };
        _stepRenumberButton.SetResourceReference(StyleProperty, "Button.Compact");
        AutomationName(_stepRenumberButton, UiText.Get("EditorStep_Renumber"));
        AutomationProperties.SetAutomationId(_stepRenumberButton, "AnnotationStepRenumber");
        _stepRenumberButton.Click += (_, _) =>
        {
            if (_controller.RenumberStepNumbers()) RefreshStepFeedback();
        };
        panel.Children.Add(_stepRenumberButton);
        return panel;
    }

    private TextBox StepInput(string id, string name, Func<string, bool> apply, Func<string> current, Func<string> range)
    {
        var input = new TextBox
        {
            Width = 52, MinHeight = 32, FontSize = TypeScale.Caption,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right, Padding = new Thickness(4, 2, 4, 2),
        };
        AutomationName(input, name);
        AutomationProperties.SetAutomationId(input, id);
        bool Commit()
        {
            if (_syncingStepProperties) return true;
            if (input.Text == current())
            {
                input.ToolTip = null;
                AutomationProperties.SetHelpText(input, string.Empty);
                return true;
            }
            if (!apply(input.Text))
            {
                input.ToolTip = range();
                AutomationProperties.SetHelpText(input, range());
                SetStatus(range());
                return false;
            }
            input.ToolTip = null;
            AutomationProperties.SetHelpText(input, string.Empty);
            RefreshStepFeedback();
            return true;
        }
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (Commit()) { input.Text = current(); Focus(); }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                input.Text = current();
                input.ToolTip = null;
                AutomationProperties.SetHelpText(input, string.Empty);
                Focus();
            }
        };
        input.LostKeyboardFocus += (_, _) => Commit();
        return input;
    }

    private void RefreshStepProperties()
    {
        if (_stepNextInput is null) return;
        var selected = _controller.SelectedItems.Count == 1
            ? _controller.Selected as TextAnnotation : null;
        if (selected is { IsStepNumber: false }) selected = null;
        bool selectionChanged = _stepInputSelection != selected?.Id;
        _stepInputSelection = selected?.Id;
        _syncingStepProperties = true;
        try
        {
            if (!_stepNextInput.IsKeyboardFocused) _stepNextInput.Text = _controller.NextStepNumber.ToString(CultureInfo.CurrentCulture);
            _stepSelectedProperties!.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
            if (selectionChanged || !_stepNumberInput!.IsKeyboardFocused) _stepNumberInput!.Text = selected?.Text ?? string.Empty;
            if (selectionChanged || !_stepSizeInput!.IsKeyboardFocused)
                _stepSizeInput!.Text = (selected?.Rect.Width ?? _controller.StepSize).ToString("0.##", CultureInfo.CurrentCulture);
            _stepSizeInput!.IsEnabled = _controller.CanFitStepNumber;
            _stepSizeInput.ToolTip = _controller.CanFitStepNumber ? UiText.Format("EditorStep_SizeRange", _controller.MaximumStepSize) : UiText.Get("EditorStep_DoesNotFit");
            _stepRenumberButton!.IsEnabled = _controller.CanRenumberStepNumbers;
        }
        finally { _syncingStepProperties = false; }
    }

    private static double MeasureStepNumberFont(TextAnnotation badge)
    {
        // Use the same formatter as preview and export, in image pixels independent of monitor DPI.
        var probe = (TextAnnotation)badge.Clone();
        double low = 10, high = Math.Max(low, Math.Min(badge.Rect.Width, badge.Rect.Height) / 2);
        for (int i = 0; i < 16; i++)
        {
            double size = (low + high) / 2;
            probe.FontSize = size;
            var formatted = TextAnnotationLayout.Format(probe, 1);
            Rect ink = formatted.BuildGeometry(default).Bounds;
            double outline = probe.HasOutline ? TextAnnotationLayout.OutlineMargin(probe) : 0;
            if (ink.Width + outline <= badge.Rect.Width - 4 && ink.Height + outline <= badge.Rect.Height - 4)
                low = size;
            else high = size;
        }
        return low;
    }
}
