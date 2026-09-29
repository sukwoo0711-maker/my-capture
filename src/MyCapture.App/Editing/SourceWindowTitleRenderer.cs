using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MyCapture.App.Editing;

/// <summary>One image-pixel renderer for the editor preview and every exported result.</summary>
internal static class SourceWindowTitleRenderer
{
    internal static void Draw(DrawingContext dc, string? title, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(title) || width < 24 || height < 12) return;

        // Window titles can contain line breaks or tabs. Keep the badge to one line,
        // with an ellipsis rather than wrapping over the captured document.
        string text = string.Join(" ", title.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return;

        double margin = Math.Min(8, Math.Min(width, height) / 12);
        double padding = Math.Min(5, height / 12);
        double fontSize = Math.Min(13, (height - 2 * margin - 2 * padding) / 1.5);
        double availableWidth = Math.Min(680, width - 2 * margin - 2 * padding);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize, Brushes.White, 1)
        {
            MaxTextWidth = availableWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        double badgeWidth = Math.Min(availableWidth, formatted.WidthIncludingTrailingWhitespace) + 2 * padding;
        var bounds = new Rect(width - margin - badgeWidth, margin,
            badgeWidth, Math.Min(height - 2 * margin, formatted.Height + 2 * padding));
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(215, 24, 24, 27)), null,
            bounds, Math.Min(4, padding), Math.Min(4, padding));
        dc.DrawText(formatted, new Point(bounds.Left + padding, bounds.Top + padding));
        dc.Pop();
    }
}
