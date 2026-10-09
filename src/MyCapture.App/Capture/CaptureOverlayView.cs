using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.App.Recording;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// Full-monitor frozen-frame selector for one free-form rectangular drag.
/// </summary>
/// <remarks>
/// A valid drag is committed as soon as the mouse button is released. Keyboard users retain
/// Ctrl+A + Enter as a full-monitor alternative and Esc always cancels. When the owner enables
/// <see cref="DetectWindows"/>, hovering highlights the frontmost top-level window and a click
/// (no drag) selects it; there is still no snapping or Tab target selection. The owner can also
/// enable C to copy the frozen pixel's colour and T to switch to text capture.
/// </remarks>
internal sealed class CaptureOverlayView : FrameworkElement
{
    private const double MinimumSelectionPixels = 2;
    private const int MagnifierSourcePixels = 15;
    private const double MagnifierDestinationPixels = 150;

    internal static string InstructionText => UiText.Get("Text_798E080A9262");

    private FrozenFrame? _frame;
    private readonly RectD _screenBounds;
    private readonly bool _showMagnifier;
    private readonly bool _showSelectionGrid;

    /// <summary>
    /// Optional fixed selection size (physical pixels). When set, dragging only positions
    /// the rectangle: the drag start seeds a preset-sized box clamped to the frame, so
    /// repeated captures keep identical dimensions.
    /// </summary>
    internal System.Drawing.Size? PresetSize { get; set; }
    private Brush _dimmerBrush = null!;
    private Brush _chromeBrush = null!;
    private Brush _primaryTextBrush = null!;
    private Brush _mutedTextBrush = null!;
    private IReadOnlyDictionary<string, Brush> _guidePalette;
    internal IReadOnlyDictionary<string, Brush> GuidePalette => _guidePalette;
    internal int GuidePenBuildCount { get; private set; }
    internal int GuideGeometryBuildCount { get; private set; }
    private DpiScale _guideDpi;
    internal double GuidePixelSizeX => 1 / _guideDpi.DpiScaleX;
    internal double GuidePixelSizeY => 1 / _guideDpi.DpiScaleY;
    private readonly Typeface _uiTypeface;
    private readonly Typeface _monoTypeface;

    private RectD? _selection;
    private PointD _cursorPixel;
    private PointD _dragAnchor;
    private InteractionMode _interaction;
    private WriteableBitmap? _magnifierCrop;
    private int _magnifierCropX = -1;
    private int _magnifierCropY = -1;
    private string _sampleLabel = "#000000";
    private bool _ended;
    private readonly CapturePrecisionPointer _precisionPointer = new();
    private readonly byte[] _samplePixels = new byte[MagnifierSourcePixels * MagnifierSourcePixels * 4];
    private PointD? _lastSample;
    private bool _pointerInitialized;
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _desktopVisual = new();
    private readonly DrawingVisual _dimmerVisual = new();
    private readonly DrawingVisual _revealVisual = new();
    private readonly DrawingVisual _horizontalGuideVisual = new();
    private readonly DrawingVisual _verticalGuideVisual = new();
    private readonly RectangleGeometry _horizontalGuideClip = new();
    private readonly RectangleGeometry _verticalGuideClip = new();
    private readonly DrawingVisual _selectionVisual = new();
    private readonly DrawingVisual _instructionVisual = new();
    private readonly DrawingVisual _anchorVisual = new();
    private readonly DrawingVisual _pointerVisual = new();
    private readonly DrawingVisual _magnifierVisual = new();
    private readonly DrawingVisual _toastVisual = new();
    private readonly CompositionFrameScheduler _feedbackScheduler;
    private IReadOnlyList<WindowCandidate> _windowCandidates = [];
    private WindowCandidate? _hoverWindow;
    private RectD? _hoverRegion;
    private RectD? _drawnHover;
    private WindowCandidate? _selectedWindow;
    private bool _dragBeyondClick;
    private bool _textCaptureMode;
    private bool _drawnTextMode;
    private FormattedText? _hintText;
    private FormattedText? _textModeText;
    private string? _toastMessage;
    private string? _drawnToast;
    private PointD? _drawnToastPointer;
    private DispatcherTimer? _toastTimer;
    private Pen _windowBorderPen = null!;
    private bool _staticDirty = true;
    private bool _released;
    private RectD? _drawnSelection;
    private PointD? _drawnAnchor;
    private bool _drawnPrecision;
    private PointD? _drawnPointer;
    private PointD? _drawnMagnifierPointer;
    private bool _drawnMagnifierMouseOver;
    private bool _guideGeometryReady;
    private bool _guidesVisible;
    private FormattedText? _instructionText;
    private FormattedText? _anchorText;
    private FormattedText? _precisionText;
    private Pen _pointerOuterPen = null!;
    private Pen _pointerInnerPen = null!;
    private Pen _anchorPen = null!;
    private Pen _guideShadowPen = null!;
    private Pen _guideLinePen = null!;
    private Pen _selectionBorderPen = null!;
    private Pen _innerGlowPen = null!;
    private Pen _chipBorderPen = null!;
    private Pen _magnifierBorderPen = null!;
    private Pen _magnifierGridPen = null!;
    internal int StaticRenderCount { get; private set; }
    internal int FeedbackRenderCount { get; private set; }
    internal int MagnifierUpdateCount { get; private set; }
    internal bool HasPendingFeedback => _feedbackScheduler.IsPending;
    internal BitmapSource? MagnifierBitmapForTest => _magnifierCrop;
    internal string SampleLabelForTest => _sampleLabel;
    internal void FlushFeedbackForTest() => _feedbackScheduler.FlushForTest();
    internal RectD? HoverRegionForTest => _hoverRegion;
    internal string? ToastMessageForTest => _toastMessage;

    /// <summary>How long the in-overlay copy confirmation stays visible.</summary>
    internal static readonly TimeSpan ToastDuration = TimeSpan.FromMilliseconds(1600);

    /// <summary>
    /// Highlights the frontmost top-level window under the pointer and lets a click select it.
    /// Off by default so recording selection and diagnostics keep the plain region selector.
    /// </summary>
    internal bool DetectWindows { get; set; }

    /// <summary>Top-level windows in z-order (frontmost first), in virtual-desktop pixels.</summary>
    internal IReadOnlyList<WindowCandidate> WindowCandidates
    {
        get => _windowCandidates;
        set
        {
            _windowCandidates = value ?? [];
            if (_pointerInitialized && RefreshHover(_cursorPixel.Offset(_screenBounds.Left, _screenBounds.Top)))
                QueueFeedback();
        }
    }

    /// <summary>The overlay's own HWND, never offered as a detected window.</summary>
    internal IntPtr OwnWindowHandle { get; set; }

    /// <summary>Copies the sampled colour (C key). Null disables colour copy.</summary>
    internal Func<string, Task<bool>>? CopyColorText { get; set; }

    internal Func<ColorFormat> ReadColorFormat { get; set; } = () => ColorFormat.Hex;

    /// <summary>Lets T switch between image capture and text capture.</summary>
    internal bool TextCaptureAvailable { get; set; }

    /// <summary>When true, the confirmed region is recognised as text instead of opening the editor.</summary>
    internal bool TextCaptureMode
    {
        get => _textCaptureMode;
        set
        {
            if (_textCaptureMode == value) return;
            _textCaptureMode = value;
            QueueFeedback();
        }
    }
    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    private void QueueFeedback()
    {
        if (!_released && IsLoaded) _feedbackScheduler.Request();
    }

    internal void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        ThemeService.CaptureGuideChanged -= OnGuidePaletteChanged;
        _ended = true;
        EndPointerInteraction();
        _feedbackScheduler.Dispose();
        _toastTimer?.Stop();
        _toastTimer = null;
        _toastMessage = null;
        _windowCandidates = [];
        _hoverWindow = _selectedWindow = null;
        _hoverRegion = null;
        CopyColorText = null;
        _frame = null;
        _magnifierCrop = null;
        _lastSample = null;
        foreach (DrawingVisual visual in _visuals.Cast<DrawingVisual>())
        {
            using DrawingContext dc = visual.RenderOpen();
            visual.Clip = null;
        }
        _visuals.Clear();
    }
    internal Func<PointD> ReadCursor { get; set; } = CursorLocator.GetPosition;
    internal Func<PointD, bool> PlaceCursor { get; set; } = CursorLocator.TrySetPosition;

    internal void InitializePointer()
    {
        if (_ended) return;
        _precisionPointer.Reset();
        UpdatePointer();
    }

    internal void EndPointerInteraction()
    {
        _precisionPointer.Reset();
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
    }

    private PointD UpdatePointer()
    {
        bool active = IsVisible && Window.GetWindow(this)?.IsActive == true;
        return UpdatePointer(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), active);
    }

    internal PointD UpdatePointer(bool precision, bool active)
    {
        if (_ended) return ClampEdgePoint(_cursorPixel);
        bool wasPrecision = _precisionPointer.IsPrecision;
        PointD actual = ReadCursor();
        PointD target = _precisionPointer.Update(actual,
            active && precision, _screenBounds);
        if (target != actual && (!active || !PlaceCursor(target)))
        {
            _precisionPointer.Reset();
            target = actual;
        }
        PointD local = target.Offset(-_screenBounds.Left, -_screenBounds.Top);
        PointD cursor = ClampSamplePoint(local);
        bool changed = _staticDirty || !_pointerInitialized || _cursorPixel != cursor || _drawnPointer != cursor
            || wasPrecision != _precisionPointer.IsPrecision || _drawnPrecision != _precisionPointer.IsPrecision
            || _drawnMagnifierMouseOver != IsMouseOver;
        _cursorPixel = cursor;
        _pointerInitialized = true;
        if (RefreshHover(target)) changed = true;
        if (changed) QueueFeedback();
        return ClampEdgePoint(local);
    }

    /// <summary>
    /// Re-evaluates the highlighted window before a drag starts. A press keeps the window it
    /// started on until the pointer leaves the click threshold, so the hover is frozen while
    /// a selection or drag exists. Returns whether the highlight changed.
    /// </summary>
    private bool RefreshHover(PointD screenPoint)
    {
        WindowCandidate? window = null;
        RectD? region = null;
        if (DetectWindows && !_ended && _interaction == InteractionMode.None && !_selection.HasValue)
        {
            window = WindowDetectHitTest.FindTopmost(_windowCandidates, screenPoint, OwnWindowHandle);
            region = window is null ? null : WindowDetectHitTest.ToBitmapRegion(window, _screenBounds);
            if (region is null) window = null;
        }
        else if (_interaction == InteractionMode.Create && !_dragBeyondClick)
        {
            return false;
        }

        bool changed = _hoverRegion != region || !ReferenceEquals(_hoverWindow, window);
        _hoverWindow = window;
        _hoverRegion = region;
        return changed;
    }

    private void ClearHover()
    {
        if (_hoverRegion is null && _hoverWindow is null) return;
        _hoverWindow = null;
        _hoverRegion = null;
        QueueFeedback();
    }

    private double ClickThresholdX => Math.Max(1, SystemParameters.MinimumHorizontalDragDistance * _guideDpi.DpiScaleX);

    private double ClickThresholdY => Math.Max(1, SystemParameters.MinimumVerticalDragDistance * _guideDpi.DpiScaleY);

    internal CaptureOverlayView(FrozenFrame frame, bool showMagnifier = true, bool showSelectionGrid = true)
        : this(frame.ScreenBounds, frame, showMagnifier, showSelectionGrid)
    {
    }

    internal CaptureOverlayView(RectD screenBounds, FrozenFrame? frame = null, bool showMagnifier = true,
        bool showSelectionGrid = true)
    {
        _screenBounds = screenBounds.ToPixelBounds();
        if (_screenBounds.IsEmpty)
        {
            throw new ArgumentException("A non-empty virtual-desktop rectangle is required.", nameof(screenBounds));
        }

        _frame = frame;
        _showMagnifier = showMagnifier;
        _showSelectionGrid = showSelectionGrid;
        _visuals = new VisualCollection(this)
        {
            _desktopVisual, _dimmerVisual, _revealVisual, _horizontalGuideVisual, _verticalGuideVisual, _selectionVisual,
            _instructionVisual, _anchorVisual, _pointerVisual, _magnifierVisual, _toastVisual,
        };
        _feedbackScheduler = new CompositionFrameScheduler(Dispatcher, RenderLayers);
        _horizontalGuideVisual.Clip = _horizontalGuideClip;
        _verticalGuideVisual.Clip = _verticalGuideClip;
        _horizontalGuideVisual.Opacity = _verticalGuideVisual.Opacity = 0;
        Loaded += (_, _) =>
        {
            if (_released) return;
            ThemeService.CaptureGuideChanged -= OnGuidePaletteChanged;
            ThemeService.CaptureGuideChanged += OnGuidePaletteChanged;
            OnGuidePaletteChanged();
        };
        Unloaded += (_, _) =>
        {
            ThemeService.CaptureGuideChanged -= OnGuidePaletteChanged;
            _feedbackScheduler.CancelPending();
        };

        Focusable = true;
        // The selector never accepts text; keep single-letter commands out of the IME.
        InputMethod.SetIsInputMethodEnabled(this, false);
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        _guideDpi = VisualTreeHelper.GetDpi(this);
        _guidePalette = CaptureGuidePalette.Resolve();
        ApplyGuideBrushes();

        FontFamily uiFont = Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        FontFamily monoFont = Application.Current?.TryFindResource("Font.Mono") as FontFamily
            ?? new FontFamily("Consolas");
        _uiTypeface = new Typeface(uiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _monoTypeface = new Typeface(monoFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    }

    internal FrozenFrame? Frame => _frame;

    internal event EventHandler<RegionSelectionEventArgs>? SelectionConfirmed;

    internal event EventHandler? CancelRequested;

    internal RectD? Selection => _selection;

    /// <summary>Normalizes, clips and validates one completed physical-pixel drag.</summary>
    internal static RectD? ResolveCompletedDrag(PointD start, PointD end, RectD frameBounds)
    {
        RectD pixels = RectD.FromCorners(start, end)
            .ClampTo(frameBounds)
            .ToPixelBounds();

        return pixels.Width >= MinimumSelectionPixels && pixels.Height >= MinimumSelectionPixels
            ? pixels
            : null;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        // Hit testing stays on this element. Pixel content lives in retained child visuals;
        // moving the pointer never reopens the full-desktop drawing command list.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        _feedbackScheduler.CancelPending();
        RenderLayers();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _staticDirty = true;
        QueueFeedback();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _guideDpi = newDpi;
        _staticDirty = true;
        QueueFeedback();
    }

    private void RenderLayers()
    {
        if (_released || ActualWidth <= 0 || ActualHeight <= 0) return;
        FeedbackRenderCount++;
        bool rebuild = _staticDirty;
        if (rebuild)
        {
            _staticDirty = false;
            StaticRenderCount++;
            BuildGuidePens();
            _instructionText = _anchorText = _precisionText = _hintText = _textModeText = null;
            using (DrawingContext dc = _desktopVisual.RenderOpen())
                if (_frame?.Bitmap is { } bitmap) dc.DrawImage(bitmap, new Rect(RenderSize));
            using (DrawingContext dc = _dimmerVisual.RenderOpen())
                dc.DrawRectangle(_dimmerBrush, null, new Rect(RenderSize));
            // Both image visuals share exactly one source; there is no rasterized cache.
            using (DrawingContext dc = _revealVisual.RenderOpen())
                if (_frame?.Bitmap is { } revealBitmap) dc.DrawImage(revealBitmap, new Rect(RenderSize));
        }
        RectD? hover = _selection.HasValue ? null : _hoverRegion;
        if (rebuild || _drawnSelection != _selection || _drawnHover != hover)
        {
            _drawnSelection = _selection;
            _drawnHover = hover;
            RectD? revealed = _selection ?? hover;
            _revealVisual.Clip = new RectangleGeometry(revealed is { } selected
                ? ToDipRect(selected.ClampTo(FrameBounds)) : new Rect(0, 0, 0, 0));
            using DrawingContext dc = _selectionVisual.RenderOpen();
            if (_selection is { } selection) DrawSelection(dc, selection);
            else if (hover is { } window) DrawDetectedWindow(dc, window);
        }
        if (rebuild || _drawnPrecision != _precisionPointer.IsPrecision || _drawnTextMode != _textCaptureMode)
        {
            if (_drawnTextMode != _textCaptureMode) _hintText = null;
            _drawnPrecision = _precisionPointer.IsPrecision;
            _drawnTextMode = _textCaptureMode;
            using DrawingContext dc = _instructionVisual.RenderOpen();
            DrawInstructions(dc);
        }
        PointD? anchor = _interaction == InteractionMode.Create && _pointerInitialized && !_ended ? _dragAnchor : null;
        if (rebuild || _drawnAnchor != anchor)
        {
            _drawnAnchor = anchor;
            using DrawingContext dc = _anchorVisual.RenderOpen();
            if (anchor.HasValue) DrawPointerFeedback(dc, anchor: true);
        }
        UpdateStartingGuides(rebuild);
        PointD? pointer = _pointerInitialized && !_ended ? _cursorPixel : null;
        if (rebuild || _drawnPointer != pointer)
        {
            _drawnPointer = pointer;
            using DrawingContext dc = _pointerVisual.RenderOpen();
            DrawPointerFeedback(dc, anchor: false);
        }
        bool mouseOver = IsMouseOver;
        if (rebuild || _drawnMagnifierPointer != pointer || _drawnMagnifierMouseOver != mouseOver)
        {
            _drawnMagnifierPointer = pointer;
            _drawnMagnifierMouseOver = mouseOver;
            if (_showMagnifier)
            {
                if (pointer.HasValue) UpdateMagnifier();
                using DrawingContext dc = _magnifierVisual.RenderOpen();
                if (pointer.HasValue) DrawMagnifier(dc);
            }
        }
        if (rebuild || _drawnToast != _toastMessage || (_toastMessage is not null && _drawnToastPointer != pointer))
        {
            _drawnToast = _toastMessage;
            _drawnToastPointer = pointer;
            using DrawingContext dc = _toastVisual.RenderOpen();
            if (_toastMessage is { } message && !_ended) DrawToast(dc, message);
        }
    }

    /// <summary>Short confirmation chip above and to the right of the pointer, clear of the magnifier.</summary>
    private void DrawToast(DrawingContext dc, string message)
    {
        FormattedText text = CreateText(message, _uiTypeface, 12, _primaryTextBrush);
        const double paddingX = 10;
        const double paddingY = 6;
        double width = text.Width + (paddingX * 2);
        double height = text.Height + (paddingY * 2);
        Point cursor = ToDipPoint(_cursorPixel);
        double x = Math.Clamp(cursor.X + 16, 8, Math.Max(8, ActualWidth - width - 8));
        double y = cursor.Y - height - 14;
        if (y < 8) y = Math.Min(Math.Max(8, ActualHeight - height - 8), cursor.Y + 18);
        var background = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(_chromeBrush, _windowBorderPen, background, 7, 7);
        dc.DrawText(text, new Point(x + paddingX, y + paddingY));
    }

    private void DrawPointerFeedback(DrawingContext dc, bool anchor)
    {
        if (!_pointerInitialized || _ended) return;
        void Target(PointD pixel)
        {
            Point p = ToDipPoint(pixel);
            void Cross(Pen pen)
            {
                dc.DrawLine(pen, new Point(p.X - 11, p.Y), new Point(p.X + 11, p.Y));
                dc.DrawLine(pen, new Point(p.X, p.Y - 11), new Point(p.X, p.Y + 11));
                if (anchor) dc.DrawEllipse(null, pen, p, 5, 5);
            }
            Cross(_pointerOuterPen);
            Cross(anchor ? _anchorPen : _pointerInnerPen);
            if (anchor)
            {
                FormattedText text = _anchorText ??= CreateText(UiText.Get("CaptureSelectionStart"), _uiTypeface, 11, _primaryTextBrush);
                double x = Math.Clamp(p.X + 15, 4, Math.Max(4, ActualWidth - text.Width - 12));
                double y = Math.Clamp(p.Y + 12, 4, Math.Max(4, ActualHeight - text.Height - 8));
                dc.DrawRoundedRectangle(_chromeBrush, _chipBorderPen, new Rect(x - 4, y - 2, text.Width + 8, text.Height + 4), 4, 4);
                dc.DrawText(text, new Point(x, y));
            }
        }
        Target(anchor ? _dragAnchor : _cursorPixel);
    }

    private void DrawSelection(DrawingContext dc, RectD pixelRect) => DrawRegion(dc, pixelRect, borderPen: null);

    /// <summary>The auto-detected window uses the same token colour with a 2px border.</summary>
    private void DrawDetectedWindow(DrawingContext dc, RectD pixelRect) => DrawRegion(dc, pixelRect, _windowBorderPen);

    private void DrawRegion(DrawingContext dc, RectD pixelRect, Pen? borderPen)
    {
        Rect mapped = ToDipRect(pixelRect.ToPixelBounds());
        double px = GuidePixelSizeX, py = GuidePixelSizeY;
        double left = Math.Round(mapped.Left / px) * px, top = Math.Round(mapped.Top / py) * py;
        double right = Math.Round(mapped.Right / px) * px, bottom = Math.Round(mapped.Bottom / py) * py;
        var rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        if (rect.Width <= 0 || rect.Height <= 0) return;
        // Clip the low-opacity accent to the inside; the crisp border occupies one
        // physical pixel inside the selected pixel bounds at every display scale.
        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawRectangle(null, _innerGlowPen, rect);
        dc.Pop();
        var border = new Rect(rect.X + px / 2, rect.Y + py / 2,
            Math.Max(0, rect.Width - px), Math.Max(0, rect.Height - py));
        if (borderPen is not null)
        {
            // The thicker detected-window border stays inside the window bounds like the 1px one.
            double inset = borderPen.Thickness / 2;
            border = new Rect(rect.X + inset, rect.Y + inset,
                Math.Max(0, rect.Width - borderPen.Thickness), Math.Max(0, rect.Height - borderPen.Thickness));
        }
        dc.DrawRectangle(null, borderPen ?? _selectionBorderPen, border);
        DrawDimensionLabel(dc, pixelRect.ToPixelBounds(), rect);
    }

    private void UpdateStartingGuides(bool rebuild)
    {
        if (rebuild) _guideGeometryReady = false;
        bool visible = _showSelectionGrid && _pointerInitialized && !_ended
            && _interaction == InteractionMode.None && !_selection.HasValue;
        if (!visible)
        {
            if (_guidesVisible)
                _horizontalGuideVisual.Opacity = _verticalGuideVisual.Opacity = 0;
            _guidesVisible = false;
            return;
        }
        if (rebuild || !_guideGeometryReady)
        {
            GuideGeometryBuildCount++;
            // Retain two narrow strips rather than redrawing one viewport-sized visual.
            // Their order preserves the original horizontal-then-vertical intersection.
            using (DrawingContext dc = _horizontalGuideVisual.RenderOpen())
                DrawGuide(dc, new Point(0, 0), new Point(ActualWidth, 0));
            using (DrawingContext dc = _verticalGuideVisual.RenderOpen())
                DrawGuide(dc, new Point(0, 0), new Point(0, ActualHeight));
            _guideGeometryReady = true;
        }
        Point p = ToDipPoint(_cursorPixel);
        _horizontalGuideVisual.Offset = new Vector(0, p.Y);
        _verticalGuideVisual.Offset = new Vector(p.X, 0);
        double halfStroke = _guideShadowPen.Thickness / 2;
        // Leave one physical pixel around the stroke's antialias fringe. Clipping at
        // the stroke edge itself would multiply its partial coverage a second time.
        double horizontalExtent = halfStroke + DipPerPixelY;
        double verticalExtent = halfStroke + DipPerPixelX;
        double top = Math.Max(-horizontalExtent, -p.Y);
        double bottom = Math.Min(horizontalExtent, ActualHeight - p.Y);
        double left = Math.Max(-verticalExtent, -p.X);
        double right = Math.Min(verticalExtent, ActualWidth - p.X);
        Rect horizontal = new(0, top, ActualWidth, Math.Max(0, bottom - top));
        Rect vertical = new(left, 0, Math.Max(0, right - left), ActualHeight);
        if (_horizontalGuideClip.Rect != horizontal) _horizontalGuideClip.Rect = horizontal;
        if (_verticalGuideClip.Rect != vertical) _verticalGuideClip.Rect = vertical;
        if (!_guidesVisible)
            _horizontalGuideVisual.Opacity = _verticalGuideVisual.Opacity = 1;
        _guidesVisible = true;

        void DrawGuide(DrawingContext dc, Point from, Point to)
        {
            dc.DrawLine(_guideShadowPen, from, to);
            dc.DrawLine(_guideLinePen, from, to);
        }
    }

    private void DrawDimensionLabel(DrawingContext dc, RectD pixels, Rect selectionDip)
    {
        string text = $"{(int)pixels.Width} × {(int)pixels.Height}";
        FormattedText formatted = CreateText(text, _monoTypeface, 12, _primaryTextBrush);
        const double paddingX = 9;
        const double paddingY = 5;
        double width = formatted.Width + (paddingX * 2);
        double height = formatted.Height + (paddingY * 2);

        double x = Math.Clamp(selectionDip.Left, 8, Math.Max(8, ActualWidth - width - 8));
        double y = selectionDip.Top - height - 8;
        if (y < 8)
        {
            y = Math.Min(ActualHeight - height - 8, selectionDip.Bottom + 8);
        }

        var background = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(_chromeBrush, _chipBorderPen, background, 7, 7);
        dc.DrawText(formatted, new Point(x + paddingX, y + paddingY));
    }

    private void DrawInstructions(DrawingContext dc)
    {
        FormattedText text = _instructionText ??= CreateText(InstructionText, _uiTypeface, 13, _primaryTextBrush);
        const double paddingX = 14;
        const double paddingY = 9;
        double width = text.Width + (paddingX * 2);
        double left = Math.Max(16, (ActualWidth - width) / 2);
        var background = new Rect(left, 18, width, text.Height + (paddingY * 2));
        dc.DrawRoundedRectangle(_chromeBrush, _chipBorderPen, background, 10, 10);
        dc.DrawText(text, new Point(background.Left + paddingX, background.Top + paddingY));
        double nextTop = background.Bottom + 6;
        if (OverlayHintText is { } hint)
        {
            FormattedText hintText = _hintText ??= CreateText(hint, _uiTypeface, 12, _primaryTextBrush);
            nextTop = DrawCenteredChip(dc, hintText, nextTop, _chipBorderPen) + 6;
        }
        if (_textCaptureMode)
        {
            FormattedText modeText = _textModeText ??= CreateText(UiText.Get("TextCapture_ModeActive"), _uiTypeface, 12, _primaryTextBrush);
            DrawCenteredChip(dc, modeText, nextTop, _windowBorderPen);
        }
        if (_precisionPointer.IsPrecision)
        {
            FormattedText precision = _precisionText ??= CreateText(UiText.Get("CapturePrecisionActive"), _uiTypeface, 12, _primaryTextBrush);
            dc.DrawRoundedRectangle(_chromeBrush, _chipBorderPen,
                new Rect(16, 64, precision.Width + 20, precision.Height + 12), 6, 6);
            dc.DrawText(precision, new Point(26, 70));
        }
    }

    /// <summary>Optional key hints for the features the owner enabled; null when none are.</summary>
    internal string? OverlayHintText
    {
        get
        {
            var parts = new List<string>(3);
            if (DetectWindows) parts.Add(UiText.Get("WindowDetect_OverlayHint"));
            if (CopyColorText is not null) parts.Add(UiText.Get("ColorPick_OverlayHint"));
            if (TextCaptureAvailable)
                parts.Add(UiText.Get(_textCaptureMode ? "TextCapture_OverlayHintImage" : "TextCapture_OverlayHint"));
            return parts.Count == 0 ? null : string.Join("  ·  ", parts);
        }
    }

    private double DrawCenteredChip(DrawingContext dc, FormattedText text, double top, Pen border)
    {
        const double paddingX = 12;
        const double paddingY = 6;
        double width = text.Width + (paddingX * 2);
        double left = Math.Max(16, (ActualWidth - width) / 2);
        var background = new Rect(left, top, width, text.Height + (paddingY * 2));
        dc.DrawRoundedRectangle(_chromeBrush, border, background, 8, 8);
        dc.DrawText(text, new Point(background.Left + paddingX, background.Top + paddingY));
        return background.Bottom;
    }

    private void DrawMagnifier(DrawingContext dc)
    {
        if (!_showMagnifier || _magnifierCrop is null || !IsMouseOver)
        {
            return;
        }

        double width = MagnifierDestinationPixels * DipPerPixelX;
        double imageHeight = MagnifierDestinationPixels * DipPerPixelY;
        double footerHeight = 38 * DipPerPixelY;
        double gapX = 20 * DipPerPixelX;
        double gapY = 22 * DipPerPixelY;
        Point cursorDip = ToDipPoint(_cursorPixel);

        double x = cursorDip.X + gapX;
        double y = cursorDip.Y + gapY;
        if (x + width > ActualWidth - 8)
        {
            x = cursorDip.X - gapX - width;
        }

        if (y + imageHeight + footerHeight > ActualHeight - 8)
        {
            y = cursorDip.Y - gapY - imageHeight - footerHeight;
        }

        x = Math.Clamp(x, 8, Math.Max(8, ActualWidth - width - 8));
        y = Math.Clamp(y, 8, Math.Max(8, ActualHeight - imageHeight - footerHeight - 8));

        var panel = new Rect(x - 3, y - 3, width + 6, imageHeight + footerHeight + 6);
        dc.DrawRoundedRectangle(_chromeBrush, _magnifierBorderPen, panel, 9, 9);
        dc.DrawImage(_magnifierCrop, new Rect(x, y, width, imageHeight));

        double cellWidth = width / _magnifierCrop.PixelWidth;
        double cellHeight = imageHeight / _magnifierCrop.PixelHeight;
        Pen gridPen = _magnifierGridPen;
        for (int index = 1; index < _magnifierCrop.PixelWidth; index++)
        {
            dc.DrawLine(gridPen, new Point(x + (index * cellWidth), y), new Point(x + (index * cellWidth), y + imageHeight));
        }
        for (int index = 1; index < _magnifierCrop.PixelHeight; index++)
        {
            dc.DrawLine(gridPen, new Point(x, y + (index * cellHeight)), new Point(x + width, y + (index * cellHeight)));
        }

        int cursorX = Math.Clamp((int)Math.Floor(_cursorPixel.X) - _magnifierCropX, 0, _magnifierCrop.PixelWidth - 1);
        int cursorY = Math.Clamp((int)Math.Floor(_cursorPixel.Y) - _magnifierCropY, 0, _magnifierCrop.PixelHeight - 1);
        var cell = new Rect(x + (cursorX * cellWidth), y + (cursorY * cellHeight), cellWidth, cellHeight);
        dc.DrawRectangle(null, _magnifierBorderPen, cell);

        string coordinate = $"{(int)_cursorPixel.X}, {(int)_cursorPixel.Y}";
        FormattedText color = CreateText(_sampleLabel, _monoTypeface, 12, _primaryTextBrush);
        FormattedText location = CreateText(coordinate, _monoTypeface, 10, _mutedTextBrush);
        double footerTop = y + imageHeight;
        dc.DrawText(color, new Point(x + 8, footerTop + 5));
        dc.DrawText(location, new Point(x + width - location.Width - 8, footerTop + 6));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_ended) return;
        PointD rawPixel = UpdatePointer();
        RectD? previous = _selection;

        if (_interaction == InteractionMode.Create)
        {
            PointD edge = ClampEdgePoint(rawPixel);
            if (_hoverRegion.HasValue && !_dragBeyondClick)
            {
                // Inside the click threshold the press still targets the highlighted window.
                if (WindowDetectHitTest.IsClick(_dragAnchor, edge, ClickThresholdX, ClickThresholdY)) return;
                _dragBeyondClick = true;
                ClearHover();
            }

            if (PresetSize is { } preset && preset.Width > 0 && preset.Height > 0)
            {
                // Fixed size: the drag only moves the box's top-left corner. The stored
                // size is honoured exactly (clamped to the frame) so every capture from
                // the same preset has identical dimensions.
                double width = Math.Min(preset.Width, FrameBounds.Width);
                double height = Math.Min(preset.Height, FrameBounds.Height);
                _selection = new RectD(_dragAnchor.X, _dragAnchor.Y, width, height).ClampTo(FrameBounds);
            }
            else
            {
                _selection = RectD.FromCorners(_dragAnchor, edge).ClampTo(FrameBounds);
            }
        }

        if (previous != _selection) QueueFeedback();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_ended)
        {
            return;
        }

        Focus();
        PointD rawPoint = UpdatePointer();
        PointD point = ClampEdgePoint(rawPoint);
        _cursorPixel = ClampSamplePoint(rawPoint);
        _dragAnchor = point;
        _selectedWindow = null;
        _dragBeyondClick = false;
        // A press on a highlighted window keeps that highlight until the pointer leaves the
        // click threshold; otherwise the free-region drag starts exactly as before.
        _selection = _hoverRegion.HasValue ? null : new RectD(point.X, point.Y, 0, 0);
        _interaction = InteractionMode.Create;
        _ = Mouse.Capture(this);
        e.Handled = true;
        QueueFeedback();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_interaction != InteractionMode.Create || _ended)
        {
            return;
        }

        PointD rawPoint = UpdatePointer();
        PointD point = ClampEdgePoint(rawPoint);
        _cursorPixel = ClampSamplePoint(rawPoint);
        if (_hoverRegion is { } windowRegion && !_dragBeyondClick &&
            WindowDetectHitTest.IsClick(_dragAnchor, point, ClickThresholdX, ClickThresholdY))
        {
            _selection = windowRegion.ClampTo(FrameBounds).ToPixelBounds();
            _selectedWindow = _hoverWindow;
            _hoverWindow = null;
            _hoverRegion = null;
        }
        else
        {
            _selection = ResolveCompletedDrag(_dragAnchor, point, FrameBounds);
        }
        _interaction = InteractionMode.None;
        Mouse.Capture(null);
        e.Handled = true;
        QueueFeedback();

        if (_selection.HasValue)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ConfirmSelection));
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        RequestCancel();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // A Korean IME in Hangul mode reports letters as ImeProcessed; C and T must still work.
        Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                RequestCancel();
                e.Handled = true;
                return;
            case Key.LeftShift:
            case Key.RightShift:
                UpdatePointer();
                return;
            case Key.Enter:
                ConfirmSelection();
                e.Handled = true;
                return;
            case Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                _selection = FrameBounds;
                _selectedWindow = null;
                ClearHover();
                QueueFeedback();
                e.Handled = true;
                return;
            // Plain C/T only: Shift may be held for precision, Ctrl/Alt chords stay untouched.
            case Key.C when CopyColorText is not null && IsPlainOrShift(Keyboard.Modifiers):
                UpdatePointer();
                _ = CopyColorAtPointerAsync();
                e.Handled = true;
                return;
            case Key.T when TextCaptureAvailable && IsPlainOrShift(Keyboard.Modifiers):
                TextCaptureMode = !TextCaptureMode;
                e.Handled = true;
                return;
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                NudgeSelection(e.Key, Keyboard.Modifiers);
                e.Handled = true;
                return;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!_ended && e.Key is Key.LeftShift or Key.RightShift) UpdatePointer();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _precisionPointer.Reset();
        if (_interaction == InteractionMode.Create && !_ended)
        {
            _interaction = InteractionMode.None;
            _selection = null;
            QueueFeedback();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_interaction == InteractionMode.None)
        {
            _lastSample = null;
            QueueFeedback();
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!_ended) QueueFeedback();
    }

    private void NudgeSelection(Key key, ModifierKeys modifiers)
    {
        if (_selection is not RectD selection)
        {
            return;
        }

        double step = modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1;
        bool resize = modifiers.HasFlag(ModifierKeys.Shift);
        _selectedWindow = null;

        if (resize)
        {
            double left = selection.Left;
            double top = selection.Top;
            double right = selection.Right;
            double bottom = selection.Bottom;
            switch (key)
            {
                case Key.Left: right = Math.Max(left + 1, right - step); break;
                case Key.Right: right = Math.Min(FrameBounds.Right, right + step); break;
                case Key.Up: bottom = Math.Max(top + 1, bottom - step); break;
                case Key.Down: bottom = Math.Min(FrameBounds.Bottom, bottom + step); break;
            }

            _selection = new RectD(left, top, right - left, bottom - top).ToPixelBounds();
        }
        else
        {
            double dx = key switch { Key.Left => -step, Key.Right => step, _ => 0 };
            double dy = key switch { Key.Up => -step, Key.Down => step, _ => 0 };
            _selection = new RectD(selection.Left + dx, selection.Top + dy, selection.Width, selection.Height)
                .ClampTo(FrameBounds)
                .ToPixelBounds();
        }

        QueueFeedback();
    }

    private void ConfirmSelection()
    {
        if (_ended || _selection is not RectD selection)
        {
            return;
        }

        RectD pixels = selection.ToPixelBounds().ClampTo(FrameBounds);
        if (pixels.Width < MinimumSelectionPixels || pixels.Height < MinimumSelectionPixels)
        {
            return;
        }

        _ended = true;
        EndPointerInteraction();
        SelectionConfirmed?.Invoke(this, new RegionSelectionEventArgs(pixels)
        {
            DetectedWindow = _selectedWindow,
            TextCapture = _textCaptureMode,
        });
    }

    private static bool IsPlainOrShift(ModifierKeys modifiers) =>
        modifiers is ModifierKeys.None or ModifierKeys.Shift;

    /// <summary>
    /// Copies the frozen pixel under the pointer (the one the magnifier centres on) in the
    /// configured format and shows a short confirmation inside the overlay. The overlay stays
    /// open. Returns the copied value, or null when nothing was copied.
    /// </summary>
    internal async Task<string?> CopyColorAtPointerAsync()
    {
        if (_ended || _released || CopyColorText is not { } copy || !TrySampleColor(_cursorPixel, out byte red, out byte green, out byte blue))
        {
            return null;
        }

        string value = ColorSampleFormatter.Format(red, green, blue, ReadColorFormat());
        bool copied;
        try
        {
            copied = await copy(value).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            copied = false;
        }

        string message = copied ? UiText.Format("Tools_ColorCopied", value) : UiText.Get("ColorPick_CopyFailed");
        if (Dispatcher.CheckAccess()) ShowToast(message);
        else _ = Dispatcher.BeginInvoke(new Action(() => ShowToast(message)));
        return copied ? value : null;
    }

    private bool TrySampleColor(PointD pixel, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (_frame is not { } frame || frame.PixelWidth < 1 || frame.PixelHeight < 1) return false;
        int x = Math.Clamp((int)Math.Floor(pixel.X), 0, frame.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Floor(pixel.Y), 0, frame.PixelHeight - 1);
        var rect = new Int32Rect(x, y, 1, 1);
        byte[] sample = new byte[4];
        BitmapSource source = frame.Bitmap;
        if (source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32)
        {
            source.CopyPixels(rect, sample, 4, 0);
        }
        else
        {
            // Same narrow conversion as the magnifier: never convert the whole desktop.
            new FormatConvertedBitmap(new CroppedBitmap(source, rect), PixelFormats.Bgra32, null, 0)
                .CopyPixels(sample, 4, 0);
        }

        blue = sample[0];
        green = sample[1];
        red = sample[2];
        return true;
    }

    private void ShowToast(string message)
    {
        if (_released || _ended) return;
        _toastMessage = message;
        _toastTimer ??= CreateToastTimer();
        _toastTimer.Stop();
        _toastTimer.Start();
        QueueFeedback();
    }

    private DispatcherTimer CreateToastTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = ToastDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toastMessage = null;
            QueueFeedback();
        };
        return timer;
    }

    private void RequestCancel()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        EndPointerInteraction();
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void AttachFrame(FrozenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_released) return;
        _frame = frame;
        _staticDirty = true;
        _magnifierCrop = null;
        _magnifierCropX = -1;
        _magnifierCropY = -1;
        _lastSample = null;
        QueueFeedback();
    }

    private void UpdateMagnifier()
    {
        if (_frame is null)
        {
            _magnifierCrop = null;
            return;
        }

        int centerX = Math.Clamp((int)Math.Floor(_cursorPixel.X), 0, _frame.PixelWidth - 1);
        int centerY = Math.Clamp((int)Math.Floor(_cursorPixel.Y), 0, _frame.PixelHeight - 1);
        PointD sample = new(centerX, centerY);
        if (_magnifierCrop is not null && _lastSample == sample) return;
        _lastSample = sample;
        int half = MagnifierSourcePixels / 2;
        int x = Math.Clamp(centerX - half, 0, Math.Max(0, _frame.PixelWidth - MagnifierSourcePixels));
        int y = Math.Clamp(centerY - half, 0, Math.Max(0, _frame.PixelHeight - MagnifierSourcePixels));
        int width = Math.Min(MagnifierSourcePixels, _frame.PixelWidth);
        int height = Math.Min(MagnifierSourcePixels, _frame.PixelHeight);

        int stride = width * 4;
        BitmapSource source = _frame.Bitmap;
        bool direct = source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32
            || source.Format == PixelFormats.Pbgra32;
        PixelFormat format = direct ? source.Format : PixelFormats.Bgra32;
        if (_magnifierCrop is null)
        {
            _magnifierCrop = new WriteableBitmap(width, height, 96, 96, format, null);
        }
        var sourceRect = new Int32Rect(x, y, width, height);
        if (direct) source.CopyPixels(sourceRect, _samplePixels, stride, 0);
        else
        {
            // Unusual imported/test formats convert only the tiny sampled region. Never
            // materialize a converted desktop merely to display a magnifier pixel.
            var region = new CroppedBitmap(source, sourceRect);
            var converted = new FormatConvertedBitmap(region, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(_samplePixels, stride, 0);
        }
        _magnifierCrop.WritePixels(new Int32Rect(0, 0, width, height), _samplePixels, stride, 0);
        _magnifierCropX = x;
        _magnifierCropY = y;
        int sampleOffset = ((centerY - y) * width + centerX - x) * 4;
        _sampleLabel = $"#{_samplePixels[sampleOffset + 2]:X2}{_samplePixels[sampleOffset + 1]:X2}{_samplePixels[sampleOffset]:X2}";
        MagnifierUpdateCount++;
    }

    private void ApplyGuideBrushes()
    {
        _dimmerBrush = _guidePalette["CaptureGuide.Dimmer"];
        _chromeBrush = _guidePalette["CaptureGuide.ChipSurface"];
        _primaryTextBrush = _guidePalette["CaptureGuide.Text"];
        _mutedTextBrush = _guidePalette["CaptureGuide.MutedText"];
    }

    private void OnGuidePaletteChanged()
    {
        if (_released) return;
        _guidePalette = CaptureGuidePalette.Resolve();
        ApplyGuideBrushes();
        _staticDirty = true;
        QueueFeedback();
    }

    private void BuildGuidePens()
    {
        GuidePenBuildCount++;
        double pixel = GuidePixelSizeX;
        Pen Create(string key, double width, DashStyle? dash = null)
        {
            var pen = new Pen(_guidePalette[key], width * pixel);
            if (dash is not null) pen.DashStyle = dash;
            pen.Freeze();
            return pen;
        }
        _pointerOuterPen = Create("CaptureGuide.CrosshairShadow", 3);
        _pointerInnerPen = Create("CaptureGuide.Crosshair", 1);
        _anchorPen = Create("CaptureGuide.StartPoint", 1);
        _guideShadowPen = Create("CaptureGuide.LineShadow", 2, new DashStyle([1.5, 1.5], 0));
        _guideLinePen = Create("CaptureGuide.Line", 1, new DashStyle([3, 3], 0));
        _selectionBorderPen = Create("CaptureGuide.SelectionBorder", 1);
        _windowBorderPen = Create("CaptureGuide.SelectionBorder", 2);
        _innerGlowPen = Create("CaptureGuide.InnerGlow", 3);
        _chipBorderPen = Create("CaptureGuide.ChipBorder", 1);
        _magnifierBorderPen = Create("CaptureGuide.MagnifierBorder", 1);
        _magnifierGridPen = Create("CaptureGuide.MagnifierGrid", 0.5);
    }

    private FormattedText CreateText(string text, Typeface typeface, double size, Brush brush) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private int PixelWidth => _frame?.PixelWidth ?? Math.Max(1, (int)_screenBounds.Width);

    private int PixelHeight => _frame?.PixelHeight ?? Math.Max(1, (int)_screenBounds.Height);

    private RectD FrameBounds => new(0, 0, PixelWidth, PixelHeight);

    private double DipPerPixelX => ActualWidth / PixelWidth;

    private double DipPerPixelY => ActualHeight / PixelHeight;

    private PointD ToPixelPoint(Point dipPoint) => new(
        dipPoint.X / Math.Max(double.Epsilon, DipPerPixelX),
        dipPoint.Y / Math.Max(double.Epsilon, DipPerPixelY));

    private Point ToDipPoint(PointD pixelPoint) => new(
        pixelPoint.X * DipPerPixelX,
        pixelPoint.Y * DipPerPixelY);

    private Rect ToDipRect(RectD pixelRect) => new(
        pixelRect.Normalized().Left * DipPerPixelX,
        pixelRect.Normalized().Top * DipPerPixelY,
        pixelRect.Normalized().Width * DipPerPixelX,
        pixelRect.Normalized().Height * DipPerPixelY);

    private PointD ClampSamplePoint(PointD point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, PixelWidth - 1)),
        Math.Clamp(point.Y, 0, Math.Max(0, PixelHeight - 1)));

    private PointD ClampEdgePoint(PointD point) => new(
        Math.Clamp(point.X, 0, PixelWidth),
        Math.Clamp(point.Y, 0, PixelHeight));

    private enum InteractionMode
    {
        None,
        Create,
    }
}

internal sealed class RegionSelectionEventArgs : EventArgs
{
    internal RegionSelectionEventArgs(RectD bitmapRegion)
    {
        BitmapRegion = bitmapRegion;
    }

    internal RectD BitmapRegion { get; }

    /// <summary>The auto-detected window a click selected, or null for a drag or keyboard selection.</summary>
    internal WindowCandidate? DetectedWindow { get; init; }

    /// <summary>True when the overlay was in text capture mode at confirmation.</summary>
    internal bool TextCapture { get; init; }
}
