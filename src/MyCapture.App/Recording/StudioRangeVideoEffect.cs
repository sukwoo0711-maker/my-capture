using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Extensions.Logging;

namespace MyCapture.App.Recording;

/// <summary>Corrects video range at WPF presentation; this shader expands retained studio RGB.</summary>
/// <remarks>
/// <para>
/// Each real player compares its uncorrected presented frame to a range-aware decoded frame
/// after 300 ms without a seek request. Verified decisions seed a session cache by host, range
/// and coarse presentation scale. Unknown results keep studio Expand / full None (or the cache).
/// No probe, encoding or child process runs in the product path. Software rendering uses no effect.
/// </para>
/// <para>
/// The shader runs on the GPU as part of composition, preserving MediaElement's hardware decode,
/// frame pacing and seek path. <see cref="Source"/> is the HLSL the bytecode below was compiled from
/// with <c>D3DCompile(..., "main", "ps_2_0", D3DCOMPILE_OPTIMIZATION_LEVEL3)</c>;
/// a test recompiles it and compares the instructions.
/// </para>
/// </remarks>
internal sealed class StudioRangeVideoEffect : ShaderEffect
{
    internal const string Source = """
        sampler2D Input : register(s0);

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float4 color = tex2D(Input, uv);
            // Premultiplied: studio black is 16/255 of the coverage, studio white 235/255 of it.
            color.rgb = clamp((color.rgb - color.a * (16.0 / 255.0)) * (255.0 / 219.0), 0, color.a);
            return color;
        }
        """;

    internal static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(StudioRangeVideoEffect), 0);

    // ps_2_0: texld; mad rgb, a, -16/255, rgb; mul rgb, 255/219; max 0; min a.
    private static readonly byte[] Bytecode =
    [
        0x00, 0x02, 0xFF, 0xFF, 0xFE, 0xFF, 0x1F, 0x00, 0x43, 0x54, 0x41, 0x42, 0x1C, 0x00, 0x00, 0x00,
        0x4F, 0x00, 0x00, 0x00, 0x00, 0x02, 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00,
        0x00, 0x81, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x02, 0x00, 0x38, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49, 0x6E, 0x70, 0x75,
        0x74, 0x00, 0xAB, 0xAB, 0x04, 0x00, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x70, 0x73, 0x5F, 0x32, 0x5F, 0x30, 0x00, 0x4D, 0x69, 0x63, 0x72, 0x6F,
        0x73, 0x6F, 0x66, 0x74, 0x20, 0x28, 0x52, 0x29, 0x20, 0x48, 0x4C, 0x53, 0x4C, 0x20, 0x53, 0x68,
        0x61, 0x64, 0x65, 0x72, 0x20, 0x43, 0x6F, 0x6D, 0x70, 0x69, 0x6C, 0x65, 0x72, 0x20, 0x31, 0x30,
        0x2E, 0x31, 0x00, 0xAB, 0x51, 0x00, 0x00, 0x05, 0x00, 0x00, 0x0F, 0xA0, 0x81, 0x80, 0x80, 0x3D,
        0x85, 0x0A, 0x95, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x02,
        0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x03, 0xB0, 0x1F, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x90,
        0x00, 0x08, 0x0F, 0xA0, 0x42, 0x00, 0x00, 0x03, 0x00, 0x00, 0x0F, 0x80, 0x00, 0x00, 0xE4, 0xB0,
        0x00, 0x08, 0xE4, 0xA0, 0x04, 0x00, 0x00, 0x04, 0x00, 0x00, 0x07, 0x80, 0x00, 0x00, 0xFF, 0x80,
        0x00, 0x00, 0x00, 0xA1, 0x00, 0x00, 0xE4, 0x80, 0x05, 0x00, 0x00, 0x03, 0x00, 0x00, 0x07, 0x80,
        0x00, 0x00, 0xE4, 0x80, 0x00, 0x00, 0x55, 0xA0, 0x0B, 0x00, 0x00, 0x03, 0x01, 0x00, 0x07, 0x80,
        0x00, 0x00, 0xE4, 0x80, 0x00, 0x00, 0xAA, 0xA0, 0x0A, 0x00, 0x00, 0x03, 0x02, 0x00, 0x07, 0x80,
        0x00, 0x00, 0xFF, 0x80, 0x01, 0x00, 0xE4, 0x80, 0x01, 0x00, 0x00, 0x02, 0x02, 0x00, 0x08, 0x80,
        0x00, 0x00, 0xFF, 0x80, 0x01, 0x00, 0x00, 0x02, 0x00, 0x08, 0x0F, 0x80, 0x02, 0x00, 0xE4, 0x80,
        0xFF, 0xFF, 0x00, 0x00,
    ];

    internal StudioRangeVideoEffect()
    {
        var shader = new PixelShader();
        shader.SetStreamSource(new MemoryStream(Bytecode, writable: false));
        shader.Freeze();
        PixelShader = shader;
        UpdateShaderValue(InputProperty);
    }

    internal static ReadOnlySpan<byte> BytecodeForTest => Bytecode;

    internal Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>
    /// Applies an initial guess and schedules verification of this player once it presents a frame.
    /// </summary>
    internal static void Apply(MediaElement media, bool studioRange, VideoPlayerKind kind = VideoPlayerKind.Editor, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        media.VerifyAccess();
        if (SuppressForDiagnostic)
        {
            Clear(media);
            return;
        }
        if (Previews.TryGetValue(media, out PreviewState? existing)
            && existing.Source == media.Source && existing.StudioRange == studioRange) return;
        Clear(media);
        var state = new PreviewState { StudioRange = studioRange, Source = media.Source };
        Previews.Add(media, state);
        if (_testCalibration is { } calibration)
        {
            // Compatibility for frame-save tests owned by another worktree; never assigned by product code.
            state.Calibration = calibration;
            ApplyDecision(media, calibration.CorrectionFor(studioRange));
            _ = RefreshWhenReadyAsync(new WeakReference<MediaElement>(media), calibration);
            return;
        }
        if (!HardwareShaderAvailable()) return;
        ApplyDecision(media, studioRange ? VideoRangeCorrection.Expand : VideoRangeCorrection.None);
        if (!VerificationEnabled || media.Source is not { IsFile: true }) return;
        state.Verification = new(media, studioRange, kind, correction => ApplyDecision(media, correction), log);
    }

    internal static void PositionRequested(MediaElement media)
    {
        if (!media.Dispatcher.CheckAccess())
        {
            _ = media.Dispatcher.InvokeAsync(() => PositionRequested(media));
            return;
        }
        if (Previews.TryGetValue(media, out PreviewState? state)) state.Verification?.PositionRequested();
    }

    internal static PresentedFrameVerification? VerificationFor(MediaElement media) =>
        Previews.TryGetValue(media, out PreviewState? state) ? state.Verification?.Session : null;

    // A reused player may open another clip before its metadata arrives. Detach the old range
    // so a pending calibration cannot reapply that clip's correction in the meantime.
    internal static void Clear(MediaElement media)
    {
        media.VerifyAccess();
        if (Previews.TryGetValue(media, out PreviewState? state)) state.Verification?.Dispose();
        Previews.Remove(media);
        ApplyDecision(media, VideoRangeCorrection.None);
    }

    private static readonly ConditionalWeakTable<MediaElement, PreviewState> Previews = new();

    private static readonly StudioRangePresentationCalibration EmptyTestCalibration = new(_ => Task.FromResult(default(VideoRangeCalibrationResult)));
    private static StudioRangePresentationCalibration? _testCalibration;
    // Retained only for existing injected frame-save tests; the default cannot encode or launch a process.
    internal static StudioRangePresentationCalibration Calibration
    {
        get => _testCalibration ?? EmptyTestCalibration;
        set => _testCalibration = ReferenceEquals(value, EmptyTestCalibration) ? null : value;
    }
    internal static bool VerificationEnabled { get; set; } = true;
    internal static bool SuppressForDiagnostic { get; set; }

    private static async Task RefreshWhenReadyAsync(
        WeakReference<MediaElement> target, StudioRangePresentationCalibration calibration)
    {
        VideoRangeCalibrationResult result = await calibration.GetResultAsync().ConfigureAwait(false);
        if (!target.TryGetTarget(out MediaElement? media) || media.Dispatcher.HasShutdownStarted) return;
        try
        {
            await media.Dispatcher.InvokeAsync(() =>
            {
                if (Previews.TryGetValue(media, out PreviewState? state)
                    && ReferenceEquals(state.Calibration, calibration))
                    ApplyDecision(media, result.CorrectionFor(state.StudioRange));
            });
        }
        catch (TaskCanceledException) { } // The preview's dispatcher ended during calibration.
        catch (InvalidOperationException) when (media.Dispatcher.HasShutdownStarted) { }
    }

    private static void ApplyDecision(MediaElement media, VideoRangeCorrection correction)
    {
        // Software shader emulation slows every frame and seek. Keep the hardware-only policy.
        if (!HardwareShaderAvailable()) correction = VideoRangeCorrection.None;
        switch (correction)
        {
            case VideoRangeCorrection.Expand:
                if (media.Effect is not StudioRangeVideoEffect)
                    media.Effect = new StudioRangeVideoEffect();
                break;
            case VideoRangeCorrection.Compress:
                if (media.Effect is not FullRangeVideoEffect)
                    media.Effect = new FullRangeVideoEffect();
                break;
            default:
                if (media.Effect is StudioRangeVideoEffect or FullRangeVideoEffect)
                    media.ClearValue(UIElement.EffectProperty);
                break;
        }
    }

    private sealed class PreviewState
    {
        internal bool StudioRange { get; set; }
        internal Uri? Source { get; set; }
        internal MediaElementRangeVerification? Verification { get; set; }
        internal StudioRangePresentationCalibration? Calibration { get; set; }
    }

    /// <summary>
    /// True when WPF renders in hardware (tier 2) with pixel shader 2.0 support. Read on the UI
    /// thread; tests replace it.
    /// </summary>
    internal static Func<bool> HardwareShaderAvailable { get; set; } = () =>
        (RenderCapability.Tier >> 16) >= 2 && RenderCapability.IsPixelShaderVersionSupported(2, 0);

    protected override Freezable CreateInstanceCore() => new StudioRangeVideoEffect();
}
