using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.App.Recording;

namespace MyCapture.App.Capture;

/// <summary>
/// Full-monitor frozen-frame selector for one free-form rectangular drag.
/// </summary>
/// <remarks>
/// The region hotkey is deliberately region-only: there is no window hover, snapping or Tab
/// target selection. A valid drag is committed as soon as the mouse button is released. Keyboard
/// users retain Ctrl+A + Enter as a full-monitor alternative and Esc always cancels.
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
    private readonly Brush _dimmerBrush;
    private readonly Brush _selectionBrush;
    private readonly Brush _chromeBrush;
    private readonly Brush _primaryTextBrush;
    private readonly Brush _mutedTextBrush;
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
    private readonly DrawingVisual _selectionVisual = new();
    private readonly DrawingVisual _instructionVisual = new();
    private readonly DrawingVisual _anchorVisual = new();
    private readonly DrawingVisual _pointerVisual = new();
    private readonly DrawingVisual _magnifierVisual = new();
    private readonly CompositionFrameScheduler _feedbackScheduler;
    private bool _staticDirty = true;
    private bool _released;
    private RectD? _drawnSelection;
    private PointD? _drawnAnchor;
    private bool _drawnPrecision;
    private FormattedText? _instructionText;
    private FormattedText? _anchorText;
    private FormattedText? _precisionText;
    private readonly Pen _pointerOuterPen = new(Brushes.Black, 4);
    private readonly Pen _pointerInnerPen = new(Brushes.White, 1.5);
    private readonly Pen _anchorPen;
    internal int StaticRenderCount { get; private set; }
    internal int FeedbackRenderCount { get; private set; }
    internal int MagnifierUpdateCount { get; private set; }
    internal bool HasPendingFeedback => _feedbackScheduler.IsPending;
    internal BitmapSource? MagnifierBitmapForTest => _magnifierCrop;
    internal string SampleLabelForTest => _sampleLabel;
    internal void FlushFeedbackForTest() => _feedbackScheduler.FlushForTest();
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
        _ended = true;
        EndPointerInteraction();
        _feedbackScheduler.Dispose();
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
        PointD actual = ReadCursor();
        PointD target = _precisionPointer.Update(actual,
            active && precision, _screenBounds);
        if (target != actual && (!active || !PlaceCursor(target)))
        {
            _precisionPointer.Reset();
            target = actual;
        }
        PointD local = target.Offset(-_screenBounds.Left, -_screenBounds.Top);
        _cursorPixel = ClampSamplePoint(local);
        _pointerInitialized = true;
        QueueFeedback();
        return ClampEdgePoint(local);
    }

    internal CaptureOverlayView(FrozenFrame frame, bool showMagnifier = true)
        : this(frame.ScreenBounds, frame, showMagnifier)
    {
    }

    internal CaptureOverlayView(RectD screenBounds, FrozenFrame? frame = null, bool showMagnifier = true)
    {
        _screenBounds = screenBounds.ToPixelBounds();
        if (_screenBounds.IsEmpty)
        {
            throw new ArgumentException("A non-empty virtual-desktop rectangle is required.", nameof(screenBounds));
        }

        _frame = frame;
        _showMagnifier = showMagnifier;
        _visuals = new VisualCollection(this)
        {
            _desktopVisual, _dimmerVisual, _revealVisual, _selectionVisual,
            _instructionVisual, _anchorVisual, _pointerVisual, _magnifierVisual,
        };
        _feedbackScheduler = new CompositionFrameScheduler(Dispatcher, RenderLayers);
        Unloaded += (_, _) => _feedbackScheduler.CancelPending();

        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        _dimmerBrush = ResourceBrush("Overlay.Dimmer", new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)));
        _selectionBrush = ResourceBrush("Overlay.SelectionBorder", new SolidColorBrush(Color.FromRgb(0x7D, 0xD7, 0xF8)));
        _anchorPen = new Pen(_selectionBrush, 1.5);
        _pointerOuterPen.Freeze();
        _pointerInnerPen.Freeze();
        if (_anchorPen.CanFreeze) _anchorPen.Freeze();
        _chromeBrush = ResourceBrush("Surface.Floating", new SolidColorBrush(Color.FromArgb(0xF2, 0x15, 0x1E, 0x2B)));
        _primaryTextBrush = ResourceBrush("Text.Primary", Brushes.White);
        _mutedTextBrush = ResourceBrush("Text.Secondary", Brushes.LightGray);

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
            _instructionText = _anchorText = _precisionText = null;
            using (DrawingContext dc = _desktopVisual.RenderOpen())
                if (_frame?.Bitmap is { } bitmap) dc.DrawImage(bitmap, new Rect(RenderSize));
            using (DrawingContext dc = _dimmerVisual.RenderOpen())
                dc.DrawRectangle(_dimmerBrush, null, new Rect(RenderSize));
            // Both image visuals share exactly one source; there is no rasterized cache.
            using (DrawingContext dc = _revealVisual.RenderOpen())
                if (_frame?.Bitmap is { } revealBitmap) dc.DrawImage(revealBitmap, new Rect(RenderSize));
        }
        if (rebuild || _drawnSelection != _selection)
        {
            _drawnSelection = _selection;
            _revealVisual.Clip = new RectangleGeometry(_selection is { } selected
                ? ToDipRect(selected.ClampTo(FrameBounds)) : new Rect(0, 0, 0, 0));
            using DrawingContext dc = _selectionVisual.RenderOpen();
            if (_selection is { } selection) DrawSelection(dc, selection);
        }
        if (rebuild || _drawnPrecision != _precisionPointer.IsPrecision)
        {
            _drawnPrecision = _precisionPointer.IsPrecision;
            using DrawingContext dc = _instructionVisual.RenderOpen();
            DrawInstructions(dc);
        }
        PointD? anchor = _interaction == InteractionMode.Create ? _dragAnchor : null;
        if (rebuild || _drawnAnchor != anchor)
        {
            _drawnAnchor = anchor;
            using DrawingContext dc = _anchorVisual.RenderOpen();
            if (anchor.HasValue) DrawPointerFeedback(dc, anchor: true);
        }
        using (DrawingContext dc = _pointerVisual.RenderOpen()) DrawPointerFeedback(dc, anchor: false);
        if (_showMagnifier && _pointerInitialized) UpdateMagnifier();
        using (DrawingContext dc = _magnifierVisual.RenderOpen()) DrawMagnifier(dc);
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
                dc.DrawRoundedRectangle(_chromeBrush, null, new Rect(x - 4, y - 2, text.Width + 8, text.Height + 4), 4, 4);
                dc.DrawText(text, new Point(x, y));
            }
        }
        Target(anchor ? _dragAnchor : _cursorPixel);
    }

    private void DrawSelection(DrawingContext dc, RectD pixelRect)
    {
        Rect rect = ToDipRect(pixelRect);
        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 88, 199, 243)), Math.Max(3, 3 * DipPerPixelX));
        glowPen.Freeze();
        var borderPen = new Pen(_selectionBrush, Math.Max(1.25, 1.25 * DipPerPixelX));
        borderPen.Freeze();
        dc.DrawRectangle(null, glowPen, rect);
        dc.DrawRectangle(null, borderPen, rect);
        DrawDimensionLabel(dc, pixelRect.ToPixelBounds(), rect);
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
        dc.DrawRoundedRectangle(_chromeBrush, null, background, 7, 7);
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
        dc.DrawRoundedRectangle(_chromeBrush, new Pen(_selectionBrush, 1), background, 10, 10);
        dc.DrawText(text, new Point(background.Left + paddingX, background.Top + paddingY));
        if (_precisionPointer.IsPrecision)
        {
            FormattedText precision = _precisionText ??= CreateText(UiText.Get("CapturePrecisionActive"), _uiTypeface, 12, _primaryTextBrush);
            dc.DrawRoundedRectangle(_chromeBrush, new Pen(_selectionBrush, 1),
                new Rect(16, 64, precision.Width + 20, precision.Height + 12), 6, 6);
            dc.DrawText(precision, new Point(26, 70));
        }
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
        dc.DrawRoundedRectangle(_chromeBrush, new Pen(_selectionBrush, Math.Max(1, DipPerPixelX)), panel, 9, 9);
        dc.DrawImage(_magnifierCrop, new Rect(x, y, width, imageHeight));

        double cellWidth = width / _magnifierCrop.PixelWidth;
        double cellHeight = imageHeight / _magnifierCrop.PixelHeight;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), Math.Max(0.5, DipPerPixelX));
        gridPen.Freeze();
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
        var crosshairPen = new Pen(_selectionBrush, Math.Max(1.5, 1.5 * DipPerPixelX));
        crosshairPen.Freeze();
        dc.DrawRectangle(null, crosshairPen, cell);

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

        if (_interaction == InteractionMode.Create)
        {
            PointD edge = ClampEdgePoint(rawPixel);
            _selection = RectD.FromCorners(_dragAnchor, edge).ClampTo(FrameBounds);
        }

        QueueFeedback();
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
        _selection = new RectD(point.X, point.Y, 0, 0);
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
        _selection = ResolveCompletedDrag(_dragAnchor, point, FrameBounds);
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

        switch (e.Key)
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
                QueueFeedback();
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

    private void NudgeSelection(Key key, ModifierKeys modifiers)
    {
        if (_selection is not RectD selection)
        {
            return;
        }

        double step = modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1;
        bool resize = modifiers.HasFlag(ModifierKeys.Shift);

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
        SelectionConfirmed?.Invoke(this, new RegionSelectionEventArgs(pixels));
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

    private Brush ResourceBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

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
}
