using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Exercises the hover contract against each renderer's actual reveal, border, and label.</summary>
internal sealed class GlideSelector : Decorator
{
    private readonly CaptureOverlayView? _wpf;
    private readonly NativeCaptureOverlay? _native;
    private readonly NativeSelectorChrome? _chrome;
    private readonly Func<PointD> _pointer;
    private bool _released;

    internal GlideSelector(bool native, Func<PointD> pointer, IReadOnlyList<WindowCandidate> windows)
    {
        _pointer = pointer;
        if (!native)
        {
            _wpf = WindowDetectOverlayTests.DetectingView(pointer);
            _wpf.PlaceCursor = _ => false;
            _wpf.WindowCandidates = windows;
            _wpf.SelectionConfirmed += (_, e) => SelectionConfirmed?.Invoke(this, e);
            Child = _wpf;
            return;
        }

        FrozenFrame frame = WindowDetectOverlayTests.Frame();
        MonitorInfo[] monitors = [new("fixture", frame.ScreenBounds, frame.ScreenBounds, 96, true)];
        _native = new NativeCaptureOverlay(frame.ScreenBounds, false, false)
        {
            ReadCursor = pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
            SourceWindows = windows, DetectWindows = true,
        };
        _native.AttachFrame(frame);
        _native.PreparePointerFeedback();
        _native.SelectionCompleted += (_, e) => SelectionConfirmed?.Invoke(this,
            new RegionSelectionEventArgs(e.BitmapRegion)
            {
                // The native public completion contract reports source identity as a title.
                // Resolve that actual result, independently of the displayed or hovered rectangle.
                DetectedWindow = windows.FirstOrDefault(window => window.Title == e.SourceTitle),
                TextCapture = e.TextCapture,
            });
        _chrome = new NativeSelectorChrome(_native, false, false);
        Loaded += (_, _) => InitializePointer();
    }

    internal event EventHandler<RegionSelectionEventArgs>? SelectionConfirmed;
    internal Func<TimeSpan> ReadClock
    {
        set { if (_native is not null) _native.ReadClock = value; else _wpf!.ReadClock = value; }
    }
    internal RectD? ShownHoverForTest => _native is not null ? _native.ShownHoverForTest : _wpf!.ShownHoverForTest;
    internal RectD? HoverRegionForTest => _native is not null ? _native.HoverRegion : _wpf!.HoverRegionForTest;
    internal bool HoverGlidingForTest => _native?.HoverGlidingForTest ?? _wpf!.HoverGlidingForTest;
    internal bool HasPendingFeedback => _native?.HasPendingFeedback ?? _wpf!.HasPendingFeedback;
    internal Rect? TargetTabForTest => _chrome is not null ? _chrome.TargetTabForTest : _wpf!.TargetTabForTest;
    internal string? TargetLabelForTest => _chrome is not null ? _chrome.TargetLabelForTest : _wpf!.TargetLabelForTest;
    internal int StaticRenderCount => _native?.DibAllocations ?? _wpf!.StaticRenderCount;
    internal int DimmerRenderCount => _native is not null ? _native.DisplayDib!.InitializeCount : _wpf!.DimmerRenderCount;

    internal void InitializePointer()
    {
        if (_native is not null) UpdatePointer(false, false); else _wpf!.InitializePointer();
    }

    internal void UpdatePointer(bool precision, bool active)
    {
        if (_native is not null)
        {
            _native.UpdatePointer(precision, active);
            _native.RefreshHover();
        }
        else _wpf!.UpdatePointer(precision, active);
    }

    internal void Draw()
    {
        if (_wpf is not null)
        {
            _wpf.InvalidateVisual();
            _wpf.Measure(new Size(640, 360));
            _wpf.Arrange(new Rect(0, 0, 640, 360));
            _wpf.UpdateLayout();
        }
        else FlushFeedbackForTest();
    }

    internal void FlushFeedbackForTest()
    {
        if (_wpf is not null) _wpf.FlushFeedbackForTest();
        else
        {
            _native!.FlushFeedbackForTest();
            _chrome!.Update(_native.EffectivePointer, _native.DisplayedRegionForTest);
            _chrome.FlushHoverTabForTest();
        }
    }

    internal Rect RevealClip
    {
        get
        {
            if (_wpf is not null)
                return ((RectangleGeometry)WindowDetectOverlayTests.Visual(_wpf, "_revealVisual").Clip).Rect;
            Int32Rect reveal = _native!.DisplayDib!.Reveal;
            return new Rect(reveal.X, reveal.Y, reveal.Width, reveal.Height);
        }
    }

    internal object Field(string name)
    {
        if (_wpf is not null)
            return typeof(CaptureOverlayView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_wpf)!;
        if (name == "_tabLabelText") return Assert.IsType<System.Windows.Media.FormattedText>(_chrome!.HoverLabelLayoutForTest);
        throw new ArgumentOutOfRangeException(nameof(name));
    }

    internal Rect SelectionContentBounds
    {
        get
        {
            if (_wpf is not null) return WindowDetectOverlayTests.Visual(_wpf, "_selectionVisual").ContentBounds;
            // Match WPF's separate selection visual: temporarily hide the pointer/axis layer.
            // Hover has no drag anchor. Target, shown rectangle, clock and label remain untouched.
            FieldInfo initialized = typeof(NativeCaptureOverlay).GetField("_pointerInitialized",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool previous = (bool)initialized.GetValue(_native)!;
            initialized.SetValue(_native, false);
            try
            {
                _native!.UpdateVisualFeedback();
                NativeSelectorDib display = _native.DisplayDib!;
                using var painted = new NativeSelectorDib(_native.Frame!.Bitmap, new SolidColorBrush(Colors.Transparent));
                _native.PaintTo(painted.Dc);
                Assert.True(NativeSelectorInterop.GdiFlush());
                byte[] before = new byte[display.ByteLength], after = new byte[display.ByteLength];
                Marshal.Copy(display.Bits, before, 0, before.Length);
                Marshal.Copy(painted.Bits, after, 0, after.Length);
                int left = display.Width, top = display.Height, right = -1, bottom = -1;
                for (int y = 0; y < display.Height; y++)
                    for (int x = 0; x < display.Width; x++)
                    {
                        int offset = y * display.Stride + x * 4;
                        if (before[offset] == after[offset] && before[offset + 1] == after[offset + 1]
                            && before[offset + 2] == after[offset + 2]) continue;
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                    }
                // PaintTo copies the retained DIB and adds the actual GDI selection band.
                Assert.True(right >= left && bottom >= top, "The native selection band painted no pixels.");
                return new Rect(left, top, right - left + 1, bottom - top + 1);
            }
            finally
            {
                initialized.SetValue(_native, previous);
                _native!.UpdateVisualFeedback();
            }
        }
    }

    internal void SimulatePointerDown()
    {
        if (_native is not null) _native.BeginSelection(_pointer()); else _wpf!.SimulatePointerDown();
    }

    internal void SimulatePointerUp()
    {
        if (_native is not null) _native.EndSelection(_pointer()); else _wpf!.SimulatePointerUp();
    }

    internal void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        _chrome?.Dispose();
        _native?.Close();
        _wpf?.ReleaseResources();
    }
}
