using System.Windows;
using System.Windows.Media;

namespace MyCapture.App.Capture;

internal readonly record struct CaptureKeyPanelPlacement(Rect WorkArea, Point Pointer, Rect? Highlight,
    Rect? ModeBar, Rect? Magnifier, Rect? Readout, Rect? HoverTab, double Scale, double PixelX = 1, double PixelY = 1);

/// <summary>Renderer geometry and ownership for the shared deferred panel.</summary>
internal interface ICaptureKeyPanelHost
{
    bool ShouldShow { get; }
    bool CanPresent { get; }
    CaptureKeyPanelContext Context { get; }
    FontFamily UiFont { get; }
    FontFamily MonoFont { get; }
    IReadOnlyDictionary<string, Brush> Palette { get; }
    CaptureKeyPanelPlacement Placement { get; }
    void Add(CaptureKeyPanel panel);
    void Remove(CaptureKeyPanel panel);
    void Place(CaptureKeyPanel? active, Rect? bounds, double scale);
}

/// <summary>Reached only for an explicitly requested or eligible panel, after the presented frame.</summary>
internal sealed class CaptureKeyPanelController
{
    private readonly ICaptureKeyPanelHost _host;
    private CaptureKeyPanel? _ready;
    private CaptureKeyPanel? _selection;
    private CaptureKeyPanel? _active;
    private bool _right;

    internal CaptureKeyPanelController(ICaptureKeyPanelHost host) => _host = host;
    internal CaptureKeyPanel? Active => _active;
    internal IEnumerable<CaptureKeyPanel> Panels
    {
        get
        {
            if (_ready is not null) yield return _ready;
            if (_selection is not null) yield return _selection;
        }
    }

    internal static void Attach(CaptureOverlayView view)
    {
        Diagnostics.SelectorTimeline.Mark?.Invoke("key-panel-controller");
        var controller = new CaptureKeyPanelController(new WpfHost(view));
        view.RegisterDeferredGuide(controller.Update, controller.ApplyPalette, controller.Release, () => controller._active);
        controller.Update();
    }

    internal void Update()
    {
        if (!_host.ShouldShow)
        {
            if (_ready is not null) _ready.Visibility = Visibility.Hidden;
            if (_selection is not null) _selection.Visibility = Visibility.Hidden;
            _host.Place(_active, null, 1);
            return;
        }
        if (!_host.CanPresent) return;
        if (_ready is null)
        {
            Diagnostics.SelectorTimeline.Mark?.Invoke("key-panel-construct");
            _ready = Create();
            _selection = Create();
            CaptureKeyPanel Create()
            {
                var panel = new CaptureKeyPanel(_host.UiFont, _host.MonoFont);
                panel.ApplyPalette(_host.Palette);
                _host.Add(panel);
                return panel;
            }
        }
        CaptureKeyPanelContext context = _host.Context;
        Prepare(_ready, false);
        Prepare(_selection!, true);
        bool selected = context.HasSelection;
        _active = selected ? _selection : _ready;
        (selected ? _ready : _selection)!.Visibility = Visibility.Hidden;
        void Prepare(CaptureKeyPanel panel, bool hasSelection)
        {
            bool changed = panel.Configure(context with { HasSelection = hasSelection });
            if (changed || !panel.IsMeasureValid)
            {
                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
            }
        }
        CaptureKeyPanelPlacement geometry = _host.Placement;
        double scale = geometry.Scale;
        Size size = _active!.DesiredSize;
        Rect placed = CaptureKeyPanelLayout.Place(geometry.WorkArea, new Size(size.Width * scale, size.Height * scale),
            geometry.Pointer, geometry.Highlight, geometry.ModeBar, geometry.Magnifier,
            geometry.Readout, scale, ref _right, geometry.HoverTab);
        _active.Visibility = placed.IsEmpty ? Visibility.Hidden : Visibility.Visible;
        _host.Place(_active, placed.IsEmpty ? null : placed, scale);
    }

    internal void ApplyPalette()
    {
        _ready?.ApplyPalette(_host.Palette);
        _selection?.ApplyPalette(_host.Palette);
    }

    internal void Release()
    {
        if (_ready is not null) _host.Remove(_ready);
        if (_selection is not null) _host.Remove(_selection);
        _ready?.ReleaseResources();
        _selection?.ReleaseResources();
        _active = _ready = _selection = null;
    }

    private sealed class WpfHost(CaptureOverlayView view) : ICaptureKeyPanelHost
    {
        private readonly ScaleTransform _scale = new();
        private readonly TranslateTransform _offset = new();
        private TransformGroup? _transform;

        public bool ShouldShow => view.ShowKeyPanel && !view.DeferredGuideSuppressed;
        public bool CanPresent => view.DeferredGuideHasFrame && view.ActualWidth > 0 && view.ActualHeight > 0;
        public CaptureKeyPanelContext Context => new(view.ShowKeyPanel, view.DeferredGuideCanDrag,
            view.DeferredGuideWindowDetection, view.DeferredGuideHasSelection, view.DeferredGuideRatioAvailable,
            view.CopyColorText is not null, view.TextCaptureAvailable, view.TextCaptureMode,
            view.PinSelectionAvailable, view.PasteToScreenHotkey, view.FreeformAvailable,
            view.Mode == CaptureSelectorMode.Freeform);
        public FontFamily UiFont => view.DeferredGuideUiFont;
        public FontFamily MonoFont => view.DeferredGuideMonoFont;
        public IReadOnlyDictionary<string, Brush> Palette => view.GuidePalette;
        public CaptureKeyPanelPlacement Placement
        {
            get
            {
                CaptureGuideArea area = CaptureModeBarLayout.ChooseArea(view.DeferredGuideMonitors,
                    view.DeferredGuidePointer.Offset(view.DeferredGuideScreenBounds.Left, view.DeferredGuideScreenBounds.Top),
                    view.DeferredGuideScreenBounds);
                double dipX = view.DeferredGuideDipPerPixelX, dipY = view.DeferredGuideDipPerPixelY;
                return new(CaptureModeBarLayout.ToDip(area.WorkArea, dipX, dipY),
                    new Point(view.DeferredGuidePointer.X * dipX, view.DeferredGuidePointer.Y * dipY),
                    view.DeferredGuideHighlight, view.ModeBarBoundsForTest, view.DeferredGuideMagnifier,
                    view.DeferredGuideReadout, view.DeferredGuideHoverTab,
                    CaptureModeBarLayout.ContentScale(area.Scale, dipX), view.GuidePixelSizeX, view.GuidePixelSizeY);
            }
        }

        public void Add(CaptureKeyPanel panel)
        {
            if (_transform is null)
            {
                _transform = new TransformGroup();
                _transform.Children.Add(_scale);
                _transform.Children.Add(_offset);
            }
            panel.RenderTransform = _transform;
            view.AddDeferredGuide(panel);
        }

        public void Remove(CaptureKeyPanel panel) => view.RemoveDeferredGuide(panel);

        public void Place(CaptureKeyPanel? active, Rect? bounds, double scale)
        {
            view.KeyPanelBoundsForTest = bounds;
            if (bounds is not { } placed) return;
            _scale.ScaleX = _scale.ScaleY = scale;
            _offset.X = Math.Round(placed.X / view.GuidePixelSizeX) * view.GuidePixelSizeX;
            _offset.Y = Math.Round(placed.Y / view.GuidePixelSizeY) * view.GuidePixelSizeY;
        }
    }
}
