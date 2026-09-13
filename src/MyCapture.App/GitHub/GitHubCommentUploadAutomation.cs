using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using MyCapture.Core.GitHub;

namespace MyCapture.App.GitHub;

/// <summary>Uses the signed-in browser's comment draft. Never submits a comment.</summary>
internal static class GitHubCommentUploadAutomation
{
    internal static Task<string?> WaitAsync(string issueUrl, uint clipboardVersion, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // UI Automation providers can block; do not query them on the WPF dispatcher.
        return Task.Run(async () =>
        {
            var elapsed = Stopwatch.StartNew();
            AutomationElement? editor = null;
            AutomationElement? address = null;
            nint window = 0;
            string before = string.Empty;
            bool pasted = false;
            while (elapsed.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (editor is null)
                    {
                        window = GetForegroundWindow();
                        var target = FindTarget(window, issueUrl);
                        editor = target.Editor;
                        address = target.Address;
                    }

                    if (editor is not null && address is not null)
                    {
                        // A title containing "GitHub" is not proof of the destination.
                        if (GetForegroundWindow() != window || !AddressMatches(ReadText(address), issueUrl))
                        {
                            if (pasted) return null;
                            editor = null;
                            address = null;
                        }
                        else if (!pasted)
                        {
                            if (GetClipboardSequenceNumber() != clipboardVersion) return null;
                            before = ReadText(editor);
                            if (HasPendingUpload(before)) return null;
                            if (editor.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object scroll))
                                ((ScrollItemPattern)scroll).ScrollIntoView();
                            editor.SetFocus();
                            if (GetForegroundWindow() != window || !SameElement(editor, AutomationElement.FocusedElement)
                                || !AddressMatches(ReadText(address), issueUrl)
                                || GetClipboardSequenceNumber() != clipboardVersion) return null;
                            SendControlV();
                            pasted = true;
                        }
                        else if (TryExtractNewUrl(before, ReadText(editor), out string url))
                        {
                            // Preserve a clipboard change made by the user while uploading.
                            return GetClipboardSequenceNumber() == clipboardVersion ? url : null;
                        }
                    }
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
                {
                    // Re-rendered draft after paste: fail without duplicating the upload.
                    if (pasted) return null;
                    editor = null;
                    address = null;
                }
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }, cancellationToken);
    }

    internal static bool AddressMatches(string value, string issueUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string raw = value.Trim();
        if (raw.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)) raw = "https://" + raw;
        return GitHubIssueImageUrl.TryNormalize(raw, out string normalized)
            && string.Equals(normalized, issueUrl, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryExtractNewUrl(string before, string after, out string url)
    {
        url = string.Empty;
        if (HasPendingUpload(before) || HasPendingUpload(after)) return false;
        var old = GitHubIssueImageUrl.ExtractAttachmentUrls(before).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] added = GitHubIssueImageUrl.ExtractAttachmentUrls(after)
            .Where(candidate => !old.Contains(candidate)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        url = added.Length == 1 ? added[0] : string.Empty;
        return added.Length == 1; // Never return an older image already present in the draft.
    }

    internal static bool HasPendingUpload(string text) =>
        text.Contains("Uploading", StringComparison.OrdinalIgnoreCase)
        || text.Contains("upload in progress", StringComparison.OrdinalIgnoreCase)
        || text.Contains("업로드 중", StringComparison.OrdinalIgnoreCase)
        || text.Contains("업로드중", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCommentIdentity(string id, string name, bool newIssue)
    {
        if (id.Equals("new_comment_field", StringComparison.OrdinalIgnoreCase)) return true;
        string[] labels = ["Add a comment", "Comment", "Comment body", "Add your comment here...", "Leave a comment",
            "댓글 추가", "댓글 본문", "코멘트 추가"];
        if (labels.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase)) return true;
        if (!newIssue) return false;
        return id.Equals("issue_body", StringComparison.OrdinalIgnoreCase)
            || new[] { "Add a description", "Description", "Body", "설명 추가", "설명", "본문" }
                .Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static (AutomationElement? Address, AutomationElement? Editor) FindTarget(nint hwnd, string issueUrl)
    {
        if (hwnd == 0) return default;
        GetWindowThreadProcessId(hwnd, out uint pid);
        using Process process = Process.GetProcessById((int)pid);
        if (!new[] { "chrome", "msedge", "firefox", "brave", "vivaldi", "opera" }
            .Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return default;
        AutomationElement window = AutomationElement.FromHandle(hwnd);
        AutomationElement? address = null;
        // Browser chrome only. A URL appearing in page content must not authorize a paste.
        foreach (AutomationElement toolbar in window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar)))
        {
            if (InsideDocument(toolbar)) continue;
            foreach (AutomationElement input in toolbar.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
            {
                if (AddressMatches(ReadText(input), issueUrl)) { address = input; break; }
            }
            if (address is not null) break;
        }
        if (address is null) return default;
        bool newIssue = issueUrl.EndsWith("/new", StringComparison.OrdinalIgnoreCase);
        var candidates = new List<AutomationElement>();
        foreach (AutomationElement input in window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
        {
            if (!InsideDocument(input) || !input.Current.IsEnabled || !input.Current.IsKeyboardFocusable
                || !IsCommentIdentity(input.Current.AutomationId, input.Current.Name, newIssue)) continue;
            if (input.TryGetCurrentPattern(ValuePattern.Pattern, out object value) && ((ValuePattern)value).Current.IsReadOnly) continue;
            candidates.Add(input);
        }
        AutomationElement? focused = AutomationElement.FocusedElement;
        AutomationElement? candidate = candidates.FirstOrDefault(input => SameElement(input, focused));
        candidate ??= candidates.Count == 1 ? candidates[0] : null;
        return (address, candidate);
    }

    private static bool InsideDocument(AutomationElement element)
    {
        for (AutomationElement? parent = TreeWalker.ControlViewWalker.GetParent(element);
            parent is not null; parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            if (parent.Current.ControlType == ControlType.Document) return true;
            if (parent.Current.ControlType == ControlType.Window) break;
        }
        return false;
    }

    private static bool SameElement(AutomationElement a, AutomationElement? b) =>
        b is not null && a.GetRuntimeId().SequenceEqual(b.GetRuntimeId());

    private static string ReadText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object value)) return ((ValuePattern)value).Current.Value ?? string.Empty;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out object text)) return ((TextPattern)text).DocumentRange.GetText(-1) ?? string.Empty;
        return string.Empty;
    }

    private static void SendControlV()
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        try { keybd_event(0x56, 0, 0, UIntPtr.Zero); }
        finally
        {
            keybd_event(0x56, 0, 0x0002, UIntPtr.Zero);
            keybd_event(0x11, 0, 0x0002, UIntPtr.Zero);
        }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
