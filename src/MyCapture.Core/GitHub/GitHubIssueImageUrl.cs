using System.Text.RegularExpressions;

namespace MyCapture.Core.GitHub;

/// <summary>
/// Validates the configured GitHub issue used as an image host, and extracts the
/// <c>user-attachments</c> URL GitHub writes after a clipboard paste.
/// </summary>
public static class GitHubIssueImageUrl
{
    public const string DefaultIssueUrl = "https://github.com/sukwoo0711-maker/my-capture/issues/new";

    private static readonly Regex AttachmentUrl = new(
        @"https://github\.com/user-attachments/assets/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex OwnerRepo = new(
        @"^[a-zA-Z0-9](?:[a-zA-Z0-9._-]{0,37}[a-zA-Z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Accepts <c>https://github.com/{owner}/{repo}/issues/{n}</c> or <c>…/issues/new</c>.
    /// Fragments and query strings are stripped. Empty input maps to <see cref="DefaultIssueUrl"/>.
    /// </summary>
    public static bool TryNormalize(string? raw, out string url)
    {
        url = DefaultIssueUrl;
        string trimmed = raw?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (!uri.IsDefaultPort && uri.Port != 443)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4
            || !string.Equals(segments[2], "issues", StringComparison.OrdinalIgnoreCase)
            || !OwnerRepo.IsMatch(segments[0])
            || !OwnerRepo.IsMatch(segments[1]))
        {
            return false;
        }

        string issue = segments[3];
        bool numbered = int.TryParse(issue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0;
        if (!numbered && !string.Equals(issue, "new", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        url = $"https://github.com/{segments[0]}/{segments[1]}/issues/{(numbered ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "new")}";
        return true;
    }

    public static bool TryExtractAttachmentUrl(string? text, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = AttachmentUrl.Match(text);
        if (!match.Success)
        {
            return false;
        }

        url = match.Value;
        return true;
    }
}
