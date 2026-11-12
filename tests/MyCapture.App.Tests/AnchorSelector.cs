using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Projects each renderer's actual anchor/readout drawings into the shared overlay-DIP contract.</summary>
internal sealed class AnchorSelector : IDisposable
{
    private readonly CaptureOverlayView? _wpf;
    private readonly NativeCaptureOverlay? _native;
    private readonly RectD _bounds;
    private readonly double _scale;
    private readonly AppTheme _previousTheme = ThemeService.Current;
    private readonly AppAccent _previousAccent = ThemeService.CurrentAccent;
    private readonly string _previousGuideColor = ThemeService.CaptureGuideColor;
    private readonly bool? _previousHighContrast = ThemeService.SystemHighContrastOverrideForTest;
    private readonly bool _scopedTheme;
    private NativeSelectorChrome? _chrome;
    private Func<PointD> _pointer = static () => new PointD(0, 0);
    private bool _disposed;

    internal AnchorSelector(bool native, FrozenFrame frame, double scale)
    {
        _bounds = frame.ScreenBounds;
        _scale = scale;
        if (!native)
        {
            _wpf = new CaptureOverlayView(frame, showMagnifier: false) { PlaceCursor = _ => false };
            return;
        }
        _scopedTheme = true;
        // Match the existing WPF fixture's explicit Daylight/Violet palette. These methods update
        // only in-memory theme state; no Application or user settings/profile is created.
        ThemeService.SystemHighContrastOverrideForTest = false;
        ThemeService.ApplyFromSettings(AppThemeNames.ToSetting(AppTheme.Daylight), null,
            AppAccentNames.ToSetting(AppAccent.Violet));
        // Persisted legacy names migrate to GlassLight; this shared fixture explicitly uses Daylight.
        ThemeService.Apply(AppTheme.Daylight);
        try
        {
            MonitorInfo[] monitors = [new("fixture", _bounds, _bounds, (uint)Math.Round(96 * scale), true)];
            _native = new NativeCaptureOverlay(_bounds, false, false)
            {
                ReadCursor = () => _pointer(), ReadMonitors = () => monitors, PlaceCursor = _ => false,
            };
            _native.AttachFrame(frame);
        }
        catch { Dispose(); throw; }
    }

    internal Func<PointD> ReadCursor
    {
        set { _pointer = value; if (_wpf is not null) _wpf.ReadCursor = value; }
    }

    internal Func<PointD, bool> PlaceCursor
    {
        set { if (_native is { } native) native.PlaceCursor = value; else _wpf!.PlaceCursor = value; }
    }

    internal bool? SimulatedPointerInside
    {
        set { if (_wpf is not null) _wpf.SimulatedPointerInside = value; }
    }

    internal void UsePalette(IReadOnlyDictionary<string, Brush> palette)
    {
        if (_wpf is not null) CaptureCueEvidenceTests.UsePalette(_wpf, palette);
        else
        {
            IReadOnlyDictionary<string, Brush> actual = CaptureGuidePalette.Resolve();
            foreach ((string key, Brush brush) in palette)
                Assert.Equal(Assert.IsType<SolidColorBrush>(brush).Color,
                    Assert.IsType<SolidColorBrush>(actual[key]).Color);
        }
    }

    internal void InitializePointer()
    {
        if (_native is not null) _native.UpdatePointer(false, false); else _wpf!.InitializePointer();
    }

    private PointD LocalPointer() => _pointer().Offset(-_bounds.Left, -_bounds.Top);

    internal void SimulatePointerDown()
    {
        if (_native is not null) _native.BeginSelection(LocalPointer()); else _wpf!.SimulatePointerDown();
    }

    internal void SimulatePointerMove()
    {
        if (_native is not null)
        {
            _native.UpdatePointer(false, false);
            _native.MoveSelection(LocalPointer());
        }
        else _wpf!.SimulatePointerMove();
    }

    internal void FlushFeedbackForTest()
    {
        if (_wpf is not null) _wpf.FlushFeedbackForTest();
        else
        {
            _chrome ??= new NativeSelectorChrome(_native!, false, false);
            _chrome.Update(_pointer(), _native!.SelectionRectangle);
        }
    }

    internal RenderTargetBitmap RenderFrame(double scale)
    {
        if (_wpf is not null) return CaptureCueEvidenceTests.Render(_wpf, scale);
        FlushFeedbackForTest();
        return NativeSelectorTestRenderer.Render(_native!, _chrome!);
    }

    internal Rect? AnchorChipBoundsForTest => _wpf is not null
        ? _wpf.AnchorChipBoundsForTest : _chrome!.AnchorChipBoundsForTest;
    internal Rect? DeferredGuideReadout
    {
        get
        {
            if (_wpf is not null) return _wpf.DeferredGuideReadout;
            RectD physical = _chrome!.SizeChipBoundsForTest;
            return new Rect((physical.Left - _bounds.Left) / _scale, (physical.Top - _bounds.Top) / _scale,
                physical.Width / _scale, physical.Height / _scale);
        }
    }
    internal double ActualWidth => _wpf?.ActualWidth ?? _bounds.Width / _scale;
    internal double ActualHeight => _wpf?.ActualHeight ?? _bounds.Height / _scale;

    internal DrawingVisual Visual(string field)
    {
        if (_wpf is not null)
            return (DrawingVisual)typeof(CaptureOverlayView).GetField(field,
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_wpf)!;
        var visual = new DrawingVisual();
        using DrawingContext dc = visual.RenderOpen();
        if (field == "_anchorVisual") dc.DrawDrawing(_chrome!.AnchorDrawingForTest);
        else if (field == "_selectionVisual") dc.DrawDrawing(ProjectReadout());
        else throw new ArgumentOutOfRangeException(nameof(field));
        return visual;
    }

    private GeometryDrawing ProjectReadout()
    {
        GeometryDrawing source = Assert.Single(Leaves(_chrome!.ReadoutDrawingForTest).OfType<GeometryDrawing>(),
            drawing => drawing.Geometry is RectangleGeometry { RadiusX: > 0 });
        var rectangle = Assert.IsType<RectangleGeometry>(source.Geometry);
        RectD physical = _chrome.SizeChipBoundsForTest;
        // The Border's actual local geometry is the evidence. Only translate it from its bounded
        // HWND into overlay DIPs; neither the expected chip bounds nor a radius constant is used.
        Rect local = rectangle.Rect;
        var global = new Rect(local.Left + (physical.Left - _bounds.Left) / _scale,
            local.Top + (physical.Top - _bounds.Top) / _scale, local.Width, local.Height);
        return new GeometryDrawing(source.Brush, source.Pen,
            new RectangleGeometry(global, rectangle.RadiusX, rectangle.RadiusY));
    }

    private static IEnumerable<Drawing> Leaves(Drawing? drawing) => drawing switch
    {
        DrawingGroup group => group.Children.SelectMany(Leaves),
        null => [],
        _ => [drawing],
    };

    internal void ReleaseResources() => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _chrome?.Dispose(); _native?.Close(); _wpf?.ReleaseResources(); }
        finally
        {
            if (_scopedTheme)
            {
                ThemeService.SystemHighContrastOverrideForTest = _previousHighContrast;
                ThemeService.ApplyFromSettings(AppThemeNames.ToSetting(_previousTheme), _previousGuideColor,
                    AppAccentNames.ToSetting(_previousAccent));
                ThemeService.Apply(_previousTheme);
            }
        }
    }
}
