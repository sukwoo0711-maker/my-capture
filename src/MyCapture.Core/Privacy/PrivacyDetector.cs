using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Privacy;

/// <summary>
/// Conservative, dependency-free detector for OCR text. It joins at most eight adjacent words
/// on the same line so OCR-split addresses and numbers are found without ever crossing lines.
/// </summary>
public sealed class PrivacyDetector : IPrivacyDetector
{
    private const int MaximumTokens = 4096;
    private const int MaximumTokenLength = 256;
    private const int MaximumSpanTokens = 8;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly Regex Email = new(
        @"^[A-Z0-9.!#$%&'*+/=?^_`{|}~-]{1,64}@[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?(?:\.[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?){1,8}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex Secret = new(
        @"^(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|(?:AKIA|ASIA)[A-Z0-9]{16}|Bearer[A-Za-z0-9._~+/=-]{16,})$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    public IReadOnlyList<PrivacyMatch> Detect(IReadOnlyList<PrivacyToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0)
        {
            return [];
        }

        List<PrivacyToken> bounded = tokens
            .Take(MaximumTokens)
            .Where(static token => token is not null
                                   && token.LineIndex >= 0
                                   && token.TokenIndex >= 0
                                   && !string.IsNullOrWhiteSpace(token.Text)
                                   && token.Text.Length <= MaximumTokenLength
                                   && !token.Bounds.Normalized().IsEmpty)
            .OrderBy(static token => token.LineIndex)
            .ThenBy(static token => token.TokenIndex)
            .ToList();

        var candidates = new List<PrivacyMatch>();
        foreach (IGrouping<int, PrivacyToken> line in bounded.GroupBy(static token => token.LineIndex))
        {
            List<PrivacyToken> words = line.ToList();
            for (int start = 0; start < words.Count; start++)
            {
                var joined = new StringBuilder();
                RectD bounds = RectD.Empty;
                int previousIndex = words[start].TokenIndex - 1;

                for (int end = start; end < words.Count && end < start + MaximumSpanTokens; end++)
                {
                    PrivacyToken word = words[end];
                    if (word.TokenIndex != previousIndex + 1)
                    {
                        break;
                    }

                    previousIndex = word.TokenIndex;
                    joined.Append(word.Text.Trim());
                    bounds = Union(bounds, word.Bounds.Normalized());

                    string value = TrimWrappingPunctuation(joined.ToString());
                    if (TryClassify(value, end - start + 1, out PrivacyMatchKind kind))
                    {
                        candidates.Add(new PrivacyMatch(
                            kind,
                            line.Key,
                            words[start].TokenIndex,
                            word.TokenIndex,
                            bounds));
                    }
                }
            }
        }

        return ResolveOverlaps(candidates);
    }

    private static bool TryClassify(string value, int spanTokenCount, out PrivacyMatchKind kind)
    {
        kind = default;
        if (value.Length is < 4 or > 512)
        {
            return false;
        }

        if (Secret.IsMatch(value))
        {
            kind = PrivacyMatchKind.SecretToken;
            return true;
        }

        if (Email.IsMatch(value))
        {
            kind = PrivacyMatchKind.EmailAddress;
            return true;
        }

        if (IsIpv4(value))
        {
            kind = PrivacyMatchKind.IpAddress;
            return true;
        }

        string digits = DigitsOnly(value);
        if (IsKoreanResidentNumber(value, digits, spanTokenCount))
        {
            kind = PrivacyMatchKind.KoreanResidentNumber;
            return true;
        }

        if (IsKoreanPhone(value, digits))
        {
            kind = PrivacyMatchKind.KoreanPhoneNumber;
            return true;
        }

        if (IsPaymentCard(value, digits))
        {
            kind = PrivacyMatchKind.PaymentCardNumber;
            return true;
        }

        return false;
    }

    private static bool IsKoreanResidentNumber(string value, string digits, int spanTokenCount)
    {
        if (digits.Length != 13 || (spanTokenCount == 1 && !value.Contains('-', StringComparison.Ordinal)))
        {
            return false;
        }

        int category = digits[6] - '0';
        if (category is < 1 or > 8)
        {
            return false;
        }

        int year = int.Parse(digits.AsSpan(0, 2), CultureInfo.InvariantCulture);
        year += category is 1 or 2 or 5 or 6 ? 1900 : 2000;
        int month = int.Parse(digits.AsSpan(2, 2), CultureInfo.InvariantCulture);
        int day = int.Parse(digits.AsSpan(4, 2), CultureInfo.InvariantCulture);
        return DateOnly.TryParseExact(
            $"{year:0000}{month:00}{day:00}",
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }

    private static bool IsKoreanPhone(string value, string digits)
    {
        if (!ContainsOnlyNumberSeparators(value))
        {
            return false;
        }

        if (digits.StartsWith("82", StringComparison.Ordinal) && digits.Length is 11 or 12)
        {
            digits = "0" + digits[2..];
        }

        return digits switch
        {
            { Length: 10 or 11 } when digits.StartsWith("010", StringComparison.Ordinal) => true,
            { Length: 10 or 11 } when digits.StartsWith("070", StringComparison.Ordinal) => true,
            { Length: 9 or 10 } when digits.StartsWith("02", StringComparison.Ordinal) => true,
            { Length: 10 or 11 } when IsKoreanAreaPrefix(digits) => true,
            _ => false,
        };
    }

    private static bool IsKoreanAreaPrefix(string digits) =>
        digits.StartsWith("031", StringComparison.Ordinal)
        || digits.StartsWith("032", StringComparison.Ordinal)
        || digits.StartsWith("033", StringComparison.Ordinal)
        || digits.StartsWith("041", StringComparison.Ordinal)
        || digits.StartsWith("042", StringComparison.Ordinal)
        || digits.StartsWith("043", StringComparison.Ordinal)
        || digits.StartsWith("044", StringComparison.Ordinal)
        || digits.StartsWith("051", StringComparison.Ordinal)
        || digits.StartsWith("052", StringComparison.Ordinal)
        || digits.StartsWith("053", StringComparison.Ordinal)
        || digits.StartsWith("054", StringComparison.Ordinal)
        || digits.StartsWith("055", StringComparison.Ordinal)
        || digits.StartsWith("061", StringComparison.Ordinal)
        || digits.StartsWith("062", StringComparison.Ordinal)
        || digits.StartsWith("063", StringComparison.Ordinal)
        || digits.StartsWith("064", StringComparison.Ordinal);

    private static bool IsPaymentCard(string value, string digits)
    {
        if (digits.Length is < 13 or > 19 || !ContainsOnlyNumberSeparators(value))
        {
            return false;
        }

        if (digits.All(character => character == digits[0]))
        {
            return false;
        }

        int sum = 0;
        bool doubleDigit = false;
        for (int index = digits.Length - 1; index >= 0; index--)
        {
            int digit = digits[index] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
    }

    private static bool IsIpv4(string value)
    {
        string[] octets = value.Split('.');
        if (octets.Length != 4)
        {
            return false;
        }

        foreach (string octet in octets)
        {
            if (octet.Length is < 1 or > 3
                || (octet.Length > 1 && octet[0] == '0')
                || !byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static string DigitsOnly(string value)
    {
        var digits = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                digits.Append(character);
            }
        }

        return digits.ToString();
    }

    private static bool ContainsOnlyNumberSeparators(string value) =>
        value.All(static character => char.IsAsciiDigit(character)
                                      || character is '-' or ' ' or '.' or '(' or ')' or '+');

    private static string TrimWrappingPunctuation(string value) =>
        value.Trim(' ', '\t', '\r', '\n', ',', ';', ':', '"', '\'', '[', ']', '{', '}');

    private static IReadOnlyList<PrivacyMatch> ResolveOverlaps(List<PrivacyMatch> candidates)
    {
        List<PrivacyMatch> ordered = candidates
            .Distinct()
            .OrderBy(static match => match.LineIndex)
            .ThenBy(static match => match.FirstTokenIndex)
            .ThenByDescending(static match => match.LastTokenIndex)
            .ThenByDescending(static match => Priority(match.Kind))
            .ToList();

        var resolved = new List<PrivacyMatch>(ordered.Count);
        foreach (PrivacyMatch candidate in ordered)
        {
            if (resolved.Count == 0)
            {
                resolved.Add(candidate);
                continue;
            }

            PrivacyMatch previous = resolved[^1];
            if (candidate.LineIndex != previous.LineIndex
                || candidate.FirstTokenIndex > previous.LastTokenIndex)
            {
                resolved.Add(candidate);
                continue;
            }

            PrivacyMatchKind kind = Priority(candidate.Kind) > Priority(previous.Kind)
                ? candidate.Kind
                : previous.Kind;
            resolved[^1] = new PrivacyMatch(
                kind,
                previous.LineIndex,
                Math.Min(previous.FirstTokenIndex, candidate.FirstTokenIndex),
                Math.Max(previous.LastTokenIndex, candidate.LastTokenIndex),
                Union(previous.Bounds, candidate.Bounds));
        }

        return resolved;
    }

    private static int Priority(PrivacyMatchKind kind) => kind switch
    {
        PrivacyMatchKind.SecretToken => 6,
        PrivacyMatchKind.KoreanResidentNumber => 5,
        PrivacyMatchKind.PaymentCardNumber => 4,
        PrivacyMatchKind.EmailAddress => 3,
        PrivacyMatchKind.KoreanPhoneNumber => 2,
        PrivacyMatchKind.IpAddress => 1,
        _ => 0,
    };

    private static RectD Union(RectD first, RectD second)
    {
        if (first.IsEmpty)
        {
            return second.Normalized();
        }

        RectD a = first.Normalized();
        RectD b = second.Normalized();
        return new RectD(
            Math.Min(a.Left, b.Left),
            Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right) - Math.Min(a.Left, b.Left),
            Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Top, b.Top));
    }
}
