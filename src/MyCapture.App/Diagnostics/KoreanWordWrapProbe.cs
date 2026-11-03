using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Diagnostics;

/// <summary>Reads the actual WPF line layout; it never changes display text or window state.</summary>
internal static class KoreanWordWrapProbe
{
    internal sealed record Orphan(TextBlock Block, string Text, int LineCount, string LastLine);

    internal static IReadOnlyList<Orphan> FindOrphans(DependencyObject root)
    {
        var found = new List<Orphan>();
        Visit(root);
        return found;

        void Visit(DependencyObject element)
        {
            if (element is TextBlock block && Inspect(block) is { } orphan) found.Add(orphan);
            if (element is not Visual && element is not System.Windows.Media.Media3D.Visual3D) return;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                Visit(VisualTreeHelper.GetChild(element, index));
        }
    }

    internal static Orphan? Inspect(TextBlock block)
    {
        if (block.TextWrapping == TextWrapping.NoWrap || block.Visibility != Visibility.Visible
            || block.ActualWidth <= 0 || block.ActualHeight <= 0) return null;

        TextPointer start = block.ContentStart;
        int length = start.GetOffsetToPosition(block.ContentEnd);
        var lines = new List<(double Top, StringBuilder Text)>();
        for (int index = 0; index < length; index++)
        {
            TextPointer? cursor = start.GetPositionAtOffset(index, LogicalDirection.Forward);
            if (cursor is null || cursor.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            string run = cursor.GetTextInRun(LogicalDirection.Forward);
            if (run.Length == 0) continue;
            char character = run[0];
            if (character == KoreanWordWrap.WordJoiner || char.IsWhiteSpace(character)) continue;
            Rect rectangle = cursor.GetCharacterRect(LogicalDirection.Forward);
            if (rectangle.IsEmpty || !double.IsFinite(rectangle.Top)) continue;
            // Inline font sizes and baseline shifts can give two glyphs on the same line
            // different tops. Normalize to that laid-out line's first character rectangle.
            double top = cursor.GetLineStartPosition(0)?.GetCharacterRect(LogicalDirection.Forward).Top ?? rectangle.Top;
            int line = lines.FindIndex(item => Math.Abs(item.Top - top) < 0.5);
            if (line < 0)
            {
                line = lines.Count;
                lines.Add((top, new StringBuilder()));
            }
            lines[line].Text.Append(character);
        }

        if (lines.Count < 2) return null;
        string last = lines.OrderBy(line => line.Top).Last().Text.ToString();
        char[] letters = last.Where(character => !char.IsPunctuation(character)).ToArray();
        return letters.Length == 1 && letters[0] is >= '가' and <= '힣'
            ? new Orphan(block, KoreanWordWrap.Strip(TextOf(block)), lines.Count, last)
            : null;
    }

    /// <summary>
    /// The text a block draws, in order, with a line break as "\n". A <see cref="TextRange"/> over
    /// the block leaves out the text of a hyperlink, so the inlines are read directly.
    /// </summary>
    internal static string TextOf(TextBlock block)
    {
        var text = new StringBuilder();
        Append(block.Inlines);
        return text.ToString();

        void Append(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Run run: text.Append(run.Text); break;
                    case Span span: Append(span.Inlines); break;
                    case LineBreak: text.Append('\n'); break;
                }
            }
        }
    }
}
