using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>Bounded windows only. A detached instance has no session, frame, or owner HWND.</summary>
internal sealed partial class NativeSelectorChrome : IDisposable
{
    private NativeCaptureOverlay? _owner;
    private readonly List<Window> _windows = [];
    private readonly Dictionary<Window, FrameworkElement> _content = [];
    private readonly Dictionary<Window, ChromeGeometry> _geometry = [];
    private CaptureGuideArea[] _pointerAreas = [];
    private CaptureGuideArea _fallbackArea;
    private bool _placing;
    private DispatcherOperation? _dpiRefresh;
    private readonly HashSet<Window> _pendingFrames = [];
    private readonly TextBlock _size, _guideText;
    private readonly NativeSelectorReadoutBorder _sizePanel;
    private readonly NativeSelectorAnchor _anchorChip;
    private readonly double[] _readoutAdvances = new double[256];
    private readonly double _readoutTextHeight;
    private bool _readoutVisible;
    private readonly NativeSelectorHoverTab _hoverTab;
    private const nuint HoverTabTimerId = 0x5a02;
    private bool _hoverTabPending;
    private RectD? _tabRequestedTarget, _tabRequestedShown;
    private double _tabRequestedScale;
    private bool _tabRequestedMonitor, _tabRequestedText, _tabRequestedElement, _tabRequestedElementHint;
    private int _guideRevision, _tabGuideRevision = -1;
    private readonly Rect[] _tabAvoid = new Rect[2];
    private readonly CaptureModeBar _bar;
    private readonly Window _barWindow;
    private readonly Window? _sizeWindow, _guideWindow, _magnifierWindow;
    private readonly NativeSelectorStaticChrome? _static;
    private readonly WriteableBitmap? _magnifier;
    private readonly NativeSelectorMagnifier? _magnifierAdornment;
    private readonly Border? _magnifierPanel;
    private BitmapSource? _magnifierSource;
    private readonly string _defaultGuide = DefaultGuide;
    private readonly string _precisionGuide = UiText.Get("CapturePrecisionActive");
    private string? _freeformGuide;
    private readonly Size _precisionGuideSize;
    private bool _precisionGuideShown;
    private bool _guideLayoutDirty = true;
    private int _readoutWidth = -1, _readoutHeight = -1;
    private SelectionRatio _readoutRatio;
    private readonly byte[] _sample = new byte[15 * 15 * 4];
    private readonly double _scale;
    private readonly RectD _workArea;
    private readonly string _paletteKey;
    private readonly string _languageKey = UiText.Culture.Name;
    private bool _closed, _invalid, _rendered, _showingWindows, _guideVisible = true;

    internal NativeSelectorChrome(NativeCaptureOverlay owner, bool magnifier, bool requireExclusion)
    {
        CaptureGuideArea area = owner.ChromeArea;
        _scale = area.Scale > 0 ? area.Scale : 1;
        _workArea = area.WorkArea;
        IReadOnlyDictionary<string, Brush> palette = CaptureGuidePalette.Resolve();
        _paletteKey = PaletteKey(palette);
        try
        {
            _bar = owner.ModeBar;
            _bar.ConstrainWidth(_workArea.Width / _scale - 24);
            _bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _barWindow = Create(_bar, _bar.DesiredSize.Width, _bar.DesiredSize.Height, interactive: true);
            _size = Text("", palette, monospaced: true);
            _size.HorizontalAlignment = HorizontalAlignment.Left;
            _size.VerticalAlignment = VerticalAlignment.Top;
            _sizePanel = new NativeSelectorReadoutBorder(palette["CaptureGuide.ChipBorder"]) { Child = _size, Padding = new Thickness(9, 5, 9, 5),
                Background = palette["CaptureGuide.ChipSurface"], CornerRadius = new CornerRadius(7),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            _anchorChip = new NativeSelectorAnchor(palette);
            _hoverTab = new NativeSelectorHoverTab(palette);
            var readoutTypeface = new Typeface(_size.FontFamily, _size.FontStyle, _size.FontWeight, _size.FontStretch);
            _readoutTextHeight = new FormattedText("0", UiText.Culture, FlowDirection.LeftToRight,
                readoutTypeface, TypeScale.Caption, _size.Foreground, 1).Height;
            if (readoutTypeface.TryGetGlyphTypeface(out GlyphTypeface readoutGlyphs))
                for (int character = 0; character < _readoutAdvances.Length; character++)
                    if (readoutGlyphs.CharacterToGlyphMap.TryGetValue(character, out ushort glyph))
                        _readoutAdvances[character] = readoutGlyphs.AdvanceWidths[glyph] * TypeScale.Caption;
            _guideText = Text(_defaultGuide, palette);
            TextBlock precisionMeasure = Text(_precisionGuide, palette);
            precisionMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _precisionGuideSize = new Size(precisionMeasure.DesiredSize.Width + 20, precisionMeasure.DesiredSize.Height + 12);
            if (owner.ChromeStrategy == NativeSelectorChromeStrategy.Dib)
                _static = new NativeSelectorStaticChrome(_sizePanel, Chip(_guideText, palette));
            else
            {
                _sizeWindow = Create(_sizePanel, 18, _readoutTextHeight + 10, interactive: false);
                _guideWindow = Create(Chip(_guideText, palette), 320, 38, interactive: false);
            }
            if (magnifier)
            {
                _magnifier = new WriteableBitmap(Math.Min(15, owner.Frame?.PixelWidth ?? 15),
                    Math.Min(15, owner.Frame?.PixelHeight ?? 15), 96, 96, PixelFormats.Bgr32, null);
                var image = new Image { Source = _magnifier, Width = 150, Height = 150, Stretch = Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                _magnifierAdornment = new NativeSelectorMagnifier(image, palette);
                _magnifierPanel = new Border { Child = image, Padding = new Thickness(3, 3, 3, 41),
                    Background = palette["CaptureGuide.ChipSurface"], CornerRadius = new CornerRadius(9) };
                _magnifierWindow = Create(_magnifierPanel, 156, 194, interactive: false);
            }
            Bind(owner, requireExclusion);
        }
        catch { Dispose(); throw; }
    }

    private static string DefaultGuide => UiText.Get("CaptureGuide2_DragCapture") + "  ·  " + UiText.Get("CaptureGuide2_Esc");
    private static string PaletteKey(IReadOnlyDictionary<string, Brush> palette) =>
        string.Join(";", palette.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
    internal bool AllCaptureExcluded { get; private set; }
    internal IReadOnlyList<Window> Windows => _windows;
    internal bool IsDetached => _owner is null;
    internal int PendingFrameCount => _pendingFrames.Count;
    internal bool CanRetain => !_closed && !_invalid && _rendered && _static is null;
    internal RectD BoundsForTest(Window window) => _geometry[window].Bounds;
    internal double ScaleForTest(Window window) => _geometry[window].Scale;
    internal Size LogicalSizeForTest(Window window) => _geometry[window].Size;
    internal bool ContainsModeBar(PointD pointer) => _geometry[_barWindow].Bounds.Contains(pointer);
    internal NativeSelectorMagnifier? MagnifierForTest => _magnifierAdornment;
    internal DrawingGroup? AnchorDrawingForTest => _anchorChip.Drawing;
    internal Rect? AnchorChipBoundsForTest => _anchorChip.Bounds;
    internal double AnchorScaleForTest => _anchorChip.Scale;
    internal NativeSelectorStamp? AnchorStampForTest => _anchorChip.Stamp;
    internal int AnchorBuildCountForTest => _anchorChip.BuildCount;
    internal bool IsReadoutVisibleForTest => _readoutVisible;
    internal Rect? TargetTabForTest => _hoverTab.Bounds is not null && _sizeWindow is { } size && _owner is { } owner
        ? new Rect((_geometry[size].Bounds.Left - owner.ScreenBounds.Left) / _geometry[size].Scale,
            (_geometry[size].Bounds.Top - owner.ScreenBounds.Top) / _geometry[size].Scale,
            _geometry[size].Size.Width, _geometry[size].Size.Height) : null;
    internal string? TargetLabelForTest => _hoverTab.TargetLabel;
    internal object? HoverLabelLayoutForTest => _hoverTab.LabelLayout;
    internal DrawingGroup HoverTabDrawingForTest => _hoverTab.Drawing;
    internal int HoverTabRenderCountForTest => _hoverTab.RenderCount;
    internal int HoverTabLayoutCountForTest => _hoverTab.LayoutCount;
    internal bool HasPendingHoverTabForTest => _hoverTabPending;
    internal void FlushHoverTabForTest() { if (_hoverTabPending) RenderHoverTab(); }
    internal DrawingGroup? ReadoutDrawingForTest => VisualTreeHelper.GetDrawing(_sizePanel);
    internal RectD SizeChipBoundsForTest => _sizeWindow is { } size && _readoutVisible
        ? new RectD(_geometry[size].Bounds.Left, _geometry[size].Bounds.Top,
            _geometry[size].Size.Width * _geometry[size].Scale, _geometry[size].Size.Height * _geometry[size].Scale)
        : RectD.Empty;
    internal RectD MagnifierImageBoundsForTest => _magnifierWindow is { } window && _magnifierAdornment is { } adornment
        ? new RectD(_geometry[window].Bounds.Left + adornment.ImageOffsetForTest.X * _geometry[window].Scale,
            _geometry[window].Bounds.Top + adornment.ImageOffsetForTest.Y * _geometry[window].Scale,
            adornment.ImageSizeForTest.Width * _geometry[window].Scale,
            adornment.ImageSizeForTest.Height * _geometry[window].Scale) : RectD.Empty;

    internal bool CanReuse(NativeCaptureOverlay owner, bool magnifier) =>
        CanRetain && IsDetached && magnifier == (_magnifier is not null)
        && (_magnifier is null || owner.Frame is not { } frame
            || (_magnifier.PixelWidth == Math.Min(15, frame.PixelWidth) && _magnifier.PixelHeight == Math.Min(15, frame.PixelHeight)))
        && owner.ChromeArea.WorkArea == _workArea && (owner.ChromeArea.Scale > 0 ? owner.ChromeArea.Scale : 1) == _scale
        && PaletteKey(CaptureGuidePalette.Resolve()) == _paletteKey && UiText.Culture.Name == _languageKey;

    internal void Bind(NativeCaptureOverlay owner, bool requireExclusion)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_owner is not null) throw new InvalidOperationException("Chrome already belongs to a selector.");
        SelectorTimeline.Mark?.Invoke("native-chrome-bind-start");
        _owner = owner;
        if (owner.ChromeStrategy != NativeSelectorChromeStrategy.Parallel) owner.PrepareVisualFeedback();
        CachePointerAreas(owner);
        if (_magnifierAdornment is { } magnifier && owner.Frame is { } frame)
        {
            _magnifierSource = frame.Bitmap.Format == PixelFormats.Bgr32 ? frame.Bitmap
                : new FormatConvertedBitmap(frame.Bitmap, PixelFormats.Bgr32, null, 0);
            magnifier.Bind(frame.PixelWidth, frame.PixelHeight, _pointerAreas, _scale);
        }
        _rendered = false;
        _showingWindows = false;
        _guideVisible = true;
        ResetGuides();
        _guideLayoutDirty = true;
        owner.AttachModeBar(_bar);
        // Detaching a previously loaded visual suspends its layout. Reattach before measuring it.
        if (_barWindow.Content is null) _barWindow.Content = _content[_barWindow];
        Size barSize = MeasureBar();
        _barWindow.Width = barSize.Width;
        _barWindow.Height = barSize.Height;
        _pendingFrames.Clear();
        AllCaptureExcluded = true;
        foreach (Window window in _windows)
        {
            new WindowInteropHelper(window).Owner = owner.Handle;
            if (window.Content is null) window.Content = _content[window];
            // Exclusion is re-applied on every lease, before any of these HWNDs can be shown.
            bool excluded = CaptureWindowExclusion.TryApply(window);
            AllCaptureExcluded &= excluded;
            if (requireExclusion && !excluded) throw new InvalidOperationException(UiText.Get("Text_D1F0DAEAA780"));
            CaptureWindowExclusion.DisableTransitions(window);
            if (!_deferredWindows.Contains(window)) _pendingFrames.Add(window);
        }
        Place(_barWindow, _workArea.Left + (_workArea.Width - barSize.Width * _scale) / 2, _workArea.Top + 12 * _scale, Placement.Layout);
        if (_guideWindow is { } guide)
            Place(guide, _workArea.Left + (_workArea.Width - 320 * _scale) / 2, _workArea.Top + 68 * _scale, Placement.Layout);
        Update(owner.ReadCursor(), Int32Rect.Empty);
        SelectorTimeline.Mark?.Invoke("native-chrome-bind-complete");
    }

    private Size MeasureBar()
    {
        ChromeGeometry geometry = _geometry[_barWindow];
        // DesiredSize includes LayoutTransform. A retained, hidden window can suspend Measure;
        // keep its last logical size until Show resumes layout, then measure the new configuration.
        _bar.LayoutTransform = Transform.Identity;
        try
        {
            _bar.ConstrainWidth(_workArea.Width / _scale - 24);
            _bar.InvalidateMeasure();
            _bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (_bar.IsMeasureValid) geometry.Size = _bar.DesiredSize;
        }
        finally { _bar.LayoutTransform = geometry.Transform; }
        return geometry.Size;
    }

    private Window Create(FrameworkElement content, double width, double height, bool interactive)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, AllowsTransparency = false,
            Width = width, Height = height, Background = CaptureWindowExclusion.OpaqueBackground(), Content = content,
            Left = _workArea.Left / _scale, Top = _workArea.Top / _scale,
        };
        _windows.Add(window);
        _content.Add(window, content);
        var geometry = new ChromeGeometry(new Size(width, height), _scale)
        {
            Bounds = new RectD(_workArea.Left, _workArea.Top, width * _scale, height * _scale).ToPixelBounds(),
        };
        _geometry.Add(window, geometry);
        content.LayoutTransform = geometry.Transform;
        FluidMotion.SetWindowEntrance(window, false);
        InputMethod.SetIsInputMethodEnabled(window, false);
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            geometry.Handle = handle;
            geometry.Source = HwndSource.FromHwnd(handle);
            if (!interactive) WindowStyleFacade.SetClickThrough(handle, true);
            HwndSource.FromHwnd(handle)?.AddHook(OnMessage);
        };
        window.Deactivated += (_, _) => _owner?.CheckFocusLater();
        window.ContentRendered += OnRendered;
        window.PreviewKeyDown += OnKeyDown;
        window.PreviewKeyUp += OnKeyUp;
        window.DpiChanged += OnDpiChanged;
        if (interactive)
        {
            window.MouseEnter += OnBarPointerBoundary;
            window.MouseLeave += OnBarPointerBoundary;
        }
        return window;
    }

    private void OnBarPointerBoundary(object sender, MouseEventArgs args)
    {
        if (_owner is not { } owner) return;
        owner.RefreshPrecisionPointer();
        owner.UpdateVisualFeedback();
    }

    private void CachePointerAreas(NativeCaptureOverlay owner)
    {
        IReadOnlyList<MonitorInfo> monitors = owner.ReadMonitors();
        _pointerAreas = new CaptureGuideArea[monitors.Count];
        _fallbackArea = new CaptureGuideArea(owner.ScreenBounds, _workArea, _scale);
        bool hasFallback = false;
        for (int index = 0; index < monitors.Count; index++)
        {
            MonitorInfo monitor = monitors[index];
            RectD bounds = Intersection(monitor.Bounds, owner.ScreenBounds);
            if (bounds.IsEmpty) continue;
            RectD work = Intersection(monitor.WorkArea, bounds);
            var area = new CaptureGuideArea(bounds, work.IsEmpty ? bounds : work, monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1);
            _pointerAreas[index] = area;
            if (!hasFallback || monitor.IsPrimary) { _fallbackArea = area; hasFallback = true; }
        }
    }

    private static RectD Intersection(RectD first, RectD second)
    {
        double left = Math.Max(first.Left, second.Left), top = Math.Max(first.Top, second.Top);
        double right = Math.Min(first.Right, second.Right), bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top ? new RectD(left, top, right - left, bottom - top) : RectD.Empty;
    }

    private CaptureGuideArea PointerArea(PointD pointer)
    {
        // The monitor snapshot belongs to the lease. Indexed traversal allocates nothing on pointer moves.
        for (int index = 0; index < _pointerAreas.Length; index++)
        {
            CaptureGuideArea area = _pointerAreas[index];
            if (pointer.X >= area.Bounds.Left && pointer.X < area.Bounds.Right
                && pointer.Y >= area.Bounds.Top && pointer.Y < area.Bounds.Bottom) return area;
        }
        return _fallbackArea;
    }

    private void OnDpiChanged(object? sender, DpiChangedEventArgs args)
    {
        if (_closed || _placing || _owner is null || _dpiRefresh is { Status: DispatcherOperationStatus.Pending }) return;
        // WPF applies its suggested rectangle after this event; repair bounds after that completes.
        _dpiRefresh = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshDpiBounds));
    }

    private void RefreshDpiBounds()
    {
        _dpiRefresh = null;
        if (_closed || _owner is null) return;
        for (int index = 0; index < _windows.Count; index++)
        {
            Window window = _windows[index];
            // Key panel and start guide windows are placed from their own layout by PlaceGuideWindow.
            if (_deferredWindows.Contains(window)) continue;
            ApplyPhysicalBounds(window, _geometry[window], Follow(window));
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0113 && (nuint)wParam == HoverTabTimerId)
        {
            RenderHoverTab();
            handled = true;
            return IntPtr.Zero;
        }
        if (message == 0x007E) // Hidden top-level chrome receives display broadcasts too.
        {
            _invalid = true;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                _owner?.Close();
                NativeSelectorChromeCache.Trim();
            }));
        }
        return IntPtr.Zero;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_owner is not { } owner) return;
        Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is not (Key.F1 or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt
            or Key.RightAlt or Key.System or Key.LWin or Key.RWin)) owner.DismissStartGuide();
        if (key is Key.LeftShift or Key.RightShift)
        {
            owner.RefreshPrecisionPointer();
            Update(owner.EffectivePointer, owner.SelectionRectangle);
        }
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = NavigateTab(Keyboard.Modifiers == ModifierKeys.Shift);
            return;
        }
        if (_bar.Holds(e.OriginalSource as DependencyObject))
        {
            if (key == Key.Enter && e.OriginalSource is ButtonBase button)
            {
                KeyboardActivation = true;
                try { _bar.ActivateButton(button); }
                finally { KeyboardActivation = false; }
                e.Handled = true;
                return;
            }
            if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter or Key.Space or Key.PageUp or Key.PageDown) return;
        }
        e.Handled = owner.HandleKey(key, Keyboard.Modifiers);
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_owner is not { } owner) return;
        Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is not (Key.LeftShift or Key.RightShift)) return;
        owner.RefreshPrecisionPointer();
        Update(owner.EffectivePointer, owner.SelectionRectangle);
    }

    private static TextBlock Text(string text, IReadOnlyDictionary<string, Brush> palette,
        bool monospaced = false, double fontSize = TypeScale.Caption) => new()
    {
        Text = text, Foreground = palette["CaptureGuide.Text"], FontSize = fontSize,
        FontFamily = Application.Current?.TryFindResource(monospaced ? "Font.Mono" : "Font.Ui") as FontFamily
            ?? new FontFamily(monospaced ? "Consolas" : "Segoe UI"),
        FontWeight = monospaced ? FontWeights.Medium : FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static Border Chip(UIElement child, IReadOnlyDictionary<string, Brush> palette) => new()
    {
        Child = child, Padding = new Thickness(7), Background = palette["CaptureGuide.ChipSurface"],
        BorderBrush = palette["CaptureGuide.ChipBorder"], BorderThickness = new Thickness(1),
    };

    internal void Show()
    {
        _showingWindows = true;
        foreach (Window window in _windows.ToArray())
        {
            if (_deferredWindows.Contains(window)) continue;
            if (_closed || _owner is null) return;
            MarkWindow(window, "show-start");
            window.Show();
            if (_closed || _owner is null) return;
            MarkWindow(window, "show-complete");
            if (ReferenceEquals(window, _barWindow))
            {
                Size barSize = MeasureBar();
                Place(window, _workArea.Left + (_workArea.Width - barSize.Width * _scale) / 2,
                    _workArea.Top + 12 * _scale, Placement.Show);
            }
            else ApplyPhysicalBounds(window, _geometry[window], Placement.Show);
            if (ReferenceEquals(window, _magnifierWindow)) _magnifierAdornment?.Attach();
            MarkWindow(window, "placed");
        }
        _owner?.FlushChromeStamps();
        _owner?.UpdateDeferredGuides();
    }

    private void OnRendered(object? sender, EventArgs args)
    {
        if (_closed || !_showingWindows || _owner is null || sender is not Window window) return;
        if (window == _keyPanelWindow)
        {
            if (!_keyPanelReady)
            {
                _keyPanelReady = true;
                MyCapture.App.Diagnostics.SelectorTimeline.Mark?.Invoke("native-key-panel-ready");
            }
            return;
        }
        if (!_pendingFrames.Remove(window)) return;
        MarkWindow(window, "rendered");
        if (_pendingFrames.Count == 0)
        {
            _rendered = true;
            _owner.FlushChromeStamps();
            _owner.ChromeReady();
        }
    }

    private void MarkWindow(Window window, string phase)
    {
        if (SelectorTimeline.Mark is not { } mark) return;
        string name = ReferenceEquals(window, _barWindow) ? "bar"
            : ReferenceEquals(window, _sizeWindow) ? "size"
            : ReferenceEquals(window, _guideWindow) ? "guide" : "magnifier";
        mark($"native-chrome-{name}-{phase}");
    }

    internal void Update(PointD pointer, Int32Rect selection)
    {
        if (_closed || _owner is not { } owner) return;
        owner.UpdateGuidePointer();
        bool hover = _static is null && owner.Selection is null && owner.HoverRegion.HasValue;
        if (!hover && (_readoutWidth != selection.Width || _readoutHeight != selection.Height || _readoutRatio != owner.ActiveRatio))
        {
            _readoutWidth = selection.Width; _readoutHeight = selection.Height; _readoutRatio = owner.ActiveRatio;
            _size.Text = CaptureOverlayView.FormatReadout(selection.Width, selection.Height, owner.ActiveRatio);
        }
        bool precision = owner.PrecisionActive && owner.ToastMessage is null;
        string guideText = owner.ToastMessage ?? (precision ? _precisionGuide
            : owner.Mode == CaptureSelectorMode.Freeform ? _freeformGuide ??= UiText.Get("CaptureFreeform_Drawing") : _defaultGuide);
        if (_guideText.Text != guideText) _guideText.Text = guideText;
        if (_guideLayoutDirty || precision != _precisionGuideShown)
        {
            _guideLayoutDirty = false;
            _guideRevision++;
            _precisionGuideShown = precision;
            if (_guideWindow is { } guide && _content[guide] is Border panel)
            {
                panel.CornerRadius = new CornerRadius(precision ? 6 : 0);
                panel.Padding = precision ? new Thickness(9, 5, 9, 5) : new Thickness(7);
                _geometry[guide].Size = precision ? _precisionGuideSize : new Size(320, 38);
                double top = precision ? 12 + _geometry[_barWindow].Size.Height + 8 : 68;
                Place(guide, _workArea.Left + (_workArea.Width - _geometry[guide].Size.Width * _scale) / 2,
                    _workArea.Top + top * _scale, Follow(guide));
            }
        }
        CaptureGuideArea area = PointerArea(pointer);
        PointD? anchor = owner.SelectionAnchor;
        double anchorScale = anchor is { } start
            ? PointerArea(new PointD(start.X + owner.ScreenBounds.Left, start.Y + owner.ScreenBounds.Top)).Scale : _scale;
        if (_anchorChip.Update(anchor, owner.ScreenBounds, anchorScale)) owner.SetAnchorChromeStamp(_anchorChip.Stamp);
        if (_sizeWindow is { } size)
        {
            _readoutVisible = hover || (selection.Width > 0 && selection.Height > 0);
            if (hover)
            {
                // Only schedule retained frame work here. Text and tab geometry are built by the frame callback.
                RequestHoverTab(owner, area);
            }
            else
            {
            StopHoverTabFrame();
            _tabRequestedTarget = _tabRequestedShown = null;
            _hoverTab.Clear();
            if (_sizePanel.ShowsHoverTab)
            {
                _sizePanel.ShowsHoverTab = false;
                _sizePanel.Child = _size;
                _sizePanel.Padding = new Thickness(9, 5, 9, 5);
                _sizePanel.InvalidateVisual();
            }
            ChromeGeometry geometry = _geometry[size];
            double textWidth = 0;
            for (int index = 0; index < _size.Text.Length; index++)
            {
                char character = _size.Text[index];
                textWidth += character < _readoutAdvances.Length && _readoutAdvances[character] > 0
                    ? _readoutAdvances[character] : _readoutAdvances['0'];
            }
            geometry.Size = new Size(textWidth + 18, _readoutTextHeight + 10);
            if (_sizePanel.Width != geometry.Size.Width) _sizePanel.Width = geometry.Size.Width;
            if (_sizePanel.Height != geometry.Size.Height) _sizePanel.Height = geometry.Size.Height;
            geometry.Scale = area.Scale;
            _sizePanel.SetScale(area.Scale);
            if (_readoutVisible)
            {
                double edge = 8 * area.Scale, width = geometry.Size.Width * area.Scale, height = geometry.Size.Height * area.Scale;
                double left = owner.ScreenBounds.Left + selection.X, top = owner.ScreenBounds.Top + selection.Y;
                double x = Math.Clamp(left, area.WorkArea.Left + edge, Math.Max(area.WorkArea.Left + edge, area.WorkArea.Right - width - edge));
                double y = top - height - edge;
                if (y < area.WorkArea.Top + edge) y = Math.Min(area.WorkArea.Bottom - height - edge, top + selection.Height + edge);
                Place(size, x, y, Follow(size), area);
            }
            else
            {
                // Keep the already shown HWND out of the desktop; following input never shows/hides or restacks it.
                geometry.Bounds = new RectD(owner.ScreenBounds.Left - geometry.Size.Width * area.Scale - 32,
                    owner.ScreenBounds.Top, geometry.Size.Width * area.Scale, geometry.Size.Height * area.Scale).ToPixelBounds();
                ApplyPhysicalBounds(size, geometry, Follow(size));
            }
            }
        }
        if (_static is not null)
        {
            IReadOnlyList<NativeSelectorStamp> stamps = _static.Update(_size.Text, _guideText.Text, pointer, _workArea, owner.ScreenBounds, _scale, _guideVisible);
            Int32Rect readout = stamps[^1].Bounds;
            _staticReadoutBounds = new Rect(readout.X, readout.Y, readout.Width, readout.Height);
            owner.SetChromeStamps(stamps);
        }
        if (_magnifier is not null && _magnifierWindow is not null && _magnifierSource is { } source && owner.Frame is { } frame)
        {
            int width = Math.Min(15, source.PixelWidth), height = Math.Min(15, source.PixelHeight);
            int x = Math.Clamp((int)(pointer.X - frame.ScreenBounds.Left) - width / 2, 0, source.PixelWidth - width);
            int y = Math.Clamp((int)(pointer.Y - frame.ScreenBounds.Top) - height / 2, 0, source.PixelHeight - height);
            source.CopyPixels(new Int32Rect(x, y, width, height), _sample, 15 * 4, 0);
            _magnifier.WritePixels(new Int32Rect(0, 0, width, height), _sample, 15 * 4, 0);
            int pointerX = Math.Clamp((int)(pointer.X - frame.ScreenBounds.Left), 0, source.PixelWidth - 1);
            int pointerY = Math.Clamp((int)(pointer.Y - frame.ScreenBounds.Top), 0, source.PixelHeight - 1);
            if (_magnifierAdornment!.Update(area.Scale, pointerX, pointerY, x, y, width, height, _sample))
            {
                _geometry[_magnifierWindow].Size = _magnifierAdornment.LogicalSize;
                double inset = _magnifierAdornment.ImageOffsetForTest.X;
                // A bounded HWND starts on whole pixels; align the image there too so its half-pixel
                // grid has the same coverage as WPF. Keep the total panel band at six DIPs.
                _magnifierPanel!.Padding = new Thickness(inset, inset, 6 - inset,
                    _magnifierAdornment.FooterHeightForTest + 6 - inset);
            }
            Size imageSize = _magnifierAdornment.ImageSizeForTest;
            double imageWidth = imageSize.Width * area.Scale;
            double imageHeight = (imageSize.Height + _magnifierAdornment.FooterHeightForTest) * area.Scale;
            double imageX = pointer.X + 20, imageY = pointer.Y + 22, margin = 8 * area.Scale;
            if (imageX + imageWidth > area.WorkArea.Right - margin) imageX = pointer.X - 20 - imageWidth;
            if (imageY + imageHeight > area.WorkArea.Bottom - margin) imageY = pointer.Y - 22 - imageHeight;
            imageX = Math.Clamp(imageX, area.WorkArea.Left + margin, Math.Max(area.WorkArea.Left + margin, area.WorkArea.Right - imageWidth - margin));
            imageY = Math.Clamp(imageY, area.WorkArea.Top + margin, Math.Max(area.WorkArea.Top + margin, area.WorkArea.Bottom - imageHeight - margin));
            double imageInset = _magnifierAdornment.ImageOffsetForTest.X * area.Scale;
            Place(_magnifierWindow, Math.Round(imageX) - imageInset, Math.Round(imageY) - imageInset, Follow(_magnifierWindow), area);
        }
        owner.UpdateVisualFeedback();
        owner.UpdateDeferredGuides();
    }

    private void RequestHoverTab(NativeCaptureOverlay owner, CaptureGuideArea area)
    {
        RectD? target = owner.HoverRegion, shown = owner.ShownHoverForTest ?? target;
        if (_tabRequestedTarget == target && _tabRequestedShown == shown && _tabRequestedScale == area.Scale
            && _tabRequestedMonitor == owner.HoverWholeMonitor && _tabRequestedText == owner.TextCaptureMode
            && _tabRequestedElement == owner.HoverElement && _tabRequestedElementHint == owner.ElementWheelHint
            && _tabGuideRevision == _guideRevision) return;
        _tabRequestedTarget = target; _tabRequestedShown = shown; _tabRequestedScale = area.Scale;
        _tabRequestedMonitor = owner.HoverWholeMonitor; _tabRequestedText = owner.TextCaptureMode;
        _tabRequestedElement = owner.HoverElement; _tabRequestedElementHint = owner.ElementWheelHint;
        _tabGuideRevision = _guideRevision;
        if (_hoverTabPending || _sizeWindow is not { } size) return;
        _hoverTabPending = NativeSelectorInterop.SetTimer(_geometry[size].Handle, HoverTabTimerId, 1, IntPtr.Zero) != 0;
        if (!_hoverTabPending) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    private void StopHoverTabFrame()
    {
        if (!_hoverTabPending) return;
        _hoverTabPending = false;
        if (_sizeWindow is { } size && _geometry[size].Handle != IntPtr.Zero)
            _ = NativeSelectorInterop.KillTimer(_geometry[size].Handle, HoverTabTimerId);
    }

    private void RenderHoverTab()
    {
        StopHoverTabFrame();
        if (_closed || _owner is not { } owner || _sizeWindow is not { } size
            || owner.Selection is not null || owner.HoverRegion is not { } target) return;
        CaptureGuideArea area = PointerArea(owner.EffectivePointer);
        double scale = area.Scale;
        RectD screen = owner.ScreenBounds;
        RectD bar = _geometry[_barWindow].Bounds;
        _tabAvoid[0] = new Rect((bar.Left - screen.Left) / scale, (bar.Top - screen.Top) / scale, bar.Width / scale, bar.Height / scale);
        if (_guideVisible && _guideWindow is { } guide)
        {
            RectD bounds = _geometry[guide].Bounds;
            _tabAvoid[1] = new Rect((bounds.Left - screen.Left) / scale, (bounds.Top - screen.Top) / scale, bounds.Width / scale, bounds.Height / scale);
        }
        else _tabAvoid[1] = Rect.Empty;
        Rect tab = _hoverTab.Render(owner.ShownHoverForTest ?? target, target, owner.HoverWholeMonitor,
            owner.TextCaptureMode, scale, new Size(screen.Width / scale, screen.Height / scale), _tabAvoid, owner.HoverElement, owner.ElementWheelHint);
        if (!_sizePanel.ShowsHoverTab)
        {
            _sizePanel.ShowsHoverTab = true;
            _sizePanel.Child = _hoverTab.Image;
            _sizePanel.Padding = new Thickness(0);
            _sizePanel.InvalidateVisual();
        }
        _geometry[size].Size = tab.Size;
        _sizePanel.Width = tab.Width;
        _sizePanel.Height = tab.Height;
        Place(size, screen.Left + tab.X * scale, screen.Top + tab.Y * scale, Follow(size), area);
    }

    private enum Placement { Layout, Show, Follow }

    private static Placement Follow(Window window) => window.IsVisible ? Placement.Follow : Placement.Layout;

    private void Place(Window window, double x, double y, Placement placement, CaptureGuideArea? area = null)
    {
        ChromeGeometry geometry = _geometry[window];
        RectD workArea = area?.WorkArea ?? _workArea;
        double scale = area?.Scale ?? _scale;
        double width = geometry.Size.Width * scale, height = geometry.Size.Height * scale;
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        geometry.Scale = scale;
        geometry.Bounds = new RectD(x, y, width, height).ToPixelBounds();
        ApplyPhysicalBounds(window, geometry, placement);
        if (ReferenceEquals(window, _barWindow)) _owner?.SetModeBarFeedbackBounds(geometry.Bounds);
    }

    private void ApplyPhysicalBounds(Window window, ChromeGeometry geometry, Placement placement)
    {
        if (_placing) return;
        _placing = true;
        try
        {
            ApplyContentScale(window, geometry);
            if (placement == Placement.Layout)
            {
                Matrix dpi = geometry.Source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                window.Left = geometry.Bounds.Left / dpi.M11;
                window.Top = geometry.Bounds.Top / dpi.M22;
                return;
            }
        // Restacking a topmost window makes Windows synthesize a mouse move over the selector. When
        // every WM_MOUSEMOVE restacked the size chip and magnifier, a resting pointer fed itself mouse
        // moves (about 110 a second), WPF's input queue never emptied and Input-priority work such as
        // ContentRendered starved. Owned chrome already stays above the background, so after the one
        // topmost placement in Show, following the pointer only moves, and not at all when unchanged.
            if (placement == Placement.Show) PhysicalWindowPositioner.PlaceTopmostWithoutActivation(geometry.Handle, geometry.Bounds);
            else PhysicalWindowPositioner.MoveAndResize(geometry.Handle, geometry.Bounds);
            // Crossing the seam can change the HWND's composition DPI inside SetWindowPos. Its content
            // compensation must use that final DPI, including when test monitor geometry is injected.
            if (ApplyContentScale(window, geometry))
                PhysicalWindowPositioner.MoveAndResize(geometry.Handle, geometry.Bounds);
        }
        finally { _placing = false; }
    }

    private bool ApplyContentScale(Window window, ChromeGeometry geometry)
    {
        Matrix dpi = geometry.Source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        double scaleX = geometry.Scale / dpi.M11, scaleY = geometry.Scale / dpi.M22;
        bool changed = geometry.Transform.ScaleX != scaleX || geometry.Transform.ScaleY != scaleY;
        if (changed) { geometry.Transform.ScaleX = scaleX; geometry.Transform.ScaleY = scaleY; }
        double width = geometry.Bounds.Width / dpi.M11, height = geometry.Bounds.Height / dpi.M22;
        if (window.Width != width) window.Width = width;
        if (window.Height != height) window.Height = height;
        return changed;
    }

    private sealed class ChromeGeometry(Size size, double scale)
    {
        internal Size Size = size;
        internal double Scale = scale;
        internal RectD Bounds;
        internal readonly ScaleTransform Transform = new();
        internal IntPtr Handle;
        internal HwndSource? Source;
    }

    internal void Hide()
    {
        _showingWindows = false;
        _bar?.DismissMenus();
        foreach (Window window in _windows.ToArray()) window.Hide();
    }

    internal void Detach()
    {
        StopHoverTabFrame();
        _tabRequestedTarget = _tabRequestedShown = null;
        _hoverTab?.Clear();
        _dpiRefresh?.Abort();
        _dpiRefresh = null;
        _magnifierAdornment?.Clear();
        _magnifierSource = null;
        _readoutWidth = _readoutHeight = -1;
        if (_owner is { } owner && _anchorChip is { } anchor)
        {
            anchor.Update(null, owner.ScreenBounds, _scale);
            owner.SetAnchorChromeStamp(null);
        }
        _readoutVisible = false;
        _owner?.SetModeBarFeedbackBounds(null);
        Hide();
        _owner?.DetachModeBar();
        _owner = null;
        ResetGuides();
        _pendingFrames.Clear();
        foreach (Window window in _windows)
        {
            new WindowInteropHelper(window).Owner = IntPtr.Zero;
            // WPF does not raise ContentRendered again for an unchanged hidden/showing Window.
            // Reattach the same small visual on the next lease to observe its new content frame.
            window.Content = null;
        }
        Array.Clear(_sample);
        if (_magnifier is { } magnifier) magnifier.WritePixels(new Int32Rect(0, 0, magnifier.PixelWidth, magnifier.PixelHeight), _sample, 15 * 4, 0);
        if (_size is { } size) size.Text = string.Empty;
        if (_guideText is { } guide) guide.Text = _defaultGuide;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Detach();
        _keyPanelController?.Release();
        _keyPanelController = null;
        foreach (Window window in _windows)
        {
            if (_geometry[window].Source is { IsDisposed: false } source) source.RemoveHook(OnMessage);
            window.ContentRendered -= OnRendered;
            window.PreviewKeyDown -= OnKeyDown;
            window.PreviewKeyUp -= OnKeyUp;
            window.DpiChanged -= OnDpiChanged;
            window.MouseEnter -= OnBarPointerBoundary;
            window.MouseLeave -= OnBarPointerBoundary;
            // Every chrome window gets its handle when it is created, so a zero handle means the window
            // is already destroyed: application shutdown closes every window before the dispatcher
            // raises ShutdownStarted.
            if (new WindowInteropHelper(window).Handle == IntPtr.Zero) continue;
            window.Content = null;
            window.Close();
        }
        _windows.Clear();
        _content.Clear();
        _geometry.Clear();
    }
}
