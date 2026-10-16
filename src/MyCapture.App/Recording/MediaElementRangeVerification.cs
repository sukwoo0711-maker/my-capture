using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace MyCapture.App.Recording;

/// <summary>Measures this visual at its current presentation scale; never creates another player.</summary>
internal sealed class MediaElementRangeVerification : IDisposable
{
    private static readonly DependencyPropertyDescriptor SourceProperty = DependencyPropertyDescriptor.FromProperty(MediaElement.SourceProperty, typeof(MediaElement));
    private readonly MediaElement _media;
    private readonly Uri _source;
    private readonly bool _studioRange;
    private readonly VideoPlayerKind _kind;
    private readonly ILogger? _log;
    private readonly Action<RangeCorrectionDecision> _apply;
    private readonly DispatcherTimer _timer;
    private PresentedFrameVerification _session;
    private bool _disposed;
    private int _compositions, _bucket;
    private static readonly SemaphoreSlim DecodeSlot = new(1, 1);
    internal static RangeVerificationCache Cache { get; set; } = new();
    internal static Func<MediaElement, RangeVerificationFrame?> Sampler { get; set; } = Sample;
    internal static Func<string, RangeVerificationFrame, CancellationToken, Task<RangeVerificationResult>> Decoder { get; set; } = DecodeAsync;
    // Test-only selection hook: sampling, reference decoding, fit validation and the real shader
    // are unchanged. No product setting or command-line switch enables it.
    internal static bool ForceMeasuredForTest { get; set; }

    internal MediaElementRangeVerification(MediaElement media, bool studioRange, VideoPlayerKind kind,
        Action<RangeCorrectionDecision> apply, ILogger? log)
    {
        _media = media; _source = media.Source; _studioRange = studioRange; _kind = kind;
        _apply = apply; _log = log;
        _bucket = ScaleBucket(media);
        _session = CreateSession();
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, media.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        media.Unloaded += OnUnloaded;
        media.MediaFailed += OnFailed;
        media.SizeChanged += OnSizeChanged;
        SourceProperty.AddValueChanged(media, OnSourceChanged);
        StartObserving();
    }

    internal PresentedFrameVerification Session => _session;
    private PresentedFrameVerification CreateSession() => new(new(_kind, _studioRange, _bucket), Cache,
        () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency,
        () => _compositions >= 2 && _media.IsLoaded && _media.HasVideo && _media.NaturalVideoWidth > 0
            && _media.ActualWidth > 0 && _media.ActualHeight > 0,
        () => Sampler(_media), (sample, token) => Decoder(_source.LocalPath, sample, token), _apply,
        result =>
        {
            if (_log is not null)
                _log.LogInformation("Video range decision: player={Player}, studio={Studio}, scale={Scale}, model={Model}, "
                    + "identity={Identity:F2}, retained={Retained:F2}, expanded={Expanded:F2}, points={Points}, "
                    + "range={Range}, attempts={Attempts}, position={Position:F2}ms, decision={Decision}",
                    _kind, _studioRange, _bucket, result.Model, result.Errors.Identity, result.Errors.StudioRetained,
                    result.Errors.ExpandedAsStudio, result.Points, result.DynamicRange, result.Attempts, result.PositionMs, result.Details);
            else Trace.TraceInformation("Video range verified: player={0}, studio={1}, scale={2}, result={3}",
                _kind, _studioRange, _bucket, result.Details);
        });

    internal void PositionRequested()
    {
        if (_disposed) return;
        _compositions = 0;
        _session.PositionRequested();
    }

    private void StartObserving()
    {
        _compositions = 0;
        _timer.Start();
    }
    private void StopObserving() => _timer.Stop();
    private async void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_media.Source != _source) { Dispose(); return; }
        // MediaOpened alone is not presentation. Require a connected video visual on two idle ticks;
        // an empty/transparent snapshot still cannot start a decode. Do not subscribe to
        // CompositionTarget.Rendering: it keeps WPF rendering every frame, which stalled the
        // editor's scrub for seconds.
        _compositions = _media.IsLoaded && _media.HasVideo && _media.NaturalVideoWidth > 0
            ? Math.Min(2, _compositions + 1) : 0;
        UpdateScale();
        PresentedFrameVerification session = _session;
        await session.TickAsync();
        if (ReferenceEquals(session, _session) && session.Completion.IsCompleted) StopObserving();
    }
    private void UpdateScale()
    {
        int bucket = ScaleBucket(_media);
        if (bucket == _bucket) return;
        _session.Dispose(); _bucket = bucket; _session = CreateSession();
        StartObserving();
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        UpdateScale();
        PositionRequested();
    }
    private void OnSourceChanged(object? sender, EventArgs e)
    {
        if (_media.Source != _source) Dispose();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();
    private void OnFailed(object? sender, ExceptionRoutedEventArgs e) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopObserving(); _session.Dispose();
        SourceProperty.RemoveValueChanged(_media, OnSourceChanged);
        _media.Unloaded -= OnUnloaded; _media.MediaFailed -= OnFailed; _media.SizeChanged -= OnSizeChanged;
    }

    internal static int ScaleBucket(MediaElement media)
    {
        if (media.NaturalVideoWidth <= 0 || media.NaturalVideoHeight <= 0) return -1;
        DpiScale dpi = VisualTreeHelper.GetDpi(media);
        double scale = Math.Min(media.ActualWidth * dpi.DpiScaleX / media.NaturalVideoWidth,
            media.ActualHeight * dpi.DpiScaleY / media.NaturalVideoHeight);
        return scale < 0.5 ? 0 : scale < 1 ? 1 : 2;
    }

    // No await, dispatcher pump or composition can interleave the null effect and restoration.
    internal static T WithoutEffect<T>(MediaElement media, Func<T> render)
    {
        media.VerifyAccess();
        using (media.Dispatcher.DisableProcessing())
        {
            var effect = media.Effect;
            try { media.SetCurrentValue(UIElement.EffectProperty, null); return render(); }
            finally { media.SetCurrentValue(UIElement.EffectProperty, effect); }
        }
    }

    internal static RangeVerificationFrame? Sample(MediaElement media)
    {
        if (!media.HasVideo || media.NaturalVideoWidth <= 0 || media.NaturalVideoHeight <= 0
            || media.ActualWidth <= 0 || media.ActualHeight <= 0) return null;
        double scale = Math.Min(1, 320.0 / Math.Max(media.NaturalVideoWidth, media.NaturalVideoHeight));
        int width = Math.Max(1, (int)Math.Round(media.NaturalVideoWidth * scale));
        int height = Math.Max(1, (int)Math.Round(media.NaturalVideoHeight * scale));
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            // The default relative viewbox covers the visual's actual content bounds, including
            // its layout offset. An absolute (0,0) viewbox shifts centred editor content.
            dc.DrawRectangle(new VisualBrush(media) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        double position = media.Position.TotalMilliseconds;
        WithoutEffect(media, () => { bitmap.Render(visual); return true; });
        byte[] pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        // An unpresented WPF video visual is transparent. Opaque black is a valid frame whose
        // reference will normally be inconclusive; do not infer a range from it.
        if (!Enumerable.Range(0, width * height).Any(i => pixels[i * 4 + 3] >= 250)) return null;
        return new(width, height, pixels, position);
    }

    private static Task<RangeVerificationResult> DecodeAsync(string path, RangeVerificationFrame sample, CancellationToken token) =>
        Task.Run(async () =>
        {
            await DecodeSlot.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                using var reader = RangeAwareVideoFrameDecoder.Open(path);
                var decoded = RangeAwareVideoFrameDecoder.ReadBgr32(reader, sample.PositionMs, token);
                var reference = PresentedFrameRangeModel.AreaDownscale(decoded.Pixels, decoded.Width, decoded.Height,
                    decoded.Stride, sample.Width, sample.Height, sample.PositionMs, token);
                token.ThrowIfCancellationRequested();
                return PresentedFrameRangeModel.Compare(sample, reference, ForceMeasuredForTest);
            }
            finally { DecodeSlot.Release(); }
        }, token);
}
