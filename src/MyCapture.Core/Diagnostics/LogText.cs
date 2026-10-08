using System.Globalization;
using System.Text;

namespace MyCapture.Core.Diagnostics;

/// <summary>
/// Converts untrusted display values into one physical log line.
/// </summary>
/// <remarks>
/// Structured logging protects the message template, but a path or title can still contain a
/// line separator and forge a second-looking entry in text log providers. Escaping instead of
/// deleting separators preserves the diagnostic value without changing log structure. Other
/// control characters (NUL, tab, ESC terminal sequences, C1 controls) are escaped as
/// <c>\uXXXX</c> for the same reason.
/// </remarks>
public static class LogText
{
    public static string SingleLine(string? value) =>
        EscapeOtherControlCharacters(value ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\u0085", "\\u0085", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    // Line separators are left for the Replace chain above so their established escapes stay
    // stable and the final value visibly passes through a string replacement.
    private static string EscapeOtherControlCharacters(string value)
    {
        int first = IndexOfOtherControl(value);
        if (first < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 16);
        builder.Append(value, 0, first);
        for (int i = first; i < value.Length; i++)
        {
            char c = value[i];
            if (IsOtherControl(c))
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static int IndexOfOtherControl(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (IsOtherControl(value[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsOtherControl(char c) =>
        char.IsControl(c) && c is not '\r' and not '\n' and not '\u0085';
}
