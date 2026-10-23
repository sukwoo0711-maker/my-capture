using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCapture.App.Editing;

/// <summary>
/// The value of a slider as a small number box with its unit, next to the slider. It always
/// shows the slider's current value and takes an exact one: Enter or leaving the box applies
/// the typed value, clamped to the slider's range, and Esc puts the current value back.
/// </summary>
/// <remarks>
/// Text that is not a number is never applied; the box returns to the current value instead,
/// like a NumberBox that overwrites invalid input. The slider stays the single owner of the
/// value, so the keyboard, UI Automation and every existing ValueChanged consumer keep working.
/// </remarks>
internal sealed class SliderValueEntry
{
    internal const double BoxWidth = 56;

    private readonly Slider _slider;
    private readonly string _unit;
    private bool _applying;

    internal SliderValueEntry(Slider slider, string unit, string automationName)
    {
        _slider = slider ?? throw new ArgumentNullException(nameof(slider));
        _unit = unit;
        Box = new TextBox
        {
            Width = BoxWidth,
            MinHeight = 32,
            Padding = new Thickness(6, 4, 6, 4),
            TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Numbers only: a Hangul-mode IME must not hold a composition in this box.
        InputMethod.SetIsInputMethodEnabled(Box, false);
        AutomationProperties.SetName(Box, automationName);
        Element = NumberStepperField.CreateValueField(Box, unit);

        Box.PreviewTextInput += (_, e) => e.Handled = !IsNumberText(e.Text);
        Box.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
                Finished?.Invoke(this, EventArgs.Empty);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Revert();
                Finished?.Invoke(this, EventArgs.Empty);
            }
            else if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            {
                e.Handled = true;
                if (e.Key == Key.Home) Apply(_slider.Minimum);
                else if (e.Key == Key.End) Apply(_slider.Maximum);
                else Step(e.Key switch { Key.Up => 1, Key.Down => -1, Key.PageUp => 5, _ => -5 });
            }
        };
        Element.PreviewMouseWheel += (_, e) =>
        {
            if (!Box.IsKeyboardFocusWithin || e.Delta == 0) return;
            e.Handled = true;
            Step(Math.Sign(e.Delta));
        };
        Box.LostKeyboardFocus += (_, _) => Commit();
        // The range can move (a thick dashed stroke starts at 6), so the hint is read when used.
        Box.GotKeyboardFocus += (_, _) => DescribeRange();
        Box.ToolTipOpening += (_, _) => DescribeRange();
        _slider.ValueChanged += (_, _) =>
        {
            if (!_applying) Show();
        };
        Show();
        DescribeRange();
    }

    /// <summary>The number box. Its automation name is the slider's name.</summary>
    internal TextBox Box { get; }

    /// <summary>The box and its unit, laid out for the slider's header row.</summary>
    internal FrameworkElement Element { get; }

    /// <summary>Raised after Enter or Esc, so the owner can hand the keyboard back.</summary>
    internal event EventHandler? Finished;

    /// <summary>Raised when a typed value was outside the range and the nearest limit was applied.</summary>
    internal event EventHandler<double>? Clamped;

    /// <summary>Shows a value the way the box does: whole numbers plainly, otherwise one decimal.</summary>
    internal static string Format(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>
    /// Reads a typed value and limits it to the range. False for empty text, text that is not a
    /// number, and infinities; <paramref name="clamped"/> tells whether a limit was applied.
    /// A positive <paramref name="step"/> rounds to the slider's ticks, so a typed value lands
    /// where the slider itself could stop.
    /// </summary>
    internal static bool TryParse(string? text, double minimum, double maximum, double step, out double value, out bool clamped)
    {
        value = 0;
        clamped = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            return false;
        }

        if (!double.IsFinite(parsed)) return false;
        // The box shows one decimal, so it never applies a value it cannot show.
        double rounded = step > 0
            ? minimum + (Math.Round((parsed - minimum) / step, MidpointRounding.AwayFromZero) * step)
            : Math.Round(parsed, 1, MidpointRounding.AwayFromZero);
        value = Math.Clamp(rounded, minimum, maximum);
        clamped = parsed < minimum || parsed > maximum;
        return true;
    }

    /// <summary>Applies the typed value. Returns false, and shows the current value, when it is not a number.</summary>
    internal bool Commit()
    {
        double step = _slider.IsSnapToTickEnabled ? _slider.TickFrequency : 0;
        if (!TryParse(Box.Text, _slider.Minimum, _slider.Maximum, step, out double value, out bool clamped))
        {
            Show();
            return false;
        }

        Apply(value);
        if (clamped) Clamped?.Invoke(this, value);
        return true;
    }

    /// <summary>Applies a pending number and its step as one slider change and one undo item.</summary>
    internal void Step(int steps)
    {
        double tick = _slider.IsSnapToTickEnabled ? _slider.TickFrequency : 0;
        double value = TryParse(Box.Text, _slider.Minimum, _slider.Maximum, tick, out double typed, out _)
            ? typed : _slider.Value;
        double step = _slider.SmallChange > 0 ? _slider.SmallChange : tick > 0 ? tick : 1;
        double requested = value + (steps * step);
        if (TryParse(Format(requested), _slider.Minimum, _slider.Maximum, tick, out double snapped, out _)) Apply(snapped);
    }

    private void Apply(double value)
    {
        _applying = true;
        try
        {
            if (Math.Abs(_slider.Value - value) > 0.0001) _slider.Value = value;
        }
        finally
        {
            _applying = false;
        }

        Show();
    }

    /// <summary>Discards the typed text and shows the slider's value again.</summary>
    internal void Revert() => Show();

    private void Show()
    {
        string text = Format(_slider.Value);
        if (!string.Equals(Box.Text, text, StringComparison.Ordinal)) Box.Text = text;
    }

    private void DescribeRange()
    {
        string help = UiText.Format("SliderValue_RangeHelp", Format(_slider.Minimum), Format(_slider.Maximum), _unit);
        Box.ToolTip = help;
        AutomationProperties.SetHelpText(Box, help);
    }

    private static bool IsNumberText(string text)
    {
        foreach (char character in text)
        {
            if (!char.IsAsciiDigit(character) && character is not '.' and not ',' and not '-') return false;
        }

        return true;
    }
}
