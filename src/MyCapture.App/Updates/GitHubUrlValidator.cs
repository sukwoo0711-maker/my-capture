using System.Text.RegularExpressions;

namespace MyCapture.App.Updates;

/// <summary>
/// Validates GitHub download and redirect URLs to prevent SSRF, credential leakage,
/// HTTP downgrade, or malicious redirects to unauthorized third-party infrastructure.
/// </summary>
public static class GitHubUrlValidator
{
    private const string GitHubHost = "github.com";
    public const string ReleaseAssetsHost = "release-assets.githubusercontent.com";
    public const string ObjectsHost = "objects.githubusercontent.com";

    private static readonly Regex RepoOwnerNameRegex = new(
        @"^[a-zA-Z0-9_.-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Validates an initial release asset download URL against canonical GitHub repository path rules.
    /// Initial asset URLs MUST match canonical repo/tag/filename on github.com exactly;
    /// they MUST NOT target githubusercontent hosts.
    /// </summary>
    public static bool IsValidDownloadUri(
        Uri? uri,
        string expectedOwner,
        string expectedRepo,
        string? expectedTag = null,
        string? expectedFileName = null)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!IsSecureHttps(uri))
        {
            return false;
        }

        // Initial asset URLs MUST match canonical github.com host; never githubusercontent hosts
        if (!string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedOwner) ||
            string.IsNullOrWhiteSpace(expectedRepo) ||
            !RepoOwnerNameRegex.IsMatch(expectedOwner) ||
            !RepoOwnerNameRegex.IsMatch(expectedRepo))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedTag) && !string.IsNullOrWhiteSpace(expectedFileName))
        {
            // Canonical release download: /{owner}/{repo}/releases/download/{tag}/{filename}
            string expectedPath = $"/{expectedOwner}/{expectedRepo}/releases/download/{expectedTag}/{expectedFileName}";
            return string.Equals(uri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(expectedFileName))
        {
            string expectedPrefix = $"/{expectedOwner}/{expectedRepo}/releases/download/";
            if (!uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string remaining = uri.AbsolutePath.Substring(expectedPrefix.Length);
            string[] segments = remaining.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2)
            {
                return false;
            }

            return string.Equals(segments[1], expectedFileName, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(expectedTag))
        {
            string expectedPrefix = $"/{expectedOwner}/{expectedRepo}/releases/download/{expectedTag}/";
            return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        string defaultPrefix = $"/{expectedOwner}/{expectedRepo}/releases/download/";
        return uri.AbsolutePath.StartsWith(defaultPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempts to extract the tag and filename from a canonical GitHub download URI.
    /// </summary>
    public static bool TryExtractDownloadInfo(
        Uri? uri,
        string expectedOwner,
        string expectedRepo,
        out string? tag,
        out string? fileName)
    {
        tag = null;
        fileName = null;

        if (uri is null || !uri.IsAbsoluteUri || !IsSecureHttps(uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedOwner) ||
            string.IsNullOrWhiteSpace(expectedRepo) ||
            !RepoOwnerNameRegex.IsMatch(expectedOwner) ||
            !RepoOwnerNameRegex.IsMatch(expectedRepo))
        {
            return false;
        }

        string expectedPrefix = $"/{expectedOwner}/{expectedRepo}/releases/download/";
        if (!uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string remaining = uri.AbsolutePath.Substring(expectedPrefix.Length);
        string[] segments = remaining.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return false;
        }

        tag = segments[0];
        fileName = segments[1];
        return true;
    }

    /// <summary>
    /// Validates that a redirect URI targets only explicit authorized GitHub release asset infrastructure
    /// (release-assets.githubusercontent.com, objects.githubusercontent.com, or canonical github.com download).
    /// </summary>
    public static bool IsValidRedirectUri(
        Uri? uri,
        string? expectedOwner = null,
        string? expectedRepo = null)
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

        // Redirects only explicit release-assets.githubusercontent.com and objects.githubusercontent.com
        if (string.Equals(host, ReleaseAssetsHost, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, ObjectsHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Bounded intermediate redirect back to canonical github.com release download
        if (string.Equals(host, GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(expectedOwner) &&
                !string.IsNullOrWhiteSpace(expectedRepo) &&
                RepoOwnerNameRegex.IsMatch(expectedOwner) &&
                RepoOwnerNameRegex.IsMatch(expectedRepo))
            {
                string expectedPrefix = $"/{expectedOwner}/{expectedRepo}/releases/download/";
                return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
            }

            return uri.AbsolutePath.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Enforces HTTPS scheme on default port 443 with no userinfo.
    /// </summary>
    public static bool IsSecureHttps(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (!uri.IsDefaultPort && uri.Port != 443)
        {
            return false;
        }

        return true;
    }
}
