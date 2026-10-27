using System.Windows;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// The resident probe's per-window render variants (<c>--resident-variant sw-selector,sw-card,...</c>):
/// called as the selector, quick access card, flight decoration and image editor windows get their
/// HWND, before their first frame, so a differential run can render one kind of window in software
/// and show how much retained memory that window's hardware rendering leaves behind.
/// </summary>
/// <remarks>Off in the product: nothing sets <see cref="SourceInitialized"/>.</remarks>
internal static class ResidentProbeWindowHook
{
    internal static Action<Window>? SourceInitialized { get; set; }
}
