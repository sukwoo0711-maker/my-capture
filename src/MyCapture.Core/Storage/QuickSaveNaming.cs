using System.Globalization;
using System.Text;

namespace MyCapture.Core.Storage;

/// <summary>
/// Turns a user filename pattern into a concrete, collision-free path.
/// </summary>
/// <remarks>
/// <para>
/// The pattern is a plain string with <c>{...}</c> tokens whose contents are a .NET
/// custom date/time format applied to the capture time — for example
/// <c>capture_{yyyyMMdd}_{HHmmss}</c> becomes <c>capture_20260829_135312</c>. A single
/// documented mechanism (a date format inside braces) is easier to explain than a fixed
/// list of named tokens, and it lets a user express any ordering they like.
/// </para>
/// <para>
/// Any character illegal in a filename is replaced so a stray token or a literal such as
/// a colon can never produce a path the filesystem rejects. The extension is supplied by
/// the caller rather than baked into the pattern, so the same pattern serves PNG quick
/// save and any future format.
/// </para>
/// <para>
/// Collisions are resolved by appending <c>-2</c>, <c>-3</c>, … so a burst of captures in
/// the same second never silently overwrites an earlier file — the one thing a capture
/// tool must never do.
/// </para>
/// </remarks>
public static class QuickSaveNaming
{
    /// <summary>Fallback used when a pattern renders to nothing usable.</summary>
    public const string FallbackStem = "capture";

    /// <summary>
    /// Expands <paramref name="pattern"/> against <paramref name="timestamp"/> into a base
    /// file name (without extension), with illegal characters sanitised.
    /// </summary>
    public static string BuildStem(string pattern, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = "capture_{yyyyMMdd}_{HHmmss}";
        }

        var builder = new StringBuilder(pattern.Length + 8);
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '{')
            {
                int close = pattern.IndexOf('}', i + 1);
                if (close > i)
                {
                    string token = pattern.Substring(i + 1, close - i - 1);
                    builder.Append(FormatToken(token, timestamp));
                    i = close + 1;
                    continue;
                }
            }

            builder.Append(c);
            i++;
        }

        string stem = Sanitize(builder.ToString());
        return string.IsNullOrWhiteSpace(stem) ? FallbackStem : stem;
    }

    /// <summary>
    /// Resolves a non-colliding absolute path under <paramref name="directory"/> for the
    /// given <paramref name="stem"/> and <paramref name="extension"/>.
    /// </summary>
    /// <param name="extension">Extension including the leading dot, for example <c>.png</c>.</param>
    /// <remarks>
    /// The returned path does not exist at the moment it is chosen. It is the caller's
    /// job to write it promptly; the tiny window between choosing and writing is accepted
    /// because quick save is a single-threaded, user-driven action.
    /// </remarks>
    public static string ResolvePath(string directory, string stem, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);

        if (string.IsNullOrEmpty(extension))
        {
            extension = ".png";
        }
        else if (extension[0] != '.')
        {
            extension = "." + extension;
        }

        string candidate = Path.Combine(directory, stem + extension);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (int suffix = 2; suffix < int.MaxValue; suffix++)
        {
            string next = Path.Combine(
                directory,
                $"{stem}-{suffix.ToString(CultureInfo.InvariantCulture)}{extension}");
            if (!File.Exists(next))
            {
                return next;
            }
        }

        // Astronomically unreachable; keep the compiler happy and never return null.
        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private static string FormatToken(string token, DateTimeOffset timestamp)
    {
        if (string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        try
        {
            // A lone standard format character (for example "d") would be interpreted as a
            // standard pattern; force custom interpretation so single-letter tokens behave
            // as the user's literal intent.
            string format = token.Length == 1 ? "%" + token : token;
            return timestamp.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            // Only genuinely malformed patterns (for example a lone unescaped quote) throw;
            // those are emitted verbatim rather than failing the whole save.
            return token;
        }
    }

    private static string Sanitize(string text)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.ToString().Trim().TrimEnd('.');
    }
}
