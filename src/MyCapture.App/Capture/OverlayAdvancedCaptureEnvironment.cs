using System.Diagnostics;
using System.Windows.Media.Imaging;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>Production binding for advanced capture platform and editor seams.</summary>
internal sealed class OverlayAdvancedCaptureEnvironment : IAdvancedCaptureEnvironment
{
    private readonly CaptureOverlayCoordinator _coordinator;
    private readonly WindowTitleService _windowTitles;
    private readonly Func<bool> _includeCursor;

    internal OverlayAdvancedCaptureEnvironment(
        CaptureOverlayCoordinator coordinator,
        WindowTitleService windowTitles,
        Func<bool> includeCursor)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _windowTitles = windowTitles ?? throw new ArgumentNullException(nameof(windowTitles));
        _includeCursor = includeCursor ?? throw new ArgumentNullException(nameof(includeCursor));
    }

    public FrozenFrame CaptureMonitorUnderCursor()
    {
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
        return _coordinator.Engine.CaptureMonitor(monitor, _includeCursor());
    }

    public FrozenFrame CaptureScreenRegion(RectD screenBounds)
    {
        RectD requested = screenBounds.Normalized().ToPixelBounds();
        RectD effective = Intersect(requested, MonitorEnumerator.GetVirtualDesktopBounds().ToPixelBounds());
        if (effective.IsEmpty)
        {
            throw new InvalidOperationException("The requested capture rectangle is outside the virtual desktop.");
        }

        var stopwatch = Stopwatch.StartNew();
        BitmapSource bitmap = _coordinator.Engine.CaptureRegion(effective, _includeCursor());
        stopwatch.Stop();

        MonitorInfo? singleMonitor = MonitorEnumerator.GetAll().FirstOrDefault(
            monitor => Contains(monitor.Bounds.ToPixelBounds(), effective));
        return new FrozenFrame(bitmap, effective, singleMonitor, stopwatch.Elapsed.TotalMilliseconds);
    }

    public BitmapSource CaptureRegion(RectD screenBounds) =>
        _coordinator.Engine.CaptureRegion(screenBounds, _includeCursor());

    public PointD CursorPosition => CursorLocator.GetPosition();

    public WindowUnderCursor? WindowAt(PointD screenPoint) => _windowTitles.ResolveAt(screenPoint);

    public RectD? ResolveRepeatRegion(RegionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetAll();

        if (string.IsNullOrWhiteSpace(entry.MonitorDeviceName))
        {
            RectD legacy = Intersect(entry.ScreenRegion.ToPixelBounds(), MonitorEnumerator.GetVirtualDesktopBounds());
            return legacy.IsEmpty ? null : legacy;
        }

        MonitorInfo? current = monitors.FirstOrDefault(monitor =>
            string.Equals(
                monitor.DeviceName,
                entry.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase));
        return current is null
            ? null
            : entry.ResolveForMonitor(current.Bounds, current.Dpi);
    }

    public bool OpenEditor(AdvancedSelection selection) =>
        _coordinator.StartWithSelection(
            selection.Frame,
            selection.Region,
            selection.SourceTitle,
            selection.RecordForRepeat);

    private static bool Contains(RectD outer, RectD inner) =>
        inner.Left >= outer.Left
        && inner.Top >= outer.Top
        && inner.Right <= outer.Right
        && inner.Bottom <= outer.Bottom;

    private static RectD Intersect(RectD a, RectD b)
    {
        double left = Math.Max(a.Left, b.Left);
        double top = Math.Max(a.Top, b.Top);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top
            ? new RectD(left, top, right - left, bottom - top).ToPixelBounds()
            : RectD.Empty;
    }
}
