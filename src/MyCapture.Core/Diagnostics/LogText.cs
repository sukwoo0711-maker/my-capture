namespace MyCapture.Core.Diagnostics;

/// <summary>
/// Converts untrusted display values into one physical log line.
/// </summary>
/// <remarks>
/// Structured logging protects the message template, but a path or title can still contain a
/// line separator and forge a second-looking entry in text log providers. Escaping instead of
/// deleting separators preserves the diagnostic value without changing log structure.
/// </remarks>
public static class LogText
{
    public static string SingleLine(string? value) =>
        (value ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\u0085", "\\u0085", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);
}
