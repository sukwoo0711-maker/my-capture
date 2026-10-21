using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MyCapture.App.Editing;

/// <summary>One image-pixel renderer for the editor preview and every exported result.</summary>
/// <remarks>
/// The editor draws the badge on every paint with the same title and image size, so each thread
/// keeps the last badge it laid out (frozen) and issues the same drawing commands from it until
/// the title, the size or the culture changes.
/// </remarks>
internal static class SourceWindowTitleRenderer
{
    private static readonly Brush BadgeBrush = CreateBadgeBrush();

    [ThreadStatic]
    private static Badge? t_last;

    internal static void Draw(DrawingContext dc, string? title, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(title) || width < 24 || height < 12) return;

        CultureInfo culture = CultureInfo.CurrentUICulture;
        Badge? badge = t_last;
        if (badge is null || !badge.IsFor(title, width, height, culture))
        {
            badge = Layout(title, width, height, culture);
            t_last = badge;
        }

        if (badge.Glyphs is null) return;
        dc.PushClip(badge.Clip!);
        dc.DrawRoundedRectangle(BadgeBrush, null, badge.Bounds, badge.CornerRadius, badge.CornerRadius);
        dc.DrawDrawing(badge.Glyphs);
        dc.Pop();
    }

    private static Badge Layout(string title, double width, double height, CultureInfo culture)
    {
        // Window titles can contain line breaks or tabs. Keep the badge to one line,
        // with an ellipsis rather than wrapping over the captured document.
        string text = string.Join(" ", title.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return new Badge(title, width, height, culture, default, 0, null, null);

        double margin = Math.Min(8, Math.Min(width, height) / 12);
        double padding = Math.Min(5, height / 12);
        double fontSize = Math.Min(13, (height - 2 * margin - 2 * padding) / 1.5);
        double availableWidth = Math.Min(680, width - 2 * margin - 2 * padding);
        var formatted = new FormattedText(text, culture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize, Brushes.White, 1)
        {
            MaxTextWidth = availableWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        double badgeWidth = Math.Min(availableWidth, formatted.WidthIncludingTrailingWhitespace) + 2 * padding;
        var bounds = new Rect(width - margin - badgeWidth, margin,
            badgeWidth, Math.Min(height - 2 * margin, formatted.Height + 2 * padding));
        var clip = new RectangleGeometry(bounds);
        clip.Freeze();
        var glyphs = new DrawingGroup();
        using (DrawingContext context = glyphs.Open())
        {
            context.DrawText(formatted, new Point(bounds.Left + padding, bounds.Top + padding));
        }

        if (glyphs.CanFreeze) glyphs.Freeze();
        return new Badge(title, width, height, culture, bounds, Math.Min(4, padding), clip, glyphs);
    }

    private static Brush CreateBadgeBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(215, 24, 24, 27));
        brush.Freeze();
        return brush;
    }

    /// <summary>A laid-out badge and the inputs it was laid out for; no glyphs when the title is blank.</summary>
    private sealed record Badge(
        string Title,
        double Width,
        double Height,
        CultureInfo Culture,
        Rect Bounds,
        double CornerRadius,
        RectangleGeometry? Clip,
        DrawingGroup? Glyphs)
    {
        internal bool IsFor(string title, double width, double height, CultureInfo culture) =>
            ReferenceEquals(Culture, culture)
            && Width.Equals(width)
            && Height.Equals(height)
            && string.Equals(Title, title, StringComparison.Ordinal);
    }
}
