using MyCapture.Core.Settings;

namespace MyCapture.App.Pinning;

/// <summary>Which host presents a pin when it opens.</summary>
internal enum PinHostKind
{
    /// <summary>The pin's own WPF window: every feature, today's behaviour.</summary>
    Wpf,

    /// <summary>A native layered window (<see cref="NativePinHost"/>) that keeps one picture and no WPF render target.</summary>
    Native,
}

/// <summary>
/// The lightweight-pin decision for a pin that opens or reopens. Only a plain picture is drawn by
/// the native host; text, tables, Markdown and Mermaid pins, pictures with original text, a shown
/// translation, a folded pin and a session with a screen reader stay WPF, because those are WPF views
/// or need UI Automation that only the WPF host gives.
/// </summary>
internal static class PinHostPolicy
{
    internal static PinHostKind Choose(PinContent content, PinSettings settings, bool screenReader, bool collapsed)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.LightweightImagePins
            && !screenReader
            && !collapsed
            && content.Kind == PinContentKind.Image
            && content.OriginalText is null
            && !content.HasOverlay
            && !content.UsesDeviceIndependentPixels
            && content.MermaidResults is not { Count: > 0 }
            ? PinHostKind.Native
            : PinHostKind.Wpf;
    }
}
