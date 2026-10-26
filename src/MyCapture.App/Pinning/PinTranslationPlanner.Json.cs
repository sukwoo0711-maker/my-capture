using System.Globalization;
using System.Text;

namespace MyCapture.App.Pinning;

// JSON: a hand-written scanner over the original text, so every character outside a translated string value is kept.
internal static partial class PinTranslationPlanner
{
    /// <summary>A string value: <see cref="Start"/>..<see cref="End"/> are the characters between the quotes, still escaped.</summary>
    private readonly record struct JsonValue(int Start, int End, string Decoded);

    private static PinTranslationPlan CreateJson(string source, PinTranslationDirection direction)
    {
        var builder = new PlanBuilder(source);
        foreach (JsonValue value in ScanJsonValues(source) ?? [])
        {
            if (!IsTranslatableJsonValue(value.Decoded, direction)) continue;
            string raw = source[value.Start..value.End];
            builder.Piece(value.Start, value.End, PlanKind.JsonString, string.Equals(raw, value.Decoded, StringComparison.Ordinal) ? null : value.Decoded);
        }
        return new PinTranslationPlan(PinTextFormat.Json, builder.Finish());
    }

    /// <summary>Object keys are never candidates; array elements and object values that are strings are. Null when the text is malformed.</summary>
    private static List<JsonValue>? ScanJsonValues(string text)
    {
        var values = new List<JsonValue>();
        // 'a' array, 'k' object expecting a key, 'v' object expecting a value.
        var stack = new List<char>();
        for (int index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '{': stack.Add('k'); break;
                case '[': stack.Add('a'); break;
                case '}' or ']':
                    if (stack.Count == 0) return null;
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case ',':
                    if (stack.Count > 0 && stack[^1] == 'v') stack[^1] = 'k';
                    break;
                case ':':
                    if (stack.Count > 0 && stack[^1] == 'k') stack[^1] = 'v';
                    break;
                case '"':
                    int end = index + 1;
                    while (end < text.Length && text[end] != '"') end += text[end] == '\\' ? 2 : 1;
                    if (end >= text.Length) return null;
                    bool isKey = stack.Count > 0 && stack[^1] == 'k';
                    if (!isKey && stack.Count > 0)
                    {
                        string? decoded = DecodeJson(text, index + 1, end);
                        if (decoded is null) return null;
                        values.Add(new JsonValue(index + 1, end, decoded));
                    }
                    index = end;
                    break;
            }
        }
        return values;
    }

    private static string? DecodeJson(string text, int start, int end)
    {
        var decoded = new StringBuilder(end - start);
        for (int index = start; index < end; index++)
        {
            char c = text[index];
            if (c != '\\')
            {
                decoded.Append(c);
                continue;
            }
            if (++index >= end) return null;
            switch (text[index])
            {
                case '"': decoded.Append('"'); break;
                case '\\': decoded.Append('\\'); break;
                case '/': decoded.Append('/'); break;
                case 'b': decoded.Append('\b'); break;
                case 'f': decoded.Append('\f'); break;
                case 'n': decoded.Append('\n'); break;
                case 'r': decoded.Append('\r'); break;
                case 't': decoded.Append('\t'); break;
                case 'u':
                    if (index + 4 >= end
                        || !ushort.TryParse(text.AsSpan(index + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort unit))
                        return null;
                    decoded.Append((char)unit);
                    index += 4;
                    break;
                default: return null;
            }
        }
        return decoded.ToString();
    }

    /// <summary>
    /// A string value is translated when it is source-language prose. Values that are URLs, e-mails, paths,
    /// identifiers, versions, dates, numbers, code or only a placeholder are data and stay. A single lowercase
    /// word ("active", "admin") is treated as an enum-like identifier; a capitalised word ("Save") is a label.
    /// </summary>
    private static bool IsTranslatableJsonValue(string value, PinTranslationDirection direction)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || (trimmed[0] is '{' or '[' && trimmed[^1] is '}' or ']')) return false;
        if (LooksLikeData(trimmed) || !IsTranslatableText(trimmed, direction)) return false;
        bool singleToken = !trimmed.Any(char.IsWhiteSpace);
        return !(singleToken && direction == PinTranslationDirection.EnglishToKorean && !char.IsUpper(trimmed[0]));
    }
}
