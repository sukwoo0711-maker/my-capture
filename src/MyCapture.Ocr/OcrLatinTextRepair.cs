namespace MyCapture.Ocr;

/// <summary>Repairs narrowly identifiable Latin OCR confusions in text offered for translation.</summary>
internal static class OcrLatinTextRepair
{
    internal static string Repair(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        char[]? repaired = null;
        for (int start = 0; start < text.Length;)
        {
            if (!IsTokenCharacter(text[start])) { start++; continue; }
            int end = start + 1;
            while (end < text.Length && IsTokenCharacter(text[end])) end++;
            // A sentence's final full stop is punctuation; an internal dot belongs to an identifier.
            int wordEnd = end;
            while (wordEnd > start && text[wordEnd - 1] == '.') wordEnd--;
            bool identifier = false;
            for (int index = start; index < wordEnd; index++)
                identifier |= text[index] is '_' or '.' or '/' or '@' or '\\';
            if (!identifier)
            {
                // Keep an entire e-mail/path atom together across '-' or '+', while punctuation
                // in ordinary prose still separates independently repairable Latin words.
                for (int wordStart = start; wordStart < wordEnd;)
                {
                    if (!IsLetterOrDigit(text[wordStart])) { wordStart++; continue; }
                    int limit = wordStart + 1;
                    while (limit < wordEnd && IsLetterOrDigit(text[limit])) limit++;
                    for (int index = wordStart + 1; index < limit; index++)
                    {
                        char candidate = text[index];
                        if (candidate is not ('O' or '0' or 'I')) continue;
                        if (candidate == 'I' && (index + 1 == limit
                            || !IsLower(text[index - 1]) || !IsLower(text[index + 1]))) continue;
                        bool allowed = true, hasLower = false;
                        for (int other = wordStart; other < limit; other++)
                        {
                            if (other == index) continue;
                            char value = text[other];
                            hasLower |= IsLower(value);
                            if (!IsLower(value) && !(other == wordStart && value is >= 'A' and <= 'Z'))
                            { allowed = false; break; }
                        }
                        if (!allowed || !hasLower) continue;
                        repaired ??= text.ToCharArray();
                        repaired[index] = candidate == 'I' ? 'l' : 'o';
                    }
                    wordStart = limit;
                }
            }
            start = end;
        }
        return repaired is null ? text : new string(repaired);
    }

    private static bool IsLower(char value) => value is >= 'a' and <= 'z';
    private static bool IsLetterOrDigit(char value) => value is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z' or >= '0' and <= '9';
    private static bool IsTokenCharacter(char value) => IsLetterOrDigit(value)
        || value is '_' or '.' or '/' or '@' or '\\' or '-' or '+' or '%' or ':';
}
