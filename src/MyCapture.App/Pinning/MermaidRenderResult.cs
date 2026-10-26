using System.Windows.Media.Imaging;

namespace MyCapture.App.Pinning;

internal sealed record MarkdownMermaidBlock(int Index, string Source);

/// <summary>A local diagram result; a missing dictionary entry means rendering is pending.</summary>
internal sealed record MermaidRenderResult(BitmapSource? Image, string? Error);

internal interface IMermaidRenderer
{
    Task<MermaidRenderResult> RenderAsync(string source, double scale, CancellationToken cancellationToken);
}
