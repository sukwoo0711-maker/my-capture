using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MyCapture.App.Recording;

/// <summary>
/// Expands studio-range video to full range where WPF presents it.
/// </summary>
/// <remarks>
/// <para>
/// WPF's MediaElement converts studio-range (16??35) Y'CbCr with the right matrix but hands the
/// result over as studio-range RGB: measured on hardware and software rendering alike, a recorded
/// white shows as 235, black as 16 and pure red as (235, 16, 16), so recordings look flat. Every
/// channel is off by the same affine map, so stretching 16??35 back to 0??55 restores the decoded
/// colours to within a step. The file is untouched, so exports stay standard studio-range BT.709
/// for every other player.
/// </para>
/// <para>
/// The shader runs on the GPU as part of composition (WPF emulates <c>ps_2_0</c> when it renders
/// in software), so playback keeps MediaElement's hardware decode, its frame pacing and its seek
/// path. <see cref="Source"/> is the HLSL the bytecode below was compiled from with
/// <c>D3DCompile(?? "main", "ps_2_0", D3DCOMPILE_OPTIMIZATION_LEVEL3)</c>; a test recompiles it
/// and compares the instructions.
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
    /// Shows <paramref name="media"/> at full range when its clip is studio range, and as decoded
    /// otherwise. Players call this before the first frame with the recording default (studio)
    /// and again once the clip's declaration is known.
    /// </summary>
    internal static void Apply(MediaElement media, bool studioRange)
    {
        ArgumentNullException.ThrowIfNull(media);
        // Without a GPU (Remote Desktop, virtual machines, hosted CI) WPF runs pixel shaders on the
        // CPU for every video frame, which slows playback and seeking far more than the slightly
        // flat colours cost. The correction is applied only where the shader runs in hardware.
        if (studioRange && HardwareShaderAvailable())
        {
            if (media.Effect is not StudioRangeVideoEffect)
            {
                media.Effect = new StudioRangeVideoEffect();
            }
        }
        else if (media.Effect is StudioRangeVideoEffect)
        {
            media.ClearValue(UIElement.EffectProperty);
        }
    }

    /// <summary>
    /// True when WPF renders in hardware (tier 2) with pixel shader 2.0 support. Read on the UI
    /// thread; tests replace it.
    /// </summary>
    internal static Func<bool> HardwareShaderAvailable { get; set; } = () =>
        (RenderCapability.Tier >> 16) >= 2 && RenderCapability.IsPixelShaderVersionSupported(2, 0);

    protected override Freezable CreateInstanceCore() => new StudioRangeVideoEffect();
}
