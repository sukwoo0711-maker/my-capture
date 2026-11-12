using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MyCapture.Core.Capture;

/// <summary>Pure document-source rules. Titles alone never invent a directory.</summary>
public static class SourceDocument
{
    public static string AppId(string? appPath) => Path.GetFileNameWithoutExtension(appPath ?? string.Empty).ToLowerInvariant();

    public static bool IsSupportedApp(string? appPath) => AppId(appPath) is
        "excel" or "winword" or "powerpnt" or "notepad" or "acrord32" or "acrobat" or "sumatrapdf"
        or "foxitpdfreader" or "foxitreader" or "code" or "code - insiders" or "notepad++" or "hwp";

    /// <summary>
    /// Whether a source's document path names a file rather than a folder: a document application's
    /// document, or the local file a browser showed. Explorer's path is its folder.
    /// </summary>
    public static bool IsFileSource(string? appPath) => IsSupportedApp(appPath) || SourceAppLaunch.IsBrowser(appPath);

    public static string ResolvePath(string? appPath, string title, IEnumerable<string> properties)
    {
        if (!IsSupportedApp(appPath)) return string.Empty;
        string documentTitle = RemoveAppSuffix(title);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in properties.Prepend(documentTitle))
        {
            string path = CaptureSourceText.NormalizeLocalPath(value.Trim().Trim('"'));
            if (path.Length == 0 || Path.GetFileName(path).Length == 0) continue;
            string name = Path.GetFileName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            // A tab's tooltip can describe a different document. Match the captured title as well.
            if (documentTitle.Equals(path, StringComparison.OrdinalIgnoreCase)
                || documentTitle.Equals(name, StringComparison.OrdinalIgnoreCase)
                || documentTitle.Equals(stem, StringComparison.OrdinalIgnoreCase)
                || documentTitle.StartsWith(name + " - ", StringComparison.OrdinalIgnoreCase)
                || documentTitle.StartsWith(name + " — ", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
        }
        return paths.Count == 1 ? paths.Single() : string.Empty;
    }

    public static bool IsUnsaved(string? appPath, string? title)
    {
        string pattern = AppId(appPath) switch
        {
            "excel" => @"^(Book\d+|통합 문서\s*\d+)$",
            "winword" => @"^(Document\d+|문서\s*\d+)$",
            "powerpnt" => @"^(Presentation\d+|프레젠테이션\s*\d+)$",
            "notepad" => @"^(Untitled|제목 없음)$",
            "code" or "code - insiders" => @"^(Untitled-\d+|제목 없음-\d+)(?: [—–-] .+)?$",
            _ => string.Empty,
        };
        return pattern.Length > 0 && Regex.IsMatch(RemoveAppSuffix(title ?? string.Empty), pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
    }

    private static string RemoveAppSuffix(string title)
    {
        string text = title.Trim().TrimStart('*', '●', '•').Trim();
        string[] apps = ["Excel", "Word", "PowerPoint", "Notepad", "메모장", "Adobe Acrobat Reader",
            "Adobe Acrobat", "SumatraPDF", "Foxit PDF Reader", "Visual Studio Code - Insiders", "Visual Studio Code",
            "Notepad++", "한글"];
        foreach (string app in apps)
        foreach (string separator in TitleSeparators)
        {
            string suffix = separator + app;
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return text[..^suffix.Length].TrimEnd();
        }
        return text;
    }

    private static readonly string[] TitleSeparators = [" - ", " — ", " – "];

    /// <summary>
    /// File names a document application's title can carry: the title without the application's name,
    /// and its first part before a separator (VS Code adds the folder: <c>main.cs - Project</c>). Only
    /// names with an extension qualify; a title alone never yields a folder.
    /// </summary>
    public static IReadOnlyList<string> TitleFileNames(string? appPath, string? title)
    {
        if (!IsSupportedApp(appPath) || string.IsNullOrWhiteSpace(title) || IsUnsaved(appPath, title)) return [];
        string document = RemoveAppSuffix(title);
        var names = new List<string>(2);
        Add(document);
        int cut = TitleSeparators.Select(separator => document.IndexOf(separator, StringComparison.Ordinal))
            .Where(index => index > 0).DefaultIfEmpty(-1).Min();
        if (cut > 0) Add(document[..cut]);
        return names;

        void Add(string candidate)
        {
            string name = candidate.Trim();
            if (name.Length is 0 or > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.EndsWith('.') || names.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            string extension = Path.GetExtension(name);
            if (extension.Length is < 2 or > 9 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit)
                || Path.GetFileNameWithoutExtension(name).Trim().Length == 0) return;
            names.Add(name);
        }
    }

    public static string ShortenPath(string path, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 5);
        if (path.Length <= maxLength) return path;
        int tail = Math.Min(Path.GetFileName(path).Length, maxLength - 4);
        tail = Math.Max(tail, (maxLength - 1) / 2);
        int head = maxLength - tail - 1;
        if (char.IsHighSurrogate(path[head - 1])) head--;
        int tailStart = path.Length - tail;
        if (char.IsLowSurrogate(path[tailStart])) tailStart++;
        return path[..head] + "…" + path[tailStart..];
    }

    public static bool FileExists(string? path, Func<string, bool>? exists = null)
    {
        string normalized = CaptureSourceText.NormalizeLocalPath(path);
        if (normalized.Length == 0) return false;
        try { return (exists ?? File.Exists)(normalized); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    /// <summary>Builds an Explorer launch plan; the caller checks whether the source is a file or folder.</summary>
    public static ProcessStartInfo? CreateOpenFolderStartInfo(string path, string explorerPath, bool selectFile)
    {
        string normalized = CaptureSourceText.NormalizeLocalPath(path);
        if (normalized.Length == 0 || normalized.Contains('"')) return null;
        return ExplorerStartInfo(explorerPath, normalized, selectFile);
    }

    /// <summary>
    /// Explorer with one argument the caller has validated: the one place that starts it. The argument is
    /// a folder, a file to select, or a packaged application's shell item, and has no quotation mark.
    /// </summary>
    internal static ProcessStartInfo ExplorerStartInfo(string explorerPath, string normalized, bool selectFile)
    {
        var start = new ProcessStartInfo(explorerPath) { UseShellExecute = false };
        // Explorer parses commas itself, so quote the validated path even when it has no spaces.
        if (selectFile) start.Arguments = "/select,\"" + normalized + "\"";
        else start.ArgumentList.Add(normalized);
        return start;
    }

    /// <summary>
    /// Builds a launch plan for an existing source file; the caller checks its existence before opening.
    /// The captured application opens it when it is a known document application that still exists
    /// (<see cref="SourceAppLaunch.CreateDocument"/>); otherwise the safe plan below applies.
    /// </summary>
    public static ProcessStartInfo? CreateOpenStartInfo(string path, string textEditorPath, Func<string, bool>? exists = null,
        string? sourceAppPath = null)
    {
        string normalized = CaptureSourceText.NormalizeLocalPath(path);
        if (normalized.Length == 0) return null;
        if (SourceAppLaunch.CreateDocument(sourceAppPath, normalized, exists) is { } captured) return captured;
        // Only known document formats use file associations. Scripts, executables, shortcuts,
        // and unknown extensions go to an explicit editor, never their default shell action.
        if (Path.GetExtension(normalized).ToLowerInvariant() is
            ".doc" or ".docx" or ".docm" or ".odt" or ".rtf"
            or ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".ods" or ".csv"
            or ".ppt" or ".pptx" or ".pptm" or ".odp" or ".pdf" or ".hwp" or ".hwpx")
            return new ProcessStartInfo(normalized) { UseShellExecute = true, Verb = "open" };

        string editor = CaptureSourceText.NormalizeLocalPath(textEditorPath);
        if (!FileExists(editor, exists)) return null;
        var start = new ProcessStartInfo(editor) { UseShellExecute = false };
        start.ArgumentList.Add(normalized);
        return start;
    }
}
