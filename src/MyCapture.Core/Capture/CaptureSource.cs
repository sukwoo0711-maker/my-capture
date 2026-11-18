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
/// <param name="AppUserModelId">
/// Application user model ID of a packaged (Store) application. Its executable path names the package
/// version, so after an update the path is gone and this ID is what still opens the application.
/// </param>
/// <param name="DocumentOrigin">How <paramref name="DocumentPath"/> was found (<see cref="CaptureDocumentOrigin"/>).</param>
public sealed record CaptureSourceDetails(string AppName = "", string AppPath = "", string DocumentPath = "",
    string AppUserModelId = "", string DocumentOrigin = "")
{
    public static CaptureSourceDetails Empty { get; } = new();

    public bool IsEmpty => AppName.Length == 0 && AppPath.Length == 0 && DocumentPath.Length == 0
        && AppUserModelId.Length == 0;

    /// <summary>Validates every field; anything unsafe or unproven becomes empty.</summary>
    public static CaptureSourceDetails Create(string? appName, string? appPath, string? documentPath,
        string? appUserModelId = null, string? documentOrigin = null)
    {
        string document = CaptureSourceText.NormalizeLocalPath(documentPath);
        var details = new CaptureSourceDetails(
            CaptureSourceText.NormalizeAppName(appName),
            CaptureSourceText.NormalizeLocalPath(appPath),
            document,
            CaptureSourceText.NormalizeAppUserModelId(appUserModelId),
            document.Length == 0 ? string.Empty : CaptureDocumentOrigin.Normalize(documentOrigin));
        return details.IsEmpty ? Empty : details;
    }

    /// <summary>
    /// Adds a document found by another reader (a browser's local file) when this source has none yet;
    /// an application's own document is never replaced.
    /// </summary>
    public static CaptureSourceDetails? WithDocument(CaptureSourceDetails? details, string? documentPath, string origin)
    {
        string document = CaptureSourceText.NormalizeLocalPath(documentPath);
        if (document.Length == 0 || details is { DocumentPath.Length: > 0 }) return details;
        CaptureSourceDetails source = details ?? Empty;
        return Create(source.AppName, source.AppPath, document, source.AppUserModelId, origin);
    }
}

/// <summary>Where a capture's document path came from. Stored for diagnosis and trust, never shown.</summary>
public static class CaptureDocumentOrigin
{
    /// <summary>The current folder of a File Explorer window.</summary>
    public const string Explorer = "explorer";

    /// <summary>The document application's own window: its title, accessibility tab data or Office object model.</summary>
    public const string Window = "window";

    /// <summary>A local file shown in a browser tab (<c>file:</c> address).</summary>
    public const string Browser = "browser";

    /// <summary>The single Recent Items shortcut matching the window title's file name.</summary>
    public const string Recent = "recent";

    /// <summary>A code editor's selected tab, read again after the capture (<see cref="EditorDocumentMatch"/>).</summary>
    public const string Editor = "editor";

    /// <summary>Known values only; anything else is dropped rather than stored.</summary>
    public static string Normalize(string? value) =>
        value is Explorer or Window or Browser or Recent or Editor ? value : string.Empty;
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

    /// <summary>Longest stored application user model ID (the documented limit is 130 characters).</summary>
    public const int MaxAppUserModelIdLength = 130;

    /// <summary>
    /// A packaged application's ID, <c>PackageFamilyName!ApplicationId</c>: letters, digits, dot, hyphen
    /// and underscore around exactly one <c>!</c>. Anything else could not name a Store app and becomes empty,
    /// so the value can be placed after <c>shell:AppsFolder\</c> without escaping.
    /// </summary>
    public static string NormalizeAppUserModelId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string text = value.Trim();
        if (text.Length > MaxAppUserModelIdLength) return string.Empty;
        int bang = text.IndexOf('!');
        if (bang <= 0 || bang == text.Length - 1 || text.IndexOf('!', bang + 1) >= 0) return string.Empty;
        foreach (char character in text)
        {
            if (character == '!' || char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') continue;
            return string.Empty;
        }
        // The family name ends in "_<13-character publisher ID>".
        string family = text[..bang];
        int underscore = family.LastIndexOf('_');
        return underscore > 0 && family.Length - underscore - 1 == 13 ? text : string.Empty;
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
