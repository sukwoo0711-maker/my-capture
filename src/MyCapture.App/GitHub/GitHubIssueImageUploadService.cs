using System.Diagnostics;


using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.Core.GitHub;
using MyCapture.Core.Settings;

namespace MyCapture.App.GitHub;

internal enum GitHubIssueImageUploadStatus
{
    CopiedUrl,
    NeedImage,
    InvalidUrl,
    Failed,
    Busy,
}

internal sealed record GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus Status, string? Url = null);

/// <summary>
/// Opens the configured GitHub issue, pastes the clipboard image into the Write box,
/// waits for a user-attachments URL, and copies that URL.
/// </summary>
internal sealed class GitHubIssueImageUploadService
{
    private readonly Func<AppSettings> _settings;
    private readonly Func<BitmapSource, Task<bool>> _copyImageAsync;
    private readonly Func<string, uint, Task<bool>> _copyTextAsync;
    private readonly Func<string, bool> _openUrl;
    private readonly Func<string, uint, TimeSpan, CancellationToken, Task<string?>> _waitForAttachment;
    private readonly Func<uint> _clipboardVersion;
    private readonly Func<CancellationToken, Task<BitmapSource?>> _readClipboardImage;
    private readonly ILogger _log;
    private bool _inFlight;

    internal GitHubIssueImageUploadService(
        Func<AppSettings> settings,
        ILogger log)
        : this(
            settings,
            ClipboardImageService.CopyImageAsync,
            GitHubClipboardCommit.CopyUrlAsync,
            OpenDefaultBrowser,
            GitHubCommentUploadAutomation.WaitAsync,
            ReadClipboardImageAsync,
            log,
            GitHubClipboardCommit.GetVersion)
    {
    }

    internal GitHubIssueImageUploadService(
        Func<AppSettings> settings,
        Func<BitmapSource, Task<bool>> copyImageAsync,
        Func<string, uint, Task<bool>> copyTextAsync,
        Func<string, bool> openUrl,
        Func<string, uint, TimeSpan, CancellationToken, Task<string?>> waitForAttachment,
        Func<CancellationToken, Task<BitmapSource?>> readClipboardImage,
        ILogger log,
        Func<uint> clipboardVersion)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _copyImageAsync = copyImageAsync ?? throw new ArgumentNullException(nameof(copyImageAsync));
        _copyTextAsync = copyTextAsync ?? throw new ArgumentNullException(nameof(copyTextAsync));
        _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        _waitForAttachment = waitForAttachment ?? throw new ArgumentNullException(nameof(waitForAttachment));
        _readClipboardImage = readClipboardImage ?? throw new ArgumentNullException(nameof(readClipboardImage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clipboardVersion = clipboardVersion ?? throw new ArgumentNullException(nameof(clipboardVersion));
    }

    internal async Task<GitHubIssueImageUploadResult> UploadClipboardImageAsync(
        CancellationToken cancellationToken = default)
    {
        if (_inFlight)
        {
            return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.Busy);
        }

        _inFlight = true;
        try
        {
            if (!GitHubIssueImageUrl.TryNormalize(_settings().GitHub.IssueUrl, out string issueUrl))
            {
                return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.InvalidUrl);
            }

            BitmapSource? image = await _readClipboardImage(cancellationToken);
            if (image is null)
            {
                return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.NeedImage);
            }

            if (!await _copyImageAsync(image))
            {
                return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.Failed);
            }

            uint clipboardVersion = _clipboardVersion();
            if (!_openUrl(issueUrl))
            {
                return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.Failed);
            }

            string? extracted = await _waitForAttachment(issueUrl, clipboardVersion, TimeSpan.FromSeconds(60), cancellationToken);
            if (string.IsNullOrWhiteSpace(extracted)
                || !GitHubIssueImageUrl.TryExtractAttachmentUrl(extracted, out string url))
            {
                return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.Failed);
            }

            if (!await _copyTextAsync(url, clipboardVersion))
            {
                return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.Failed, url);
            }

            _log.LogInformation("Copied GitHub attachment URL");
            return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.CopiedUrl, url);
        }
        catch (OperationCanceledException)
        {
            return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.Failed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub image URL upload failed");
            return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.Failed);
        }
        finally
        {
            _inFlight = false;
        }
    }

    private static async Task<BitmapSource?> ReadClipboardImageAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Pinning.ClipboardImageReader.PinReadAttempt read = await Pinning.ClipboardImageReader.ReadPinAsync();
        return read.Content?.Image;
    }

    private static bool OpenDefaultBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

}
