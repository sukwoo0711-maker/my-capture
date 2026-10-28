using System.Windows;

namespace MyCapture.App.Gallery;

/// <summary>
/// Which size of card a list shows. The compact list of an opened folder sets
/// <see cref="IsCompactProperty"/> once; every card below it follows, so full and compact cards share
/// one template instead of two copies.
/// </summary>
public static class GalleryCardStyle
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(GalleryCardStyle),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);

    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);
}
