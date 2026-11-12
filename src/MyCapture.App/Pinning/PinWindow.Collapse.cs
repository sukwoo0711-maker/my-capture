using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

internal sealed partial class PinWindow
{
    private PinContent? _thumbnailContent;
    private BitmapSource? _thumbnail;
    private MenuItem? _collapseItem;

    internal void ToggleCollapsed()
    {
        if (_isClosed || _state.IsClickThrough) return;
        // A folded pin is a WPF thumbnail; a native pin moves to its WPF window first.
        if (_native is not null) { SwitchToWpf(PinSwitchReason.Command, ToggleCollapsed); return; }
        FinishZoomMotion();
        var origin = PhysicalBounds;
        LeaveTextSelection(restoreFocus: false);
        EndDrag();
        _ctrlClickTimer.Stop();
        _zoomSettleTimer.Stop();
        _liveZooming = false;
        _imageElement.CacheMode = null;
        RenderOptions.SetBitmapScalingMode(_imageElement, BitmapScalingMode.HighQuality);
        HideHoverBar();
        _heldFeedback = null;
        _feedbackCountdown.Stop();
        HideFeedback(immediate: true);
        CancelPendingProgress();
        _state.SetCollapsed(!_state.IsCollapsed);
        Width = _state.WidthDip;
        Height = _state.HeightDip;
        BeginAnimation(OpacityProperty, null);
        Opacity = _state.Opacity;
        _pinScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _pinScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _pinScale.ScaleX = _pinScale.ScaleY = 1;
        // There is no existing pin size transition. An immediate resize also respects reduced motion.
        DpiScale displayDpi = VisualTreeHelper.GetDpi(this);
        if (!_state.IsCollapsed)
            RerenderAt(displayDpi.DpiScaleX, ZoomRasterScale(displayDpi.DpiScaleX, _state.Zoom), notify: false);
        UpdateImageRasterLayout();
        ShowDisplayAt(new Size(Width, Height), displayDpi);
        if (_handle != IntPtr.Zero)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double left = origin.Left, top = origin.Top;
            if (!_state.IsCollapsed)
            {
                RectD work = ReadWorkArea(_handle);
                (left, top) = PinGeometry.KeepGrabbable(left, top, Width * dpi.DpiScaleX,
                    Height * dpi.DpiScaleY, work.Left, work.Top, work.Width, work.Height,
                    GrabMarginDip * dpi.DpiScaleX);
            }
            PhysicalWindowPositioner.Move(_handle, new RectD(left, top, Width, Height));
        }
        RefreshCollapseMenu();
        ViewStateChanged?.Invoke();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        HandleCollapseDoubleClick(e);
    }

    private bool HandleCollapseDoubleClick(MouseButtonEventArgs e)
    {
        if (e.Handled || _isClosed || _state.IsClickThrough || IsTextSelectionMode || e.ClickCount < 2) return false;
        ModifierKeys modifiers = _dragInput.Modifiers;
        if (!modifiers.HasFlag(ModifierKeys.Shift) && !(_state.IsCollapsed && modifiers == ModifierKeys.None)) return false;
        ToggleCollapsed();
        e.Handled = true;
        return true;
    }

    private void RefreshCollapseMenu()
    {
        ToolTip = UiText.Get(_state.IsCollapsed ? "Pin_CollapsedTip" : "Onboard_PinTip");
        AutomationProperties.SetHelpText(this, _state.IsCollapsed ? UiText.Get("Pin_CollapsedTip") :
            (_originalText is null ? UiText.Format("Text_E22E2C953A8B", ClickThroughRecoveryHint()) : UiText.Get("Text_55694B0252DB"))
            + " " + UiText.Get("Pin_OrganizeHelp"));
        if (_collapseItem is not null)
        {
            _collapseItem.Header = UiText.Get(_state.IsCollapsed ? "Pin_Expand" : "Pin_Collapse");
            _collapseItem.IsEnabled = !_state.IsClickThrough;
        }
        if (ContextMenu is not { } menu) return;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            if (item.InputGestureText is "0" or "+" or "-" or "F2") item.IsEnabled = !_state.IsCollapsed;
    }

    private void ShowThumbnail(DpiScale dpi)
    {
        PinContent content = DisplayContent;
        int width = Math.Max(1, (int)Math.Ceiling(_state.WidthDip * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(_state.HeightDip * dpi.DpiScaleY));
        if (_thumbnail is null || !ReferenceEquals(_thumbnailContent, content)
            || _thumbnail.PixelWidth != width || _thumbnail.PixelHeight != height)
        {
            ImageSource source = content.Display;
            // Shrink the expanded viewport, including its top-left text fit and background.
            // Only the viewport's extreme aspect ratio can require the thumbnail's centre crop.
            double scale = Math.Max(_state.WidthDip / _state.ImageWidthDip,
                _state.HeightDip / _state.ImageHeightDip);
            var viewport = new Rect((_state.WidthDip - _state.ImageWidthDip * scale) / 2,
                (_state.HeightDip - _state.ImageHeightDip * scale) / 2,
                _state.ImageWidthDip * scale, _state.ImageHeightDip * scale);
            double drawWidth = _content.CanRerender ? source.Width * TranslationFitScale : _state.ImageWidthDip;
            double drawHeight = _content.CanRerender ? source.Height * TranslationFitScale : _state.ImageHeightDip;
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.PushClip(new RectangleGeometry(viewport));
                if (_showingMarkdownSource || _showingTranslation && _content.Kind != PinContentKind.Image)
                    drawing.DrawRectangle(Brushes.White, null, viewport);
                drawing.DrawImage(source, new Rect(viewport.X, viewport.Y, drawWidth * scale, drawHeight * scale));
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            _thumbnail = bitmap;
            _thumbnailContent = content;
        }
        _imageElement.Source = _thumbnail;
        // Keep source pixels and translation layers for expansion, not a second full-sized raster.
        ReleaseCollapsedDisplayCaches();
    }

    private void ReleaseCollapsedDisplayCaches()
    {
        DisplayContent.ReleaseDisplayCache();
        _translationOriginal?.ReleaseDisplayCache();
        _translationResult?.ReleaseDisplayCache();
        ClipboardCache.Clear();
    }
}
