using System.Text;

namespace MyCapture.Core.Capture;

/// <summary>
/// Where a capture came from beyond its window title and web page: the application that owned the
/// captured window and, when the shell reports one, the folder or file that window was showing.
/// </summary>
/// <remarks>
/// These values describe the user's own desktop, so they stay in the local library record. They are
/// never written to logs, exported PNG metadata, clipboard text or archives.
/// </remarks>
/// <param name="AppName">Friendly application name, for example <c>Microsoft Edge</c>.</param>
/// <param name="AppPath">Full path of the application's executable.</param>
/// <param name="DocumentPath">Folder or file the window showed, for example an Explorer folder.</param>
public sealed record CaptureSourceDetails(string AppName = "", string AppPath = "", string DocumentPath = "")
{
    public static CaptureSourceDetails Empty { get; } = new();

    public bool IsEmpty => AppName.Length == 0 && AppPath.Length == 0 && DocumentPath.Length == 0;

    /// <summary>Validates every field; anything unsafe or unproven becomes empty.</summary>
    public static CaptureSourceDetails Create(string? appName, string? appPath, string? documentPath)
    {
        var details = new CaptureSourceDetails(
            CaptureSourceText.NormalizeAppName(appName),
            CaptureSourceText.NormalizeLocalPath(appPath),
            CaptureSourceText.NormalizeLocalPath(documentPath));
        return details.IsEmpty ? Empty : details;
    }
}

/// <summary>Validation shared by capture-time source metadata and the library's folder actions.</summary>
public static class CaptureSourceText
{
    /// <summary>Longest stored application name; a version resource can hold arbitrary text.</summary>
    public const int MaxAppNameLength = 128;

    /// <summary>Longest stored path, the Win32 extended-length limit.</summary>
    public const int MaxPathLength = 32_767;

    /// <summary>One line of display text: control characters and runs of white space become one space.</summary>
    public static string NormalizeAppName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(Math.Min(value.Length, MaxAppNameLength));
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (builder.Length + 1 >= MaxAppNameLength) break;
                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length >= MaxAppNameLength) break;
            builder.Append(character);
        }

        // Never end on half of a surrogate pair cut by the length limit.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1])) builder.Length--;
        return builder.ToString();
    }

    /// <summary>
    /// Accepts only a fully qualified local or UNC path. Relative paths, shell namespace names such as
    /// <c>::{GUID}</c>, URLs and device paths (<c>\\?\</c>, <c>\\.\</c>) are not a location the library
    /// can open, so they become empty.
    /// </summary>
    public static string NormalizeLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxPathLength) return string.Empty;
        string text = value.Trim();
        if (text.Any(char.IsControl) || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return string.Empty;
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal)
            || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith(@"\??\", StringComparison.Ordinal))
            return string.Empty;
        if (!Path.IsPathFullyQualified(text)) return string.Empty;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
            return full.Length is > 0 and <= MaxPathLength && Path.IsPathFullyQualified(full) ? full : string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
