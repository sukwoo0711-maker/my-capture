using System.Reflection;
using System.Windows.Input;
using System.Windows.Interop;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Exercises the same coordinator contract through either selector renderer.</summary>
internal sealed class CoordinatorSelector
{
    private readonly CaptureOverlayWindow? _wpf;
    private readonly NativeCaptureOverlay? _native;

    internal CoordinatorSelector(CaptureOverlayCoordinator coordinator, bool? native = null)
    {
        while (Field<CaptureOverlayCoordinator>(coordinator, "_editorScreenCapture") is { } nested)
            coordinator = nested;
        _native = coordinator.NativeOverlayForProbe;
        _wpf = Field<CaptureOverlayWindow>(coordinator, "_activeOverlay");
        Assert.True(_native is not null || _wpf is not null);
        if (native is { } expected) Assert.Equal(expected, _native is not null);
    }

    private static T? Field<T>(object owner, string name) where T : class =>
        (T?)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

    internal bool IsVisible => _native?.IsVisible ?? _wpf!.IsVisible;
    internal nint Handle => _native?.Handle ?? new WindowInteropHelper(_wpf!).Handle;
    internal bool TextCaptureMode => _native?.TextCaptureMode ?? _wpf!.TextCaptureMode;
    internal bool SourceDetailsPending => _native is { } native
        ? Field<Task>(native, "_sourceDetails") is { IsCompleted: false }
        : _wpf!.SourceDetailsPendingForTest;
    internal string? ToastMessage => _native?.ToastMessage ?? (_wpf is { } wpf
        ? Assert.IsType<CaptureOverlayView>(wpf.Content).ToastMessageForTest : null);
    internal void SetPointer(Func<PointD> readCursor)
    {
        if (_native is { } native) { native.ReadCursor = readCursor; native.RefreshHover(); }
        else
        {
            var view = Assert.IsType<CaptureOverlayView>(_wpf!.Content);
            view.ReadCursor = readCursor;
            view.InitializePointer();
        }
    }
    internal Task<string?> CopyColorAtPointerAsync() => _native is { } native
        ? native.CopyColorAtPointerAsync()
        : Assert.IsType<CaptureOverlayView>(_wpf!.Content).CopyColorAtPointerAsync();
    internal void HandleKeyDown(KeyEventArgs key, ModifierKeys modifiers)
    {
        if (_native is { } native) key.Handled = native.HandleKey(key.Key, modifiers);
        else Assert.IsType<CaptureOverlayView>(_wpf!.Content).HandleKeyDown(key, modifiers);
    }
    internal void SelectAll()
    {
        if (_native is { } native) native.HandleKey(Key.A, ModifierKeys.Control);
        else Assert.IsType<CaptureOverlayView>(_wpf!.Content)
            .HandleKeyDown(WindowDetectOverlayTests.KeyArgs(Key.A), ModifierKeys.Control);
    }
    internal bool CaptureFullScreen()
    {
        if (_native is not { } native) return Assert.IsType<CaptureOverlayView>(_wpf!.Content).CaptureFullScreen();
        native.CompleteMonitor();
        return native.IsClosed;
    }
    internal void CompleteSelection(RectD region)
    {
        if (_native is { } native) native.CompleteSelection(region);
        else _wpf!.CompleteSelection(region, null, _wpf.TextCaptureMode);
    }
    internal void Close() { if (_native is { } native) native.Close(); else _wpf!.Close(); }
}
