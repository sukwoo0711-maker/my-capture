using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MyCapture.App.Gallery;

/// <summary>
/// Maps a bool to <see cref="Visibility.Visible"/> / <see cref="Visibility.Collapsed"/>.
/// </summary>
/// <remarks>
/// A local converter rather than the framework's <c>BooleanToVisibilityConverter</c> so the
/// gallery's resource keys are self-contained and the collapse (not hide) behaviour is
/// explicit.
/// </remarks>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>
/// Maps the pinned state to the pin button's Korean label ("고정 해제" when pinned, "고정" when not).
/// </summary>
public sealed class PinLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "고정 해제" : "고정";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when the bound row is a <see cref="GalleryHeaderRow"/>, used by the outer list's
/// container style to swap in the header template and make the row non-selectable.
/// </summary>
/// <remarks>
/// A converter rather than a <c>DataType</c>-keyed template because the outer list needs to
/// alter the <see cref="System.Windows.Controls.ListBoxItem"/> container (focusable, hit-test)
/// per row type, which a plain typed <c>DataTemplate</c> cannot reach.
/// </remarks>
public sealed class IsHeaderRowConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GalleryHeaderRow;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
