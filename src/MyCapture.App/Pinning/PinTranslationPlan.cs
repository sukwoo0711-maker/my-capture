using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyCapture.App.Pinning;

/// <summary>Which language pair a translation uses; the target is the application display language.</summary>
internal enum PinTranslationDirection { EnglishToKorean, KoreanToEnglish }

/// <summary>The structure of the text a pin shows; each structure translates only its human text.</summary>
internal enum PinTextFormat { Prose, Json, Code, Table, Markdown, Mermaid }

/// <summary>What a plan piece is, which decides how a translation is validated and put back.</summary>
internal enum PlanKind
{
    /// <summary>Syntax, code, keys, numbers, separators and untranslatable text. Never translated.</summary>
    Literal,
    /// <summary>One paragraph or line of plain text.</summary>
    Prose,
    /// <summary>A Markdown line after its block prefix (heading marker, list marker, quote marker, task box).</summary>
    MarkdownLine,
    /// <summary>The decoded content of a JSON string value. Assembled with JSON string escaping.</summary>
    JsonString,
    /// <summary>The text of one code comment line, without its comment markers.</summary>
    Comment,
    /// <summary>One table cell without its separators.</summary>
    TableCell,
    /// <summary>A diagram label. Assembly quotes it when the translation would break the diagram syntax.</summary>
    MermaidLabel,
}

/// <summary>
/// A span of the source text. <see cref="Raw"/> is the exact source characters the piece covers; joining
/// every piece's Raw reproduces the source byte for byte. <see cref="Content"/> is the text to translate
/// when it differs from the raw characters (decoded JSON escapes).
/// </summary>
internal sealed record PlanPiece(string Raw, PlanKind Kind = PlanKind.Literal, bool Translate = false, string? Content = null)
{
    internal string Source => Content ?? Raw;

    /// <summary>The characters that replace <see cref="Raw"/> when <paramref name="translated"/> is used.</summary>
    internal string Emit(string translated)
    {
        string text = translated.ReplaceLineEndings(" ").Trim();
        return Kind switch
        {
            PlanKind.JsonString => EscapeJson(text),
            PlanKind.MermaidLabel => EscapeMermaid(text, Raw),
            _ => text,
        };
    }

    private static readonly JsonSerializerOptions JsonEscape = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string EscapeJson(string text)
    {
        string quoted = JsonSerializer.Serialize(text, JsonEscape);
        return quoted[1..^1];
    }

    private static string EscapeMermaid(string text, string raw)
    {
        // A label that was written without quotes cannot carry syntax characters. Mermaid accepts them
        // inside double quotes, with the quote itself written as the #quot; entity.
        bool risky = text.IndexOfAny(['(', ')', '[', ']', '{', '}', '|', '"', '<', '>', ';', '`']) >= 0
            || text.StartsWith('-') || text.StartsWith('=');
        return risky ? "\"" + text.Replace("\"", "#quot;") + "\"" : text;
    }
}

/// <summary>The ordered pieces of one source text for one language direction.</summary>
internal sealed record PinTranslationPlan(PinTextFormat Format, IReadOnlyList<PlanPiece> Pieces)
{
    internal bool HasTranslatable => Pieces.Any(static piece => piece.Translate);

    /// <summary>The texts to translate, in piece order.</summary>
    internal IReadOnlyList<string> Sources => [.. Pieces.Where(static piece => piece.Translate).Select(static piece => piece.Source)];

    /// <summary>The source text rebuilt from the pieces; equals the original source exactly.</summary>
    internal string Original => string.Concat(Pieces.Select(static piece => piece.Raw));

    /// <summary>
    /// Rebuilds the text. <paramref name="translations"/> has one entry per translatable piece in order;
    /// null keeps that piece's original characters.
    /// </summary>
    internal string Assemble(IReadOnlyList<string?> translations)
    {
        var text = new StringBuilder(Original.Length + 64);
        int index = 0;
        foreach (PlanPiece piece in Pieces)
        {
            if (!piece.Translate)
            {
                text.Append(piece.Raw);
                continue;
            }
            string? translated = index < translations.Count ? translations[index] : null;
            index++;
            text.Append(string.IsNullOrWhiteSpace(translated) ? piece.Raw : piece.Emit(translated));
        }
        return text.ToString();
    }
}

internal readonly record struct ScriptCounts(int Latin, int Hangul, int Kana, int Han, int Other)
{
    internal int Letters => Latin + Hangul + Kana + Han + Other;
}

/// <summary>Script statistics shared by the language decisions. Pure; no resources.</summary>
internal static class PinScript
{
    internal static ScriptCounts Count(string text)
    {
        int latin = 0, hangul = 0, kana = 0, han = 0, other = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (!Rune.IsLetter(rune)) continue;
            if (value < 0x250 && char.IsAsciiLetter((char)value)) latin++;
            else if (value is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xD7B0 and <= 0xD7FF) hangul++;
            else if (value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F) kana++;
            else if (value is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2A6DF or >= 0xF900 and <= 0xFAFF) han++;
            else other++;
        }
        return new ScriptCounts(latin, hangul, kana, han, other);
    }

    /// <summary>
    /// True when <paramref name="text"/> is written mostly in the direction's source language and has
    /// real words to translate (not only identifiers, acronyms, numbers or symbols).
    /// </summary>
    internal static bool IsTranslatable(string text, PinTranslationDirection direction)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        ScriptCounts counts = Count(text);
        return direction == PinTranslationDirection.EnglishToKorean
            ? counts.Latin > counts.Hangul + counts.Kana + counts.Han + counts.Other && HasEnglishWord(text)
            : counts.Hangul > counts.Latin + counts.Kana + counts.Han + counts.Other && counts.Hangul >= 2;
    }

    /// <summary>A word of at least three letters that is not an acronym, identifier or code symbol.</summary>
    internal static bool HasEnglishWord(string text)
    {
        int start = -1;
        for (int index = 0; index <= text.Length; index++)
        {
            bool letter = index < text.Length && char.IsAsciiLetter(text[index]);
            if (letter)
            {
                if (start < 0) start = index;
                continue;
            }
            if (start >= 0)
            {
                int length = index - start;
                bool joined = (start > 0 && text[start - 1] is '_' or '.' or '/' or '\\' or '@' or ':')
                    || (index < text.Length && text[index] is '_' or '@' or '/' or '\\' || char.IsAsciiDigit(index < text.Length ? text[index] : ' '));
                string word = text.Substring(start, length);
                bool acronym = word.All(char.IsAsciiLetterUpper) && length <= 5;
                bool camel = word.Skip(1).Any(char.IsAsciiLetterUpper) && word.Any(char.IsAsciiLetterLower);
                if (length >= 3 && !joined && !acronym && !camel) return true;
                start = -1;
            }
        }
        return false;
    }
}
