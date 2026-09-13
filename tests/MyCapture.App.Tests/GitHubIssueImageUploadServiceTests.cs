using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.GitHub;
using MyCapture.Core.GitHub;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GitHubIssueImageUploadServiceTests
{
    [Fact]
    public async Task Upload_CopiesExtractedUserAttachmentsUrl()
    {
        var image = FrozenPixel();
        string? copiedText = null;
        string? opened = null;
        string? awaitedIssue = null;
        var settings = new AppSettings();

        var service = new GitHubIssueImageUploadService(
            () => settings,
            _ => Task.FromResult(true),
            (text, _) =>
            {
                copiedText = text;
                return Task.FromResult(true);
            },
            url =>
            {
                opened = url;
                return true;
            },
            (issue, _, _, _) =>
            {
                awaitedIssue = issue;
                return Task.FromResult<string?>(
                    """<img src="https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9"/>""");
            },
            _ => Task.FromResult<BitmapSource?>(image),
            NullLogger.Instance, () => 7u);

        GitHubIssueImageUploadResult result = await service.UploadClipboardImageAsync();

        Assert.Equal(GitHubIssueImageUploadStatus.CopiedUrl, result.Status);
        Assert.Equal("https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9", result.Url);
        Assert.Equal(result.Url, copiedText);
        Assert.Equal(GitHubIssueImageUrl.DefaultIssueUrl, opened);
        Assert.Equal(opened, awaitedIssue);
    }

    [Fact]
    public async Task Upload_NeedImage_WhenClipboardHasNoBitmap()
    {
        var service = new GitHubIssueImageUploadService(
            () => new AppSettings(),
            _ => Task.FromResult(true),
            (_, _) => Task.FromResult(true),
            _ => true,
            (_, _, _, _) => Task.FromResult<string?>("https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9"),
            _ => Task.FromResult<BitmapSource?>(null),
            NullLogger.Instance, () => 7u);

        GitHubIssueImageUploadResult result = await service.UploadClipboardImageAsync();
        Assert.Equal(GitHubIssueImageUploadStatus.NeedImage, result.Status);
    }

    [Fact]
    public async Task Upload_InvalidUrl_WhenSettingsRejectTheIssue()
    {
        var settings = new AppSettings();
        settings.GitHub.IssueUrl = "https://evil.com/a/b/issues/1";
        var service = new GitHubIssueImageUploadService(
            () => settings,
            _ => Task.FromResult(true),
            (_, _) => Task.FromResult(true),
            _ => true,
            (_, _, _, _) => Task.FromResult<string?>(null),
            _ => Task.FromResult<BitmapSource?>(FrozenPixel()),
            NullLogger.Instance, () => 7u);

        GitHubIssueImageUploadResult result = await service.UploadClipboardImageAsync();
        Assert.Equal(GitHubIssueImageUploadStatus.InvalidUrl, result.Status);
    }

    private static BitmapSource FrozenPixel()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return image;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadCarriesOriginalClipboardVersionThroughBrowserAndFinalWrite(bool changeWhileOpening)
    {
        uint currentVersion = 7;
        uint? awaitedVersion = null;
        uint? committedVersion = null;
        const string url = "https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9";
        var service = new GitHubIssueImageUploadService(
            () => new AppSettings(), _ => Task.FromResult(true),
            (_, version) => { committedVersion = version; return Task.FromResult(version == currentVersion); },
            _ => { if (changeWhileOpening) currentVersion++; return true; },
            (_, version, _, _) =>
            {
                awaitedVersion = version;
                if (!changeWhileOpening) currentVersion++; // User copies between automation and final commit.
                return Task.FromResult<string?>(url);
            },
            _ => Task.FromResult<BitmapSource?>(FrozenPixel()), NullLogger.Instance, () => currentVersion);

        var result = await service.UploadClipboardImageAsync();
        Assert.Equal(7u, awaitedVersion);
        Assert.Equal(7u, committedVersion);
        Assert.Equal(GitHubIssueImageUploadStatus.Failed, result.Status);
    }
}
