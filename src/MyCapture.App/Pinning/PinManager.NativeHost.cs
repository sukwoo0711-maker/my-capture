using System.ComponentModel;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;

namespace MyCapture.App.Pinning;

// Lightweight picture pins: the host decision at open and reopen, and the fallback to WPF.
internal sealed partial class PinManager
{
    private static int _nativeFailureLogged;

    /// <summary>
    /// Whether a screen reader runs; the app points it at its <see cref="Accessibility.ScreenReaderState"/>.
    /// The default is no screen reader.
    /// </summary>
    internal Func<bool> ScreenReaderActive { get; set; } = static () => false;

    /// <summary>Reads the app's screen reader flag, and moves every native pin to WPF when it turns on.</summary>
    internal void AttachScreenReader(Accessibility.ScreenReaderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ScreenReaderActive = () => state.Active;
        state.Changed += active => { if (active) SwitchNativePins(PinSwitchReason.ScreenReader); };
    }

    /// <summary>
    /// The pin settings changed. Turning the lightweight setting off moves every open native pin to WPF at
    /// once; turning it on only affects pins opened or reopened afterwards.
    /// </summary>
    internal void ApplySettings()
    {
        if (!_settings().LightweightImagePins) SwitchNativePins(PinSwitchReason.SettingOff);
    }

    private void SwitchNativePins(PinSwitchReason reason)
    {
        foreach (PinWindow pin in _pins.ToArray())
        {
            if (!pin.IsNativeHosted) continue;
            try { pin.SwitchToWpf(reason); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _log.LogWarning(error, "Could not move a lightweight pin to the standard pin ({Reason})", reason);
            }
        }
    }

    /// <summary>A native pin's move or resize ended; the app reclaims idle memory as after a WPF pin's mouse-up.</summary>
    internal event Action? InteractionEnded;

    /// <summary>Test hook: lets the next native failure write its one log line again.</summary>
    internal static void ResetNativeFailureLogForTest() => Interlocked.Exchange(ref _nativeFailureLogged, 0);

    private void TryUseNativeHost(PinWindow pin, PinContent content, PinSettings settings, PinViewState state,
        double leftPhysical, double topPhysical, double scale)
    {
        if (PinHostPolicy.Choose(content, settings, ScreenReaderActive(), state.IsCollapsed) != PinHostKind.Native) return;
        NativePinHost? host = null;
        try
        {
            host = NativePinHost.Create(pin, content, leftPhysical, topPhysical, scale);
            pin.UseNativeHost(host);
        }
        catch (Exception error) when (error is Win32Exception or OutOfMemoryException
            or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // The pin keeps its WPF window, exactly as with the setting off.
            host?.Dispose();
            LogNativeFailureOnce(error, content);
        }
    }

    // One warning per process, however many pins fail: the exception type and the picture size, never content.
    private void LogNativeFailureOnce(Exception error, PinContent content)
    {
        if (Interlocked.Exchange(ref _nativeFailureLogged, 1) != 0) return;
        _log.LogWarning("Lightweight pin unavailable, using the standard pin: {Error} for {Width}x{Height}",
            error.GetType().Name, content.PixelWidth, content.PixelHeight);
    }

    private void OnPinInteractionEnded()
    {
        if (InteractionEnded is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _log.LogWarning(error, "Could not notify a pin interaction observer");
            }
        }
    }
}
