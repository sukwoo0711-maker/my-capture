namespace MyCapture.Core.Capture;

/// <summary>Validation shared by capture metadata and explicit library URL actions.</summary>
public static class SourcePageUrl
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl))
            return string.Empty;
        string text = value.Trim();
        if (!(text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
              || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || !uri.IsWellFormedOriginalString()
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return string.Empty;
        return uri.AbsoluteUri;
    }

    /// <summary>
    /// The local file a browser tab shows through a <c>file:</c> address, for example a PDF opened from
    /// disk; empty for any other address. Query and fragment (a PDF page anchor) are not part of the file.
    /// </summary>
    public static string LocalFilePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > CaptureSourceText.MaxPathLength * 3
            || value.Any(char.IsControl)) return string.Empty;
        string text = value.Trim();
        if (!text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || !uri.IsFile || !string.IsNullOrEmpty(uri.UserInfo))
            return string.Empty;
        try { return CaptureSourceText.NormalizeLocalPath(uri.LocalPath); }
        catch (InvalidOperationException) { return string.Empty; }
    }

    /// <summary>
    /// A browser document's address as captured: a web page (<see cref="Normalize"/>) or a local file as
    /// a canonical <c>file:</c> address. Empty for anything else, such as internal browser pages.
    /// </summary>
    public static string NormalizeDocument(string? value)
    {
        string page = Normalize(value);
        if (page.Length > 0) return page;
        string file = LocalFilePath(value);
        return file.Length == 0 ? string.Empty : new Uri(file).AbsoluteUri;
    }
}
