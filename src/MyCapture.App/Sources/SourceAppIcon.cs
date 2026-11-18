using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyCapture.Core.Capture;

namespace MyCapture.App.Sources;

/// <summary>
/// The source symbol: the kind's glyph, replaced in place by the source application's icon once the
/// icon cache has it. The glyph stays in the layout (hidden, not collapsed) and the icon is no larger
/// than the glyph's em box, so the swap never moves anything. Creating it reads only memory.
/// </summary>
public static class SourceAppIcon
{
    internal const string GlyphName = "SourceGlyph";
    internal const string ImageName = "SourceAppIcon";

    /// <summary>A plain attached hook for XAML: an element whose content shows a source's symbol.</summary>
    public static readonly DependencyProperty ProvenanceProperty = DependencyProperty.RegisterAttached(
        "Provenance", typeof(CaptureProvenance), typeof(SourceAppIcon),
        new PropertyMetadata(CaptureProvenance.None, OnProvenanceChanged));

    public static CaptureProvenance GetProvenance(DependencyObject target) => (CaptureProvenance)target.GetValue(ProvenanceProperty);

    public static void SetProvenance(DependencyObject target, CaptureProvenance value) => target.SetValue(ProvenanceProperty, value);

    private static void OnProvenanceChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Decorator host) return;
        var source = (CaptureProvenance?)e.NewValue ?? CaptureProvenance.None;
        host.Child = source.HasSource ? Create(source, TextElement.GetFontSize(host)) : null;
    }

    internal static Grid Create(CaptureProvenance source, double size, SourceIconCache? cache = null)
    {
        var glyph = new TextBlock
        {
            Name = GlyphName, Text = SourceChip.GlyphFor(source.Kind),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var image = new Image
        {
            Name = ImageName, Width = size, Height = size, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var symbol = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        symbol.Children.Add(glyph);
        symbol.Children.Add(image);
        if (!source.HasAppIdentity) return symbol;
        cache ??= SourceIconCache.Shared;
        if (cache.TryGet(source, out ImageSource? icon))
        {
            if (icon is not null) Show(glyph, image, icon);
        }
        else
        {
            cache.Request(source, ready => Show(glyph, image, ready));
        }
        return symbol;
    }

    private static void Show(TextBlock glyph, Image image, ImageSource icon)
    {
        image.Source = icon;
        image.Visibility = Visibility.Visible;
        glyph.Visibility = Visibility.Hidden;
    }
}
