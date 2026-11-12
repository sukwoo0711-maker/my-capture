using System.Runtime.CompilerServices;
using MyCapture.App.Capture;

namespace MyCapture.App.Tests;

/// <summary>Selectors shown by tests never register the real Esc hotkey on the shared desktop.</summary>
internal static class SystemEscapeHotkeyGuard
{
    [ModuleInitializer]
    internal static void Install() => NativeCaptureOverlay.SuppressSystemEscapeHotkey = true;
}
