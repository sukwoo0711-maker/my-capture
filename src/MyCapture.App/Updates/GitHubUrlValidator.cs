namespace MyCapture.App.Updates;

/// <summary>
/// Validates GitHub download and redirect URLs to prevent SSRF, credential leakage,
/// HTTP downgrade, or malicious redirects to unauthorized third-party infrastructure.
/// </summary>
public static class GitHubUrlValidator
{
    private const string GitHubHost = "github.com";
    private const string GitHubApiHost = "api.github.com";
    private const string GitHubUserContentSuffix = ".githubusercontent.com";
    private const string ObjectsCdnHost = "objects.githubusercontent.com";
    private const string ReleasesCdnHost = "github-releases.githubusercontent.com";

    /// <summary>
    /// Validates an initial release asset download URL against canonical GitHub repository path rules.
    /// </summary>
    public static bool IsValidDownloadUri(Uri? uri, string expectedOwner, string expectedRepo)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!IsSecureHttps(uri))
        {
            return false;
        }

        string host = uri.Host;

        // Canonical release download: https://github.com/{owner}/{repo}/releases/download/...
        if (string.Equals(host, GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            string path = uri.AbsolutePath;
            string expectedPrefix = $"/{expectedOwner}/{expectedRepo}/releases/download/";
            return path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // Canonical API asset download: https://api.github.com/repos/{owner}/{repo}/releases/assets/...
        if (string.Equals(host, GitHubApiHost, StringComparison.OrdinalIgnoreCase))
        {
            string path = uri.AbsolutePath;
            string expectedPrefix = $"/repos/{expectedOwner}/{expectedRepo}/releases/assets/";
            return path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // Legitimate redirect CDNs are acceptable for intermediate or final destinations
        if (IsLegitimateGitHubCdnHost(host))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates that a redirect URI targets only legitimate GitHub release asset storage.
    /// </summary>
    public static bool IsValidRedirectUri(Uri? uri, string? expectedOwner = null, string? expectedRepo = null)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!IsSecureHttps(uri))
        {
            return false;
        }

        string host = uri.Host;

        if (string.Equals(host, GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(expectedOwner) && !string.IsNullOrEmpty(expectedRepo))
            {
                string expectedPrefix = $"/{expectedOwner}/{expectedRepo}/releases/download/";
                return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
            }

            return uri.AbsolutePath.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(host, GitHubApiHost, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(expectedOwner) && !string.IsNullOrEmpty(expectedRepo))
            {
                string expectedPrefix = $"/repos/{expectedOwner}/{expectedRepo}/releases/assets/";
                return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
            }

            return uri.AbsolutePath.Contains("/releases/assets/", StringComparison.OrdinalIgnoreCase);
        }

        return IsLegitimateGitHubCdnHost(host);
    }

    /// <summary>
    /// Checks whether the host is legitimate GitHub release CDN infrastructure (*.githubusercontent.com).
    /// </summary>
    public static bool IsLegitimateGitHubCdnHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, ObjectsCdnHost, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, ReleasesCdnHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for legitimate single-level subdomains: <subdomain>.githubusercontent.com
        if (host.EndsWith(GitHubUserContentSuffix, StringComparison.OrdinalIgnoreCase))
        {
            int prefixLength = host.Length - GitHubUserContentSuffix.Length;
            if (prefixLength <= 0)
            {
                return false;
            }

            string subdomain = host.Substring(0, prefixLength);
            // Must be non-empty, single DNS label, containing only alphanumeric and hyphen
            return !subdomain.Contains('.') &&
                   !subdomain.Contains('/') &&
                   !subdomain.Contains('\\') &&
                   subdomain.All(c => char.IsLetterOrDigit(c) || c == '-');
        }

        return false;
    }

    private static bool IsSecureHttps(Uri uri)
    {
        // Enforce HTTPS
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reject user-info (e.g. https://user:pass@host)
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        // Enforce default HTTPS port
        if (!uri.IsDefaultPort && uri.Port != 443)
        {
            return false;
        }

        return true;
    }
}
