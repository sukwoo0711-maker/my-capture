using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;

namespace MyCapture.App.Pinning;

/// <summary>
/// Hands what a running picture translation learns (its blocks, blocks placed early) to the pin's UI thread. Nothing
/// reaches the pin once the translation is cancelled, replaced or the pin closes.
/// </summary>
internal sealed class PinTranslationRevealSink(PinWindow pin, CancellationToken cancellation) : IPinTranslationRevealSink
{
    public void BlocksKnown(int pixelWidth, int pixelHeight, IReadOnlyList<PinRevealBlock> blocks)
    {
        if (cancellation.IsCancellationRequested) return;
        _ = pin.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!cancellation.IsCancellationRequested) pin.RevealBlocksKnown(pixelWidth, pixelHeight, blocks);
        }));
    }

    public void BlocksReady(IReadOnlyList<PinOverlayBlockLayer> layers)
    {
        if (cancellation.IsCancellationRequested) return;
        _ = pin.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!cancellation.IsCancellationRequested) pin.RevealBlocksReady(layers);
        }));
    }
}

internal sealed partial class PinWindow
{
    [ThreadStatic]
    private static Func<double>? s_translationRevealClockForTest;

    [ThreadStatic]
    private static bool s_translationRevealSuppressedForTest;

    private PinTranslationReveal? _reveal;
    private PinTranslationRevealLayer? _revealLayer;
    private bool _revealRenderingAttached;
    private bool _revealWantsFrames;
    private IReadOnlyList<PinOverlayBlockLayer>? _pendingRevealLayers;
    private double _translationGlowAmount;

    /// <summary>
    /// A clock in milliseconds for this test thread's pins. While it is set no frame clock runs: the test advances the
    /// change with <see cref="AdvanceTranslationRevealForTest"/>.
    /// </summary>
    internal static Func<double>? TranslationRevealClockForTest
    {
        get => s_translationRevealClockForTest;
        set => s_translationRevealClockForTest = value;
    }

    /// <summary>For a measurement: this test thread's pins translate with motion on but without the live change.</summary>
    internal static bool TranslationRevealSuppressedForTest
    {
        get => s_translationRevealSuppressedForTest;
        set => s_translationRevealSuppressedForTest = value;
    }

    internal PinTranslationReveal? TranslationRevealForTest => _reveal;

    /// <summary>Whether the change currently asks for frames (with the test clock no frame clock is attached either way).</summary>
    internal bool TranslationRevealWantsFramesForTest => _revealWantsFrames;

    /// <summary>Whether the frame clock (<see cref="CompositionTarget.Rendering"/>) is attached for the change.</summary>
    internal bool TranslationRevealClockAttachedForTest => _revealRenderingAttached;
    internal bool IsTranslationRevealActive => _reveal is not null;
    internal double TranslationGlowAmountForTest => _translationGlowAmount;
    internal UIElement? TranslationRevealLayerForTest => _revealLayer;

    /// <summary>Draws the change as it is at the test clock's time.</summary>
    internal void AdvanceTranslationRevealForTest()
    {
        TickTranslationReveal();
        _revealLayer?.InvalidateVisual();
        StopRevealFramesWhenStill();
    }

    /// <summary>Ends a running change at once, as if its time had passed.</summary>
    internal void FinishTranslationRevealForTest() => EndTranslationReveal();

    private static Func<double> RevealClock()
    {
        if (s_translationRevealClockForTest is { } clock) return clock;
        long start = Stopwatch.GetTimestamp();
        return () => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private bool CanShowTranslationReveal => FluidMotion.AnimationsEnabled && !s_translationRevealSuppressedForTest
        && !_isClosed && _native is null && !_state.IsCollapsed;

    /// <summary>The translation command was pressed: the sweep starts in this turn.</summary>
    internal void BeginTranslationReveal()
    {
        EndTranslationReveal();
        if (!CanShowTranslationReveal || _showingTranslation || _showingMarkdownSource) return;
        PinContent source = TranslationSourceContent;
        bool picture = source.Kind == PinContentKind.Image;
        (double luminance, _) = PinTranslationReveal.Sample(_content.Image);
        StartReveal(PinTranslationReveal.ForTranslation(RevealClock(), picture, picture ? source.Image : null, luminance));
    }

    internal void RevealBlocksKnown(int pixelWidth, int pixelHeight, IReadOnlyList<PinRevealBlock> blocks)
    {
        if (_reveal is not { Ended: false } reveal) return;
        reveal.BlocksKnown(pixelWidth, pixelHeight, blocks);
        _revealLayer?.InvalidateVisual();
        RequestRevealFrames();
    }

    internal void RevealBlocksReady(IReadOnlyList<PinOverlayBlockLayer> layers)
    {
        if (_reveal is not { Ended: false } reveal) return;
        reveal.BlocksReady(layers);
        _revealLayer?.InvalidateVisual();
        RequestRevealFrames();
    }

    /// <summary>The final blocks of the result about to be applied.</summary>
    internal void PrepareTranslationReveal(IReadOnlyList<PinOverlayBlockLayer>? layers) => _pendingRevealLayers = layers;

    /// <summary>Called by <see cref="SetTranslationState"/>: a state other than running stops a change that was not shown.</summary>
    private void OnTranslationStateForReveal(PinTranslationState state)
    {
        if (_reveal is not { } reveal || reveal.FinalApplied) return;
        if (state == PinTranslationState.Translating)
        {
            if (!reveal.IsPicture)
            {
                reveal.CoverContent(PinTranslationReveal.Sample(_content.Image).Color);
                RequestRevealFrames();
            }
            return;
        }
        if (state is PinTranslationState.Checking or PinTranslationState.Downloading) return;
        AbortTranslationReveal();
    }

    /// <summary>Called by <see cref="ApplyTranslation"/> once the result is in place, shown or not.</summary>
    private void OnTranslationAppliedForReveal()
    {
        _pendingRevealLayers = null;
        if (_reveal is { FinalApplied: false } && !_showingTranslation) AbortTranslationReveal();
    }

    /// <summary>What the pin showed before a view change, for the change's fade.</summary>
    private (ImageSource? Image, Rect Destination, bool Document) RevealSnapshot()
    {
        ImageSource? image = _imageElement.Source;
        Rect destination = new(0, 0, Math.Max(0, _chrome.ActualWidth > 0 ? _chrome.ActualWidth : Width),
            Math.Max(0, _chrome.ActualHeight > 0 ? _chrome.ActualHeight : Height));
        if (image is not null && _imageElement.Stretch == Stretch.None)
        {
            double scale = _imageElement.LayoutTransform is ScaleTransform transform ? transform.ScaleX : 1;
            destination = new Rect(0, 0, image.Width * scale, image.Height * scale);
        }
        bool document = _translationOriginal is not null && !_translationOriginal.CanRerender && _content.CanRerender;
        return (image, destination, document);
    }

    /// <summary>
    /// Called by <see cref="SetTranslationDisplay"/> after the view changed. The first translated view finishes the running
    /// change; switching between views the pin already has fades the old one out (83 ms back to the source, 167 ms to the
    /// translation). A picture that becomes a document, or back, changes its frame and does not fade.
    /// </summary>
    private void OnTranslationDisplayChangedForReveal((ImageSource? Image, Rect Destination, bool Document) before, bool translated)
    {
        IReadOnlyList<PinOverlayBlockLayer>? layers = _pendingRevealLayers;
        _pendingRevealLayers = null;
        (_, _, bool document) = RevealSnapshot();
        bool frameChanged = document != before.Document;
        if (_reveal is { FinalApplied: false, Aborting: false } reveal && translated)
        {
            reveal.ApplyFinal(frameChanged ? null : layers, frameChanged ? null : before.Image, before.Destination);
            _revealLayer?.InvalidateVisual();
            RequestRevealFrames();
            return;
        }
        EndTranslationReveal();
        if (!CanShowTranslationReveal || frameChanged || before.Image is null || ReferenceEquals(before.Image, _imageElement.Source)) return;
        StartReveal(PinTranslationReveal.ForSwitch(RevealClock(), before.Image, before.Destination,
            translated ? TranslationRevealMotion.ReshowMs : TranslationRevealMotion.RevertMs));
    }

    private void StartReveal(PinTranslationReveal reveal)
    {
        _reveal = reveal;
        // No clip: a rounded clip would draw the whole layer through a pin-sized intermediate surface every frame. The
        // layer gives its pin-sized primitives the rounded corners instead.
        _revealLayer = new PinTranslationRevealLayer(reveal);
        UpdateRevealFrame();
        // Over the content, under the outline and the bottom message.
        int index = Math.Max(0, _chrome.Children.IndexOf(_imageElement)) + 1;
        _chrome.Children.Insert(index, _revealLayer);
        _chrome.SizeChanged += OnRevealChromeSizeChanged;
        _revealLayer.InvalidateVisual();
        RequestRevealFrames();
    }

    private void UpdateRevealFrame()
    {
        if (_revealLayer is not { } layer) return;
        Size size = new(_chrome.ActualWidth > 0 ? _chrome.ActualWidth : Width, _chrome.ActualHeight > 0 ? _chrome.ActualHeight : Height);
        layer.Frame = ImageClipBounds(size);
        layer.Radius = Math.Max(0, FrameCornerRadiusDip - FrameClipInsetDip);
    }

    private void OnRevealChromeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRevealFrame();
        _revealLayer?.InvalidateVisual();
    }

    /// <summary>Something can move from now: the frame clock runs until the change is still again.</summary>
    private void RequestRevealFrames()
    {
        if (_reveal is null) return;
        _revealWantsFrames = true;
        if (s_translationRevealClockForTest is null && !_revealRenderingAttached)
        {
            CompositionTarget.Rendering += OnRevealRendering;
            _revealRenderingAttached = true;
        }
    }

    /// <summary>After a frame is drawn: a change that stands still until the next event needs no frame clock.</summary>
    private void StopRevealFramesWhenStill()
    {
        if (_reveal is { } reveal && reveal.NeedsFrames()) return;
        _revealWantsFrames = false;
        DetachRevealRendering();
    }

    private void DetachRevealRendering()
    {
        if (!_revealRenderingAttached) return;
        CompositionTarget.Rendering -= OnRevealRendering;
        _revealRenderingAttached = false;
    }

    private void OnRevealRendering(object? sender, EventArgs e)
    {
        TickTranslationReveal();
        _revealLayer?.InvalidateVisual();
        StopRevealFramesWhenStill();
    }

    private void TickTranslationReveal()
    {
        if (_reveal is not { } reveal) return;
        // A folded, closed or native pin, or a text selection, shows no change.
        if (_isClosed || _native is not null || _state.IsCollapsed || IsTextSelectionMode)
        {
            EndTranslationReveal();
            return;
        }
        bool running = reveal.Tick();
        SetTranslationGlow(reveal.GlowAmount);
        if (!running) EndTranslationReveal();
    }

    private void AbortTranslationReveal()
    {
        if (_reveal is not { } reveal) return;
        if (!FluidMotion.AnimationsEnabled) { EndTranslationReveal(); return; }
        reveal.Abort();
        _revealLayer?.InvalidateVisual();
        RequestRevealFrames();
    }

    /// <summary>Removes the change and everything it holds: its layer, frame clock and the outline's glow.</summary>
    private void EndTranslationReveal()
    {
        _pendingRevealLayers = null;
        _revealWantsFrames = false;
        DetachRevealRendering();
        if (_reveal is null && _revealLayer is null) return;
        _reveal?.End();
        _reveal = null;
        _chrome.SizeChanged -= OnRevealChromeSizeChanged;
        if (_revealLayer is { } layer) _chrome.Children.Remove(layer);
        _revealLayer = null;
        SetTranslationGlow(0);
    }

    private void SetTranslationGlow(double amount)
    {
        if (amount == _translationGlowAmount) return;
        _translationGlowAmount = amount;
        if (!_isClosed) UpdateChromeColor(animate: false);
    }
}
