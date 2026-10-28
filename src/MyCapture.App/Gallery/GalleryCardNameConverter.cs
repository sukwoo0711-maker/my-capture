using System.Globalization;
using System.Windows.Data;

namespace MyCapture.App.Gallery;

/// <summary>
/// Puts a card's name into a localized phrase: the selection box is announced as "Capture review 선택", not
/// as a bare "선택", so a screen reader says which card it selects.
/// </summary>
public sealed class GalleryCardNameConverter : IValueConverter
{
    /// <summary>The UI string key of the phrase; {0} is the card's name.</summary>
    public string Format { get; set; } = string.Empty;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Format.Length == 0 ? value?.ToString() ?? string.Empty : UiText.Format(Format, value?.ToString() ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
