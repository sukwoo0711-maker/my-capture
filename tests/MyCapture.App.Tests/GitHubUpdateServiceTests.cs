using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyCapture.App.Updates;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Unit tests for GitHubUpdateService covering version resolution, metadata validation,
/// secure redirect handling, streaming payload bounds, cryptographic verification,
/// cross-tag isolation, options validation, and directory preservation during cleanup.
/// </summary>
public sealed class GitHubUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdate_WhenUpToDate_ReturnsAlreadyUpToDate()
    {
        var current = new UpdateVersion(1, 8, 0);
        string json = CreateReleaseJson("v1.8.0");
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.AlreadyUpToDate, result.ErrorKind);
        Assert.Null(result.PackageInfo);
        Assert.Equal(current, result.CurrentVersion);
        Assert.Equal(current, result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdate_WhenCurrentNewer_ReturnsAlreadyUpToDate()
    {
        var current = new UpdateVersion(1, 9, 0);
        string json = CreateReleaseJson("v1.8.0");
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.AlreadyUpToDate, result.ErrorKind);
    }

    [Fact]
    public async Task CheckForUpdate_WhenNewerStable_ReturnsAvailableWithPackageInfo()
    {
        var current = new UpdateVersion(1, 7, 0);
        string json = CreateReleaseJson("v1.8.0");
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.None, result.ErrorKind);
        Assert.NotNull(result.PackageInfo);
        Assert.Equal(new UpdateVersion(1, 8, 0), result.PackageInfo.Version);
        Assert.Equal("v1.8.0", result.PackageInfo.ReleaseTag);
        Assert.Equal("MyCapture-1.8.0-win-x64-setup.exe", result.PackageInfo.SetupAssetName);
    }

    [Theory]
    [InlineData(true, false, "v1.8.0")]      // Draft release
    [InlineData(false, true, "v1.8.0")]      // Prerelease flag on release
    [InlineData(false, false, "v1.8.0-rc1")] // Prerelease version tag
    public async Task CheckForUpdate_WhenDraftOrPrerelease_ReturnsInvalidReleaseData(bool draft, bool prerelease, string tag)
    {
        var current = new UpdateVersion(1, 7, 0);
        string json = CreateReleaseJson(tag, draft: draft, prerelease: prerelease);
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.InvalidReleaseData, result.ErrorKind);
    }

    [Fact]
    public async Task CheckForUpdate_WhenMalformedTag_ReturnsInvalidReleaseData()
    {
        var current = new UpdateVersion(1, 7, 0);
        string json = CreateReleaseJson("not-a-valid-version-tag");
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.InvalidReleaseData, result.ErrorKind);
    }

    [Fact]
    public async Task CheckForUpdate_WhenDuplicateSetupAsset_ReturnsInvalidReleaseData()
    {
        var current = new UpdateVersion(1, 7, 0);
        string json = CreateReleaseJson("v1.8.0", duplicateSetup: true);
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.InvalidReleaseData, result.ErrorKind);
    }

    [Fact]
    public async Task CheckForUpdate_WhenDuplicateChecksumAsset_ReturnsInvalidReleaseData()
    {
        var current = new UpdateVersion(1, 7, 0);
        string json = CreateReleaseJson("v1.8.0", duplicateChecksum: true);
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.InvalidReleaseData, result.ErrorKind);
    }

    [Fact]
    public async Task CheckForUpdate_WhenSetupAssetMissing_ReturnsAssetNotFound()
    {
        var current = new UpdateVersion(1, 7, 0);
        string json = CreateReleaseJson("v1.8.0", customSetupName: "Unexpected-Installer.exe");
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.AssetNotFound, result.ErrorKind);
    }

    [Fact]
    public async Task CheckForUpdate_WhenSetupUrlIsForeignRepo_ReturnsInvalidUrl()
    {
        var current = new UpdateVersion(1, 7, 0);
        string foreignUrl = "https://github.com/evil-attacker/evil-repo/releases/download/v1.8.0/MyCapture-1.8.0-win-x64-setup.exe";
        string json = CreateReleaseJson("v1.8.0", setupUrl: foreignUrl);
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.InvalidUrl, result.ErrorKind);
    }

    [Fact]
    public async Task CheckForUpdate_WhenSetupUrlTargetsCdnDirectly_ReturnsInvalidUrl()
    {
        var current = new UpdateVersion(1, 7, 0);
        string cdnUrl = "https://objects.githubusercontent.com/github-production-release-asset/MyCapture-1.8.0-win-x64-setup.exe";
        string json = CreateReleaseJson("v1.8.0", setupUrl: cdnUrl);
        using var httpClient = CreateMockClient(_ => CreateStringResponse(HttpStatusCode.OK, json));
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.InvalidUrl, result.ErrorKind);
    }

    [Fact]
    public async Task CheckForUpdate_WhenApiFinalUriDiverted_ReturnsInvalidUrl()
    {
        var current = new UpdateVersion(1, 7, 0);
        string json = CreateReleaseJson("v1.8.0");
        using var httpClient = CreateMockClient(_ =>
        {
            var resp = CreateStringResponse(HttpStatusCode.OK, json);
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.com/diverted/releases/latest");
            return resp;
        });
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.InvalidUrl, result.ErrorKind);
    }

    [Fact]
    public async Task CheckForUpdate_WhenApiPayloadExceedsOneMib_ReturnsPayloadTooLarge()
    {
        var current = new UpdateVersion(1, 7, 0);
        byte[] oversized = new byte[1024 * 1024 + 16];
        Array.Fill(oversized, (byte)' ');
        using var httpClient = CreateMockClient(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(oversized)
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return resp;
        });
        using var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync(current);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(UpdateErrorKind.PayloadTooLarge, result.ErrorKind);
    }

    [Fact]
    public async Task DownloadAndStage_WhenChecksumStreamOversized_ReturnsPayloadTooLarge()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            var version = new UpdateVersion(1, 8, 0);
            var package = CreateValidPackage(version);
            var options = new UpdateServiceOptions { MaxChecksumSizeBytes = 256 };

            byte[] oversizedChecksum = new byte[512];
            Array.Fill(oversizedChecksum, (byte)'#');

            using var httpClient = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(oversizedChecksum)
            });

            using var service = new GitHubUpdateService(httpClient, options);
            var result = await service.DownloadAndStageAsync(package, stagingRoot);

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorKind.PayloadTooLarge, result.ErrorKind);
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task DownloadAndStage_WhenHashMismatch_ReturnsHashMismatch()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            var version = new UpdateVersion(1, 8, 0);
            byte[] installerBytes = Encoding.UTF8.GetBytes("Fake installer binary content");
            string wrongHash = "0000000000000000000000000000000000000000000000000000000000000000";
            string setupName = "MyCapture-1.8.0-win-x64-setup.exe";
            string checksumContent = $"{wrongHash}  {setupName}\n";

            var package = CreateValidPackage(version, setupSize: installerBytes.Length);

            using var httpClient = CreateMockClient(req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateStringResponse(HttpStatusCode.OK, checksumContent);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installerBytes)
                };
            });

            using var service = new GitHubUpdateService(httpClient);
            var result = await service.DownloadAndStageAsync(package, stagingRoot);

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorKind.HashMismatch, result.ErrorKind);
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task DownloadAndStage_WhenDownloadSizeMismatch_ReturnsDownloadFailed()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            var version = new UpdateVersion(1, 8, 0);
            byte[] installerBytes = Encoding.UTF8.GetBytes("Short payload");
            string hash = ComputeSha256(installerBytes);
            string setupName = "MyCapture-1.8.0-win-x64-setup.exe";
            string checksumContent = $"{hash}  {setupName}\n";

            // Declare size 1000, but deliver fewer bytes
            var package = CreateValidPackage(version, setupSize: 1000);

            using var httpClient = CreateMockClient(req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateStringResponse(HttpStatusCode.OK, checksumContent);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installerBytes)
                };
            });

            using var service = new GitHubUpdateService(httpClient);
            var result = await service.DownloadAndStageAsync(package, stagingRoot);

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorKind.DownloadFailed, result.ErrorKind);
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task DownloadAndStage_WhenCrossTagMismatch_RejectedBeforeFilesystem()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            var version = new UpdateVersion(1, 8, 0);
            string setupName = "MyCapture-1.8.0-win-x64-setup.exe";
            var setupUrl = new Uri($"https://github.com/sukwoo0711-maker/my-capture/releases/download/v1.8.0/{setupName}");
            // Mismatched checksum URL tag v1.7.0 vs setup URL tag v1.8.0
            var checksumUrl = new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/download/v1.7.0/SHA256SUMS.txt");

            var package = new UpdatePackageInfo(
                version: version,
                releaseTitle: "Test",
                releaseNotes: "",
                releaseUrl: new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/tag/v1.8.0"),
                publishedAt: DateTimeOffset.UtcNow,
                setupAssetName: setupName,
                setupDownloadUrl: setupUrl,
                setupSizeBytes: 50,
                checksumDownloadUrl: checksumUrl,
                releaseTag: "v1.8.0");

            using var httpClient = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var service = new GitHubUpdateService(httpClient);

            var result = await service.DownloadAndStageAsync(package, stagingRoot);

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorKind.InvalidUrl, result.ErrorKind);
            Assert.Empty(Directory.GetDirectories(stagingRoot));
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task DownloadAndStage_WhenFilenameMismatch_RejectedBeforeFilesystem()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            var version = new UpdateVersion(1, 8, 0);
            string setupName = "MyCapture-1.8.0-win-x64-setup.exe";
            // Setup URL path ends with foreign.exe instead of setupName
            var setupUrl = new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/download/v1.8.0/foreign.exe");
            var checksumUrl = new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/download/v1.8.0/SHA256SUMS.txt");

            var package = new UpdatePackageInfo(
                version: version,
                releaseTitle: "Test",
                releaseNotes: "",
                releaseUrl: new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/tag/v1.8.0"),
                publishedAt: DateTimeOffset.UtcNow,
                setupAssetName: setupName,
                setupDownloadUrl: setupUrl,
                setupSizeBytes: 50,
                checksumDownloadUrl: checksumUrl,
                releaseTag: "v1.8.0");

            using var httpClient = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var service = new GitHubUpdateService(httpClient);

            var result = await service.DownloadAndStageAsync(package, stagingRoot);

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorKind.InvalidUrl, result.ErrorKind);
            Assert.Empty(Directory.GetDirectories(stagingRoot));
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task DownloadAndStage_WhenTagVersionMismatch_RejectedBeforeFilesystem()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            var version = new UpdateVersion(1, 8, 0);
            string setupName = "MyCapture-1.8.0-win-x64-setup.exe";
            // Tag v2.0.0 does not match package version 1.8.0
            var setupUrl = new Uri($"https://github.com/sukwoo0711-maker/my-capture/releases/download/v2.0.0/{setupName}");
            var checksumUrl = new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/download/v2.0.0/SHA256SUMS.txt");

            var package = new UpdatePackageInfo(
                version: version,
                releaseTitle: "Test",
                releaseNotes: "",
                releaseUrl: new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/tag/v2.0.0"),
                publishedAt: DateTimeOffset.UtcNow,
                setupAssetName: setupName,
                setupDownloadUrl: setupUrl,
                setupSizeBytes: 50,
                checksumDownloadUrl: checksumUrl,
                releaseTag: "v2.0.0");

            using var httpClient = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var service = new GitHubUpdateService(httpClient);

            var result = await service.DownloadAndStageAsync(package, stagingRoot);

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorKind.InvalidReleaseData, result.ErrorKind);
            Assert.Empty(Directory.GetDirectories(stagingRoot));
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task DownloadAndStage_WhenAllowedCdnRedirect_Succeeds()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            var version = new UpdateVersion(1, 8, 0);
            byte[] installerBytes = Encoding.UTF8.GetBytes("Synthetic installer content for CDN redirect test");
            string hash = ComputeSha256(installerBytes);
            string setupName = "MyCapture-1.8.0-win-x64-setup.exe";
            string checksumContent = $"{hash}  {setupName}\n";

            var package = CreateValidPackage(version, setupSize: installerBytes.Length);

            using var httpClient = CreateMockClient(req =>
            {
                if (req.RequestUri!.Host == "github.com" && req.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt"))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://objects.githubusercontent.com/checksums/SHA256SUMS.txt");
                    return redirect;
                }
                if (req.RequestUri.Host == "objects.githubusercontent.com" && req.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt"))
                {
                    return CreateStringResponse(HttpStatusCode.OK, checksumContent);
                }
                if (req.RequestUri.Host == "github.com" && req.RequestUri.AbsolutePath.EndsWith(setupName))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri($"https://objects.githubusercontent.com/assets/{setupName}");
                    return redirect;
                }
                if (req.RequestUri.Host == "objects.githubusercontent.com" && req.RequestUri.AbsolutePath.EndsWith(setupName))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(installerBytes)
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var service = new GitHubUpdateService(httpClient);
            var result = await service.DownloadAndStageAsync(package, stagingRoot);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.VerifiedPackage);
            Assert.True(File.Exists(result.VerifiedPackage.InstallerPath));
            Assert.True(File.Exists(result.VerifiedPackage.ChecksumPath));
            Assert.Equal(hash, result.VerifiedPackage.ActualSha256);

            // Verify safe package cleanup
            result.VerifiedPackage.Cleanup();
            Assert.False(File.Exists(result.VerifiedPackage.InstallerPath));
            Assert.False(File.Exists(result.VerifiedPackage.ChecksumPath));
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task DownloadAndStage_WhenHttpDowngradeRedirect_ReturnsInvalidUrl()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            var version = new UpdateVersion(1, 8, 0);
            var package = CreateValidPackage(version);

            using var httpClient = CreateMockClient(req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt"))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("http://objects.githubusercontent.com/insecure/SHA256SUMS.txt");
                    return redirect;
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            using var service = new GitHubUpdateService(httpClient);
            var result = await service.DownloadAndStageAsync(package, stagingRoot);

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorKind.InvalidUrl, result.ErrorKind);
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task DownloadAndStage_WhenInjectedClientAutoRedirects_ReturnsInvalidUrl()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            var version = new UpdateVersion(1, 8, 0);
            var package = CreateValidPackage(version);

            using var httpClient = CreateMockClient(req =>
            {
                var resp = CreateStringResponse(HttpStatusCode.OK, "content");
                resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://objects.githubusercontent.com/silently-redirected");
                return resp;
            });

            using var service = new GitHubUpdateService(httpClient);
            var result = await service.DownloadAndStageAsync(package, stagingRoot);

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorKind.InvalidUrl, result.ErrorKind);
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task DownloadAndStage_WhenCancelled_CleansUpSessionFilesAndPreservesUnrelatedFiles()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            string unrelatedFile = Path.Combine(stagingRoot, "unrelated-user-file.txt");
            await File.WriteAllTextAsync(unrelatedFile, "Critical user data that must not be deleted");

            var version = new UpdateVersion(1, 8, 0);
            byte[] installerBytes = Encoding.UTF8.GetBytes("Installer bytes");
            string hash = ComputeSha256(installerBytes);
            string setupName = "MyCapture-1.8.0-win-x64-setup.exe";
            string checksumContent = $"{hash}  {setupName}\n";
            var package = CreateValidPackage(version, setupSize: installerBytes.Length);

            using var cts = new CancellationTokenSource();

            using var httpClient = CreateMockClient(req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateStringResponse(HttpStatusCode.OK, checksumContent);
                }

                // Trigger cancellation when installer download request begins
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

            using var service = new GitHubUpdateService(httpClient);
            var result = await service.DownloadAndStageAsync(package, stagingRoot, cancellationToken: cts.Token);

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorKind.Cancelled, result.ErrorKind);

            // Unrelated file in staging root must be preserved
            Assert.True(File.Exists(unrelatedFile), "Unrelated file in staging root must be preserved.");
            Assert.Equal("Critical user data that must not be deleted", await File.ReadAllTextAsync(unrelatedFile));
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Fact]
    public void VerifiedPackage_Cleanup_PreservesUnrelatedFilesInSessionDirectory()
    {
        string stagingRoot = CreateTempStagingDir();
        try
        {
            string sessionDir = Path.Combine(stagingRoot, "session-dir");
            Directory.CreateDirectory(sessionDir);

            string installerPath = Path.Combine(sessionDir, "setup.exe");
            string checksumPath = Path.Combine(sessionDir, "SHA256SUMS.txt");
            string unrelatedPath = Path.Combine(sessionDir, "unrelated-log.txt");

            File.WriteAllText(installerPath, "exe content");
            File.WriteAllText(checksumPath, "checksum content");
            File.WriteAllText(unrelatedPath, "unrelated session log");

            var verifiedPackage = new VerifiedUpdatePackage(
                version: new UpdateVersion(1, 8, 0),
                installerPath: installerPath,
                checksumPath: checksumPath,
                expectedSha256: "abc",
                actualSha256: "abc",
                fileSizeBytes: 11,
                stagingDirectory: sessionDir,
                releaseTitle: "Test",
                releaseNotes: "Notes",
                releaseUrl: new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/tag/v1.8.0"),
                publishedAt: DateTimeOffset.UtcNow);

            verifiedPackage.Cleanup();

            // Owned files must be deleted
            Assert.False(File.Exists(installerPath), "Installer file should be cleaned up.");
            Assert.False(File.Exists(checksumPath), "Checksum file should be cleaned up.");

            // Unrelated file and session directory must be preserved
            Assert.True(File.Exists(unrelatedPath), "Unrelated file in session dir must be preserved.");
            Assert.True(Directory.Exists(sessionDir), "Session directory containing unrelated files must not be deleted.");
        }
        finally
        {
            DeleteDirectory(stagingRoot);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_BufferSizeBytes_RejectsNonPositive(int bufferSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateServiceOptions { BufferSizeBytes = bufferSize });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_MaxInstallerSizeBytes_RejectsNonPositive(long size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateServiceOptions { MaxInstallerSizeBytes = size });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_MaxChecksumSizeBytes_RejectsNonPositive(long size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateServiceOptions { MaxChecksumSizeBytes = size });
    }

    [Fact]
    public void Options_Timeouts_RejectNonPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateServiceOptions { CheckTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateServiceOptions { DownloadTimeout = TimeSpan.FromSeconds(-1) });
    }

    // --- Helper fixtures ---

    private static string CreateReleaseJson(
        string tagName = "v1.8.0",
        bool draft = false,
        bool prerelease = false,
        string? setupUrl = null,
        string? checksumUrl = null,
        long setupSize = 100,
        long checksumSize = 100,
        bool duplicateSetup = false,
        bool duplicateChecksum = false,
        string? customSetupName = null,
        string? customChecksumName = null)
    {
        string normalized = UpdateVersion.TryParse(tagName, out var v) ? v.Value.ToNormalizedString() : tagName;
        string setupName = customSetupName ?? $"MyCapture-{normalized}-win-x64-setup.exe";
        string checksumName = customChecksumName ?? "SHA256SUMS.txt";
        string defaultSetupUrl = $"https://github.com/sukwoo0711-maker/my-capture/releases/download/{tagName}/{setupName}";
        string defaultChecksumUrl = $"https://github.com/sukwoo0711-maker/my-capture/releases/download/{tagName}/{checksumName}";

        var assets = new List<object>
        {
            new
            {
                name = setupName,
                size = setupSize,
                browser_download_url = setupUrl ?? defaultSetupUrl,
                state = "uploaded"
            },
            new
            {
                name = checksumName,
                size = checksumSize,
                browser_download_url = checksumUrl ?? defaultChecksumUrl,
                state = "uploaded"
            }
        };

        if (duplicateSetup)
        {
            assets.Add(new
            {
                name = setupName,
                size = setupSize,
                browser_download_url = setupUrl ?? defaultSetupUrl,
                state = "uploaded"
            });
        }

        if (duplicateChecksum)
        {
            assets.Add(new
            {
                name = checksumName,
                size = checksumSize,
                browser_download_url = checksumUrl ?? defaultChecksumUrl,
                state = "uploaded"
            });
        }

        var releaseDto = new
        {
            tag_name = tagName,
            name = $"MyCapture {tagName}",
            draft = draft,
            prerelease = prerelease,
            published_at = DateTimeOffset.UtcNow,
            html_url = $"https://github.com/sukwoo0711-maker/my-capture/releases/tag/{tagName}",
            body = "Release notes",
            assets = assets
        };

        return JsonSerializer.Serialize(releaseDto);
    }

    private static UpdatePackageInfo CreateValidPackage(UpdateVersion version, long setupSize = 50, string? releaseTag = null)
    {
        string tag = releaseTag ?? $"v{version.ToNormalizedString()}";
        string setupName = $"MyCapture-{version.ToNormalizedString()}-win-x64-setup.exe";
        var setupUrl = new Uri($"https://github.com/sukwoo0711-maker/my-capture/releases/download/{tag}/{setupName}");
        var checksumUrl = new Uri($"https://github.com/sukwoo0711-maker/my-capture/releases/download/{tag}/SHA256SUMS.txt");
        var releaseUrl = new Uri($"https://github.com/sukwoo0711-maker/my-capture/releases/tag/{tag}");

        return new UpdatePackageInfo(
            version: version,
            releaseTitle: $"MyCapture {version}",
            releaseNotes: "Notes",
            releaseUrl: releaseUrl,
            publishedAt: DateTimeOffset.UtcNow,
            setupAssetName: setupName,
            setupDownloadUrl: setupUrl,
            setupSizeBytes: setupSize,
            checksumDownloadUrl: checksumUrl,
            releaseTag: tag);
    }

    private static HttpClient CreateMockClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        return new HttpClient(handler);
    }

    private static HttpResponseMessage CreateStringResponse(HttpStatusCode statusCode, string content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private static string ComputeSha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateTempStagingDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "mycapture-updater-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task CheckForUpdate_UnknownLengthApiPayloadIsStillBounded()
    {
        using var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[1024 * 1024 + 1])
        });
        using var service = new GitHubUpdateService(client);
        var result = await service.CheckForUpdateAsync(new UpdateVersion(1, 7, 0));
        Assert.Equal(UpdateErrorKind.PayloadTooLarge, result.ErrorKind);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response = _responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
