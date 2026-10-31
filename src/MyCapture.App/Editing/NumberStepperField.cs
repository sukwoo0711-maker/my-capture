using MyCapture.App.Themes;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.Core.Themes;

namespace MyCapture.App.Editing;

/// <summary>A single numeric field whose buttons share its outline, focus and value model.</summary>
internal sealed class NumberStepperField
{
    private static readonly ConditionalWeakTable<Dispatcher, ControlTemplate> ValueTemplates = new();
    private static readonly LimitConverter DecreaseLimit = new(false);
    private static readonly LimitConverter IncreaseLimit = new(true);

    internal NumberStepperField(Slider slider, SliderValueEntry entry, string decreaseName, string increaseName)
    {
        Element = (Border)entry.Element;
        var content = (StackPanel)Element.Child;
        DecreaseButton = StepButton("−", decreaseName);
        IncreaseButton = StepButton("+", increaseName);
        content.Children.Insert(0, DecreaseButton);
        content.Children.Insert(1, Divider());
        content.Children.Add(Divider());
        content.Children.Add(IncreaseButton);
        DecreaseButton.Click += (_, _) => entry.Step(-1);
        IncreaseButton.Click += (_, _) => entry.Step(1);

        BindLimit(DecreaseButton, slider, entry.Box, increase: false);
        BindLimit(IncreaseButton, slider, entry.Box, increase: true);
    }

    internal Border Element { get; }
    internal Button DecreaseButton { get; }
    internal Button IncreaseButton { get; }

    /// <summary>Crop validation keeps its error boundary until a corrected value is accepted.</summary>
    internal static void SetValueFieldError(Border field, bool invalid)
    {
        if (field is not NumericFieldBorder numeric) throw new ArgumentException("Expected a numeric value field.", nameof(field));
        numeric.SetError(invalid);
    }

    /// <summary>Shared value/unit chrome, also used by crop fields with their own commit rules.</summary>
    internal static Border CreateValueField(TextBox box, string unit)
    {
        box.Height = 32;
        box.MinHeight = 32;
        box.Padding = new Thickness(6, 4, 4, 4);
        box.TextAlignment = TextAlignment.Right;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.BorderThickness = new Thickness(0);
        box.Background = Brushes.Transparent;
        box.FocusVisualStyle = null;
        box.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        // The shared outer frame owns all chrome; the native TextBox retains its Value peer,
        // selection, caret and editing behavior through the standard content-host part.
        box.Template = ValueTemplates.GetValue(box.Dispatcher, static _ =>
        {
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var template = new ControlTemplate(typeof(TextBox)) { VisualTree = host };
            template.Seal();
            return template;
        });
        var unitText = new TextBlock
        {
            Text = unit, FontSize = TypeScale.Caption, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        unitText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            // Keep the controls' full 32-DIP hit area inside the 32-DIP outline.
            Margin = new Thickness(0, -1, 0, -1),
        };
        content.Children.Add(box);
        content.Children.Add(unitText);
        var frame = new NumericFieldBorder
        {
            Height = 32, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center, Child = content, SnapsToDevicePixels = true,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");
        frame.SetResourceReference(Border.CornerRadiusProperty, "Radius.Sm");
        return frame;
    }

    private static Button StepButton(string glyph, string name)
    {
        var button = new Button
        {
            Content = glyph, Width = 32, Height = 32, MinWidth = 32, MinHeight = 32,
            Padding = new Thickness(0), FontSize = TypeScale.Caption, ToolTip = name,
            Focusable = false, IsTabStop = false,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.GhostCompact");
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static Border Divider()
    {
        var divider = new Border { Width = 1, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        divider.SetResourceReference(Border.BackgroundProperty, "Border.Subtle");
        return divider;
    }

    private static void BindLimit(Button button, Slider slider, TextBox box, bool increase)
    {
        var binding = new MultiBinding { Converter = increase ? IncreaseLimit : DecreaseLimit };
        binding.Bindings.Add(new Binding(nameof(Slider.IsEnabled)) { Source = slider });
        binding.Bindings.Add(new Binding(nameof(Slider.Value)) { Source = slider });
        binding.Bindings.Add(new Binding(nameof(TextBox.Text)) { Source = box });
        binding.Bindings.Add(new Binding(nameof(Slider.Minimum)) { Source = slider });
        binding.Bindings.Add(new Binding(nameof(Slider.Maximum)) { Source = slider });
        binding.Bindings.Add(new Binding(nameof(Slider.IsSnapToTickEnabled)) { Source = slider });
        binding.Bindings.Add(new Binding(nameof(Slider.TickFrequency)) { Source = slider });
        button.SetBinding(UIElement.IsEnabledProperty, binding);
    }

    private sealed class LimitConverter(bool increase) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values is not [true, double value, string text, double minimum, double maximum, bool snap, double tick])
                return false;
            if (SliderValueEntry.TryParse(text, minimum, maximum, snap ? tick : 0, out double pending, out _))
                value = pending;
            return increase ? value < maximum : value > minimum;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The numeric well is darker than a raised panel in light themes. Keep the normal control
    /// boundary where it meets 3:1 on both sides; otherwise use the palette's stronger boundary.
    /// Every source is a dynamic semantic resource, so this decision follows live theme changes.
    /// </summary>
    private sealed class NumericFieldBorder : Border
    {
        private static readonly ThemeColor[] ContrastBackdrops = [ThemeColor.Rgb(0, 0, 0), ThemeColor.Rgb(255, 255, 255)];
        private static readonly DependencyProperty ControlBrushProperty = BrushProperty("ControlBrush");
        private static readonly DependencyProperty StrongBrushProperty = BrushProperty("StrongBrush");
        private static readonly DependencyProperty RaisedBrushProperty = BrushProperty("RaisedBrush");
        private static readonly DependencyProperty BaseBrushProperty = BrushProperty("BaseBrush");
        private bool _invalid;

        internal NumericFieldBorder()
        {
            SetResourceReference(ControlBrushProperty, "Border.Control");
            SetResourceReference(StrongBrushProperty, "Border.Strong");
            SetResourceReference(RaisedBrushProperty, "Surface.Raised");
            SetResourceReference(BaseBrushProperty, "Surface.Base");
            IsKeyboardFocusWithinChanged += (_, _) => RefreshBoundary();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == BackgroundProperty) RefreshBoundary();
        }

        internal void SetError(bool invalid)
        {
            _invalid = invalid;
            RefreshBoundary();
        }

        private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
            name, typeof(Brush), typeof(NumericFieldBorder), new PropertyMetadata(null,
                static (owner, _) => ((NumericFieldBorder)owner).RefreshBoundary()));

        private void RefreshBoundary()
        {
            if (_invalid)
            {
                SetResourceReference(BorderBrushProperty, "State.Danger");
                return;
            }
            if (IsKeyboardFocusWithin)
            {
                SetResourceReference(BorderBrushProperty, "Border.Focus");
                return;
            }
            if (GetValue(ControlBrushProperty) is not Brush control) return;
            BorderBrush = HasContrast(control) || GetValue(StrongBrushProperty) is not Brush strong
                ? control : strong;
        }

        private bool HasContrast(Brush boundary)
        {
            if (boundary is not SolidColorBrush control || Background is not SolidColorBrush sunken
                || GetValue(RaisedBrushProperty) is not SolidColorBrush raised
                || GetValue(BaseBrushProperty) is not SolidColorBrush surfaceBase) return true;
            // Translucent themes must work over either extreme of the desktop backdrop.
            foreach (ThemeColor backdrop in ContrastBackdrops)
            {
                ThemeColor outer = ThemeContrast.Composite(ColorOf(raised), ThemeContrast.Composite(ColorOf(surfaceBase), backdrop));
                ThemeColor inner = ThemeContrast.Composite(ColorOf(sunken), outer);
                if (ThemeContrast.Ratio(ThemeContrast.Composite(ColorOf(control), outer), outer) < 3
                    || ThemeContrast.Ratio(ThemeContrast.Composite(ColorOf(control), inner), inner) < 3) return false;
            }
            return true;
        }

        private static ThemeColor ColorOf(SolidColorBrush brush) => new(
            (byte)Math.Round(brush.Color.A * brush.Opacity), brush.Color.R, brush.Color.G, brush.Color.B);
    }
}
