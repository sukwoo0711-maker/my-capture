using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

internal sealed partial class NativeSelectorChrome
{
    private readonly HashSet<Window> _deferredWindows = [];
    private CaptureKeyPanelController? _keyPanelController;
    private Canvas? _keyPanelCanvas;
    private Window? _keyPanelWindow, _startGuideWindow;
    private CaptureStartGuideCard? _startGuideCard;
    private bool _keyPanelReady, _keyPanelContentPrepared;
    private Rect? _staticReadoutBounds;
    internal FrameworkElement? KeyPanel => _keyPanelController?.Active;
    internal IEnumerable<CaptureKeyPanel> KeyPanels => _keyPanelController?.Panels ?? [];
    internal Rect? KeyPanelBounds { get; private set; }
    internal CaptureStartGuideCard? StartGuideCard => _startGuideCard;

    internal void UpdateGuides()
    {
        if (_closed || _owner is not { FirstFramePresentedForTest: true } owner) return;
        UpdateStartGuide(owner);
        if (owner.ShowKeyPanel && !owner.RecordingSelection && !CaptureOverlayView.SuppressKeyPanelForProbe)
        {
            if (!_keyPanelContentPrepared)
            {
                owner.PrepareKeyPanelContent();
                _keyPanelContentPrepared = true;
            }
            if (_keyPanelController is null)
            {
                SelectorTimeline.Mark?.Invoke("key-panel-controller");
                _keyPanelController = new CaptureKeyPanelController(new KeyPanelHost(this));
            }
        }
        _keyPanelController?.Update();
    }

    private Window CreateDeferred(FrameworkElement content)
    {
        Window window = Create(content, 1, 1, interactive: false);
        _deferredWindows.Add(window);
        new WindowInteropHelper(window).Owner = _owner!.Handle;
        bool excluded = CaptureWindowExclusion.TryApply(window);
        AllCaptureExcluded &= excluded;
        if (_owner.RequireCaptureExclusion && !excluded) throw new InvalidOperationException(UiText.Get("Text_D1F0DAEAA780"));
        CaptureWindowExclusion.DisableTransitions(window);
        return window;
    }

    private void PlaceGuideWindow(Window window, FrameworkElement content, Rect bounds, double scale)
    {
        if (_owner is not { } owner) return;
        double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        double contentScale = scale / dpi;
        if (content.LayoutTransform is not ScaleTransform transform || Math.Abs(transform.ScaleX - contentScale) > 0.0001)
            content.LayoutTransform = new ScaleTransform(contentScale, contentScale);
        window.Width = bounds.Width / dpi;
        window.Height = bounds.Height / dpi;
        double x = bounds.X + owner.ScreenBounds.Left, y = bounds.Y + owner.ScreenBounds.Top;
        window.Left = x / dpi;
        window.Top = y / dpi;
        // The HWND is already owned and topmost. Guide updates, including F1 and pointer movement,
        // never restack it; that would feed synthetic mouse moves back into the selector.
        PhysicalWindowPositioner.Move(new WindowInteropHelper(window).Handle,
            new RectD(x, y, bounds.Width, bounds.Height));
        if (_showingWindows && !window.IsVisible) window.Show();
    }

    private Rect LocalBounds(Window window)
    {
        RectD screen = _owner!.ScreenBounds;
        double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        return new Rect(window.Left * dpi - screen.Left, window.Top * dpi - screen.Top,
            window.Width * dpi, window.Height * dpi);
    }

    private void UpdateStartGuide(NativeCaptureOverlay owner)
    {
        if (!owner.ShowStartGuide)
        {
            if (_startGuideCard is not null) _startGuideCard.Visibility = Visibility.Collapsed;
            _startGuideWindow?.Hide();
            return;
        }
        if (_startGuideCard is null)
        {
            _startGuideCard = new CaptureStartGuideCard();
            TextElement.SetFontFamily(_startGuideCard, ThemeFonts.Ui);
            _startGuideCard.ApplyPalette(CaptureGuidePalette.Resolve());
            _startGuideWindow = CreateDeferred(_startGuideCard);
        }
        _startGuideCard.Configure(owner.UsesWindowDetection && owner.Mode != CaptureSelectorMode.Color, owner.RecordingSelection);
        _startGuideCard.Visibility = Visibility.Visible;
        _startGuideCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _startGuideCard.Arrange(new Rect(_startGuideCard.DesiredSize));
        CaptureGuideArea area = CaptureModeBarLayout.ChooseArea(owner.ReadMonitors(), owner.ReadCursor(), owner.ScreenBounds);
        double scale = area.Scale > 0 ? area.Scale : 1;
        // DesiredSize includes a previous layout scale; use the natural card size here.
        double previousScale = (_startGuideCard.LayoutTransform as ScaleTransform)?.ScaleX ?? 1;
        Size size = new(_startGuideCard.DesiredSize.Width / previousScale * scale,
            _startGuideCard.DesiredSize.Height / previousScale * scale);
        Rect placed = CaptureModeBarLayout.PlaceCard(CaptureModeBarLayout.ToDip(area.WorkArea, 1, 1), size,
            new Size(owner.ScreenBounds.Width, owner.ScreenBounds.Height), bar: LocalBounds(_barWindow), scale: scale);
        PlaceGuideWindow(_startGuideWindow!, _startGuideCard, placed, scale);
    }

    private void ResetGuides()
    {
        _keyPanelReady = _keyPanelContentPrepared = false;
        KeyPanelBounds = null;
        foreach (CaptureKeyPanel panel in KeyPanels) panel.Visibility = Visibility.Hidden;
        if (_startGuideCard is not null) _startGuideCard.Visibility = Visibility.Collapsed;
    }

    private sealed class KeyPanelHost(NativeSelectorChrome chrome) : ICaptureKeyPanelHost
    {
        private NativeCaptureOverlay Owner => chrome._owner!;
        public bool ShouldShow => chrome._owner is { ShowKeyPanel: true, ShowStartGuide: false, RecordingSelection: false, IsClosed: false }
            && !CaptureOverlayView.SuppressKeyPanelForProbe;
        public bool CanPresent => chrome._owner is { FirstFramePresentedForTest: true, Frame: not null };
        public CaptureKeyPanelContext Context => new(Owner.ShowKeyPanel, Owner.Mode != CaptureSelectorMode.Color,
            Owner.UsesWindowDetection && Owner.Mode != CaptureSelectorMode.Color,
            Owner.FreeformStrokeActive || (Owner.SelectionRectangle.Width >= 2 && Owner.SelectionRectangle.Height >= 2),
            Owner.Mode is not (CaptureSelectorMode.Color or CaptureSelectorMode.Freeform) && Owner.PresetSize is null, Owner.CopyColorText is not null,
            Owner.TextCaptureAvailable, Owner.TextCaptureMode, Owner.PinSelectionAvailable, Owner.PasteToScreenHotkey,
            Owner.FreeformAvailable, Owner.Mode == CaptureSelectorMode.Freeform);
        public FontFamily UiFont => ThemeFonts.Ui;
        public FontFamily MonoFont { get; } = new("Consolas");
        public IReadOnlyDictionary<string, Brush> Palette => CaptureGuidePalette.Resolve();
        public CaptureKeyPanelPlacement Placement
        {
            get
            {
                PointD pointer = Owner.ReadCursor();
                CaptureGuideArea area = CaptureModeBarLayout.ChooseArea(Owner.ReadMonitors(), pointer, Owner.ScreenBounds);
                Rect? highlight = (Owner.Selection ?? Owner.HoverRegion) is { } rect
                    ? CaptureModeBarLayout.ToDip(rect, 1, 1) : null;
                return new CaptureKeyPanelPlacement(CaptureModeBarLayout.ToDip(area.WorkArea, 1, 1),
                    new Point(pointer.X - Owner.ScreenBounds.Left, pointer.Y - Owner.ScreenBounds.Top),
                    highlight, chrome.LocalBounds(chrome._barWindow),
                    chrome._magnifierWindow is { } magnifier ? chrome.LocalBounds(magnifier) : null,
                    chrome._sizeWindow is { } size ? chrome.LocalBounds(size) : chrome._staticReadoutBounds,
                    null, area.Scale > 0 ? area.Scale : 1);
            }
        }
        public void Add(CaptureKeyPanel panel)
        {
            if (chrome._keyPanelCanvas is null)
            {
                chrome._keyPanelCanvas = new Canvas { IsHitTestVisible = false, Focusable = false };
                chrome._keyPanelWindow = chrome.CreateDeferred(chrome._keyPanelCanvas);
            }
            chrome._keyPanelCanvas.Children.Add(panel);
        }
        public void Remove(CaptureKeyPanel panel) => chrome._keyPanelCanvas?.Children.Remove(panel);
        public void Place(CaptureKeyPanel? active, Rect? bounds, double scale)
        {
            chrome.KeyPanelBounds = bounds;
            if (active is null || bounds is null)
            {
                chrome._keyPanelWindow?.Hide();
                return;
            }
            chrome._keyPanelCanvas!.Width = active.DesiredSize.Width;
            chrome._keyPanelCanvas.Height = active.DesiredSize.Height;
            chrome.PlaceGuideWindow(chrome._keyPanelWindow!, chrome._keyPanelCanvas, bounds.Value, scale);
        }
    }
}
