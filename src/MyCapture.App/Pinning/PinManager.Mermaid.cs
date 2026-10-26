using Microsoft.Extensions.Logging;
using System.Windows.Threading;

namespace MyCapture.App.Pinning;

internal sealed partial class PinManager
{
    private IMermaidRenderer? _mermaidRenderer;
    private readonly Dictionary<PinWindow, (double Scale, bool Translated, CancellationTokenSource Cancellation)> _mermaidOperations = [];

    internal void ConfigureMermaid(IMermaidRenderer renderer) =>
        _mermaidRenderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    private void AttachMermaid(PinWindow pin)
    {
        if (!pin.TranslationSourceContent.IsMarkdown || _mermaidRenderer is null) return;
        pin.CloseStarted += (_, _) => CancelMermaid(pin);
        pin.ViewStateChanged += () => StartMermaid(pin);
        StartMermaid(pin);
    }

    private void CancelMermaid(PinWindow pin)
    {
        if (_mermaidOperations.Remove(pin, out var operation)) operation.Cancellation.Cancel();
    }

    private void StartMermaid(PinWindow pin)
    {
        if (pin.IsClosed || _mermaidRenderer is null) return;
        // The document on screen: a translated diagram has its own labels and so its own bitmaps.
        PinContent content = pin.DisplayedDocumentContent;
        bool translated = pin.IsShowingTranslation;
        if (!content.IsMarkdown || content.OriginalText is null) return;
        IReadOnlyList<MarkdownMermaidBlock> blocks = ClipboardMarkdownRenderer.ExtractMermaidBlocks(content.OriginalText!);
        if (blocks.Count == 0) return;
        double scale = pin.MermaidDisplayScale;
        if (_mermaidOperations.TryGetValue(pin, out var active) && Math.Abs(active.Scale - scale) < .001
            && active.Translated == translated) return;
        if (blocks.All(block => content.MermaidResults?.TryGetValue(block.Index, out var result) == true
            && (result.Image is null || Math.Abs(result.Image.DpiX / 96 - scale) < .001))) return;
        CancelMermaid(pin);
        var cancellation = new CancellationTokenSource();
        _mermaidOperations[pin] = (scale, translated, cancellation);
        // Let the placeholder pin paint before creating a browser or running JavaScript.
        pin.MermaidWork = pin.Dispatcher.InvokeAsync(
            () => RenderMermaidAsync(pin, blocks, scale, translated, cancellation), DispatcherPriority.Background).Task.Unwrap();
    }

    private async Task RenderMermaidAsync(PinWindow pin, IReadOnlyList<MarkdownMermaidBlock> blocks,
        double scale, bool translated, CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        bool Current() => !pin.IsClosed && !token.IsCancellationRequested
            && _mermaidOperations.TryGetValue(pin, out var active) && ReferenceEquals(active.Cancellation, cancellation);
        var results = new Dictionary<int, MermaidRenderResult>();
        try
        {
            foreach (MarkdownMermaidBlock block in blocks)
            {
                token.ThrowIfCancellationRequested();
                MermaidRenderResult result;
                try { result = await _mermaidRenderer!.RenderAsync(block.Source, scale, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning("Diagram rendering failed ({ErrorType})", ex.GetType().Name);
                    result = new MermaidRenderResult(null, "그림을 표시할 수 없어 원문을 표시합니다.");
                }
                if (!Current()) return;
                results[block.Index] = result;
                pin.ApplyMermaidResults(results, scale, translated);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogWarning("Diagram pin update failed ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            if (_mermaidOperations.TryGetValue(pin, out var active) && ReferenceEquals(active.Cancellation, cancellation))
                _mermaidOperations.Remove(pin);
            cancellation.Dispose();
        }
    }
}
