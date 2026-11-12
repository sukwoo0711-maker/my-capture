using System.Windows;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Pinning;

/// <summary>
/// What a pin's auxiliary window (hover bar, readout, recovery handle, landing ornament) needs from the
/// window that presents the pin, whichever host that is: the native HWND of a lightweight picture pin,
/// or the pin's WPF window. Every rectangle is in physical pixels.
/// </summary>
internal interface IPinAnchor
{
    /// <summary>The presenting window's handle, or zero before it exists.</summary>
    IntPtr AnchorHandle { get; }

    /// <summary>The presenting window's whole surface on the screen.</summary>
    RectD AnchorBounds { get; }

    /// <summary>The monitor scale the pin is drawn at.</summary>
    double AnchorScale { get; }

    /// <summary>The pin is on the screen (fading counts).</summary>
    bool IsAnchorShown { get; }

    bool IsClosed { get; }

    /// <summary>The work area of the monitor the presenting window is on.</summary>
    RectD ReadAnchorWorkArea();

    /// <summary>Makes <paramref name="window"/> owned by the presenting window, and by the next one after a switch.</summary>
    void AttachOwned(Window window);

    /// <summary>The presenting window moved, resized or changed scale.</summary>
    event Action? AnchorBoundsChanged;

    /// <summary>The pin was shown (true) or hidden (false).</summary>
    event Action<bool>? AnchorShownChanged;

    /// <summary>The pin closed.</summary>
    event Action? AnchorClosed;
}
