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
/// <para>
/// A layout is a pure function of its <see cref="FormatKey"/>. Measurements are cached by that
/// key, so measuring the same caption again (a live box laid out twice per keystroke, the commit
/// that stores the last live fit, opening a layer) reuses the numbers the first layout produced
/// instead of formatting the whole text again.
/// </para>
/// </remarks>
internal static class TextAnnotationLayout
{
    private const int MetricsCacheLimit = 512;
    private static readonly Lock MetricsGate = new();
    private static readonly Dictionary<FormatKey, TextMetrics> MetricsCache = [];

    internal static FormattedText Format(TextAnnotation text, double pixelsPerDip) => Format(KeyOf(text, pixelsPerDip));

    /// <summary>Every value <see cref="Format(FormatKey)"/> reads from a caption and its surface.</summary>
    internal static FormatKey KeyOf(TextAnnotation text, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new FormatKey(
            text.Text ?? string.Empty,
            text.FontFamily,
            text.Italic,
            text.Bold,
            Math.Max(1, text.FontSize),
            text.Foreground,
            WrapWidth(text),
            pixelsPerDip,
            CultureInfo.CurrentUICulture);
    }

    /// <summary>Lays out <paramref name="key"/>'s text; equal keys give equal layouts.</summary>
    internal static FormattedText Format(in FormatKey key)
    {
        var typeface = new Typeface(
            FontCatalog.Resolve(key.FontFamily),
            key.Italic ? FontStyles.Italic : FontStyles.Normal,
            key.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var formatted = new FormattedText(
            key.Text,
            key.Culture,
            FlowDirection.LeftToRight,
            typeface,
            key.EmSize,
            key.Foreground.ToBrush(),
            key.PixelsPerDip)
        {
            Trimming = TextTrimming.None,
        };

        if (key.WrapWidth is double wrap)
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

        FormatKey key = KeyOf(text, 1.0);
        TextMetrics formatted = MetricsOf(key);
        double margin = OutlineMargin(text);
        // An empty caption still has one line: the caret's.
        double lines = formatted.Height > 0 ? formatted.Height : formatted.LineHeight;
        double width = key.WrapWidth is double wrap
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
        // The same key as Measure's, so the fitting layout below reuses this one.
        TextMetrics formatted = MetricsOf(KeyOf(text, 1.0));
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

    /// <summary>
    /// The extents of <paramref name="key"/>'s layout, formatted once per distinct key.
    /// </summary>
    private static TextMetrics MetricsOf(in FormatKey key)
    {
        lock (MetricsGate)
        {
            if (MetricsCache.TryGetValue(key, out TextMetrics cached))
            {
                return cached;
            }
        }

        FormattedText formatted = Format(key);
        double height = formatted.Height;
        var metrics = new TextMetrics(
            formatted.Width,
            formatted.WidthIncludingTrailingWhitespace,
            height,
            // One line of the same caption, for an empty one; the same layout with a single space.
            height > 0 ? height : Format(key with { Text = " " }).Height);

        lock (MetricsGate)
        {
            if (MetricsCache.Count >= MetricsCacheLimit)
            {
                MetricsCache.Clear();
            }

            MetricsCache[key] = metrics;
        }

        return metrics;
    }

    /// <summary>
    /// Every input of a caption's layout: its text and font, the wrapping width, the culture
    /// and the surface's pixels per DIP. Strings compare ordinally and the culture by identity,
    /// so an equal key always describes the same layout.
    /// </summary>
    internal readonly record struct FormatKey(
        string Text,
        string FontFamily,
        bool Italic,
        bool Bold,
        double EmSize,
        ColorRgba Foreground,
        double? WrapWidth,
        double PixelsPerDip,
        CultureInfo Culture)
    {
        public bool Equals(FormatKey other) =>
            ReferenceEquals(Culture, other.Culture)
            && Italic == other.Italic
            && Bold == other.Bold
            && EmSize.Equals(other.EmSize)
            && Foreground == other.Foreground
            && Nullable.Equals(WrapWidth, other.WrapWidth)
            && PixelsPerDip.Equals(other.PixelsPerDip)
            && string.Equals(FontFamily, other.FontFamily, StringComparison.Ordinal)
            && string.Equals(Text, other.Text, StringComparison.Ordinal);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Text, StringComparer.Ordinal);
            hash.Add(FontFamily, StringComparer.Ordinal);
            hash.Add(Italic);
            hash.Add(Bold);
            hash.Add(EmSize);
            hash.Add(Foreground);
            hash.Add(WrapWidth);
            hash.Add(PixelsPerDip);
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Culture));
            return hash.ToHashCode();
        }
    }

    /// <summary>The extents <see cref="Measure"/> and <see cref="GrowToText"/> read from a layout.</summary>
    private readonly record struct TextMetrics(double Width, double WidthIncludingTrailingWhitespace, double Height, double LineHeight);
}
