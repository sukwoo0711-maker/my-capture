using MyCapture.App.Themes;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using MyCapture.Core.Pin;

namespace MyCapture.App.Pinning;

/// <summary>Renders a bounded subset of editor HTML as local WPF text, never a web page.</summary>
internal static class ClipboardCodeRenderer
{
    internal const string FontFamilyName = "Consolas";
    internal const double FontSizeDip = TypeScale.Content.CodeText;
    internal const double LineHeightDip = 23;
    internal const int HorizontalPaddingDip = 24;
    internal const int VerticalPaddingDip = 20;

    internal static bool IsCodeHtml(string? html) => html is not null
        && !html.Contains("<table", StringComparison.OrdinalIgnoreCase)
        && (html.Contains("<pre", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<code", StringComparison.OrdinalIgnoreCase)
            || html.Contains("white-space: pre", StringComparison.OrdinalIgnoreCase)
            || html.Contains("white-space:pre", StringComparison.OrdinalIgnoreCase));

    internal static BitmapSource? TryRender(string originalText, string? html) =>
        TryRender(originalText, html, 1);

    internal static BitmapSource? TryRender(string originalText, string? html, double scale)
    {
        ClipboardTextRenderer.ValidateScale(scale);
        try
        {
            PinCodeDocument? document = TryParse(originalText, html);
            return document is null ? null : Render(document, scale);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Detaches bounded text and colours from the clipboard HTML exactly once.</summary>
    internal static PinCodeDocument? TryParse(string originalText, string? html)
    {
        if (!IsCodeHtml(html) || html!.Length > 65_536 || originalText.Length > 16_384) return null;
        try
        {
            int start = html.IndexOf("<!--StartFragment-->", StringComparison.Ordinal);
            int end = html.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
            string fragment = start >= 0 && end > start
                ? html[(start + "<!--StartFragment-->".Length)..end] : html;
            fragment = fragment.Replace("&nbsp;", "&#160;", StringComparison.Ordinal);
            fragment = Regex.Replace(fragment, "<br\\s*/?>", "<br/>", RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100));
            using var reader = XmlReader.Create(new StringReader("<root>" + fragment + "</root>"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 70_000 });
            XElement root = XElement.Load(reader);
            var text = new StringBuilder();
            var colors = new List<PinCodeRun>();
            const string foreground = "#DCDCDC";
            string background = "#1E1E1E";
            double? fontSize = null, lineHeight = null;
            string? fontFamily = null;
            bool metricsRead = false;
            int visitedNodes = 0;
            void Visit(XNode node, string inherited, int depth = 0)
            {
                if (depth > 64 || ++visitedNodes > 4096)
                {
                    throw new FormatException("Clipboard markup exceeds the preview complexity limit.");
                }
                if (node is XText value)
                {
                    string decoded = value.Value.Replace('\u00a0', ' ').Replace("\t", "    ", StringComparison.Ordinal);
                    colors.Add(new PinCodeRun(text.Length, decoded.Length, inherited));
                    text.Append(decoded);
                    return;
                }
                if (node is not XElement element) return;
                string tag = element.Name.LocalName.ToLowerInvariant();
                if (tag is not ("root" or "div" or "span" or "pre" or "code" or "br")) return;
                if (tag == "br") { text.Append('\n'); return; }
                string style = (string?)element.Attribute("style") ?? "";
                string color = ReadColor(style, "color") ?? inherited;
                if (tag is "div" or "pre" or "code") background = ReadColor(style, "background-color") ?? background;
                // The editor's block names its font once, on the outermost element that sets one.
                if (!metricsRead && tag is ("div" or "pre" or "code") && ReadDeclaration(style, "font-size") is not null)
                {
                    metricsRead = true;
                    (fontSize, lineHeight, fontFamily) = ReadMetrics(style);
                }
                foreach (XNode child in element.Nodes()) Visit(child, color, depth + 1);
                if (tag == "div" && text.Length > 0 && text[^1] != '\n') text.Append('\n');
            }
            Visit(root, foreground);
            string preview = text.ToString().TrimEnd('\r', '\n');
            // Markup must describe the Unicode payload. Hidden/script text cannot replace it.
            static string Compact(string value) => string.Concat(value.Where(static c => !char.IsWhiteSpace(c)));
            if (preview.Length == 0 || Compact(preview) != Compact(originalText)) return null;
            var runs = new List<PinCodeRun>(colors.Count);
            foreach (var run in colors)
            {
                int length = Math.Min(run.Length, preview.Length - run.Start);
                if (length > 0) runs.Add(run with { Length = length });
            }
            return new PinCodeDocument(preview, Array.AsReadOnly(runs.ToArray()), background, foreground)
            {
                FontSizeDip = fontSize,
                LineHeightDip = lineHeight,
                FontFamily = fontFamily,
            };
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or InvalidOperationException or FormatException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    internal static BitmapSource Render(PinCodeDocument document, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(document);
        ClipboardTextRenderer.ValidateScale(scale);
        if (!document.IsValid())
        {
            throw new ArgumentException("The code document is invalid.", nameof(document));
        }
        // Ideal DIP metrics preserve the original wrapping and trimming at every target DPI.
        // The drawing retains vector glyphs until RenderTargetBitmap rasterises them.
        var formatted = new FormattedText(document.DisplayText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamilyFor(document), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), FontSizeFor(document), ColorBrush(document.Foreground), 1)
        { MaxTextWidth = 1152, MaxTextHeight = 852, LineHeight = LineHeightFor(document), Trimming = TextTrimming.CharacterEllipsis };
        foreach (PinCodeRun run in document.Runs)
        {
            formatted.SetForegroundBrush(ColorBrush(run.Color), run.Start, run.Length);
        }
        int width = Math.Clamp((int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + (2 * HorizontalPaddingDip), 240, 1200);
        int height = Math.Clamp((int)Math.Ceiling(formatted.Height) + (2 * VerticalPaddingDip), 80, 900);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(ColorBrush(document.Background), null, ClipboardTextRenderer.PixelBounds(width, height, scale));
            drawing.DrawText(formatted, new Point(HorizontalPaddingDip, VerticalPaddingDip));
        }
        return ClipboardTextRenderer.RenderFrozen(visual, width, height, scale);
    }

    private static Brush ColorBrush(string value)
    {
        uint rgb = uint.Parse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    /// <summary>The font size the pin draws <paramref name="document"/> with: its source's, or the default.</summary>
    internal static double FontSizeFor(PinCodeDocument? document) => document?.FontSizeDip ?? FontSizeDip;

    /// <summary>The line height the pin draws <paramref name="document"/> with: its source's, or the default.</summary>
    internal static double LineHeightFor(PinCodeDocument? document) => document?.LineHeightDip ?? LineHeightDip;

    /// <summary>
    /// The source's installed families first, then the default code font, as one WPF fallback list.
    /// </summary>
    internal static FontFamily FontFamilyFor(PinCodeDocument? document) =>
        document?.FontFamily is { } families && PinCodeDocument.IsFontFamilyList(families)
            ? new FontFamily(families + ", " + FontFamilyName)
            : new FontFamily(FontFamilyName);

    /// <summary>
    /// Reads an editor's font size, line height and family from one CSS declaration list. CSS
    /// px are DIP and pt are 96/72 DIP. A size outside <see cref="PinCodeDocument.MinFontSizeDip"/>
    /// to <see cref="PinCodeDocument.MaxFontSizeDip"/>, or in any other unit, keeps every default.
    /// A missing or unusable line height follows the size at a typical editor spacing.
    /// </summary>
    internal static (double? FontSize, double? LineHeight, string? FontFamily) ReadMetrics(string style)
    {
        double? size = ReadLength(ReadDeclaration(style, "font-size"));
        if (size is not { } fontSize || fontSize < PinCodeDocument.MinFontSizeDip || fontSize > PinCodeDocument.MaxFontSizeDip)
            return (null, null, null);
        string? lineValue = ReadDeclaration(style, "line-height");
        double? line = ReadLength(lineValue);
        if (line is null && lineValue is not null
            && double.TryParse(lineValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor)
            && double.IsFinite(factor) && factor > 0)
        {
            line = fontSize * factor;
        }
        if (line is not { } lineHeight || lineHeight < fontSize
            || lineHeight < PinCodeDocument.MinLineHeightDip || lineHeight > PinCodeDocument.MaxLineHeightDip)
        {
            line = Math.Clamp(Math.Round(fontSize * DefaultLineSpacing), PinCodeDocument.MinLineHeightDip, PinCodeDocument.MaxLineHeightDip);
        }
        return (fontSize, line, ReadFontFamilies(ReadDeclaration(style, "font-family")));
    }

    // VS Code's default editor line height for a 14 px font is 19 px.
    private const double DefaultLineSpacing = 1.35;

    private static readonly string[] GenericFamilies =
    [
        "monospace", "serif", "sans-serif", "cursive", "fantasy", "system-ui", "ui-monospace",
        "ui-serif", "ui-sans-serif", "ui-rounded", "emoji", "math", "fangsong", "inherit", "initial",
        "unset", "revert", "-apple-system", "blinkmacsystemfont",
    ];

    /// <summary>Plain family names from a CSS font-family list, without generic or unsafe entries.</summary>
    internal static string? ReadFontFamilies(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var families = new List<string>();
        foreach (string entry in value.Split(','))
        {
            string name = entry.Trim().Trim('"', '\'').Trim();
            if (name.Length is 0 or > 64 || GenericFamilies.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (name.Equals(FontFamilyName, StringComparison.OrdinalIgnoreCase)) break;
            if (!PinCodeDocument.IsFontFamilyList(name) || families.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            families.Add(name);
            if (families.Count == 4) break;
        }
        string joined = string.Join(", ", families);
        return families.Count == 0 || joined.Length > PinCodeDocument.MaxFontFamilyLength - FontFamilyName.Length - 2
            ? null : joined;
    }

    private static double? ReadLength(string? value)
    {
        if (value is null) return null;
        double unit;
        string number;
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)) { unit = 1; number = value[..^2]; }
        else if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase)) { unit = 96.0 / 72.0; number = value[..^2]; }
        else return null;
        return double.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double length)
               && double.IsFinite(length) && length > 0
            ? length * unit : null;
    }

    private static string? ReadDeclaration(string style, string property)
    {
        foreach (string declaration in style.Split(';'))
        {
            string[] pair = declaration.Split(':', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                string value = pair[1].Trim();
                if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase)) value = value[..^"!important".Length].Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    private static string? ReadColor(string style, string property)
    {
        foreach (string declaration in style.Split(';'))
        {
            string[] pair = declaration.Split(':', 2);
            if (pair.Length != 2 || !pair[0].Trim().Equals(property, StringComparison.OrdinalIgnoreCase)) continue;
            string value = pair[1].Trim();
            if (value.Length != 7 || value[0] != '#' || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) continue;
            return $"#{rgb:X6}";
        }
        return null;
    }
}
