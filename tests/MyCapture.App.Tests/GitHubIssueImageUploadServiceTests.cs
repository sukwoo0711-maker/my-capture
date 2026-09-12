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
        var settings = new AppSettings();

        var service = new GitHubIssueImageUploadService(
            () => settings,
            _ => Task.FromResult(true),
            text =>
            {
                copiedText = text;
                return Task.FromResult(true);
            },
            url =>
            {
                opened = url;
                return true;
            },
            (_, _) => Task.FromResult<string?>(
                """<img src="https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9"/>"""),
            _ => Task.FromResult<BitmapSource?>(image),
            NullLogger.Instance);

        GitHubIssueImageUploadResult result = await service.UploadClipboardImageAsync();

        Assert.Equal(GitHubIssueImageUploadStatus.CopiedUrl, result.Status);
        Assert.Equal("https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9", result.Url);
        Assert.Equal(result.Url, copiedText);
        Assert.Equal(GitHubIssueImageUrl.DefaultIssueUrl, opened);
    }

    [Fact]
    public async Task Upload_NeedImage_WhenClipboardHasNoBitmap()
    {
        var service = new GitHubIssueImageUploadService(
            () => new AppSettings(),
            _ => Task.FromResult(true),
            _ => Task.FromResult(true),
            _ => true,
            (_, _) => Task.FromResult<string?>("https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9"),
            _ => Task.FromResult<BitmapSource?>(null),
            NullLogger.Instance);

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
            _ => Task.FromResult(true),
            _ => true,
            (_, _) => Task.FromResult<string?>(null),
            _ => Task.FromResult<BitmapSource?>(FrozenPixel()),
            NullLogger.Instance);

        GitHubIssueImageUploadResult result = await service.UploadClipboardImageAsync();
        Assert.Equal(GitHubIssueImageUploadStatus.InvalidUrl, result.Status);
    }

    private static BitmapSource FrozenPixel()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return image;
    }
}
