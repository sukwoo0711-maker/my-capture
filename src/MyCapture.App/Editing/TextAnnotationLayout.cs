using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Editing;

/// <summary>
/// The one text layout used both to draw a <see cref="TextAnnotation"/> and to size its box.
/// </summary>
/// <remarks>
/// <para>
/// Text is never trimmed and never cut at the bottom: <see cref="FormattedText"/> would otherwise
/// replace whatever does not fit the box with an ellipsis (its default trimming), which is how a
/// caption enlarged with the size slider turned into "ㅅ…". Instead the box follows the text.
/// </para>
/// <para>
/// An auto-size caption keeps each paragraph on one line. A fixed-width caption wraps at its
/// box width, never narrower than one em. <see cref="FormattedText"/> wraps with
/// <see cref="TextWrapping.WrapWithOverflow"/> but, with trimming off, still breaks a word longer
/// than the line, so no line is wider than the box; the live editing box therefore uses
/// <see cref="TextWrapping.Wrap"/>, which breaks at the same places.
/// </para>
/// <para>
/// The box is measured at one pixel per DIP, the flattener's scale, so its size is a property of
/// the document and not of the monitor the editor runs on.
/// </para>
/// </remarks>
internal static class TextAnnotationLayout
{
    internal static FormattedText Format(TextAnnotation text, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(text);
        var typeface = new Typeface(
            new FontFamily(text.FontFamily),
            text.Italic ? FontStyles.Italic : FontStyles.Normal,
            text.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var formatted = new FormattedText(
            text.Text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(1, text.FontSize),
            text.Foreground.ToBrush(),
            pixelsPerDip)
        {
            Trimming = TextTrimming.None,
        };

        if (WrapWidth(text) is double wrap)
        {
            formatted.MaxTextWidth = wrap;
        }

        return formatted;
    }

    /// <summary>
    /// Where lines wrap, or <see langword="null"/> when each paragraph stays on one line
    /// (auto-size captions and centred step badges).
    /// </summary>
    internal static double? WrapWidth(TextAnnotation text) =>
        text.AutoSize || text.IsStepNumber ? null : Math.Max(text.Rect.Width, Math.Max(1, text.FontSize));

    /// <summary>
    /// Room for the glyph outline: the outline pen's full width, half on each side.
    /// </summary>
    internal static double OutlineMargin(TextAnnotation text) => Math.Max(1, text.FontSize / 12);

    /// <summary>
    /// The box <paramref name="text"/> needs for its current text, size and wrapping mode,
    /// keeping its top-left corner.
    /// </summary>
    /// <remarks>
    /// Auto size: as wide as the longest line plus the outline margin. Fixed width: exactly the
    /// wrapping width, so measuring a measured box again changes nothing. Height: every line plus
    /// the outline margin. A step badge keeps the box it was given; its number is centred in it.
    /// </remarks>
    internal static RectD Measure(TextAnnotation text)
    {
        ArgumentNullException.ThrowIfNull(text);
        RectD rect = text.Rect;
        if (text.IsStepNumber)
        {
            return rect;
        }

        FormattedText formatted = Format(text, 1.0);
        double margin = OutlineMargin(text);
        // An empty caption still has one line: the caret's.
        double lines = formatted.Height > 0 ? formatted.Height : LineHeight(text);
        double width = WrapWidth(text) is double wrap
            ? Math.Max(wrap, formatted.Width)
            : formatted.WidthIncludingTrailingWhitespace + margin;
        return new RectD(rect.X, rect.Y, width, lines + margin);
    }

    /// <summary>
    /// Grows the box of a caption saved by an older version whose text does not fit it (that
    /// text used to be cut with an ellipsis), so selection and hit-testing cover the whole
    /// caption. A caption whose text fits keeps its exact box, and so its exact pixels.
    /// </summary>
    /// <returns>Whether the box changed.</returns>
    internal static bool GrowToText(TextAnnotation text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IsStepNumber || text.AutoSize)
        {
            return false;
        }

        RectD rect = text.Rect;
        FormattedText formatted = Format(text, 1.0);
        if (formatted.Width <= rect.Width + 0.01 && formatted.Height <= rect.Height + 0.01)
        {
            return false;
        }

        RectD measured = Measure(text);
        text.Rect = new RectD(rect.X, rect.Y, Math.Max(rect.Width, measured.Width), Math.Max(rect.Height, measured.Height));
        return true;
    }

    /// <summary>
    /// The background plate: the stored box, grown to the laid-out text when a layer saved by an
    /// older version holds more text than its box (that text used to be cut with an ellipsis).
    /// </summary>
    internal static Rect PlateBounds(Rect box, FormattedText formatted) => new(
        box.Left,
        box.Top,
        Math.Max(box.Width, formatted.WidthIncludingTrailingWhitespace),
        Math.Max(box.Height, formatted.Height));

    private static double LineHeight(TextAnnotation text)
    {
        var probe = (TextAnnotation)text.Clone();
        probe.Text = " ";
        return Format(probe, 1.0).Height;
    }
}
