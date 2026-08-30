using System.Windows.Media;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Editing;

/// <summary>
/// Bridges the domain's UI-agnostic primitives to WPF media types.
/// </summary>
/// <remarks>
/// Kept in one place so the domain layer never takes a WPF dependency (see
/// <see cref="PointD"/>'s rationale) yet the editor never open-codes the channel
/// order, which is the usual source of red/blue swaps.
/// </remarks>
internal static class WpfConversions
{
    internal static Color ToColor(this ColorRgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    internal static ColorRgba ToColorRgba(this Color c) => new(c.A, c.R, c.G, c.B);

    /// <summary>
    /// A frozen brush for the colour, or <see langword="null"/> when fully transparent.
    /// </summary>
    /// <remarks>
    /// Returning null for a zero-alpha fill lets callers pass it straight to
    /// <c>DrawRectangle</c>: WPF treats a null brush as "no fill", which is cheaper and
    /// clearer than drawing a transparent one.
    /// </remarks>
    internal static SolidColorBrush? ToBrushOrNull(this ColorRgba c)
    {
        if (c.A == 0)
        {
            return null;
        }

        var brush = new SolidColorBrush(c.ToColor());
        brush.Freeze();
        return brush;
    }

    internal static SolidColorBrush ToBrush(this ColorRgba c)
    {
        var brush = new SolidColorBrush(c.ToColor());
        brush.Freeze();
        return brush;
    }
}
