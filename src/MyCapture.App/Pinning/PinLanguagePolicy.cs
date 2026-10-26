using System.Text;
using System.Text.RegularExpressions;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal enum PinLanguageReason { Available, AlreadyTarget, Unsupported, NoText }
internal sealed record PinLanguageAssessment(bool CanTranslate, PinLanguageReason Reason);

internal static partial class PinLanguagePolicy
{
    internal static string NormalizeTarget(string targetLanguage) => targetLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en"
        : targetLanguage.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? "ko" : targetLanguage.ToLowerInvariant();

    internal static PinLanguageAssessment Assess(string? source, string targetLanguage)
    {
        string target = NormalizeTarget(targetLanguage);
        if (target is not ("ko" or "en")) return new(false, PinLanguageReason.Unsupported);
        if (string.IsNullOrWhiteSpace(source)) return new(false, PinLanguageReason.NoText);
        string prose = WithoutProtectedText(source);
        if (prose.Any(character => char.IsLetter(character) && !IsKorean(character) && !IsLatin(character)))
            return new(false, PinLanguageReason.Unsupported);
        int korean = prose.Count(IsKorean), latin = prose.Count(IsLatin);
        if (korean == 0 && latin == 0) return new(false, PinLanguageReason.NoText);
        return (target == "ko" ? latin > 0 : korean > 0)
            ? new(true, PinLanguageReason.Available) : new(false, PinLanguageReason.AlreadyTarget);
    }

    internal static string WithoutProtectedText(string source)
    {
        var text = new StringBuilder(source);
        foreach (ProtectedSpan span in ProtectedTokens.Find(source))
            for (int index = span.Start; index < span.Start + span.Length; index++) text[index] = ' ';
        foreach (Match match in AddressesAndPlaceholders().Matches(source))
            for (int index = match.Index; index < match.Index + match.Length; index++) text[index] = ' ';
        return text.ToString();
    }

    internal static bool IsKorean(char character) => character is >= '\uAC00' and <= '\uD7AF'
        or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F';
    internal static bool IsLatin(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
        or >= '\u00C0' and <= '\u024F';

    [GeneratedRegex(@"(?:\b(?:https?|ftp)://|\bwww\.|\bmailto:)[^\s<>]+|(?<![\w])(?:[A-Za-z]:[\\/]|\\\\|(?:\.{1,2})?/)[^\s<>]+|[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}|\{\d+(?:[^{}]*)\}|\b\d{4}[-/.]\d{1,2}[-/.]\d{1,2}\b", RegexOptions.CultureInvariant)]
    internal static partial Regex AddressesAndPlaceholders();
}
