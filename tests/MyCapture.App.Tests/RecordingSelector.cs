using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Runs the recording selector contract through the selected renderer.</summary>
internal sealed class RecordingSelector
{
    private readonly CaptureOverlayWindow? _wpf;
    private readonly NativeCaptureOverlay? _native;

    internal RecordingSelector(RegionRecordingCoordinator coordinator, bool native)
    {
        object? current = Current(coordinator);
        Assert.NotNull(current);
        if (native) _native = Assert.IsType<NativeCaptureOverlay>(current);
        else _wpf = Assert.IsType<CaptureOverlayWindow>(current);
    }

    internal static object? Current(RegionRecordingCoordinator coordinator) =>
        (object?)coordinator.NativeOverlayForProbe ?? typeof(RegionRecordingCoordinator)
            .GetField("_selectionOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator);

    internal bool IsVisible => _native?.IsVisible ?? _wpf!.IsVisible;
    internal nint Handle => _native?.Handle ?? new WindowInteropHelper(_wpf!).Handle;
    internal bool RecordingSelection => _native?.RecordingSelection ?? _wpf!.RecordingSelection;
    internal bool ShowModeBar => _native is { } native
        ? native.ModeBar.Visibility == Visibility.Visible : _wpf!.ShowModeBar;
    internal bool OffersOtherActions => _native?.OffersOtherActions ?? _wpf!.OffersOtherActions;
    internal System.Drawing.Size? PresetSize => _native is { } native ? native.PresetSize : _wpf!.PresetSize;
    internal CaptureModeBar ModeBar => _native?.ModeBar ?? Assert.IsType<CaptureOverlayView>(_wpf!.Content).ModeBarForTest!;

    internal void SubscribeClosed(Action closed)
    {
        if (_native is { } native) native.Closed += (_, _) => closed();
        else _wpf!.Closed += (_, _) => closed();
    }

    internal void CompleteSelection(RectD region)
    {
        if (_native is { } native) native.CompleteSelection(region);
        else _wpf!.CompleteSelection(region);
    }

    internal void Close() { if (_native is { } native) native.Close(); else _wpf!.Close(); }
}
