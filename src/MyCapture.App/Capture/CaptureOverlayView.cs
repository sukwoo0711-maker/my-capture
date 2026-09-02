using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

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

    internal const string InstructionText =
        "드래그해 캡처할 영역을 선택하세요  ·  놓으면 편집 창이 열립니다  ·  Esc 취소";

    private readonly FrozenFrame _frame;
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
    private BitmapSource? _magnifierCrop;
    private int _magnifierCropX = -1;
    private int _magnifierCropY = -1;
    private string _sampleLabel = "#000000";
    private bool _ended;

    internal CaptureOverlayView(FrozenFrame frame, bool showMagnifier = true)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _showMagnifier = showMagnifier;

        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        _dimmerBrush = ResourceBrush("Overlay.Dimmer", new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)));
        _selectionBrush = ResourceBrush("Overlay.SelectionBorder", new SolidColorBrush(Color.FromRgb(0x7D, 0xD7, 0xF8)));
        _chromeBrush = ResourceBrush("Surface.Floating", new SolidColorBrush(Color.FromArgb(0xF2, 0x15, 0x1E, 0x2B)));
        _primaryTextBrush = ResourceBrush("Text.Primary", Brushes.White);
        _mutedTextBrush = ResourceBrush("Text.Secondary", Brushes.LightGray);

        FontFamily uiFont = Application.Current.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        FontFamily monoFont = Application.Current.TryFindResource("Font.Mono") as FontFamily
            ?? new FontFamily("Consolas");
        _uiTypeface = new Typeface(uiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _monoTypeface = new Typeface(monoFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    }

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

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        drawingContext.DrawImage(_frame.Bitmap, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_selection is RectD focused)
        {
            DrawDimmer(drawingContext, focused);
            DrawSelection(drawingContext, focused);
        }
        else
        {
            drawingContext.DrawRectangle(_dimmerBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        DrawInstructions(drawingContext);
        DrawMagnifier(drawingContext);
    }

    private void DrawDimmer(DrawingContext dc, RectD pixelRect)
    {
        Rect selection = ToDipRect(pixelRect.ClampTo(FrameBounds));

        DrawIfPositive(dc, new Rect(0, 0, ActualWidth, selection.Top));
        DrawIfPositive(dc, new Rect(0, selection.Bottom, ActualWidth, Math.Max(0, ActualHeight - selection.Bottom)));
        DrawIfPositive(dc, new Rect(0, selection.Top, selection.Left, selection.Height));
        DrawIfPositive(dc, new Rect(selection.Right, selection.Top, Math.Max(0, ActualWidth - selection.Right), selection.Height));
    }

    private void DrawIfPositive(DrawingContext dc, Rect rect)
    {
        if (rect.Width > 0 && rect.Height > 0)
        {
            dc.DrawRectangle(_dimmerBrush, null, rect);
        }
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
        FormattedText text = CreateText(InstructionText, _uiTypeface, 13, _primaryTextBrush);
        const double paddingX = 14;
        const double paddingY = 9;
        double width = text.Width + (paddingX * 2);
        double left = Math.Max(16, (ActualWidth - width) / 2);
        var background = new Rect(left, 18, width, text.Height + (paddingY * 2));
        dc.DrawRoundedRectangle(_chromeBrush, new Pen(_selectionBrush, 1), background, 10, 10);
        dc.DrawText(text, new Point(background.Left + paddingX, background.Top + paddingY));
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

        double cellWidth = width / MagnifierSourcePixels;
        double cellHeight = imageHeight / MagnifierSourcePixels;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), Math.Max(0.5, DipPerPixelX));
        gridPen.Freeze();
        for (int index = 1; index < MagnifierSourcePixels; index++)
        {
            dc.DrawLine(gridPen, new Point(x + (index * cellWidth), y), new Point(x + (index * cellWidth), y + imageHeight));
            dc.DrawLine(gridPen, new Point(x, y + (index * cellHeight)), new Point(x + width, y + (index * cellHeight)));
        }

        int cursorX = Math.Clamp((int)Math.Floor(_cursorPixel.X) - _magnifierCropX, 0, MagnifierSourcePixels - 1);
        int cursorY = Math.Clamp((int)Math.Floor(_cursorPixel.Y) - _magnifierCropY, 0, MagnifierSourcePixels - 1);
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
        PointD rawPixel = ToPixelPoint(e.GetPosition(this));
        _cursorPixel = ClampSamplePoint(rawPixel);
        if (_showMagnifier)
        {
            UpdateMagnifier();
        }

        if (_interaction == InteractionMode.Create)
        {
            PointD edge = ClampEdgePoint(rawPixel);
            _selection = RectD.FromCorners(_dragAnchor, edge).ClampTo(FrameBounds);
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_ended)
        {
            return;
        }

        Focus();
        PointD rawPoint = ToPixelPoint(e.GetPosition(this));
        PointD point = ClampEdgePoint(rawPoint);
        _cursorPixel = ClampSamplePoint(rawPoint);
        _dragAnchor = point;
        _selection = new RectD(point.X, point.Y, 0, 0);
        _interaction = InteractionMode.Create;
        _ = Mouse.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_interaction != InteractionMode.Create || _ended)
        {
            return;
        }

        PointD rawPoint = ToPixelPoint(e.GetPosition(this));
        PointD point = ClampEdgePoint(rawPoint);
        _cursorPixel = ClampSamplePoint(rawPoint);
        _selection = ResolveCompletedDrag(_dragAnchor, point, FrameBounds);
        _interaction = InteractionMode.None;
        Mouse.Capture(null);
        e.Handled = true;
        InvalidateVisual();

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
            case Key.Enter:
                ConfirmSelection();
                e.Handled = true;
                return;
            case Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                _selection = FrameBounds;
                InvalidateVisual();
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

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_interaction == InteractionMode.None)
        {
            _magnifierCrop = null;
            InvalidateVisual();
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

        InvalidateVisual();
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
        SelectionConfirmed?.Invoke(this, new RegionSelectionEventArgs(pixels));
    }

    private void RequestCancel()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateMagnifier()
    {
        int centerX = Math.Clamp((int)Math.Floor(_cursorPixel.X), 0, _frame.PixelWidth - 1);
        int centerY = Math.Clamp((int)Math.Floor(_cursorPixel.Y), 0, _frame.PixelHeight - 1);
        int half = MagnifierSourcePixels / 2;
        int x = Math.Clamp(centerX - half, 0, Math.Max(0, _frame.PixelWidth - MagnifierSourcePixels));
        int y = Math.Clamp(centerY - half, 0, Math.Max(0, _frame.PixelHeight - MagnifierSourcePixels));
        int width = Math.Min(MagnifierSourcePixels, _frame.PixelWidth);
        int height = Math.Min(MagnifierSourcePixels, _frame.PixelHeight);

        if (_magnifierCrop is null || x != _magnifierCropX || y != _magnifierCropY)
        {
            var crop = new CroppedBitmap(_frame.Bitmap, new Int32Rect(x, y, width, height));
            crop.Freeze();
            _magnifierCrop = crop;
            _magnifierCropX = x;
            _magnifierCropY = y;
        }

        var pixel = new byte[4];
        _frame.Bitmap.CopyPixels(new Int32Rect(centerX, centerY, 1, 1), pixel, 4, 0);
        _sampleLabel = $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";
    }

    private Brush ResourceBrush(string key, Brush fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback;

    private FormattedText CreateText(string text, Typeface typeface, double size, Brush brush) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private RectD FrameBounds => new(0, 0, _frame.PixelWidth, _frame.PixelHeight);

    private double DipPerPixelX => ActualWidth / _frame.PixelWidth;

    private double DipPerPixelY => ActualHeight / _frame.PixelHeight;

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
        Math.Clamp(point.X, 0, Math.Max(0, _frame.PixelWidth - 1)),
        Math.Clamp(point.Y, 0, Math.Max(0, _frame.PixelHeight - 1)));

    private PointD ClampEdgePoint(PointD point) => new(
        Math.Clamp(point.X, 0, _frame.PixelWidth),
        Math.Clamp(point.Y, 0, _frame.PixelHeight));

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
