using MyCapture.App.Threading;
using System.Windows;

namespace MyCapture.App.Pinning;

/// <summary>Initializes WPF text layout during startup, before the first clipboard shortcut.</summary>
internal static class PinRenderWarmup
{
    private static readonly Lazy<Task> Warmup = new(() => StaThreadTask.RunAsync(() =>
    {
        // Fixed public text only. No clipboard, profile, browser, model or network access.
        const string source = "# 문서\n\n내용을 **확인**합니다.\n\n- [x] 준비\n- [ ] 검토\n\n> 안내\n\n| 항목 | 상태 |\n| --- | --- |\n| 문서 | 준비 |\n\n```text\nexample\n```";
        var data = new DataObject(DataFormats.UnicodeText, source);
        var captured = ClipboardImageReader.CaptureFrom(data, 0, static () => null);
        _ = ClipboardImageReader.CompletePinAttemptAsync(captured, static _ => null,
            static (text, table) => Task.FromResult(ClipboardTextRenderer.Render(text, table)),
            static _ => throw new InvalidOperationException("Synthetic text has no image fallback."),
            static text => Task.FromResult(ClipboardMarkdownRenderer.Render(text))).GetAwaiter().GetResult();
        return true;
    }, "MyCapture text layout preparation"));

    internal static async Task StartAsync()
    {
        // A failure here must not affect application startup. The normal read path keeps
        // its own bounded plain-text fallback and can retry rendering on a fresh STA.
        try { await Warmup.Value.ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException) { }
    }
}
