using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

    /// <summary>
    /// Rounds a card preview's top corners to the inside of the card's border. A Border's CornerRadius shapes
    /// only its own fill, so the square preview, and a 16:10 picture that fills it, drew over the card's
    /// rounded top corners. The clip follows the preview's size and the nearest rounded border above it.
    /// </summary>
    public static readonly DependencyProperty ClipsToCardCornersProperty = DependencyProperty.RegisterAttached(
        "ClipsToCardCorners", typeof(bool), typeof(GalleryCardStyle),
        new PropertyMetadata(false, OnClipsToCardCornersChanged));

    public static bool GetClipsToCardCorners(DependencyObject element) => (bool)element.GetValue(ClipsToCardCornersProperty);

    public static void SetClipsToCardCorners(DependencyObject element, bool value) => element.SetValue(ClipsToCardCornersProperty, value);

    private static void OnClipsToCardCornersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.SizeChanged -= OnClippedPreviewSizeChanged;
        if (e.NewValue is true)
        {
            element.SizeChanged += OnClippedPreviewSizeChanged;
            ClipTopCorners(element);
        }
        else element.ClearValue(UIElement.ClipProperty);
    }

    private static void OnClippedPreviewSizeChanged(object sender, SizeChangedEventArgs e) => ClipTopCorners((FrameworkElement)sender);

    private static void ClipTopCorners(FrameworkElement element)
    {
        Border? card = CardBorder(element);
        double radius = card is null ? 0 : Math.Max(0, card.CornerRadius.TopLeft - card.BorderThickness.Top);
        double width = element.ActualWidth, height = element.ActualHeight;
        if (radius <= 0 || width <= 0 || height <= 0)
        {
            element.ClearValue(UIElement.ClipProperty);
            return;
        }
        // Taller than the preview by the radius, so only its top corners are rounded.
        var clip = new RectangleGeometry(new Rect(0, 0, width, height + radius), radius, radius);
        clip.Freeze();
        element.Clip = clip;
    }

    private static Border? CardBorder(DependencyObject element)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Border { CornerRadius.TopLeft: > 0 } border) return border;
        }
        return null;
    }
}
