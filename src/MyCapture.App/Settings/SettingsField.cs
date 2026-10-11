using System.Windows;
using System.Windows.Controls;

namespace MyCapture.App.Settings;

/// <summary>
/// One short value in the settings window, laid out as a single row: label, input, then its
/// range or unit. A longer explanation goes on the line below.
/// </summary>
/// <remarks>
/// <see cref="HeaderedContentControl.Header"/> is the label and the content is the input, which
/// keeps its own accessible name and validation. The look lives in SettingsWindow.xaml; every
/// field on a page shares the label column (<c>Grid.IsSharedSizeScope</c> on the page), so the
/// inputs line up. The row itself never takes focus.
/// </remarks>
public sealed class SettingsField : HeaderedContentControl
{
    /// <summary>The range or unit shown right after the input, such as "1 ~ 64.".</summary>
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(SettingsField), new PropertyMetadata(null));

    /// <summary>
    /// A longer explanation shown under the row: text, or an element (a named
    /// <see cref="TextBlock"/> that code-behind keeps current).
    /// </summary>
    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(object), typeof(SettingsField), new PropertyMetadata(null));

    static SettingsField()
    {
        FocusableProperty.OverrideMetadata(typeof(SettingsField), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingsField), new FrameworkPropertyMetadata(false));
    }

    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public object? Note
    {
        get => GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }
}
