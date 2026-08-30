using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Annotations;

namespace MyCapture.App.Editing;

/// <summary>
/// Flattens an annotation layer onto its capture at 1:1 physical pixels.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of this type is fidelity: the exported PNG must be pixel-identical
/// to what the editor drew, at the capture's real resolution. It therefore reuses the
/// very same <see cref="AnnotationRenderer"/> the on-screen surface uses, drawing into
/// a <see cref="RenderTargetBitmap"/> sized to the selection's physical pixels with a
/// 1:1 mapping — no DPI scale, no crop offset. A separate "export renderer" would be a
/// second code path that could drift from what the user saw.
/// </para>
/// <para>
/// The <see cref="RenderTargetBitmap"/> is created at 96 DPI so one DIP equals one
/// device pixel; the renderer already works in image-pixel coordinates, so no transform
/// is pushed at all. <c>pixelsPerDip</c> is passed as 1.0 for the same reason: text is
/// laid out in the image's own pixels.
/// </para>
/// </remarks>
internal static class AnnotationFlattener
{
    /// <summary>
    /// Composites <paramref name="document"/> over <paramref name="baseBitmap"/> using the
    /// supplied renderer, returning a frozen bitmap at the base image's physical resolution.
    /// </summary>
    /// <param name="baseBitmap">The original (unannotated) selection, in physical pixels.</param>
    /// <param name="document">The annotation layer, in the base image's pixel space.</param>
    /// <param name="renderer">
    /// The same renderer the surface uses, backed by a store that can resolve every used
    /// image asset from an in-memory bitmap.
    /// </param>
    internal static BitmapSource Flatten(
        BitmapSource baseBitmap,
        AnnotationDocument document,
        AnnotationRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(baseBitmap);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderer);

        int width = Math.Max(1, baseBitmap.PixelWidth);
        int height = Math.Max(1, baseBitmap.PixelHeight);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // Draw the base at 1:1. The image-pixel coordinate space of the annotations is
            // exactly this rectangle, so annotations need no transform.
            dc.DrawImage(baseBitmap, new Rect(0, 0, width, height));

            // pixelsPerDip = 1.0: at 96 DPI a DIP is a device pixel, and text is measured in
            // the image's own pixels, matching what the editor showed.
            renderer.Render(dc, document, pixelsPerDip: 1.0);
        }

        // Pbgra32 preserves any transparency the annotations introduce (for example a
        // highlighter over a transparent region); the PNG encoder keeps that channel.
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
