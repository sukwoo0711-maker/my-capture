using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
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
    private readonly Func<string, Task<bool>> _copyTextAsync;
    private readonly Func<string, bool> _openUrl;
    private readonly Func<TimeSpan, CancellationToken, Task<string?>> _waitForAttachment;
    private readonly Func<CancellationToken, Task<BitmapSource?>> _readClipboardImage;
    private readonly ILogger _log;
    private bool _inFlight;

    internal GitHubIssueImageUploadService(
        Func<AppSettings> settings,
        ILogger log)
        : this(
            settings,
            ClipboardImageService.CopyImageAsync,
            ClipboardImageService.CopyTextAsync,
            OpenDefaultBrowser,
            WaitForAttachmentInForegroundAsync,
            ReadClipboardImageAsync,
            log)
    {
    }

    internal GitHubIssueImageUploadService(
        Func<AppSettings> settings,
        Func<BitmapSource, Task<bool>> copyImageAsync,
        Func<string, Task<bool>> copyTextAsync,
        Func<string, bool> openUrl,
        Func<TimeSpan, CancellationToken, Task<string?>> waitForAttachment,
        Func<CancellationToken, Task<BitmapSource?>> readClipboardImage,
        ILogger log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _copyImageAsync = copyImageAsync ?? throw new ArgumentNullException(nameof(copyImageAsync));
        _copyTextAsync = copyTextAsync ?? throw new ArgumentNullException(nameof(copyTextAsync));
        _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        _waitForAttachment = waitForAttachment ?? throw new ArgumentNullException(nameof(waitForAttachment));
        _readClipboardImage = readClipboardImage ?? throw new ArgumentNullException(nameof(readClipboardImage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
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

            if (!_openUrl(issueUrl))
            {
                return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.Failed);
            }

            string? extracted = await _waitForAttachment(TimeSpan.FromSeconds(45), cancellationToken);
            if (string.IsNullOrWhiteSpace(extracted)
                || !GitHubIssueImageUrl.TryExtractAttachmentUrl(extracted, out string url))
            {
                return new GitHubIssueImageUploadResult(GitHubIssueImageUploadStatus.Failed);
            }

            if (!await _copyTextAsync(url))
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

    internal static async Task<string?> WaitForAttachmentInForegroundAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        bool pasted = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                AutomationElement? edit = FindGitHubWriteBox();
                if (edit is not null)
                {
                    if (!pasted)
                    {
                        edit.SetFocus();
                        SendControlV();
                        pasted = true;
                    }

                    string text = ReadAutomationText(edit);
                    if (GitHubIssueImageUrl.TryExtractAttachmentUrl(text, out string url))
                    {
                        return url;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(true);
        }

        return null;
    }

    private static AutomationElement? FindGitHubWriteBox()
    {
        AutomationElement root = AutomationElement.RootElement;
        var window = root.FindFirst(
            TreeScope.Children,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.NameProperty, "GitHub", PropertyConditionFlags.IgnoreCase)));
        window ??= FindWindowContaining(root, "github.com");
        if (window is null)
        {
            return null;
        }

        foreach (string name in new[] { "Comment", "Write", "Add a comment", "댓글", "코멘트" })
        {
            AutomationElement? named = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name, PropertyConditionFlags.IgnoreCase));
            if (named is not null && IsEditable(named))
            {
                return named;
            }
        }

        return window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
    }

    private static AutomationElement? FindWindowContaining(AutomationElement root, string fragment)
    {
        AutomationElementCollection windows = root.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        foreach (AutomationElement window in windows)
        {
            string name = window.Current.Name ?? string.Empty;
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                || name.Contains("GitHub", StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }

        return null;
    }

    private static bool IsEditable(AutomationElement element)
    {
        ControlType type = element.Current.ControlType;
        return type == ControlType.Edit || type == ControlType.Document;
    }

    private static string ReadAutomationText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? value) && value is ValuePattern typed)
        {
            return typed.Current.Value ?? string.Empty;
        }

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? text) && text is TextPattern range)
        {
            return range.DocumentRange.GetText(-1) ?? string.Empty;
        }

        return element.Current.Name ?? string.Empty;
    }

    private static void SendControlV()
    {
        const byte vkControl = 0x11;
        const byte vkV = 0x56;
        const uint keyUp = 0x0002;
        keybd_event(vkControl, 0, 0, UIntPtr.Zero);
        keybd_event(vkV, 0, 0, UIntPtr.Zero);
        keybd_event(vkV, 0, keyUp, UIntPtr.Zero);
        keybd_event(vkControl, 0, keyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
