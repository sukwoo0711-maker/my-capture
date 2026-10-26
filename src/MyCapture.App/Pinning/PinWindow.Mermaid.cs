namespace MyCapture.App.Pinning;

internal sealed partial class PinWindow
{
    internal Task MermaidWork { get; set; } = Task.CompletedTask;
    private double _mermaidMonitorScale = 1;
    internal double MermaidDisplayScale => Math.Clamp(_mermaidMonitorScale * _state.Zoom, 0.5, 4);

    /// <summary>
    /// Stores finished diagrams on the original or the translated document and, when that document is the
    /// one on screen, shows them. A translated diagram has its own labels, so its bitmaps are its own.
    /// </summary>
    internal void ApplyMermaidResults(IReadOnlyDictionary<int, MermaidRenderResult> results, double scale, bool translated = false)
    {
        if (_isClosed) return;
        PinContent? target = translated ? _translationResult : _translationOriginal ?? _content;
        if (target is null || !target.IsMarkdown) return;
        PinContent updated = target.WithMermaidResults(results, scale);
        if (translated) _translationResult = updated;
        else if (_translationOriginal is not null) _translationOriginal = updated;
        else _content = updated;
        if (translated != _showingTranslation) return;
        if (translated || _translationOriginal is not null) _content = updated;
        // Keep the selected range stable; leaving F2 shows the completed bitmap.
        _state.ReplaceImageSize(updated.LayoutWidthDip, updated.LayoutHeightDip);
        Width = _state.WidthDip;
        Height = _state.HeightDip;
        if (IsVisible)
        {
            // Initial placeholders are short. Fit newly arrived content on screen without
            // overriding a deliberate later zoom when only raster density changes.
            if (updated.LayoutHeightDip > _mermaidPreviousHeight)
            {
                var desktop = VirtualDesktopDip();
                double fit = Math.Min(desktop.Width * 0.8 / updated.LayoutWidthDip, desktop.Height * 0.8 / updated.LayoutHeightDip);
                _state.SetZoom(Math.Min(_state.Zoom, fit));
                Width = _state.WidthDip;
                Height = _state.HeightDip;
            }
            ApplyPositionKeepingGrabbable(Left, Top);
        }
        _mermaidPreviousHeight = updated.LayoutHeightDip;
        ClipboardCache.Clear();
        _imageElement.Source = DisplayContent.Image;
        UpdateImageRasterLayout();
        ContentChanged?.Invoke();
    }

    private double _mermaidPreviousHeight;
}
