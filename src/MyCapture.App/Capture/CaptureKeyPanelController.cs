using System.Windows;
using System.Windows.Media;

namespace MyCapture.App.Capture;

/// <summary>Reached only for an explicitly requested or eligible panel, after the presented frame.</summary>
internal sealed class CaptureKeyPanelController
{
    private readonly CaptureOverlayView _view;
    private CaptureKeyPanel? _ready;
    private CaptureKeyPanel? _selection;
    private CaptureKeyPanel? _active;
    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _offset = new();
    private bool _right;

    private CaptureKeyPanelController(CaptureOverlayView view) => _view = view;

    internal static void Attach(CaptureOverlayView view)
    {
        Diagnostics.SelectorTimeline.Mark?.Invoke("key-panel-controller");
        var controller = new CaptureKeyPanelController(view);
        view.RegisterDeferredGuide(controller.Update, controller.ApplyPalette, controller.Release, () => controller._active);
        controller.Update();
    }

    private void Update()
    {
        if (!_view.ShowKeyPanel || _view.DeferredGuideSuppressed)
        {
            if (_ready is not null) _ready.Visibility = Visibility.Hidden;
            if (_selection is not null) _selection.Visibility = Visibility.Hidden;
            _view.KeyPanelBoundsForTest = null;
            return;
        }
        if (!_view.DeferredGuideHasFrame || _view.ActualWidth <= 0 || _view.ActualHeight <= 0) return;
        if (_ready is null)
        {
            Diagnostics.SelectorTimeline.Mark?.Invoke("key-panel-construct");
            var transform = new TransformGroup();
            transform.Children.Add(_scale);
            transform.Children.Add(_offset);
            _ready = Create();
            _selection = Create();
            CaptureKeyPanel Create()
            {
                var panel = new CaptureKeyPanel(_view.DeferredGuideUiFont, _view.DeferredGuideMonoFont);
                panel.ApplyPalette(_view.GuidePalette);
                panel.RenderTransform = transform;
                _view.AddDeferredGuide(panel);
                return panel;
            }
        }
        Prepare(_ready, false);
        Prepare(_selection!, true);
        bool selected = _view.DeferredGuideHasSelection;
        _active = selected ? _selection : _ready;
        (selected ? _ready : _selection)!.Visibility = Visibility.Hidden;
        void Prepare(CaptureKeyPanel panel, bool hasSelection)
        {
            bool changed = panel.Configure(new CaptureKeyPanelContext(_view.ShowKeyPanel, _view.DeferredGuideCanDrag,
                _view.DeferredGuideWindowDetection, hasSelection, _view.DeferredGuideRatioAvailable, _view.CopyColorText is not null,
                _view.TextCaptureAvailable, _view.TextCaptureMode, _view.PinSelectionAvailable, _view.PasteToScreenHotkey));
            if (changed || !panel.IsMeasureValid)
            {
                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
            }
        }
        CaptureGuideArea area = CaptureModeBarLayout.ChooseArea(_view.DeferredGuideMonitors,
            _view.DeferredGuidePointer.Offset(_view.DeferredGuideScreenBounds.Left, _view.DeferredGuideScreenBounds.Top),
            _view.DeferredGuideScreenBounds);
        double dipX = _view.DeferredGuideDipPerPixelX, dipY = _view.DeferredGuideDipPerPixelY;
        Rect work = CaptureModeBarLayout.ToDip(area.WorkArea, dipX, dipY);
        double scale = CaptureModeBarLayout.ContentScale(area.Scale, dipX);
        Size size = _active!.DesiredSize;
        Rect placed = CaptureKeyPanelLayout.Place(work, new Size(size.Width * scale, size.Height * scale),
            new Point(_view.DeferredGuidePointer.X * dipX, _view.DeferredGuidePointer.Y * dipY),
            _view.DeferredGuideHighlight, _view.ModeBarBoundsForTest, _view.DeferredGuideMagnifier,
            _view.DeferredGuideReadout, scale, ref _right, _view.DeferredGuideHoverTab);
        _active.Visibility = placed.IsEmpty ? Visibility.Hidden : Visibility.Visible;
        _view.KeyPanelBoundsForTest = placed.IsEmpty ? null : placed;
        if (placed.IsEmpty) return;
        _scale.ScaleX = _scale.ScaleY = scale;
        _offset.X = Math.Round(placed.X / _view.GuidePixelSizeX) * _view.GuidePixelSizeX;
        _offset.Y = Math.Round(placed.Y / _view.GuidePixelSizeY) * _view.GuidePixelSizeY;
    }

    private void ApplyPalette()
    {
        _ready?.ApplyPalette(_view.GuidePalette);
        _selection?.ApplyPalette(_view.GuidePalette);
    }

    private void Release()
    {
        if (_ready is not null) _view.RemoveDeferredGuide(_ready);
        if (_selection is not null) _view.RemoveDeferredGuide(_selection);
        _ready?.ReleaseResources();
        _selection?.ReleaseResources();
        _active = _ready = _selection = null;
    }
}
