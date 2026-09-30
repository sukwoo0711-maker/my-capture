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
}
