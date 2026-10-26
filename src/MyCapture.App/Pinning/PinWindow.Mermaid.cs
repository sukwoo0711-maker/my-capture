namespace MyCapture.App.Pinning;

internal sealed partial class PinWindow
{
    internal Task MermaidWork { get; set; } = Task.CompletedTask;
    private double _mermaidMonitorScale = 1;
    internal double MermaidDisplayScale => Math.Clamp(_mermaidMonitorScale * _state.Zoom, 0.5, 4);

    internal void ApplyMermaidResults(IReadOnlyDictionary<int, MermaidRenderResult> results, double scale, string? expectedSource = null)
    {
        if (_isClosed || !_content.IsMarkdown
            || (expectedSource is not null && _content.OriginalText != expectedSource)) return;
        PinContent source = _content.WithMermaidResults(results, scale);
        if (_translationOriginal is not null)
        {
            if (_showingTranslation) _translationResult = source;
            else _translationOriginal = source;
        }
        _content = source;
        // The original owns the frame. A translated diagram can wrap into a different
        // height, and is fitted inside it so returning to the original cannot clip it.
        if (!_showingTranslation)
        {
            _state.ReplaceImageSize(source.LayoutWidthDip, source.LayoutHeightDip);
            Width = _state.WidthDip;
            Height = _state.HeightDip;
            if (IsVisible)
            {
                // Fit newly arrived content, preserving deliberate zoom on density changes.
                if (source.LayoutHeightDip > _mermaidPreviousHeight)
                {
                    var desktop = VirtualDesktopDip();
                    double fit = Math.Min(desktop.Width * 0.8 / source.LayoutWidthDip, desktop.Height * 0.8 / source.LayoutHeightDip);
                    _state.SetZoom(Math.Min(_state.Zoom, fit));
                    Width = _state.WidthDip;
                    Height = _state.HeightDip;
                }
                ApplyPositionKeepingGrabbable(Left, Top);
            }
            _mermaidPreviousHeight = source.LayoutHeightDip;
        }
        ClipboardCache.Clear();
        _imageElement.Source = DisplayContent.Image;
        UpdateImageRasterLayout();
        ContentChanged?.Invoke();
    }

    private double _mermaidPreviousHeight;
}
