using MyCapture.App.Editing;
using MyCapture.App.Threading;

namespace MyCapture.App.GitHub;

internal static class GitHubClipboardCommit
{
    // Compare and write under the same clipboard lock, including after the STA queue wait.
    internal static Task<bool> CopyUrlAsync(string url, uint expectedVersion) =>
        ClipboardImageService.RunCopySerializedAsync(() => StaThreadTask.RunAsync(
            () => ClipboardWriter.TrySetTextAtVersion(url, expectedVersion),
            "MyCapture attachment URL writer"));

    internal static uint GetVersion() => ClipboardWriter.GetVersion();
}
