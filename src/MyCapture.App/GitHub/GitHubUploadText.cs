using MyCapture.Core.Capture;

namespace MyCapture.App.GitHub;

/// <summary>
/// Clipboard text left by an F9 upload. The attachment URL is always first. When the
/// image carried a source page, that origin link follows as its own paragraph so a paste
/// into the issue keeps both addresses.
/// </summary>
internal static class GitHubUploadText
{
    internal static string Compose(string attachmentUrl, string? sourcePageUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(attachmentUrl);
        string origin = SourcePageUrl.Normalize(sourcePageUrl);
        return origin.Length == 0 ? attachmentUrl : attachmentUrl + "\n\n" + origin;
    }
}
