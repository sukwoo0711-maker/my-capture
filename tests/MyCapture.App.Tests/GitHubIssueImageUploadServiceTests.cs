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
            (_, _) => Task.FromResult(true),
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
            _ => Task.FromResult(new ClipboardImageSnapshot(image, "")),
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
            (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true),
            _ => true,
            (_, _, _, _) => Task.FromResult<string?>("https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9"),
            _ => Task.FromResult(new ClipboardImageSnapshot(null, "")),
            NullLogger.Instance, () => 7u);

        GitHubIssueImageUploadResult result = await service.UploadClipboardImageAsync();
        Assert.Equal(GitHubIssueImageUploadStatus.NeedImage, result.Status);
    }

    [Fact]
    public async Task Upload_InvalidUrl_WhenSettingsRejectTheIssue()
    {
        var settings = new AppSettings();
        settings.GitHub.IssueUrl = "http://github.com/a/b/issues/1"; // HTTP is rejected.
        var service = new GitHubIssueImageUploadService(
            () => settings,
            (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true),
            _ => true,
            (_, _, _, _) => Task.FromResult<string?>(null),
            _ => Task.FromResult(new ClipboardImageSnapshot(FrozenPixel(), "")),
            NullLogger.Instance, () => 7u);

        GitHubIssueImageUploadResult result = await service.UploadClipboardImageAsync();
        Assert.Equal(GitHubIssueImageUploadStatus.InvalidUrl, result.Status);
    }

    [Fact]
    public async Task Upload_AcceptsEnterpriseServerIssueUrl()
    {
        var settings = new AppSettings();
        settings.GitHub.IssueUrl = "https://ghe.example.corp/internal/app/issues/42";
        var service = new GitHubIssueImageUploadService(
            () => settings,
            (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true),
            _ => true,
            (_, _, _, _) => Task.FromResult<string?>("https://ghe.example.corp/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9"),
            _ => Task.FromResult(new ClipboardImageSnapshot(FrozenPixel(), "")),
            NullLogger.Instance, () => 7u);

        GitHubIssueImageUploadResult result = await service.UploadClipboardImageAsync();
        Assert.Equal(GitHubIssueImageUploadStatus.CopiedUrl, result.Status);
        Assert.Equal("https://ghe.example.corp/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9", result.Url);
    }

    [Fact]
    public async Task Upload_CopiesOriginLinkAfterTheAttachmentUrl()
    {
        const string origin = "https://example.com/docs/page";
        const string attachment = "https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9";
        string? copiedText = null;
        string? recopiedOrigin = null;
        var service = new GitHubIssueImageUploadService(
            () => new AppSettings(),
            (_, sourceUrl) =>
            {
                recopiedOrigin = sourceUrl;
                return Task.FromResult(true);
            },
            (text, _) =>
            {
                copiedText = text;
                return Task.FromResult(true);
            },
            _ => true,
            (_, _, _, _) => Task.FromResult<string?>(attachment),
            _ => Task.FromResult(new ClipboardImageSnapshot(FrozenPixel(), origin)),
            NullLogger.Instance,
            () => 7u);

        GitHubIssueImageUploadResult result = await service.UploadClipboardImageAsync();

        Assert.Equal(GitHubIssueImageUploadStatus.CopiedUrl, result.Status);
        Assert.Equal(origin, recopiedOrigin);
        Assert.Equal(attachment + "\n\n" + origin, copiedText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void Compose_OmitsAnOriginThatIsNotAPageUrl(string? origin)
    {
        const string attachment = "https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9";
        Assert.Equal(attachment, GitHubUploadText.Compose(attachment, origin));
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
            () => new AppSettings(), (_, _) => Task.FromResult(true),
            (_, version) => { committedVersion = version; return Task.FromResult(version == currentVersion); },
            _ => { if (changeWhileOpening) currentVersion++; return true; },
            (_, version, _, _) =>
            {
                awaitedVersion = version;
                if (!changeWhileOpening) currentVersion++; // User copies between automation and final commit.
                return Task.FromResult<string?>(url);
            },
            _ => Task.FromResult(new ClipboardImageSnapshot(FrozenPixel(), "")), NullLogger.Instance, () => currentVersion);

        var result = await service.UploadClipboardImageAsync();
        Assert.Equal(7u, awaitedVersion);
        Assert.Equal(7u, committedVersion);
        Assert.Equal(GitHubIssueImageUploadStatus.Failed, result.Status);
    }
}
