using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Recording;

/// <summary>A capture-included, nonactivating, click-through watermark surface.</summary>
internal sealed class RecordingWatermarkWindow : Window
{
    private RectD _region;
    private double _scale;
    private IntPtr _hwnd;
    private bool _closed;
    private readonly Image _image;

    internal RecordingWatermarkWindow(RectD region, double dpiScale, RecordingWatermarkSnapshot snapshot)
    {
        _region = region.Normalized().ToPixelBounds();
        _scale = ValidScale(dpiScale);
        Snapshot = snapshot;
        Title = UiText.Get("RecordingWatermark_Title");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FluidMotion.SetWindowEntrance(this, false);
        _image = new Image { Source = snapshot.Content, Stretch = Stretch.Uniform, Opacity = snapshot.Settings.Opacity };
        Content = _image;
        Reposition();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(_hwnd, -20);
            _ = SetWindowLong(_hwnd, -20, style | 0x20 | 0x08000000 | 0x80);
            // No capture-exclusion affinity: this is the only watermark compositing path.
            Reposition();
        };
        DpiChanged += (_, e) =>
        {
            _scale = ValidScale(e.NewDpi.DpiScaleX);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
        };
        Closed += (_, _) => _closed = true;
    }

    internal RecordingWatermarkSnapshot Snapshot { get; }
    internal RectD PhysicalBounds { get; private set; }
    internal Image ImageForTest => _image;

    internal void UpdateRecordingRegion(RectD region, double dpiScale)
    {
        _region = region.Normalized().ToPixelBounds();
        _scale = ValidScale(dpiScale);
        Reposition();
    }

    private void Reposition()
    {
        if (_closed) return;
        RectD local = Snapshot.Place(_region);
        double left = Math.Clamp(Math.Floor(local.Left), 0, Math.Max(0, _region.Width - 1));
        double top = Math.Clamp(Math.Floor(local.Top), 0, Math.Max(0, _region.Height - 1));
        double width = Math.Min(Math.Max(1, Math.Ceiling(local.Width)), Math.Max(1, _region.Width - left));
        double height = Math.Min(Math.Max(1, Math.Ceiling(local.Height)), Math.Max(1, _region.Height - top));
        PhysicalBounds = new RectD(_region.Left + left, _region.Top + top, width, height);
        Width = width / _scale;
        Height = height / _scale;
        Left = PhysicalBounds.Left / _scale;
        Top = PhysicalBounds.Top / _scale;
        if (_hwnd != IntPtr.Zero) PhysicalWindowPositioner.PlaceTopmostWithoutActivation(_hwnd, PhysicalBounds);
    }

    private static double ValidScale(double value) => double.IsFinite(value) && value > 0 ? value : 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
