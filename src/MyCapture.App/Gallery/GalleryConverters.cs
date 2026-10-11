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
/// Maps the kept (manually pinned) state to the library menu label ("보관 해제" when kept, "보관" when
/// not). "핀" names a capture floated on top of other windows (the card's pin button), and the
/// library never calls keeping "고정", the word Windows uses for taskbar and Start pins.
/// </summary>
public sealed class PinLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? UiText.Get("LibraryUx_Unkeep") : UiText.Get("LibraryUx_Keep");

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

/// <summary>
/// True when the bound row is a <see cref="GalleryFolderRow"/>, used by the outer list's
/// container style to swap in the folder card template.
/// </summary>
/// <remarks>
/// Declared in <c>GalleryWindow.xaml</c> rather than <c>App.xaml</c>: tests that host the
/// gallery with their own application resources keep working without registering it.
/// </remarks>
public sealed class IsFolderRowConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GalleryFolderRow;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound value is not <see langword="null"/>, for example a folder scope's id.</summary>
/// <remarks>Declared in <c>GalleryWindow.xaml</c>, like <see cref="IsFolderRowConverter"/>.</remarks>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
