using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MyCapture.App.Updates;

/// <summary>
/// Production implementation of <see cref="IUpdateService"/> backed by GitHub Releases API.
/// Performs bounded HTTPS requests, canonical URL verification, SHA-256 integrity validation,
/// and isolated staging directory management without executing installers.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService
{
    private const int MaxApiByteLimit = 1024 * 1024; // 1 MiB streaming bound for API metadata
    private const int MaxRedirectHops = 5;

    private static readonly Regex RepoOwnerNameRegex = new(
        @"^[a-zA-Z0-9_.-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        _options.Validate();
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

        if (string.IsNullOrWhiteSpace(_options.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(_options.RepositoryName) ||
            !RepoOwnerNameRegex.IsMatch(_options.RepositoryOwner) ||
            !RepoOwnerNameRegex.IsMatch(_options.RepositoryName))
        {
            return UpdateCheckResult.Failed(
                UpdateErrorKind.CheckFailed,
                "Repository owner or repository name in options is invalid.",
                currentVersion);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.CheckTimeout);

        // Exact canonical GitHub Releases API endpoint only; no arbitrary API URL allowed
        string url = $"https://api.github.com/repos/{_options.RepositoryOwner}/{_options.RepositoryName}/releases/latest";
        _logger?.LogDebug("Checking latest release at {Url}", url);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"MyCapture-Updater/{currentVersion.ToNormalizedString()}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            // API HeadersRead with bounded streaming
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token).ConfigureAwait(false);

            // API final URI verification: injected HttpClient must not auto-redirect or divert API requests
            if (response.RequestMessage?.RequestUri is not null &&
                !string.Equals(response.RequestMessage.RequestUri.AbsoluteUri, url, StringComparison.OrdinalIgnoreCase))
            {
                return UpdateCheckResult.Failed(
                    UpdateErrorKind.InvalidUrl,
                    "GitHub API request was redirected or final URI does not match expected releases endpoint.",
                    currentVersion);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return UpdateCheckResult.Failed(UpdateErrorKind.RateLimited, "GitHub API rate limit exceeded.", currentVersion);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.Failed(UpdateErrorKind.CheckFailed, "Latest release was not found in the target repository.", currentVersion);
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(UpdateErrorKind.CheckFailed, $"GitHub API responded with HTTP status {(int)response.StatusCode} ({response.StatusCode}).", currentVersion);
            }

            if (response.Content.Headers.ContentLength > MaxApiByteLimit)
            {
                return UpdateCheckResult.Failed(UpdateErrorKind.PayloadTooLarge, $"Release metadata declared size exceeds limit of {MaxApiByteLimit} bytes.", currentVersion);
            }

            await using Stream rawStream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            byte[] rawBytes = await ReadBoundedBytesAsync(rawStream, MaxApiByteLimit, cts.Token).ConfigureAwait(false);

            GitHubReleaseDto? release = JsonSerializer.Deserialize<GitHubReleaseDto>(rawBytes, JsonOptions);
            if (release is null || release.Draft || release.Prerelease ||
                string.IsNullOrWhiteSpace(release.TagName) ||
                !UpdateVersion.TryParse(release.TagName, out UpdateVersion? releaseVersion) ||
                releaseVersion.Value.IsPrerelease)
            {
                return UpdateCheckResult.Failed(UpdateErrorKind.InvalidReleaseData, "Release metadata is null, draft, prerelease, or has invalid version tag.", currentVersion);
            }

            UpdateVersion latest = releaseVersion.Value;
            if (latest <= currentVersion)
            {
                _logger?.LogDebug("Current version {Current} is up to date with latest {Latest}", currentVersion, latest);
                return UpdateCheckResult.UpToDate(currentVersion, latest);
            }

            // Exact unique asset names
            string expectedSetupName = $"MyCapture-{latest.ToNormalizedString()}-win-x64-setup.exe";
            const string expectedChecksumName = "SHA256SUMS.txt";

            GitHubAssetDto? setupAsset = GetExactUniqueAsset(release.Assets, expectedSetupName, out UpdateErrorKind? setupErr, out string? setupMsg);
            if (setupAsset is null)
            {
                return UpdateCheckResult.Failed(setupErr!.Value, setupMsg!, currentVersion, latest);
            }

            GitHubAssetDto? checksumAsset = GetExactUniqueAsset(release.Assets, expectedChecksumName, out UpdateErrorKind? csErr, out string? csMsg);
            if (checksumAsset is null)
            {
                return UpdateCheckResult.Failed(csErr!.Value, csMsg!, currentVersion, latest);
            }

            // Positive bounded sizes
            if (setupAsset.Size <= 0 || setupAsset.Size > _options.MaxInstallerSizeBytes)
            {
                return UpdateCheckResult.Failed(
                    setupAsset.Size <= 0 ? UpdateErrorKind.InvalidReleaseData : UpdateErrorKind.PayloadTooLarge,
                    $"Setup asset size ({setupAsset.Size} bytes) is out of valid bounds (1 to {_options.MaxInstallerSizeBytes} bytes).",
                    currentVersion,
                    latest);
            }

            if (checksumAsset.Size <= 0 || checksumAsset.Size > _options.MaxChecksumSizeBytes)
            {
                return UpdateCheckResult.Failed(
                    checksumAsset.Size <= 0 ? UpdateErrorKind.InvalidReleaseData : UpdateErrorKind.PayloadTooLarge,
                    $"Checksum asset size ({checksumAsset.Size} bytes) is out of valid bounds (1 to {_options.MaxChecksumSizeBytes} bytes).",
                    currentVersion,
                    latest);
            }

            // Same tag association & canonical repo/tag/filename initial download URLs (not githubusercontent)
            if (!Uri.TryCreate(setupAsset.BrowserDownloadUrl, UriKind.Absolute, out Uri? setupUri) ||
                !GitHubUrlValidator.IsValidDownloadUri(setupUri, _options.RepositoryOwner, _options.RepositoryName, release.TagName, setupAsset.Name))
            {
                return UpdateCheckResult.Failed(UpdateErrorKind.InvalidUrl, $"Setup download URL '{setupAsset.BrowserDownloadUrl}' was rejected by security policy.", currentVersion, latest);
            }

            if (!Uri.TryCreate(checksumAsset.BrowserDownloadUrl, UriKind.Absolute, out Uri? checksumUri) ||
                !GitHubUrlValidator.IsValidDownloadUri(checksumUri, _options.RepositoryOwner, _options.RepositoryName, release.TagName, checksumAsset.Name))
            {
                return UpdateCheckResult.Failed(UpdateErrorKind.InvalidUrl, $"Checksum download URL '{checksumAsset.BrowserDownloadUrl}' was rejected by security policy.", currentVersion, latest);
            }

            Uri releaseWebUrl = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? htmlUri) && GitHubUrlValidator.IsSecureHttps(htmlUri)
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
                checksumDownloadUrl: checksumUri,
                releaseTag: release.TagName);

            _logger?.LogInformation("Found valid update: {Version}", latest);
            return UpdateCheckResult.Available(currentVersion, packageInfo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(UpdateErrorKind.Cancelled, "Update check was cancelled by the caller.", currentVersion);
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Failed(UpdateErrorKind.CheckFailed, $"Update check timed out after {_options.CheckTimeout.TotalSeconds:F0} seconds.", currentVersion);
        }
        catch (UpdateException ex)
        {
            return UpdateCheckResult.Failed(ex.ErrorKind, ex.Message, currentVersion);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed(UpdateErrorKind.CheckFailed, $"Error during update check: {ex.Message}", currentVersion);
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

        // Validate package filename/version BEFORE any filesystem paths
        UpdateErrorKind? validationError = ValidatePackage(package, out string? validationMessage);
        if (validationError is not null)
        {
            return StagedUpdateResult.Failed(validationError.Value, validationMessage ?? "Package validation failed.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.DownloadTimeout);

        string fullSessionDir;
        string tempInstallerPath;
        string finalInstallerPath;
        string checksumFilePath;
        try
        {
            string canonicalStagingRoot = UpdatePaths.CanonicalRoot(stagingRoot);
            Directory.CreateDirectory(canonicalStagingRoot);
            UpdateInstaller.AssertNoReparsePoints(canonicalStagingRoot);
            // No remote asset name or version text participates in filesystem paths.
            fullSessionDir = UpdatePaths.Child(canonicalStagingRoot, $"update-{Guid.NewGuid():N}");
            if (Directory.Exists(fullSessionDir) || File.Exists(fullSessionDir))
                throw new IOException("Update session already exists.");
            Directory.CreateDirectory(fullSessionDir);
            UpdateInstaller.AssertNoReparsePoints(fullSessionDir);
            string installerName = UpdatePaths.InstallerName(package.Version);
            tempInstallerPath = UpdatePaths.Child(fullSessionDir, installerName + ".downloading");
            finalInstallerPath = UpdatePaths.Child(fullSessionDir, installerName);
            checksumFilePath = UpdatePaths.Child(fullSessionDir, "SHA256SUMS.txt");
        }
        catch (Exception ex)
        {
            return StagedUpdateResult.Failed(UpdateErrorKind.StagingError, $"Unable to create a safe staging directory: {ex.Message}");
        }

        try
        {
            // 1. Download and parse SHA256SUMS.txt (byte bounded)
            progress?.Report(UpdateProgress.DownloadingChecksums());
            string checksumContent = await DownloadChecksumFileContentAsync(package.ChecksumDownloadUrl, cts.Token).ConfigureAwait(false);
            UpdateInstaller.AssertNoReparsePoints(fullSessionDir);
            await using (var checksumStream = new FileStream(checksumFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(checksumStream))
                await writer.WriteAsync(checksumContent.AsMemory(), cts.Token).ConfigureAwait(false);

            var checksumFile = Sha256ChecksumFile.Parse(checksumContent);
            if (!checksumFile.TryGetChecksum(package.SetupAssetName, out string? expectedSha256))
            {
                throw new UpdateException(UpdateErrorKind.ChecksumParseFailed, $"SHA256SUMS.txt does not contain a valid checksum entry for '{package.SetupAssetName}'.");
            }

            // 2. Download installer with async sequential IO, bounded size, progress capping, and hash check
            long bytesReceived = await DownloadInstallerStreamAsync(
                package.SetupDownloadUrl,
                tempInstallerPath,
                package.SetupSizeBytes,
                expectedSha256,
                progress,
                cts.Token).ConfigureAwait(false);

            // 3. Move verified installer to final file name
            UpdateInstaller.AssertNoReparsePoints(tempInstallerPath);
            File.Move(tempInstallerPath, finalInstallerPath);

            progress?.Report(UpdateProgress.Ready(bytesReceived));
            _logger?.LogInformation("Successfully verified and staged update ({Bytes} bytes).", bytesReceived);

            var verifiedPackage = new VerifiedUpdatePackage(
                version: package.Version,
                installerPath: finalInstallerPath,
                checksumPath: checksumFilePath,
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
        catch (Exception ex)
        {
            CleanupSessionFiles(fullSessionDir, tempInstallerPath, finalInstallerPath, checksumFilePath);
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                progress?.Report(UpdateProgress.Cancelled(0));
                return StagedUpdateResult.Failed(UpdateErrorKind.Cancelled, "Update download was cancelled by the caller.");
            }
            progress?.Report(UpdateProgress.Failed(0));
            UpdateErrorKind kind = ex switch
            {
                UpdateException ue => ue.ErrorKind,
                OperationCanceledException => UpdateErrorKind.DownloadFailed,
                _ => UpdateErrorKind.DownloadFailed
            };
            string msg = ex switch
            {
                OperationCanceledException => $"Update download timed out after {_options.DownloadTimeout.TotalMinutes:F1} minutes.",
                _ => ex.Message
            };
            return StagedUpdateResult.Failed(kind, msg);
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

        return await DownloadAndStageAsync(checkResult.PackageInfo, stagingRoot, progress, cancellationToken).ConfigureAwait(false);
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

    private static GitHubAssetDto? GetExactUniqueAsset(
        List<GitHubAssetDto>? assets,
        string expectedName,
        out UpdateErrorKind? error,
        out string? message)
    {
        error = null;
        message = null;
        var matches = assets?.Where(a => string.Equals(a.Name, expectedName, StringComparison.Ordinal)).ToList();
        if (matches is null || matches.Count == 0)
        {
            error = UpdateErrorKind.AssetNotFound;
            message = $"Required asset '{expectedName}' was not found in release assets.";
            return null;
        }
        if (matches.Count > 1)
        {
            error = UpdateErrorKind.InvalidReleaseData;
            message = $"Multiple conflicting assets named '{expectedName}' found in release.";
            return null;
        }
        return matches[0];
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        byte[] buf = new byte[8192];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new UpdateException(UpdateErrorKind.PayloadTooLarge, $"Content exceeded streaming byte limit of {maxBytes} bytes.");
            }
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    private async Task<string> DownloadChecksumFileContentAsync(Uri checksumUri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithSafeRedirectsAsync(checksumUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(UpdateErrorKind.DownloadFailed, $"Failed to download checksum file: HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength > _options.MaxChecksumSizeBytes)
        {
            throw new UpdateException(UpdateErrorKind.PayloadTooLarge, $"SHA256SUMS.txt declared size exceeds limit of {_options.MaxChecksumSizeBytes} bytes.");
        }

        // Strictly count bytes read from stream
        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedBytesAsync(contentStream, _options.MaxChecksumSizeBytes, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<long> DownloadInstallerStreamAsync(
        Uri downloadUri,
        string destinationPath,
        long expectedSizeBytes,
        string expectedSha256,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_options.BufferSizeBytes <= 0)
        {
            throw new UpdateException(UpdateErrorKind.StagingError, "Buffer size must be greater than zero.");
        }

        if (expectedSizeBytes <= 0)
        {
            throw new UpdateException(UpdateErrorKind.InvalidReleaseData, "Expected installer size must be greater than zero.");
        }

        using HttpResponseMessage response = await SendWithSafeRedirectsAsync(downloadUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(UpdateErrorKind.DownloadFailed, $"Failed to download installer package: HTTP {(int)response.StatusCode}.");
        }

        long? declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > _options.MaxInstallerSizeBytes)
        {
            throw new UpdateException(UpdateErrorKind.PayloadTooLarge, $"Installer declared length ({declaredLength.Value} bytes) exceeds limit of {_options.MaxInstallerSizeBytes} bytes.");
        }

        await using Stream networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // Async sequential file IO without WriteThrough per chunk
        UpdatePaths.AssertExistingAncestors(destinationPath);
        await using var fileStream = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: _options.BufferSizeBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[_options.BufferSizeBytes];
        long bytesReceived = 0;
        int bytesRead;

        // Cap progress to prevent UI flooding
        long lastReportTicks = Stopwatch.GetTimestamp();
        TimeSpan minReportInterval = TimeSpan.FromMilliseconds(50);
        progress?.Report(UpdateProgress.DownloadingInstaller(0, expectedSizeBytes));

        while ((bytesRead = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            bytesReceived += bytesRead;
            if (bytesReceived > _options.MaxInstallerSizeBytes)
            {
                throw new UpdateException(UpdateErrorKind.PayloadTooLarge, $"Downloaded bytes exceeded limit of {_options.MaxInstallerSizeBytes} bytes.");
            }

            sha256.AppendData(buffer, 0, bytesRead);
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);

            if (progress is not null)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(lastReportTicks, nowTicks) >= minReportInterval || bytesReceived == expectedSizeBytes)
                {
                    lastReportTicks = nowTicks;
                    progress.Report(UpdateProgress.DownloadingInstaller(bytesReceived, expectedSizeBytes));
                }
            }
        }

        // Final durable flush to physical storage
        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        fileStream.Flush(flushToDisk: true);

        // Download byte count must equal selected release size
        if (bytesReceived != expectedSizeBytes)
        {
            throw new UpdateException(UpdateErrorKind.DownloadFailed, $"Downloaded installer byte count ({bytesReceived}) does not match expected size ({expectedSizeBytes}).");
        }

        // Verify SHA-256 hash
        progress?.Report(UpdateProgress.VerifyingIntegrity(bytesReceived));
        byte[] actualHashBytes = sha256.GetHashAndReset();
        string actualHash = Convert.ToHexString(actualHashBytes).ToLowerInvariant();

        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdateErrorKind.HashMismatch, $"SHA-256 verification failed. Expected: {expectedSha256}, Actual: {actualHash}");
        }

        return bytesReceived;
    }

    private async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;

        for (int redirectCount = 0; ; redirectCount++)
        {
            bool valid = redirectCount == 0
                ? GitHubUrlValidator.IsValidDownloadUri(currentUri, _options.RepositoryOwner, _options.RepositoryName)
                : GitHubUrlValidator.IsValidRedirectUri(currentUri, _options.RepositoryOwner, _options.RepositoryName);

            if (!valid)
            {
                throw new UpdateException(UpdateErrorKind.InvalidUrl, $"URI '{currentUri}' is not an authorized download endpoint.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.UserAgent.ParseAdd("MyCapture-Updater/1.0");

            HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            // Injected client must not silently bypass redirect validation
            if (response.RequestMessage?.RequestUri is not null &&
                !string.Equals(response.RequestMessage.RequestUri.AbsoluteUri, currentUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();
                throw new UpdateException(UpdateErrorKind.InvalidUrl, "Automatic redirect detected. Injected HttpClient must not auto-redirect; all redirect hops must be validated individually.");
            }

            if (IsRedirectStatusCode(response.StatusCode))
            {
                if (redirectCount >= MaxRedirectHops)
                {
                    response.Dispose();
                    throw new UpdateException(UpdateErrorKind.DownloadFailed, $"Too many HTTP redirects encountered (limit: {MaxRedirectHops}).");
                }

                Uri? location = response.Headers.Location;
                response.Dispose();

                if (location is null)
                {
                    throw new UpdateException(UpdateErrorKind.DownloadFailed, "HTTP redirect response was missing a Location header.");
                }

                Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!GitHubUrlValidator.IsValidRedirectUri(nextUri, _options.RepositoryOwner, _options.RepositoryName))
                {
                    throw new UpdateException(UpdateErrorKind.InvalidUrl, $"Redirect to '{nextUri}' was rejected because it does not target authorized GitHub infrastructure.");
                }

                currentUri = nextUri;
                continue;
            }

            return response;
        }
    }

    private UpdateErrorKind? ValidatePackage(UpdatePackageInfo package, out string? message)
    {
        message = null;

        if (package.Version.IsPrerelease || package.Version.Major < 0 || package.Version.Minor < 0 || package.Version.Patch < 0)
        {
            message = "Package version is invalid or marked as a prerelease.";
            return UpdateErrorKind.InvalidReleaseData;
        }

        string expectedSetupName = $"MyCapture-{package.Version.ToNormalizedString()}-win-x64-setup.exe";
        if (!string.Equals(package.SetupAssetName, expectedSetupName, StringComparison.Ordinal) ||
            package.SetupAssetName.Contains('/') ||
            package.SetupAssetName.Contains('\\') ||
            package.SetupAssetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            message = $"Package setup asset name '{package.SetupAssetName}' does not match expected convention '{expectedSetupName}' or contains invalid characters.";
            return UpdateErrorKind.InvalidReleaseData;
        }

        if (package.SetupSizeBytes <= 0)
        {
            message = $"Package setup size ({package.SetupSizeBytes} bytes) must be greater than zero.";
            return UpdateErrorKind.InvalidReleaseData;
        }

        if (package.SetupSizeBytes > _options.MaxInstallerSizeBytes)
        {
            message = $"Package setup size ({package.SetupSizeBytes} bytes) is out of bounds (1 to {_options.MaxInstallerSizeBytes} bytes).";
            return UpdateErrorKind.PayloadTooLarge;
        }

        // Revalidate setup and checksum URLs against authorized repo, matching tag, and expected filenames
        if (!GitHubUrlValidator.TryExtractDownloadInfo(package.SetupDownloadUrl, _options.RepositoryOwner, _options.RepositoryName, out string? setupTag, out string? setupFile))
        {
            message = $"Package setup download URL '{package.SetupDownloadUrl}' failed security validation.";
            return UpdateErrorKind.InvalidUrl;
        }

        if (!GitHubUrlValidator.TryExtractDownloadInfo(package.ChecksumDownloadUrl, _options.RepositoryOwner, _options.RepositoryName, out string? checksumTag, out string? checksumFile))
        {
            message = $"Package checksum download URL '{package.ChecksumDownloadUrl}' failed security validation.";
            return UpdateErrorKind.InvalidUrl;
        }

        if (!string.Equals(setupFile, package.SetupAssetName, StringComparison.Ordinal))
        {
            message = $"Setup URL asset filename '{setupFile}' does not match package asset name '{package.SetupAssetName}'.";
            return UpdateErrorKind.InvalidUrl;
        }

        if (!string.Equals(checksumFile, "SHA256SUMS.txt", StringComparison.Ordinal))
        {
            message = $"Checksum URL filename '{checksumFile}' is not 'SHA256SUMS.txt'.";
            return UpdateErrorKind.InvalidUrl;
        }

        // Setup and checksum must target the exact same release tag
        if (!string.Equals(setupTag, checksumTag, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Setup asset tag '{setupTag}' does not match checksum asset tag '{checksumTag}'.";
            return UpdateErrorKind.InvalidUrl;
        }

        // URL release tag must match package version
        if (!UpdateVersion.TryParse(setupTag, out UpdateVersion? urlTagVersion) ||
            urlTagVersion.Value != package.Version)
        {
            message = $"Download URL tag '{setupTag}' does not match package version '{package.Version}'.";
            return UpdateErrorKind.InvalidReleaseData;
        }

        // Retained release tag (if present) must match URL tag and package version
        if (!string.IsNullOrWhiteSpace(package.ReleaseTag))
        {
            if (!string.Equals(setupTag, package.ReleaseTag, StringComparison.OrdinalIgnoreCase))
            {
                message = $"Download URL tag '{setupTag}' does not match package release tag '{package.ReleaseTag}'.";
                return UpdateErrorKind.InvalidReleaseData;
            }

            if (!UpdateVersion.TryParse(package.ReleaseTag, out UpdateVersion? retainedTagVersion) ||
                retainedTagVersion.Value != package.Version)
            {
                message = $"Package release tag '{package.ReleaseTag}' does not match package version '{package.Version}'.";
                return UpdateErrorKind.InvalidReleaseData;
            }
        }

        if (!GitHubUrlValidator.IsValidDownloadUri(package.SetupDownloadUrl, _options.RepositoryOwner, _options.RepositoryName, setupTag, package.SetupAssetName))
        {
            message = $"Package setup download URL '{package.SetupDownloadUrl}' failed security validation.";
            return UpdateErrorKind.InvalidUrl;
        }

        if (!GitHubUrlValidator.IsValidDownloadUri(package.ChecksumDownloadUrl, _options.RepositoryOwner, _options.RepositoryName, checksumTag, "SHA256SUMS.txt"))
        {
            message = $"Package checksum download URL '{package.ChecksumDownloadUrl}' failed security validation.";
            return UpdateErrorKind.InvalidUrl;
        }

        return null;
    }

    private static void CleanupSessionFiles(string sessionDirectory, params string?[] filePaths)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionDirectory) || !Directory.Exists(sessionDirectory))
            {
                return;
            }

            UpdateInstaller.AssertNoReparsePoints(sessionDirectory);
            var dirInfo = new DirectoryInfo(sessionDirectory);
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            foreach (string? filePath in filePaths)
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    continue;
                }

                try
                {
                    string? parentDir = Path.GetDirectoryName(filePath);
                    if (!string.Equals(parentDir, dirInfo.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fileInfo = new FileInfo(filePath);
                    if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    File.Delete(filePath);
                }
                catch
                {
                }
            }

            try
            {
                Directory.Delete(sessionDirectory, recursive: false);
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode code) =>
        code is HttpStatusCode.Moved
             or HttpStatusCode.Found
             or HttpStatusCode.SeeOther
             or HttpStatusCode.TemporaryRedirect
             or (HttpStatusCode)308;

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
