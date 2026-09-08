using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MyCapture.App.Updates;

/// <summary>
/// Production implementation of <see cref="IUpdateService"/> backed by GitHub Releases API.
/// Performs bounded HTTPS requests, canonical URL verification, SHA-256 integrity validation,
/// and isolated staging directory management without executing installers.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly UpdateServiceOptions _options;
    private readonly ILogger<GitHubUpdateService>? _logger;
    private bool _disposed;

    public GitHubUpdateService(
        HttpClient? httpClient = null,
        UpdateServiceOptions? options = null,
        ILogger<GitHubUpdateService>? logger = null)
    {
        _options = options ?? new UpdateServiceOptions();
        _logger = logger;

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            };
            _httpClient = new HttpClient(handler);
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        UpdateVersion currentVersion,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.CheckTimeout);

        string url = $"{_options.ApiBaseUrl.TrimEnd('/')}/repos/{_options.RepositoryOwner}/{_options.RepositoryName}/releases/latest";
        _logger?.LogDebug("Checking latest release at {Url}", url);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"MyCapture-Updater/{currentVersion.ToNormalizedString()}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _logger?.LogWarning("GitHub API rate limit exceeded when checking updates");
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.RateLimited,
                    "GitHub API rate limit exceeded.",
                    currentVersion);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger?.LogWarning("GitHub release not found at {Url}", url);
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.CheckFailed,
                    "Latest release was not found in the target repository.",
                    currentVersion);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("GitHub release check failed with status {StatusCode}", response.StatusCode);
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.CheckFailed,
                    $"GitHub API responded with HTTP status {(int)response.StatusCode} ({response.StatusCode}).",
                    currentVersion);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            GitHubReleaseDto? release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(
                stream,
                JsonOptions,
                cts.Token).ConfigureAwait(false);

            if (release is null)
            {
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.InvalidReleaseData,
                    "Failed to deserialize GitHub release JSON.",
                    currentVersion);
            }

            if (release.Draft)
            {
                _logger?.LogInformation("Latest release is marked as draft; ignoring");
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.InvalidReleaseData,
                    "Latest release is marked as a draft and cannot be used for updates.",
                    currentVersion);
            }

            if (release.Prerelease)
            {
                _logger?.LogInformation("Latest release is marked as prerelease; ignoring");
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.InvalidReleaseData,
                    "Latest release is marked as a prerelease and cannot be used for stable updates.",
                    currentVersion);
            }

            if (string.IsNullOrWhiteSpace(release.TagName) ||
                !UpdateVersion.TryParse(release.TagName, out UpdateVersion? releaseVersion) ||
                releaseVersion.Value.IsPrerelease)
            {
                _logger?.LogWarning("Release tag '{TagName}' is invalid or prerelease", release.TagName);
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.InvalidReleaseData,
                    $"Release version tag '{release.TagName}' is missing, malformed, or a prerelease.",
                    currentVersion);
            }

            UpdateVersion latest = releaseVersion.Value;
            if (latest <= currentVersion)
            {
                _logger?.LogDebug("Current version {Current} is up to date with latest {Latest}", currentVersion, latest);
                return UpdateCheckResult.UpToDate(currentVersion, latest);
            }

            // Select exact setup asset: MyCapture-{VERSION}-win-x64-setup.exe
            string expectedSetupName = $"MyCapture-{latest.ToNormalizedString()}-win-x64-setup.exe";
            GitHubAssetDto? setupAsset = release.Assets?.FirstOrDefault(
                a => string.Equals(a.Name, expectedSetupName, StringComparison.OrdinalIgnoreCase));

            if (setupAsset is null && !string.IsNullOrWhiteSpace(release.TagName))
            {
                // Fallback check in case the asset name used the raw tag name (e.g. v-prefixed)
                setupAsset = release.Assets?.FirstOrDefault(
                    a => string.Equals(a.Name, $"MyCapture-{release.TagName.Trim()}-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase));
            }

            if (setupAsset is null)
            {
                _logger?.LogWarning("Setup executable {ExpectedName} missing from release assets", expectedSetupName);
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.AssetNotFound,
                    $"Required setup asset '{expectedSetupName}' was not found in release assets.",
                    currentVersion,
                    latest);
            }

            // Select checksum asset: SHA256SUMS.txt
            GitHubAssetDto? checksumAsset = release.Assets?.FirstOrDefault(
                a => string.Equals(a.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

            if (checksumAsset is null)
            {
                _logger?.LogWarning("SHA256SUMS.txt missing from release assets");
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.AssetNotFound,
                    "Required checksum asset 'SHA256SUMS.txt' was not found in release assets.",
                    currentVersion,
                    latest);
            }

            // Validate download URLs
            if (!Uri.TryCreate(setupAsset.BrowserDownloadUrl, UriKind.Absolute, out Uri? setupUri) ||
                !GitHubUrlValidator.IsValidDownloadUri(setupUri, _options.RepositoryOwner, _options.RepositoryName))
            {
                _logger?.LogWarning("Setup download URL is not a canonical GitHub URL: {Url}", setupAsset.BrowserDownloadUrl);
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.InvalidUrl,
                    $"Setup download URL '{setupAsset.BrowserDownloadUrl}' was rejected by security policy.",
                    currentVersion,
                    latest);
            }

            if (!Uri.TryCreate(checksumAsset.BrowserDownloadUrl, UriKind.Absolute, out Uri? checksumUri) ||
                !GitHubUrlValidator.IsValidDownloadUri(checksumUri, _options.RepositoryOwner, _options.RepositoryName))
            {
                _logger?.LogWarning("Checksum download URL is not a canonical GitHub URL: {Url}", checksumAsset.BrowserDownloadUrl);
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.InvalidUrl,
                    $"Checksum download URL '{checksumAsset.BrowserDownloadUrl}' was rejected by security policy.",
                    currentVersion,
                    latest);
            }

            if (setupAsset.Size > _options.MaxInstallerSizeBytes)
            {
                _logger?.LogWarning("Setup asset size {Size} exceeds limit {Limit}", setupAsset.Size, _options.MaxInstallerSizeBytes);
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.PayloadTooLarge,
                    $"Setup asset size ({setupAsset.Size} bytes) exceeds safety limit of {_options.MaxInstallerSizeBytes} bytes.",
                    currentVersion,
                    latest);
            }

            Uri releaseWebUrl = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? htmlUri)
                ? htmlUri
                : new Uri($"https://github.com/{_options.RepositoryOwner}/{_options.RepositoryName}/releases/tag/{release.TagName}");

            var packageInfo = new UpdatePackageInfo(
                version: latest,
                releaseTitle: release.Name ?? $"MyCapture {latest}",
                releaseNotes: release.Body ?? string.Empty,
                releaseUrl: releaseWebUrl,
                publishedAt: release.PublishedAt ?? DateTimeOffset.UtcNow,
                setupAssetName: setupAsset.Name ?? expectedSetupName,
                setupDownloadUrl: setupUri,
                setupSizeBytes: setupAsset.Size,
                checksumDownloadUrl: checksumUri);

            _logger?.LogInformation("Found valid update: {Version}", latest);
            return UpdateCheckResult.Available(currentVersion, packageInfo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(
                UpdateErrorKind.Cancelled,
                "Update check was cancelled by the caller.",
                currentVersion);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Update check timed out after {Timeout}", _options.CheckTimeout);
            return UpdateCheckResult.Failed(
                UpdateErrorKind.CheckFailed,
                $"Update check timed out after {_options.CheckTimeout.TotalSeconds:F0} seconds.",
                currentVersion);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Network error during update check");
            return UpdateCheckResult.Failed(
                UpdateErrorKind.CheckFailed,
                $"Network error while contacting release service: {ex.Message}",
                currentVersion);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error during update check");
            return UpdateCheckResult.Failed(
                UpdateErrorKind.CheckFailed,
                $"Unexpected error during update check: {ex.Message}",
                currentVersion);
        }
    }

    /// <inheritdoc />
    public Task<UpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        return CheckForUpdateAsync(UpdateVersion.FromVersion(currentVersion), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StagedUpdateResult> DownloadAndStageAsync(
        UpdatePackageInfo package,
        string stagingRoot,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.DownloadTimeout);

        string canonicalStagingRoot = Path.GetFullPath(stagingRoot);
        try
        {
            Directory.CreateDirectory(canonicalStagingRoot);
        }
        catch (Exception ex)
        {
            return StagedUpdateResult.Failed(
                UpdateErrorKind.StagingError,
                $"Unable to access or create staging root directory: {ex.Message}");
        }

        // Generate unique isolated session directory
        string sessionDirName = $"update-{package.Version.ToNormalizedString()}-{Guid.NewGuid():N}";
        string sessionDirectory = Path.Combine(canonicalStagingRoot, sessionDirName);
        string fullSessionDir = Path.GetFullPath(sessionDirectory);

        // Security check: ensure session directory stays strictly within staging root
        string requiredPrefix = canonicalStagingRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
        if (!fullSessionDir.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return StagedUpdateResult.Failed(
                UpdateErrorKind.StagingError,
                "Staging directory traversal was detected and blocked.");
        }

        try
        {
            Directory.CreateDirectory(fullSessionDir);
        }
        catch (Exception ex)
        {
            return StagedUpdateResult.Failed(
                UpdateErrorKind.StagingError,
                $"Failed to create update session directory: {ex.Message}");
        }

        string tempInstallerPath = Path.Combine(fullSessionDir, $"{package.SetupAssetName}.downloading");
        string finalInstallerPath = Path.Combine(fullSessionDir, package.SetupAssetName);
        string checksumFilePath = Path.Combine(fullSessionDir, "SHA256SUMS.txt");

        try
        {
            // 1. Download and parse SHA256SUMS.txt
            progress?.Report(UpdateProgress.DownloadingChecksums());
            _logger?.LogDebug("Downloading checksums from {Url}", package.ChecksumDownloadUrl);

            string checksumContent = await DownloadChecksumFileContentAsync(
                package.ChecksumDownloadUrl,
                cts.Token).ConfigureAwait(false);

            await File.WriteAllTextAsync(checksumFilePath, checksumContent, cts.Token).ConfigureAwait(false);

            var checksumFile = Sha256ChecksumFile.Parse(checksumContent);
            if (!checksumFile.TryGetChecksum(package.SetupAssetName, out string? expectedSha256))
            {
                throw new UpdateException(
                    UpdateErrorKind.ChecksumParseFailed,
                    $"SHA256SUMS.txt does not contain a valid checksum entry for '{package.SetupAssetName}'.");
            }

            _logger?.LogDebug("Expected SHA256 for {Asset}: {Hash}", package.SetupAssetName, expectedSha256);

            // 2. Download installer with streaming SHA-256 computation and progress
            progress?.Report(UpdateProgress.DownloadingInstaller(0, package.SetupSizeBytes));
            _logger?.LogInformation("Downloading installer to {Path}", tempInstallerPath);

            long bytesReceived = await DownloadInstallerStreamAsync(
                package.SetupDownloadUrl,
                tempInstallerPath,
                package.SetupSizeBytes,
                expectedSha256,
                progress,
                cts.Token).ConfigureAwait(false);

            // 3. Move verified installer to its final usable file name
            if (File.Exists(finalInstallerPath))
            {
                File.Delete(finalInstallerPath);
            }
            File.Move(tempInstallerPath, finalInstallerPath);

            progress?.Report(UpdateProgress.Ready(bytesReceived));
            _logger?.LogInformation("Successfully verified and staged update: {Path}", finalInstallerPath);

            var verifiedPackage = new VerifiedUpdatePackage(
                version: package.Version,
                installerPath: finalInstallerPath,
                expectedSha256: expectedSha256,
                actualSha256: expectedSha256,
                fileSizeBytes: bytesReceived,
                stagingDirectory: fullSessionDir,
                releaseTitle: package.ReleaseTitle,
                releaseNotes: package.ReleaseNotes,
                releaseUrl: package.ReleaseUrl,
                publishedAt: package.PublishedAt);

            return StagedUpdateResult.Success(verifiedPackage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("Update download was cancelled by caller");
            SafeCleanupSession(fullSessionDir, canonicalStagingRoot);
            progress?.Report(UpdateProgress.Cancelled(0));
            return StagedUpdateResult.Failed(
                UpdateErrorKind.Cancelled,
                "Update download was cancelled by the caller.");
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Update download timed out after {Timeout}", _options.DownloadTimeout);
            SafeCleanupSession(fullSessionDir, canonicalStagingRoot);
            progress?.Report(UpdateProgress.Failed(0));
            return StagedUpdateResult.Failed(
                UpdateErrorKind.DownloadFailed,
                $"Update download timed out after {_options.DownloadTimeout.TotalMinutes:F1} minutes.");
        }
        catch (UpdateException ex)
        {
            _logger?.LogWarning(ex, "Update error: {Message}", ex.Message);
            SafeCleanupSession(fullSessionDir, canonicalStagingRoot);
            progress?.Report(UpdateProgress.Failed(0));
            return StagedUpdateResult.Failed(ex.ErrorKind, ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected download failure");
            SafeCleanupSession(fullSessionDir, canonicalStagingRoot);
            progress?.Report(UpdateProgress.Failed(0));
            return StagedUpdateResult.Failed(
                UpdateErrorKind.DownloadFailed,
                $"Download failure: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<StagedUpdateResult> CheckAndStageAsync(
        UpdateVersion currentVersion,
        string stagingRoot,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(UpdateProgress.Checking());
        var checkResult = await CheckForUpdateAsync(currentVersion, cancellationToken).ConfigureAwait(false);

        if (!checkResult.IsUpdateAvailable || checkResult.PackageInfo is null)
        {
            UpdateErrorKind kind = checkResult.ErrorKind == UpdateErrorKind.None
                ? UpdateErrorKind.AlreadyUpToDate
                : checkResult.ErrorKind;
            return StagedUpdateResult.Failed(kind, checkResult.ErrorMessage ?? "Application is already up to date.");
        }

        return await DownloadAndStageAsync(
            checkResult.PackageInfo,
            stagingRoot,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<StagedUpdateResult> CheckAndStageAsync(
        Version currentVersion,
        string stagingRoot,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        return CheckAndStageAsync(UpdateVersion.FromVersion(currentVersion), stagingRoot, progress, cancellationToken);
    }

    private async Task<string> DownloadChecksumFileContentAsync(
        Uri checksumUri,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithSafeRedirectsAsync(
            checksumUri,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(
                UpdateErrorKind.DownloadFailed,
                $"Failed to download checksum file: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        if (response.Content.Headers.ContentLength > _options.MaxChecksumSizeBytes)
        {
            throw new UpdateException(
                UpdateErrorKind.PayloadTooLarge,
                $"SHA256SUMS.txt declared size exceeds maximum allowed of {_options.MaxChecksumSizeBytes} bytes.");
        }

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(contentStream);

        // Read up to limit
        char[] buffer = new char[4096];
        int totalCharsRead = 0;
        int maxChars = (int)Math.Min(_options.MaxChecksumSizeBytes, int.MaxValue);
        var sb = new System.Text.StringBuilder();

        int charsRead;
        while ((charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalCharsRead += charsRead;
            if (totalCharsRead > maxChars)
            {
                throw new UpdateException(
                    UpdateErrorKind.PayloadTooLarge,
                    "SHA256SUMS.txt content exceeded size limit.");
            }
            sb.Append(buffer, 0, charsRead);
        }

        return sb.ToString();
    }

    private async Task<long> DownloadInstallerStreamAsync(
        Uri downloadUri,
        string destinationPath,
        long expectedSizeBytes,
        string expectedSha256,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithSafeRedirectsAsync(
            downloadUri,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(
                UpdateErrorKind.DownloadFailed,
                $"Failed to download installer package: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        long? declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > _options.MaxInstallerSizeBytes)
        {
            throw new UpdateException(
                UpdateErrorKind.PayloadTooLarge,
                $"Installer declared length ({declaredLength.Value} bytes) exceeds maximum limit of {_options.MaxInstallerSizeBytes} bytes.");
        }

        long totalExpected = declaredLength ?? (expectedSizeBytes > 0 ? expectedSizeBytes : -1);

        await using Stream networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: _options.BufferSizeBytes,
            FileOptions.WriteThrough);

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[_options.BufferSizeBytes];
        long bytesReceived = 0;
        int bytesRead;

        while ((bytesRead = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            bytesReceived += bytesRead;
            if (bytesReceived > _options.MaxInstallerSizeBytes)
            {
                throw new UpdateException(
                    UpdateErrorKind.PayloadTooLarge,
                    $"Downloaded bytes exceeded maximum safety limit of {_options.MaxInstallerSizeBytes} bytes.");
            }

            sha256.AppendData(buffer, 0, bytesRead);
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);

            progress?.Report(UpdateProgress.DownloadingInstaller(
                bytesReceived,
                totalExpected > 0 ? totalExpected : null));
        }

        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        fileStream.Flush(flushToDisk: true);

        // Verify SHA-256 hash
        progress?.Report(UpdateProgress.VerifyingIntegrity(bytesReceived));
        byte[] actualHashBytes = sha256.GetHashAndReset();
        string actualHash = Convert.ToHexString(actualHashBytes).ToLowerInvariant();

        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(
                UpdateErrorKind.HashMismatch,
                $"SHA-256 integrity verification failed for downloaded installer. Expected: {expectedSha256}, Actual: {actualHash}");
        }

        return bytesReceived;
    }

    private async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;
        int redirectCount = 0;
        const int maxRedirects = 5;

        while (true)
        {
            if (!GitHubUrlValidator.IsValidDownloadUri(currentUri, _options.RepositoryOwner, _options.RepositoryName))
            {
                throw new UpdateException(
                    UpdateErrorKind.InvalidUrl,
                    $"URI '{currentUri}' is not an authorized GitHub download endpoint.");
            }

            var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.UserAgent.ParseAdd("MyCapture-Updater/1.0");

            HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (IsRedirectStatusCode(response.StatusCode))
            {
                redirectCount++;
                if (redirectCount > maxRedirects)
                {
                    response.Dispose();
                    throw new UpdateException(
                        UpdateErrorKind.DownloadFailed,
                        "Too many HTTP redirects encountered while downloading asset.");
                }

                Uri? location = response.Headers.Location;
                response.Dispose();

                if (location is null)
                {
                    throw new UpdateException(
                        UpdateErrorKind.DownloadFailed,
                        "HTTP redirect response was missing a Location header.");
                }

                Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!GitHubUrlValidator.IsValidRedirectUri(nextUri, _options.RepositoryOwner, _options.RepositoryName))
                {
                    throw new UpdateException(
                        UpdateErrorKind.InvalidUrl,
                        $"Redirect to '{nextUri}' was rejected because it does not target authorized GitHub infrastructure.");
                }

                currentUri = nextUri;
                continue;
            }

            // Verify final URI after automatic redirect (if HttpClient handler handled redirect internally)
            Uri finalUri = response.RequestMessage?.RequestUri ?? currentUri;
            if (!GitHubUrlValidator.IsValidRedirectUri(finalUri, _options.RepositoryOwner, _options.RepositoryName))
            {
                response.Dispose();
                throw new UpdateException(
                    UpdateErrorKind.InvalidUrl,
                    $"Final download URI '{finalUri}' after automatic redirect is not authorized.");
            }

            return response;
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode code) =>
        code is HttpStatusCode.Moved
             or HttpStatusCode.Found
             or HttpStatusCode.SeeOther
             or HttpStatusCode.TemporaryRedirect
             or (HttpStatusCode)308;

    /// <summary>
    /// Safely cleans up the session directory, ensuring no deletion touches outside the session dir.
    /// </summary>
    public static void SafeCleanupSession(string sessionDirectory, string stagingRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionDirectory) || string.IsNullOrWhiteSpace(stagingRoot))
            {
                return;
            }

            string fullSession = Path.GetFullPath(sessionDirectory);
            string fullStaging = Path.GetFullPath(stagingRoot);

            string prefix = fullStaging.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;

            // Strict path containment check
            if (!fullSession.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!Directory.Exists(fullSession))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(fullSession, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                }
            }

            try
            {
                Directory.Delete(fullSession, recursive: false);
            }
            catch (Exception)
            {
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Cleans up stale update session directories in the staging root older than the specified age.
    /// </summary>
    public static int CleanUpStaleUpdateDirectories(string stagingRoot, TimeSpan? maxAge = null)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot) || !Directory.Exists(stagingRoot))
        {
            return 0;
        }

        TimeSpan threshold = maxAge ?? TimeSpan.FromHours(24);
        DateTime cutoff = DateTime.UtcNow - threshold;
        int cleanedCount = 0;

        try
        {
            string canonicalRoot = Path.GetFullPath(stagingRoot);
            foreach (string dir in Directory.EnumerateDirectories(canonicalRoot, "update-*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    DateTime created = Directory.GetCreationTimeUtc(dir);
                    if (created < cutoff)
                    {
                        SafeCleanupSession(dir, canonicalRoot);
                        cleanedCount++;
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        return cleanedCount;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
