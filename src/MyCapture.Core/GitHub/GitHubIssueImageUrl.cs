using System.Text.RegularExpressions;

namespace MyCapture.Core.GitHub;

/// <summary>
/// Validates the configured GitHub issue used as an image host, and extracts the
/// <c>user-attachments</c> URL GitHub writes after a clipboard paste.
/// </summary>
public static class GitHubIssueImageUrl
{
    public const string DefaultIssueUrl = "https://github.com/sukwoo0711-maker/my-capture/issues/new";

    // Attachment URLs GitHub writes after a clipboard paste. The host is not pinned to
    // github.com so GitHub Enterprise Server issues work too; the path shape is what
    // identifies an attachment.
    private static readonly Regex AttachmentUrl = new(
        @"https://[^/\s]+/user-attachments/assets/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex OwnerRepo = new(
        @"^[^\s/\\?#:]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Accepts <c>https://{host}/{owner}/{repo}/issues/{n|new}</c> on any host
    /// (github.com or a GitHub Enterprise Server). Fragments and query strings are
    /// stripped. Empty input maps to <see cref="DefaultIssueUrl"/>.
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

        // HTTPS stays mandatory: the browser session that pastes a clipboard image into
        // the issue must not be redirected through plaintext.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (!uri.IsDefaultPort && uri.Port != 443)
            || string.IsNullOrWhiteSpace(uri.Host))
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

        // Rebuild from the parsed host so a GitHub Enterprise Server URL keeps its host
        // while fragments, query strings and a trailing slash are dropped.
        string host = $"{Uri.UriSchemeHttps}{Uri.SchemeDelimiter}{uri.Host}";
        string owner = Uri.EscapeDataString(segments[0]);
        string repo = Uri.EscapeDataString(segments[1]);
        url = numbered
            ? $"{host}/{owner}/{repo}/issues/{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{host}/{owner}/{repo}/issues/new";
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

    public static IEnumerable<string> ExtractAttachmentUrls(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : AttachmentUrl.Matches(text).Select(match => match.Value);

    /// <summary>
    /// Parses a numbered issue URL on any host (github.com or a GitHub Enterprise Server).
    /// Fails for <c>issues/new</c>, which cannot host an upload target.
    /// </summary>
    public static bool TryParseTarget(string? issueUrl, out GitHubIssueTarget target)
    {
        target = new GitHubIssueTarget(string.Empty, string.Empty, string.Empty, 0);
        if (!TryNormalize(issueUrl, out string normalized)
            || !Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4
            || !int.TryParse(segments[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
            || number <= 0)
        {
            return false;
        }

        target = new GitHubIssueTarget(
            uri.Host,
            Uri.UnescapeDataString(segments[0]),
            Uri.UnescapeDataString(segments[1]),
            number);
        return true;
    }
}

/// <summary>
/// Host-scoped parts of a validated numbered issue plus the API roots derived from the
/// host: github.com talks to api.github.com, any other host is an enterprise server whose
/// REST API lives under <c>/api/v3</c>. Attachment URLs are served by the site host itself,
/// not by the API host.
/// </summary>
public sealed record GitHubIssueTarget(string Host, string Owner, string Repo, int IssueNumber)
{
    public string ApiBase => string.Equals(Host, "github.com", StringComparison.OrdinalIgnoreCase)
        ? "https://api.github.com"
        : $"https://{Host}/api/v3";

    public string AssetUrlBase => $"https://{Host}/user-attachments/assets/";
}
