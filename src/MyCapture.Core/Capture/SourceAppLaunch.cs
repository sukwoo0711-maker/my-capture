using System.Diagnostics;

namespace MyCapture.Core.Capture;

/// <summary>
/// Launch plans that reopen a source in the application it was captured from. Only applications on an
/// explicit list receive a path or address, each in its own known argument form; shells, interpreters
/// and unknown applications never receive one, and the caller falls back to the existing safe plan.
/// </summary>
public static class SourceAppLaunch
{
    private enum Role
    {
        /// <summary>Opens any file as text; receiving a path never runs it.</summary>
        Editor,

        /// <summary>Opens only its own document types.</summary>
        Viewer,
    }

    private sealed record KnownApp(Role Role, string[] Extensions, bool EditSwitch = false);

    private static readonly string[] WordTypes = [".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".rtf", ".odt"];
    private static readonly string[] ExcelTypes = [".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".ods", ".csv"];
    private static readonly string[] PowerPointTypes = [".ppt", ".pptx", ".pptm", ".pot", ".potx", ".pps", ".ppsx", ".odp"];
    private static readonly string[] PdfTypes = [".pdf"];
    private static readonly string[] HangulTypes = [".hwp", ".hwpx"];

    /// <summary>A browser reopens what it showed from disk: documents it renders, never scripts or programs.</summary>
    private static readonly string[] BrowserTypes = [".pdf", ".htm", ".html", ".xhtml", ".svg", ".txt", ".xml", ".json",
        ".png", ".jpg", ".jpeg", ".gif", ".webp"];

    private static readonly IReadOnlyDictionary<string, KnownApp> Apps = new Dictionary<string, KnownApp>(StringComparer.Ordinal)
    {
        ["code"] = new(Role.Editor, []),
        ["code - insiders"] = new(Role.Editor, []),
        ["devenv"] = new(Role.Editor, [], EditSwitch: true),
        ["notepad"] = new(Role.Editor, []),
        ["notepad++"] = new(Role.Editor, []),
        ["winword"] = new(Role.Viewer, WordTypes),
        ["excel"] = new(Role.Viewer, ExcelTypes),
        ["powerpnt"] = new(Role.Viewer, PowerPointTypes),
        ["acrord32"] = new(Role.Viewer, PdfTypes),
        ["acrobat"] = new(Role.Viewer, PdfTypes),
        ["sumatrapdf"] = new(Role.Viewer, PdfTypes),
        ["foxitpdfreader"] = new(Role.Viewer, PdfTypes),
        ["foxitreader"] = new(Role.Viewer, PdfTypes),
        ["hwp"] = new(Role.Viewer, HangulTypes),
        ["msedge"] = new(Role.Viewer, BrowserTypes),
        ["chrome"] = new(Role.Viewer, BrowserTypes),
        ["firefox"] = new(Role.Viewer, BrowserTypes),
        ["brave"] = new(Role.Viewer, BrowserTypes),
        ["vivaldi"] = new(Role.Viewer, BrowserTypes),
        ["opera"] = new(Role.Viewer, BrowserTypes),
        ["whale"] = new(Role.Viewer, BrowserTypes),
    };

    /// <summary>Browsers that receive a web address as their only argument.</summary>
    public static bool IsBrowser(string? appPath) => SourceDocument.AppId(appPath) is
        "msedge" or "chrome" or "firefox" or "brave" or "vivaldi" or "opera" or "whale";

    /// <summary>Whether <paramref name="appPath"/> is an application this class may pass a document to.</summary>
    public static bool IsKnownDocumentApp(string? appPath) => Apps.ContainsKey(SourceDocument.AppId(appPath));

    /// <summary>
    /// Opens <paramref name="documentPath"/> in the captured application when it is a known document
    /// application whose executable still exists and the document is of a type it opens. Null otherwise.
    /// </summary>
    public static ProcessStartInfo? CreateDocument(string? appPath, string documentPath, Func<string, bool>? exists = null)
    {
        string app = CaptureSourceText.NormalizeLocalPath(appPath);
        string document = CaptureSourceText.NormalizeLocalPath(documentPath);
        if (app.Length == 0 || document.Length == 0 || !app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !Apps.TryGetValue(SourceDocument.AppId(app), out KnownApp? known)) return null;
        string extension = Path.GetExtension(document).ToLowerInvariant();
        if (known.Role == Role.Viewer && !known.Extensions.Contains(extension, StringComparer.Ordinal)) return null;
        if (!SourceDocument.FileExists(app, exists)) return null;
        var start = new ProcessStartInfo(app) { UseShellExecute = false };
        if (known.EditSwitch) start.ArgumentList.Add("/edit");
        start.ArgumentList.Add(document);
        return start;
    }

    /// <summary>
    /// Opens a web address in the browser it was captured from when that browser still exists, otherwise
    /// in the default browser. The address is validated, so it is an http or https URL and never a switch.
    /// </summary>
    public static ProcessStartInfo? CreateUrl(string? url, string? browserPath, Func<string, bool>? exists = null)
    {
        string page = SourcePageUrl.Normalize(url);
        if (page.Length == 0) return null;
        string browser = CaptureSourceText.NormalizeLocalPath(browserPath);
        if (browser.Length > 0 && browser.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsBrowser(browser)
            && SourceDocument.FileExists(browser, exists))
        {
            var start = new ProcessStartInfo(browser) { UseShellExecute = false };
            start.ArgumentList.Add(page);
            return start;
        }

        return new ProcessStartInfo(page) { UseShellExecute = true };
    }

    /// <summary>
    /// Starts the captured application without arguments: its executable while it exists, otherwise the
    /// packaged application through its ID. Null when neither is available.
    /// </summary>
    public static ProcessStartInfo? CreateApp(string? appPath, string? appUserModelId, string explorerPath,
        Func<string, bool>? exists = null)
    {
        string app = CaptureSourceText.NormalizeLocalPath(appPath);
        if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && SourceDocument.FileExists(app, exists))
            return new ProcessStartInfo(app) { UseShellExecute = false };
        string id = CaptureSourceText.NormalizeAppUserModelId(appUserModelId);
        string explorer = CaptureSourceText.NormalizeLocalPath(explorerPath);
        if (id.Length == 0 || explorer.Length == 0) return null;
        // The ID has only letters, digits and . - _ ! (NormalizeAppUserModelId), so it needs no quoting.
        return SourceDocument.ExplorerStartInfo(explorer, @"shell:AppsFolder\" + id, selectFile: false);
    }
}
