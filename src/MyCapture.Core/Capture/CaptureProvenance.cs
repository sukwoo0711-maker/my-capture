using MyCapture.Core.Queue;

namespace MyCapture.Core.Capture;

public enum CaptureProvenanceKind { Web, Document, App, None }

/// <summary>A validated, immutable source identity prepared once for every capture surface.</summary>
public sealed record CaptureProvenance
{
    private CaptureProvenance(string url, string documentPath, string appName, string appExecutablePath, string windowTitle)
    {
        Url = url;
        DocumentPath = documentPath;
        AppName = appName;
        AppExecutablePath = appExecutablePath;
        WindowTitle = windowTitle;
        Kind = url.Length > 0 ? CaptureProvenanceKind.Web
            : documentPath.Length > 0 ? CaptureProvenanceKind.Document
            : appName.Length > 0 || appExecutablePath.Length > 0 ? CaptureProvenanceKind.App
            : CaptureProvenanceKind.None;
        string application = appName.Length > 0 ? appName : Path.GetFileNameWithoutExtension(appExecutablePath);
        string target;
        switch (Kind)
        {
            case CaptureProvenanceKind.Web:
                var uri = new Uri(url);
                string host = NormalizeHost(uri);
                DisplayText = Shorten(host + (uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath), 80);
                BadgeText = host;
                SearchQuery = "host:" + host;
                target = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
                break;
            case CaptureProvenanceKind.Document:
                string fileName = Path.GetFileName(documentPath);
                DisplayText = SourceDocument.ShortenPath(fileName.Length > 0 ? fileName : documentPath, 80);
                BadgeText = DisplayText;
                SearchQuery = FieldQuery("folder", documentPath);
                target = documentPath;
                break;
            case CaptureProvenanceKind.App:
                DisplayText = application;
                BadgeText = application;
                SearchQuery = FieldQuery("app", application);
                target = appExecutablePath.Length > 0 ? appExecutablePath : application;
                break;
            default:
                DisplayText = BadgeText = SearchQuery = string.Empty;
                target = string.Empty;
                break;
        }
        Tooltip = application.Length > 0 && !target.Equals(application, StringComparison.Ordinal)
            ? target + Environment.NewLine + application : target;
    }

    public static CaptureProvenance None { get; } = new("", "", "", "", "");
    public string Url { get; }
    public string DocumentPath { get; }
    public string AppName { get; }
    public string AppExecutablePath { get; }
    public string WindowTitle { get; }
    public CaptureProvenanceKind Kind { get; }
    public bool HasSource => Kind != CaptureProvenanceKind.None;
    public bool CanOpen => Kind is CaptureProvenanceKind.Web or CaptureProvenanceKind.Document
        || Kind == CaptureProvenanceKind.App && AppExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    public string DisplayText { get; }
    public string BadgeText { get; }
    public string Tooltip { get; }
    public string SearchQuery { get; }

    public static CaptureProvenance Create(string? url = null, string? documentPath = null,
        string? appName = null, string? appExecutablePath = null, string? windowTitle = null)
    {
        string page = SourcePageUrl.Normalize(url);
        CaptureSourceDetails source = CaptureSourceDetails.Create(appName, appExecutablePath, documentPath);
        string title = windowTitle ?? string.Empty;
        if (page.Length == 0 && source.IsEmpty && title.Length == 0) return None;
        return new(page, source.DocumentPath, source.AppName, source.AppPath, title);
    }

    public static CaptureProvenance FromRecord(CaptureRecord? record) => record is null ? None
        : FromSource(record.SourceDetails, record.SourcePageUrl, record.SourceWindowTitle);

    public static CaptureProvenance FromSource(CaptureSourceDetails? source, string? url = null, string? windowTitle = null) =>
        Create(url, source?.DocumentPath, source?.AppName, source?.AppPath, windowTitle);

    internal static string NormalizeHost(Uri uri)
    {
        string host = uri.IdnHost;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return host.ToLowerInvariant();
    }

    private static string Shorten(string value, int length)
    {
        if (value.Length <= length) return value;
        int end = length - 1;
        if (char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end] + "…";
    }

    private static string FieldQuery(string field, string value)
    {
        // The library grammar quotes spaces but has no escaped-quote syntax. A quoted app
        // name can still be matched by its longest literal portion without inventing a query.
        string term = value.Split('"').MaxBy(part => part.Length) ?? string.Empty;
        return term.Length == 0 ? string.Empty : field + ":\"" + term + "\"";
    }
}
